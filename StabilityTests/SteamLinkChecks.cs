using System.Collections.Concurrent;
using BeaverBuddies.Steam;
using Newtonsoft.Json.Linq;
using TimberNet;

// The production Steam transport core (SteamLinkSocket / SteamLinkManager) over a fake Steam network.
// The fake enforces the rules the design relies on: Steam is only called on the game thread, messages
// stay under Steam's 512 KB limit, and the send buffer is small enough to force backpressure.
static class SteamLinkChecks
{
    const int MaxSteamMessage = 512 * 1024;
    const ulong HostId = 1001, GuestId = 1002, StrangerId = 1003;

    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    // ---------------- fake Steam ----------------

    sealed class FakeConn
    {
        public ulong Handle;
        public FakeBackend Owner = null!;
        public FakeConn? Peer;
        public LinkState State = LinkState.Connecting;
        public int EndReason; public string EndDebug = "";
        public bool Accepted, CloseAfterDrain, Closed, ClosedLinger;
        public int ClosedReason;
        public readonly Queue<byte[]> Outbox = new(), Inbox = new();
        public long PendingBytes;
    }

    sealed class FakeNet
    {
        public int GameThreadId;
        public readonly Dictionary<ulong, FakeBackend> Backends = new();
        public int BytesPerDeliver = int.MaxValue;
        public void Deliver()
        {
            foreach (var backend in Backends.Values)
                foreach (var conn in backend.Conns.Values.ToList())
                {
                    // A lingering close keeps draining its queued data even though the handle is closed.
                    if (conn.Peer == null || (conn.Closed && !conn.CloseAfterDrain)) continue;
                    // A connection becomes usable one delivery after the host accepts it.
                    if (conn.Accepted && conn.State == LinkState.Connecting && conn.Peer.State == LinkState.Connecting)
                    { conn.State = LinkState.Connected; conn.Peer.State = LinkState.Connected; }
                    int budget = BytesPerDeliver;
                    while (conn.Outbox.Count > 0 && (budget > 0 || BytesPerDeliver == int.MaxValue) && !conn.Peer.Closed)
                    {
                        var message = conn.Outbox.Dequeue();
                        conn.PendingBytes -= message.Length;
                        conn.Peer.Inbox.Enqueue(message);
                        budget -= message.Length;
                    }
                    if (conn.Outbox.Count == 0 && conn.CloseAfterDrain && conn.Peer.State != LinkState.ClosedByPeer)
                    { conn.Peer.State = LinkState.ClosedByPeer; conn.Peer.EndReason = conn.ClosedReason; conn.Peer.EndDebug = "closed"; }
                }
        }
    }

    sealed class FakeBackend : ISteamLinkBackend
    {
        public readonly FakeNet Net; public readonly ulong Self;
        public readonly Dictionary<ulong, FakeConn> Conns = new();
        public readonly List<(ulong Conn, ulong Remote)> Incoming = new();
        public readonly ConcurrentQueue<string> Events = new();
        public ulong ListenHandle; public bool Listening;
        public int SendBufferLimit = 256 * 1024;
        public int BufferFullCount, MaxMessageSeen, SendCount;
        public Func<ulong, bool>? ThrowOnSend;
        ulong next = 100;

        public FakeBackend(FakeNet net, ulong self) { Net = net; Self = self; net.Backends[self] = this; }

        void OnGameThread()
        {
            if (Environment.CurrentManagedThreadId != Net.GameThreadId)
                throw new InvalidOperationException("Steam was called off the game thread");
        }
        FakeConn Get(ulong h) => Conns.TryGetValue(h, out var c) ? c : throw new InvalidOperationException("unknown connection " + h);

        public ulong CreateListenSocket() { OnGameThread(); Listening = true; return ListenHandle = ++next; }
        public void CloseListenSocket(ulong listen) { OnGameThread(); Listening = false; }

        public ulong Connect(ulong remote)
        {
            OnGameThread();
            var mine = new FakeConn { Handle = ++next, Owner = this };
            Conns[mine.Handle] = mine;
            if (Net.Backends.TryGetValue(remote, out var other) && other.Listening)
            {
                var theirs = new FakeConn { Handle = ++other.next, Owner = other, Peer = mine };
                mine.Peer = theirs; other.Conns[theirs.Handle] = theirs;
                other.Incoming.Add((theirs.Handle, Self));
            }
            return mine.Handle;
        }

        public bool Accept(ulong h) { OnGameThread(); Get(h).Accepted = true; return true; }
        public void Configure(ulong h) { OnGameThread(); }

