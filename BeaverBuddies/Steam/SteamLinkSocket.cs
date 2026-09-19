using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TimberNet;

namespace BeaverBuddies.Steam
{
    /// <summary>
    /// A byte stream over one Steam connection, for TimberNet.
    /// <para>
    /// Steam is only ever touched from <see cref="Pump"/>, which the game thread calls every frame.
    /// TimberNet's own threads talk to this socket through managed queues:
    /// <see cref="Write"/> only enqueues and never blocks (it may be called on the game thread, and a
    /// blocked game thread would stop the pump that is supposed to drain the queue), and
    /// <see cref="Read"/> blocks on a queue the pump fills.
    /// </para>
    /// <para>
    /// The connection completes in the background. <see cref="ConnectAsync"/> returns immediately and
    /// <see cref="WaitForConnection"/> is meant to be called on a worker thread.
    /// </para>
    /// </summary>
    public sealed class SteamLinkSocket : ISocketStream, IConnectionAwaitable, IFailureDescriber, ITransportInfo
    {
        /// <summary>Well under Steam's 512 KB message limit, and small enough to keep latency low.</summary>
        public const int MaxMessageBytes = 128 * 1024;
        /// <summary>A safety cap that only an unresponsive peer can reach; healthy sessions never queue this much.</summary>
        public const int DefaultMaxQueuedBytes = 128 * 1024 * 1024;
        public const double ConnectTimeoutSeconds = 40;
        public const double LingerSeconds = 3;
        /// <summary>How long Steam may refuse data without progress before the connection is declared stuck.</summary>
        public const double SendStallSeconds = 30;
        const int MaxReceivesPerPump = 256;
        const int MaxSendBytesPerPump = 16 * 1024 * 1024;

        readonly ISteamLinkBackend backend;
        readonly ulong handle;
        readonly Func<double> clock;
        readonly double startedAt;

        /// <summary>Adjustable so tests can reach the cap without allocating 128 MB.</summary>
        internal int MaxQueued = DefaultMaxQueuedBytes;

        readonly object gate = new object();
        readonly Queue<byte[]> outgoing = new Queue<byte[]>();
        long queuedBytes;
        bool connected, closeRequested, finished;
        double closeRequestedAt;
        string failure;

        readonly ManualResetEventSlim settled = new ManualResetEventSlim(false);
        readonly BlockingCollection<byte[]> incoming = new BlockingCollection<byte[]>();
        volatile bool readerClosed;
        byte[] current;
        int position;

        // Pump-thread state.
        readonly byte[] scratch = new byte[MaxMessageBytes];
        int inflight;
        bool nativeClosed, announced;
        LinkState lastState = (LinkState)(-1);
        long bytesSent, bytesReceived, bufferFullCount;
        double bufferFullSince = -1;

        public ulong RemoteSteamId { get; }
        public bool IsIncoming { get; }
        public string Name { get; }
        public string TransportName => "Steam";
        public int MaxChunkSize => 32 * 1024;
        public int MaxBytesPerSecond => int.MaxValue;
        public string FailureReason { get { lock (gate) return failure; } }

        public bool Connected { get { lock (gate) return connected && !finished && !closeRequested; } }

        internal SteamLinkSocket(ISteamLinkBackend backend, ulong handle, ulong remote, string name, Func<double> clock, bool incoming)
        {
            this.backend = backend; this.handle = handle; this.clock = clock;
            RemoteSteamId = remote; Name = name; IsIncoming = incoming;
            startedAt = clock();
        }

        public Task ConnectAsync()
        {
            // The real connection is established by the pump; see the class notes for why this must not wait.
            lock (gate)
            {
                if (finished) return Task.FromException(new IOException(failure ?? "The Steam connection has ended."));
            }
            return Task.CompletedTask;
        }

        public void WaitForConnection(int timeoutMilliseconds)
        {
            if (!settled.Wait(timeoutMilliseconds))
            {
                Fail("Steam did not connect in time. Check that both players are online in Steam and that the host is still hosting.");
                throw new IOException(FailureReason);
            }
            lock (gate)
            {
                if (connected) return;
                throw new IOException(failure ?? "The Steam connection was closed before it was established.");
            }
        }