        public LinkState GetState(ulong h, out int endReason, out string endDebug)
        {
            OnGameThread();
            endReason = 0; endDebug = "";
            if (!Conns.TryGetValue(h, out var c) || c.Closed) return LinkState.Gone;
            endReason = c.EndReason; endDebug = c.EndDebug;
            return c.State;
        }

        public LinkSend Send(ulong h, byte[] data, int offset, int count)
        {
            OnGameThread();
            var c = Get(h);
            if (ThrowOnSend?.Invoke(h) == true) throw new InvalidOperationException("injected send failure");
            if (count > MaxSteamMessage) throw new InvalidOperationException($"Steam message of {count} bytes exceeds the limit");
            MaxMessageSeen = Math.Max(MaxMessageSeen, count);
            SendCount++;
            if (c.PendingBytes + count > SendBufferLimit) { BufferFullCount++; return LinkSend.BufferFull; }
            var copy = new byte[count]; Buffer.BlockCopy(data, offset, copy, 0, count);
            c.Outbox.Enqueue(copy); c.PendingBytes += count;
            return LinkSend.Ok;
        }

        public int Receive(ulong h, Action<byte[]> deliver, int max)
        {
            OnGameThread();
            if (!Conns.TryGetValue(h, out var c) || c.Closed) return -1;
            int n = 0;
            while (n < max && c.Inbox.Count > 0) { deliver(c.Inbox.Dequeue()); n++; }
            return n;
        }

        public void Close(ulong h, int reason, string debug, bool linger)
        {
            OnGameThread();
            Events.Enqueue($"close {h} reason={reason} linger={linger}");
            if (!Conns.TryGetValue(h, out var c) || c.Closed) return;
            c.ClosedReason = reason; c.ClosedLinger = linger;
            if (linger && c.Peer != null) { c.CloseAfterDrain = true; c.Closed = true; return; }
            c.Closed = true;
            if (c.Peer != null && c.Peer.State != LinkState.ClosedByPeer)
            { c.Peer.State = LinkState.ClosedByPeer; c.Peer.EndReason = reason; c.Peer.EndDebug = debug; }
        }

    }

    sealed class TestClock { public double Now; public double Read() => Now; }

    // Stands in for Unity's main thread: runs queued work, then Steam callbacks and pumps, in a loop.
    sealed class GameThread : IDisposable
    {
        readonly Thread thread; readonly ConcurrentQueue<Action> work = new(); volatile bool stop;
        readonly List<Action> ticks = new();
        public GameThread(FakeNet net) { thread = new Thread(Loop) { IsBackground = true }; net.GameThreadId = thread.ManagedThreadId; thread.Start(); }
        public void AddTick(Action tick) { lock (ticks) ticks.Add(tick); }
        public void Post(Action a)
        {
            if (Environment.CurrentManagedThreadId == thread.ManagedThreadId) a(); else work.Enqueue(a);
        }
        public void Run(Action a)
        {
            if (Environment.CurrentManagedThreadId == thread.ManagedThreadId) { a(); return; }
            using var done = new ManualResetEventSlim(); Exception? error = null;
            work.Enqueue(() => { try { a(); } catch (Exception e) { error = e; } finally { done.Set(); } });
            if (!done.Wait(4000)) throw new TimeoutException("game thread did not run the work");
            if (error != null) throw new Exception("on game thread: " + error.Message, error);
        }
        public T Run<T>(Func<T> f) { T result = default!; Run((Action)(() => { result = f(); })); return result; }
        void Loop()
        {
            while (!stop)
            {
                while (work.TryDequeue(out var a)) a();
                Action[] snapshot; lock (ticks) snapshot = ticks.ToArray();
                foreach (var tick in snapshot) tick();
                Thread.Sleep(1);
            }
        }
        public void Dispose() { stop = true; thread.Join(1000); }
    }

    // One host and one guest, each with its own manager, sharing a fake network.
    sealed class Rig : IDisposable
    {
        public readonly FakeNet Net = new(); public readonly TestClock Clock = new();
        public readonly FakeBackend HostBackend, GuestBackend;
        public readonly SteamLinkManager Host, Guest;
        public readonly GameThread Game;
        public Func<ulong, bool> Allow = _ => true;

        public Rig(int sendBuffer = 256 * 1024, int bytesPerDeliver = int.MaxValue)
        {
            Game = new GameThread(Net);
            Net.BytesPerDeliver = bytesPerDeliver;
            HostBackend = new FakeBackend(Net, HostId) { SendBufferLimit = sendBuffer };
            GuestBackend = new FakeBackend(Net, GuestId) { SendBufferLimit = sendBuffer };
            Host = new SteamLinkManager(HostBackend, Clock.Read, id => "host-" + id);
            Guest = new SteamLinkManager(GuestBackend, Clock.Read, id => "guest-" + id);
            Game.AddTick(() =>
            {
                // Steam's status callback, delivered on the game thread.
                foreach (var (conn, remote) in HostBackend.Incoming.ToList())
                { HostBackend.Incoming.Remove((conn, remote)); Host.OnIncomingConnection(HostBackend.ListenHandle, conn, remote); }
                Net.Deliver();
                Host.Pump(); Guest.Pump();
                Net.Deliver();
            });
        }

        public SteamLinkListener StartListener()
        {
            var listener = new SteamLinkListener(Host, Game.Run, id => Allow(id));
            listener.Start();
            return listener;
        }

        public SteamLinkSocket ConnectGuest() => Game.Run(() => Guest.Connect(HostId)!);

        public void Dispose() => Game.Dispose();
    }

    static (SteamLinkSocket Guest, SteamLinkSocket HostSide, SteamLinkListener Listener) Pair(Rig rig)
    {
        var listener = rig.StartListener();
        var guest = rig.ConnectGuest();
        var hostTask = Task.Run(() => (SteamLinkSocket)listener.AcceptClient());
        Check(hostTask.Wait(3000), "host never accepted the guest");
        guest.WaitForConnection(3000);
        return (guest, hostTask.Result, listener);
    }

    static byte[] Pattern(int length, int seed = 7)
    {
        var bytes = new byte[length]; var rng = new Random(seed); rng.NextBytes(bytes); return bytes;
    }