        // ---- TimberNet side (any thread) ----

        public int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return 0;
            while (current == null)
            {
                if (readerClosed) return 0;
                // Blocks until the pump delivers data, or the queue is completed and empty.
                if (!incoming.TryTake(out byte[] next, Timeout.Infinite)) return 0;
                if (readerClosed) return 0;
                if (next.Length == 0) continue;
                current = next; position = 0;
            }
            int size = Math.Min(count, current.Length - position);
            Buffer.BlockCopy(current, position, buffer, offset, size);
            position += size;
            if (position == current.Length) { current = null; position = 0; }
            return size;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            // Split here so a single queued piece always fits in one Steam message.
            while (count > 0)
            {
                int size = Math.Min(count, MaxMessageBytes);
                var piece = new byte[size];
                Buffer.BlockCopy(buffer, offset, piece, 0, size);
                lock (gate)
                {
                    if (finished || closeRequested) throw new IOException(failure ?? "The Steam connection is closed.");
                    if (queuedBytes + size > MaxQueued)
                    {
                        FailLocked("Too much data was waiting to be sent to the other player, so the connection was dropped.");
                        throw new IOException(failure);
                    }
                    outgoing.Enqueue(piece);
                    queuedBytes += size;
                }
                offset += size; count -= size;
            }
        }

        public void Close()
        {
            lock (gate)
            {
                if (closeRequested) return;
                closeRequested = true;
                closeRequestedAt = clock();
            }
            // A local close ends reads immediately; the pump later flushes pending writes and closes Steam's side.
            readerClosed = true;
            CompleteIncoming();
            settled.Set();
        }

        // ---- failure handling ----

        internal void Abort(string reason) { Fail(reason); }

        void Fail(string reason)
        {
            lock (gate) FailLocked(reason);
        }

        // Caller holds the gate.
        void FailLocked(string reason)
        {
            if (failure == null) failure = reason;
            finished = true;
            settled.Set();
            CompleteIncoming();
        }

        void CompleteIncoming()
        {
            try { incoming.CompleteAdding(); }
            catch (ObjectDisposedException) { }
        }

        // ---- game-thread pump ----

        /// <summary>Moves data and state between Steam and the queues. Returns true once this socket is finished.</summary>
        internal bool Pump(double now)
        {
            if (nativeClosed) return true;
            try { return PumpCore(now); }
            catch (Exception e)
            {
                // One misbehaving connection must never stop the others, or the game.
                Fail("Steam networking error: " + e.Message);
                return CloseNative(SteamEndReasons.SessionEnded, "error", false);
            }
        }

        bool PumpCore(double now)
        {
            bool closing, done; double closeAt;
            lock (gate) { closing = closeRequested; closeAt = closeRequestedAt; done = finished; }
            if (done) return CloseNative(SteamEndReasons.SessionEnded, "closed", false);

            LinkState state = backend.GetState(handle, out int reason, out string debug);
            if (state != lastState)
            {
                Plugin.Log($"Steam link to {Name}: {(lastState == (LinkState)(-1) ? "start" : lastState.ToString())} -> {state}" +
                    (reason != 0 ? $" (Steam code {reason}: {debug})" : ""));
                lastState = state;
            }

            switch (state)
            {
                case LinkState.Connecting:
                    if (closing) return CloseNative(SteamEndReasons.SessionEnded, "closed while connecting", false);
                    if (now - startedAt > ConnectTimeoutSeconds)
                    {
                        Fail("Steam could not reach the other player in time. Check that both players are online in Steam.");
                        return CloseNative(SteamEndReasons.SessionEnded, "connect timeout", false);
                    }
                    return false;

                case LinkState.Connected:
                    MarkConnected();
                    if (!Receive()) return CloseNative(SteamEndReasons.SessionEnded, "receive failed", false);
                    if (closing)
                    {
                        // Give queued writes a bounded chance to go out before closing.
                        bool drained = SendPending(now);
                        if (drained || now - closeAt > LingerSeconds || IsFinished())
                            return CloseNative(SteamEndReasons.SessionEnded, "session ended", true);
                        return false;
                    }
                    SendPending(now);
                    return IsFinished() && CloseNative(SteamEndReasons.SessionEnded, "send failed", false);

                case LinkState.ClosedByPeer:
                    // Anything the peer sent before closing is still delivered.
                    Receive();
                    Fail(SteamEndReasons.Describe(reason, debug));
                    return CloseNative(SteamEndReasons.SessionEnded, "closed by peer", false);

                case LinkState.Failed:
                    Fail(SteamEndReasons.Describe(reason, debug));
                    return CloseNative(SteamEndReasons.SessionEnded, "failed", false);

                default:
                    Fail("The Steam connection ended.");
                    return CloseNative(SteamEndReasons.SessionEnded, "gone", false);
            }
        }