    static byte[] ReadExactly(ISocketStream stream, int count)
    {
        var bytes = new byte[count]; int total = 0;
        while (total < count)
        {
            int n = stream.Read(bytes, total, count - total);
            if (n == 0) throw new IOException($"stream ended after {total} of {count} bytes");
            total += n;
        }
        return bytes;
    }

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Receiving always asks Steam for exactly one full buffer, whatever is waiting", () =>
        {
            // Steamworks.NET throws when the requested count differs from the buffer length. 1.0.4 asked for
            // "what is left of the limit", which broke with 193 to 255 messages waiting (a guest after a long load).
            foreach (int waiting in new[] { 0, 1, 63, 64, 65, 192, 193, 250, 255, 256, 257, 1000 })
            {
                int left = waiting, handled = 0;
                int total = ReceiveBatching.Drain(64, 256, requested =>
                {
                    if (requested != 64) throw new ArgumentException("ppOutMessages must be the same size as nMaxMessages!");
                    int count = Math.Min(left, requested);
                    left -= count;
                    return count;
                }, _ => handled++);
                Equal(Math.Min(waiting, 256), total);
                Equal(total, handled);
            }
            // Partial batches (messages still arriving) may overshoot the soft limit by less than one buffer.
            int calls = 0;
            Equal(300, ReceiveBatching.Drain(64, 256, requested => { Equal(64, requested); calls++; return 50; }, _ => { }));
            Equal(6, calls);
            Equal(-1, ReceiveBatching.Drain(64, 256, _ => -1, _ => { }));
        });
        yield return ("Steam link connects in the background without blocking the caller", () =>
        {
            using var rig = new Rig();
            var listener = rig.StartListener();
            var guest = rig.ConnectGuest();
            // Never waits for the network: the caller (TimberClient.Start on the game thread) must not stall.
            Check(guest.ConnectAsync().IsCompleted, "ConnectAsync waited");
            Check(!guest.Connected, "connected before the host accepted");
            guest.WaitForConnection(3000);
            Check(guest.Connected);
            var host = Task.Run(() => (SteamLinkSocket)listener.AcceptClient());
            Check(host.Wait(3000) && host.Result.Connected, "the host side never became usable");
        });
        yield return ("Bytes arrive intact and in order through a small send buffer", () =>
        {
            using var rig = new Rig(sendBuffer: 200 * 1024, bytesPerDeliver: 150 * 1024);
            var (guest, hostSide, _) = Pair(rig);
            var data = Pattern(3 * 1024 * 1024);
            var received = Task.Run(() => ReadExactly(guest, data.Length));
            // Written the way TimberNet writes: a 4-byte header then payload chunks.
            hostSide.Write(new byte[] { 0, 0, 0, 1 }, 0, 4);
            for (int i = 0; i < data.Length; i += 32 * 1024) hostSide.Write(data, i, Math.Min(32 * 1024, data.Length - i));
            Check(received.Wait(4000), "transfer stalled");
            var expected = new byte[4 + data.Length]; expected[3] = 1; Buffer.BlockCopy(data, 0, expected, 4, data.Length);
            // Compare the payload after the header we wrote.
            Check(received.Result.Take(4).SequenceEqual(new byte[] { 0, 0, 0, 1 }));
            Check(received.Result.Skip(4).SequenceEqual(data.Take(data.Length - 4)), "payload corrupted or reordered");
            Check(rig.HostBackend.BufferFullCount > 0, "the small buffer never applied backpressure");
            Check(rig.HostBackend.MaxMessageSeen <= SteamLinkSocket.MaxMessageBytes, "message exceeded the transport's own limit");
        });
        yield return ("Tiny writes are coalesced into fewer Steam messages", () =>
        {
            using var rig = new Rig();
            var (guest, hostSide, _) = Pair(rig);
            var payload = Pattern(10_000);
            for (int i = 0; i < 1000; i++) hostSide.Write(payload, i * 10, 10);
            Check(ReadExactly(guest, 10_000).SequenceEqual(payload));
            // 1000 writes must not become 1000 native messages.
            Check(rig.HostBackend.SendCount < 200, $"1000 tiny writes became {rig.HostBackend.SendCount} Steam messages");
        });
        yield return ("Write never blocks, even when the game thread is not pumping", () =>
        {
            using var rig = new Rig();
            var (_, hostSide, _) = Pair(rig);
            rig.Game.Dispose();                   // nothing pumps from here on
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var chunk = new byte[32 * 1024];
            for (int i = 0; i < 400; i++) hostSide.Write(chunk, 0, chunk.Length);   // ~12 MB
            Check(watch.ElapsedMilliseconds < 1500, $"Write blocked ({watch.ElapsedMilliseconds} ms)");
        });
        yield return ("A peer that never drains cannot grow the queue without limit", () =>
        {
            using var rig = new Rig();
            var (_, hostSide, _) = Pair(rig);
            rig.Game.Dispose();
            var chunk = new byte[64 * 1024];
            hostSide.MaxQueued = 1024 * 1024;
            Throws<IOException>(() => { for (int i = 0; i < 1000; i++) hostSide.Write(chunk, 0, chunk.Length); });
            Check(hostSide.FailureReason != null && hostSide.FailureReason.Contains("waiting to be sent"), "no explanation: " + hostSide.FailureReason);
            Throws<IOException>(() => hostSide.Write(chunk, 0, 1));
        });
        yield return ("A failed connection explains itself with Steam's reason", () =>
        {
            using var rig = new Rig();
            rig.Allow = _ => false;                    // stay in "connecting" so the failure cannot race a completed connect
            rig.StartListener();
            var guest = rig.ConnectGuest();
            // Steam gives up on the route.
            rig.Game.Run(() =>
            {
                var c = rig.GuestBackend.Conns.Values.Single();
                c.State = LinkState.Failed; c.EndReason = 5003; c.EndDebug = "no route to host";
                if (c.Peer != null) c.Peer.State = LinkState.Failed;
            });
            var error = Assert.Throws<IOException>(() => guest.WaitForConnection(3000));
            Check(error.Message.Contains("timed out") && error.Message.Contains("5003") && error.Message.Contains("no route to host"), error.Message);
            Equal(error.Message, guest.FailureReason);
            Check(!guest.Connected);
            Check(guest.Read(new byte[1], 0, 1) == 0, "reads must end after a failure");
        });
        yield return ("Connecting that never succeeds times out with a clear message", () =>
        {
            using var rig = new Rig();
            var guest = rig.ConnectGuest();            // nobody is listening, so Steam never answers
            rig.Clock.Now += SteamLinkSocket.ConnectTimeoutSeconds + 1;
            var error = Assert.Throws<IOException>(() => guest.WaitForConnection(3000));
            Check(error.Message.Contains("could not reach"), error.Message);
        });
        yield return ("A send that makes no progress fails clearly instead of hanging forever", () =>
        {
            // The buffer is smaller than one message, so Steam can never accept it.
            using var rig = new Rig(sendBuffer: 16 * 1024);
            var (guest, hostSide, _) = Pair(rig);
            hostSide.Write(new byte[64 * 1024], 0, 64 * 1024);
            Thread.Sleep(100);
            Check(hostSide.FailureReason == null, "failed too early");
            rig.Clock.Now += SteamLinkSocket.SendStallSeconds + 1;
            Check(SpinWait.SpinUntil(() => hostSide.FailureReason != null, 2000), "a stalled send was never reported");
            Check(hostSide.FailureReason!.Contains("stopped accepting data"), hostSide.FailureReason);
            Throws<IOException>(() => hostSide.Write(new byte[1], 0, 1));
        });
        yield return ("Data sent before the peer closed is still delivered before end-of-stream", () =>
        {
            using var rig = new Rig();
            var (guest, hostSide, _) = Pair(rig);
            var data = Pattern(500_000);
            hostSide.Write(data, 0, data.Length);
            hostSide.Close();                           // linger: flushes, then closes
            Check(ReadExactly(guest, data.Length).SequenceEqual(data), "data lost on close");
            Equal(0, guest.Read(new byte[1], 0, 1));    // then a clean end-of-stream
            Check(guest.FailureReason != null && guest.FailureReason.Contains("ended"), guest.FailureReason ?? "no reason");
            Check(rig.HostBackend.Events.Any(e => e.Contains("linger=True")), "close did not linger");
        });
        yield return ("A local close ends reads at once and later writes fail", () =>
        {
            using var rig = new Rig();
            var (guest, _, _) = Pair(rig);
            var blocked = Task.Run(() => guest.Read(new byte[1], 0, 1));
            Thread.Sleep(50); Check(!blocked.IsCompleted, "read returned with no data");
            guest.Close(); guest.Close();               // idempotent
            Check(blocked.Wait(2000) && blocked.Result == 0, "close did not release the blocked reader");
            Throws<IOException>(() => guest.Write(new byte[1], 0, 1));
            Check(!guest.Connected);
        });
        yield return ("One connection's backend error does not stop the others", () =>
        {
            using var rig = new Rig();
            var (guest, hostSide, listener) = Pair(rig);
            ulong doomed = rig.Game.Run(() => rig.HostBackend.Conns.Keys.Single());
            var guest2 = rig.ConnectGuest();
            var hostSide2 = Task.Run(() => (SteamLinkSocket)listener.AcceptClient());
            Check(hostSide2.Wait(3000)); guest2.WaitForConnection(3000);
            // Only the first host-side connection's sends throw.
            rig.HostBackend.ThrowOnSend = h => h == doomed;
            hostSide.Write(new byte[10], 0, 10);
            hostSide2.Result.Write(Pattern(100), 0, 100);
            Check(ReadExactly(guest2, 100).SequenceEqual(Pattern(100)), "the healthy connection stopped working");
            Check(SpinWait.SpinUntil(() => hostSide.FailureReason != null, 2000), "the broken connection was not marked failed");
            Check(hostSide.FailureReason!.Contains("injected send failure"), hostSide.FailureReason);
        });
        yield return ("Only players in the host's lobby are admitted, with a grace period for slow lobby updates", () =>
        {
            using var rig = new Rig();
            var lobby = new HashSet<ulong>();
            rig.Allow = id => { lock (lobby) return lobby.Contains(id); };
            var listener = rig.StartListener();
            var guest = rig.ConnectGuest();
            Thread.Sleep(80);
            Check(!guest.Connected, "a player outside the lobby was admitted");
            // The lobby list catches up inside the grace period: they are admitted.
            lock (lobby) lobby.Add(GuestId);
            var host = Task.Run(() => (SteamLinkSocket)listener.AcceptClient());
            Check(host.Wait(3000), "member was not admitted after the lobby caught up");
            guest.WaitForConnection(3000);

            // A stranger who never joins is rejected once the grace period ends.
            SteamLinkManager strangerManager = null!;
            rig.Game.Run(() =>                      // registered on the game thread: the fake network is not thread-safe
            {
                var stranger = new FakeBackend(rig.Net, StrangerId);
                strangerManager = new SteamLinkManager(stranger, rig.Clock.Read, id => "x");
            });
            rig.Game.AddTick(() => strangerManager.Pump());
            var socket = rig.Game.Run(() => strangerManager.Connect(HostId)!);
            Thread.Sleep(80);
            rig.Clock.Now += SteamLinkManager.MembershipGraceSeconds + 1;
            var error = Assert.Throws<IOException>(() => socket.WaitForConnection(3000));
            Check(error.Message.Contains("1001") || error.Message.Contains("ended"), error.Message);
            Check(rig.HostBackend.Events.Any(e => e.Contains("reason=1001")), "the stranger was not rejected");
        });
        yield return ("A duplicate incoming notification is not accepted twice", () =>
        {
            using var rig = new Rig();
            var listener = rig.StartListener();
            var guest = rig.ConnectGuest();
            var host = Task.Run(() => (SteamLinkSocket)listener.AcceptClient());
            Check(host.Wait(3000), "first arrival was not accepted");
            // Steam repeats the arrival notice for the connection that was already accepted.
            var (handle, remote) = rig.Game.Run(() => (rig.HostBackend.Conns.Keys.Single(), GuestId));
            rig.Game.Run(() => rig.Host.OnIncomingConnection(rig.HostBackend.ListenHandle, handle, remote));
            Thread.Sleep(50);
            Equal(1, rig.Game.Run(() => rig.Host.ConnectionCount));
            var second = Task.Run(() => { try { return listener.AcceptClient(); } catch (InvalidOperationException) { return null; } });
            Thread.Sleep(80);
            Check(!second.IsCompleted, "the duplicate produced a second accepted player");
            listener.Stop();
            Check(second.Wait(2000));
        });
        yield return ("A new listener replaces one that was never stopped (rehost)", () =>
        {
            using var rig = new Rig();
            var first = rig.StartListener();
            var stale = Task.Run(() => { try { first.AcceptClient(); return false; } catch (InvalidOperationException) { return true; } });
            var second = rig.StartListener();          // the old one was never stopped
            Check(rig.HostBackend.Listening, "no listen socket after the replacement");
            var guest = rig.ConnectGuest();
            var accepted = Task.Run(() => (SteamLinkSocket)second.AcceptClient());
            Check(accepted.Wait(3000), "the new listener did not receive the guest");
            guest.WaitForConnection(3000);
            // The old listener's late stop must not tear down the new one.
            first.Stop();
            Thread.Sleep(50);
            Check(rig.HostBackend.Listening, "the stale stop closed the new listener");
            Check(accepted.Result.Connected);
        });
        yield return ("Stopping the listener releases a blocked accept", () =>
        {
            using var rig = new Rig();
            var listener = rig.StartListener();
            var accept = Task.Run(() => { try { listener.AcceptClient(); return false; } catch (InvalidOperationException) { return true; } });
            Thread.Sleep(50);
            listener.Stop();
            Check(accept.Wait(2000) && accept.Result, "AcceptClient was not released by Stop");
            Check(!rig.HostBackend.Listening, "the listen socket stayed open");
        });
        yield return ("A full TimberNet session runs over Steam: handshake, save, events, activity", () =>
        {
            using var rig = new Rig(sendBuffer: 192 * 1024, bytesPerDeliver: 96 * 1024);
            var map = Pattern(2 * 1024 * 1024, 3);
            var listener = new SteamLinkListener(rig.Host, rig.Game.Run, _ => true);
            var server = new TimberServer(listener, () => Task.FromResult(map), null) { CompatibilityIdentity = "same" };
            var socket = rig.ConnectGuestAfterListen(server);
            var client = new TimberClient(socket) { CompatibilityIdentity = "same" };
            byte[]? gotMap = null; string error = "";
            client.OnMapReceived += bytes => gotMap = bytes;
            client.OnError += e => error = e;
            // Start on the game thread like the real game does: it must not stall the connection it is waiting for.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            rig.Game.Run(() => client.Start());
            Check(watch.ElapsedMilliseconds < 1000, $"Start blocked the game thread for {watch.ElapsedMilliseconds} ms");
            try
            {
                Check(SpinWait.SpinUntil(() => { server.Update(); client.Update(); return gotMap != null || error != ""; }, 4500), "no save arrived; error: " + error);
                Check(error == "", error);
                Check(gotMap!.SequenceEqual(map), "the save was corrupted in transit");
                server.DoUserInitiatedEvent(new JObject { [TimberNetBase.TYPE_KEY] = "Ping", [TimberNetBase.TICKS_KEY] = 0, ["n"] = 1 });
                Check(SpinWait.SpinUntil(() => client.ReadEvents(0).Any(e => (int?)e["n"] == 1), 3000), "event never reached the guest");
                server.SendActivity(new PlayerActivity(0, "Host", "FF8800", true, 1, 2, 3));
                Check(SpinWait.SpinUntil(() => client.TakeActivity().Length > 0, 3000), "activity never reached the guest");
                client.SendActivity(new PlayerActivity(0, "Guest", "00FF88", true, 4, 5, 6));
                Check(SpinWait.SpinUntil(() => server.TakeActivity().Any(a => a.PlayerId == 1), 3000), "guest activity never reached the host");
            }
            finally { rig.Game.Run(() => { server.Close(); client.Close(); }); }
        });
        yield return ("A failed Steam connection reaches the player as an error with the reason", () =>
        {
            using var rig = new Rig();
            var socket = rig.ConnectGuest();
            var client = new TimberClient(socket) { CompatibilityIdentity = "same" };
            string error = "";
            client.OnError += e => error = e;
            rig.Game.Run(() => client.Start());
            rig.Game.Run(() =>
            {
                var c = rig.GuestBackend.Conns.Values.Single();
                c.State = LinkState.Failed; c.EndReason = 4001; c.EndDebug = "host gone";
            });
            Check(SpinWait.SpinUntil(() => { client.Update(); return error != ""; }, 3000), "no error reached the player");
            Check(error.Contains("stopped responding") && error.Contains("4001"), error);
        });
        yield return ("Protocol parity: a scripted session gives identical events and hashes over a direct connection", () =>
        {
            var (hostStream, guestStream) = PipeStream.Pair();
            var server = new TimberServer(new PipeListener(hostStream), () => Task.FromResult(Pattern(64 * 1024, 5)), null) { CompatibilityIdentity = "same" };
            var client = new TimberClient(guestStream) { CompatibilityIdentity = "same" };
            bool mapped = false; client.OnMapReceived += _ => mapped = true;
            try { server.Start(); client.Start(); ParityScript(server, client, () => mapped); }
            finally { server.Close(); client.Close(); }
        });
        yield return ("Protocol parity: the same scripted session gives identical events and hashes over Steam, under stress", () =>
        {
            // A small send buffer and a slow drip force backpressure, coalescing and splitting mid-event.
            using var rig = new Rig(sendBuffer: 192 * 1024, bytesPerDeliver: 48 * 1024);
            var listener = new SteamLinkListener(rig.Host, rig.Game.Run, _ => true);
            var server = new TimberServer(listener, () => Task.FromResult(Pattern(64 * 1024, 5)), null) { CompatibilityIdentity = "same" };
            var client = new TimberClient(rig.ConnectGuestAfterListen(server)) { CompatibilityIdentity = "same" };
            bool mapped = false; client.OnMapReceived += _ => mapped = true;
            try { rig.Game.Run(() => client.Start()); ParityScript(server, client, () => mapped); }
            finally { rig.Game.Run(() => { server.Close(); client.Close(); }); }
        });
        yield return ("The connection panel's ping and roster work over Steam, and the link is labelled Steam", () =>
        {
            int previous = TimberServer.StatusIntervalMs;
            TimberServer.StatusIntervalMs = 60;
            using var rig = new Rig();
            var listener = new SteamLinkListener(rig.Host, rig.Game.Run, _ => true);
            var server = new TimberServer(listener, () => Task.FromResult(Pattern(1000)), null) { CompatibilityIdentity = "same" };
            var client = new TimberClient(rig.ConnectGuestAfterListen(server)) { CompatibilityIdentity = "same" };
            bool mapped = false; client.OnMapReceived += _ => mapped = true;
            try
            {
                rig.Game.Run(() => client.Start());
                Check(SpinWait.SpinUntil(() => { server.Update(); client.Update(); Thread.Sleep(1); return mapped; }, 4000), "the save never arrived");
                NetworkStatus host = server.GetNetworkStatus();
                Check(SpinWait.SpinUntil(() =>
                {
                    server.Update(); client.Update(); Thread.Sleep(1);
                    host = server.GetNetworkStatus();
                    return host.Peers.Count == 1 && host.Peers[0].RttMs != null;
                }, 4000), "the host never measured a ping over Steam");
                Equal("Steam", host.Peers[0].Transport);
                Check(host.Peers[0].SilenceSeconds < 2, "a live Steam guest should not be silent");
                NetworkStatus guest = client.GetNetworkStatus();
                Check(SpinWait.SpinUntil(() =>
                {
                    server.Update(); client.Update(); Thread.Sleep(1);
                    guest = client.GetNetworkStatus();
                    return guest.YourPlayerId == 1 && guest.Peers.Count == 1 && guest.Peers[0].RttMs != null;
                }, 4000), "the guest never received the roster over Steam");
                Equal("Steam", guest.Peers[0].Transport);
                Check(!guest.IsHost && guest.HostSilenceSeconds < 2, "the host feed should be fresh");
            }
            finally
            {
                TimberServer.StatusIntervalMs = previous;
                rig.Game.Run(() => { server.Close(); client.Close(); });
            }
        });
        yield return ("Steam end reasons are described in plain language", () =>
        {
            Check(SteamEndReasons.Describe(5003, "x").Contains("timed out"));
            Check(SteamEndReasons.Describe(5009, "").Contains("firewall"));
            Check(SteamEndReasons.Describe(1000, "bye").Contains("ended the connection"));
            Check(SteamEndReasons.Describe(3999, "").Contains("this computer"));
            Check(SteamEndReasons.Describe(9999, "").Contains("9999"));
        });
    }

    // Runs the same seeded traffic in both directions, plus presentation traffic, and checks the invariants
    // that keep two peers in sync: nothing lost, duplicated, altered or reordered, and equal state hashes.
    static void ParityScript(TimberServer server, TimberClient client, Func<bool> mapReceived)
    {
        const string Type = TimberNetBase.TYPE_KEY, Ticks = TimberNetBase.TICKS_KEY;
        Check(SpinWait.SpinUntil(() => { server.Update(); client.Update(); return mapReceived(); }, 4000), "the save never arrived");
        var rng = new Random(11);
        string Payload(int bytes) { var b = new byte[bytes]; rng.NextBytes(b); return Convert.ToBase64String(b); }
        string Compact(JObject e) => e.ToString(Newtonsoft.Json.Formatting.None);
        var hostSent = new List<string>(); var guestSent = new List<string>();
        int number = 0;
        for (int tick = 0; tick < 25; tick++)
        {
            for (int k = rng.Next(1, 5); k > 0; k--)
            {
                // One event is big enough to span several Steam messages and TimberNet chunks.
                int size = tick == 7 && k == 1 ? 220_000 : rng.Next(1, 12_000);
                var e = new JObject { [Type] = "Script", [Ticks] = tick, ["n"] = number++, ["data"] = Payload(size) };
                hostSent.Add(Compact(e)); server.DoUserInitiatedEvent(e);
            }
            var g = new JObject { [Type] = "Guest", [Ticks] = 0, ["n"] = tick, ["data"] = Payload(rng.Next(1, 6000)) };
            var withoutTick = (JObject)g.DeepClone(); withoutTick.Remove(Ticks);   // the host re-stamps the tick on receipt
            guestSent.Add(Compact(withoutTick)); client.DoUserInitiatedEvent(g);
            // Presentation traffic shares the connection but must never disturb the above.
            server.SendActivity(new PlayerActivity(0, "Host", "FF8800", true, tick, 0, 0));
            client.SendActivity(new PlayerActivity(0, "Guest", "00FF88", true, 0, tick, 0));
        }

        var received = new List<string>();
        Check(SpinWait.SpinUntil(() =>
        {
            foreach (var e in client.ReadEvents(24)) if ((string)e[Type]! == "Script") received.Add(Compact(e));
            return received.Count >= hostSent.Count;
        }, 4500), $"the guest received {received.Count} of {hostSent.Count} events");
        Check(received.SequenceEqual(hostSent), "host events were altered, duplicated or reordered on the way to the guest");
        Equal(server.Hash, client.Hash);                       // the desync detector's core comparison

        var atHost = new List<string>();
        Check(SpinWait.SpinUntil(() =>
        {
            foreach (var e in server.ReadEvents(24))
                if ((string)e[Type]! == "Guest") { e.Remove(Ticks); atHost.Add(Compact(e)); }
            return atHost.Count >= guestSent.Count;
        }, 4500), $"the host received {atHost.Count} of {guestSent.Count} guest events");
        Check(atHost.SequenceEqual(guestSent), "guest events were altered, duplicated or reordered on the way to the host");
    }

    static class Assert
    {
        public static T Throws<T>(Action run) where T : Exception
        {
            try { run(); } catch (T e) { return e; }
            throw new Exception($"Expected {typeof(T).Name}");
        }
    }
    static void Throws<T>(Action run) where T : Exception => Assert.Throws<T>(run);

    static SteamLinkSocket ConnectGuestAfterListen(this Rig rig, TimberServer server)
    {
        server.Start();                               // starts the listener and the accept loop
        return rig.ConnectGuest();
    }
}