        bool IsFinished() { lock (gate) return finished; }

        void MarkConnected()
        {
            lock (gate)
            {
                if (connected) return;
                connected = true;
            }
            settled.Set();
        }

        /// <summary>True the first time an accepted connection is usable, so the listener can hand it out once.</summary>
        internal bool TakeAnnouncement()
        {
            if (announced || !IsIncoming || !Connected) return false;
            announced = true;
            return true;
        }

        bool Receive()
        {
            int n = backend.Receive(handle, OnMessage, MaxReceivesPerPump);
            if (n < 0)
            {
                Fail("Steam could not read data from the other player.");
                return false;
            }
            return true;
        }

        void OnMessage(byte[] message)
        {
            if (readerClosed) return;
            bytesReceived += message.Length;
            try { incoming.Add(message); }
            catch (InvalidOperationException) { /* completed by a concurrent close */ }
        }

        // Returns true when nothing is left to send.
        bool SendPending(double now)
        {
            int sent = 0;
            while (true)
            {
                if (inflight == 0 && !FillScratch()) return true;
                LinkSend result = backend.Send(handle, scratch, 0, inflight);
                if (result == LinkSend.Ok)
                {
                    lock (gate) queuedBytes -= inflight;
                    bufferFullSince = -1;
                    bytesSent += inflight; sent += inflight; inflight = 0;
                    if (sent >= MaxSendBytesPerPump) return false;
                    continue;
                }
                if (result == LinkSend.BufferFull)
                {
                    // Steam's buffer is full: keep the data and try again next frame. Nothing is dropped.
                    if (bufferFullCount++ == 0) Plugin.Log($"Steam link to {Name}: send buffer full; queueing (this is normal during the initial save transfer).");
                    if (bufferFullSince < 0) bufferFullSince = now;
                    else if (now - bufferFullSince > SendStallSeconds)
                        Fail($"Steam stopped accepting data for {SendStallSeconds:0} seconds, so the connection was dropped. The other player may have lost their connection.");
                    return false;
                }
                Fail("Steam refused to send data to the other player.");
                return false;
            }
        }

        // Gathers queued writes into one Steam message. Returns true if there is something to send.
        bool FillScratch()
        {
            lock (gate)
            {
                while (outgoing.Count > 0)
                {
                    byte[] head = outgoing.Peek();
                    if (inflight + head.Length > MaxMessageBytes) break;
                    outgoing.Dequeue();
                    Buffer.BlockCopy(head, 0, scratch, inflight, head.Length);
                    inflight += head.Length;
                }
            }
            return inflight > 0;
        }

        bool CloseNative(int reason, string debug, bool linger)
        {
            if (nativeClosed) return true;
            nativeClosed = true;
            lock (gate) { finished = true; }
            CompleteIncoming();
            settled.Set();
            try { backend.Close(handle, reason, debug, linger); }
            catch (Exception e) { Plugin.LogWarning($"Steam link to {Name}: error while closing: {e.Message}"); }
            Plugin.Log($"Steam link to {Name} closed: sent {bytesSent} bytes, received {bytesReceived} bytes, " +
                $"send buffer was full {bufferFullCount} time(s)." + (FailureReason != null ? " Reason: " + FailureReason : ""));
            return true;
        }

        /// <summary>Immediate shutdown, for when the game itself is exiting.</summary>
        internal void Shutdown()
        {
            Close();
            if (!nativeClosed) CloseNative(SteamEndReasons.SessionEnded, "game closing", true);
        }
    }
}
