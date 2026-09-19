using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Steamworks;
using TimberNet;

namespace BeaverBuddies.Steam
{
    // Each reload gets a new native connection handle. No packets or stream tails from
    // the old simulation can be delivered to its replacement, even for the same SteamID.
    public sealed class SteamRelaySocket : ISocketStream, IConnectionOptions, IFlushableSocket
    {
        public const int VirtualPort = 47;
        readonly object gate = new object();
        readonly ManualResetEventSlim closed = new ManualResetEventSlim(false);
        readonly CSteamID remote;
        HSteamNetConnection handle;
        bool connected;
        byte[] packet;
        int position;
        readonly IntPtr[] incoming = new IntPtr[1];
        public string Name { get; }
        public int MaxChunkSize => 32 * 1024;
        public int MaxBytesPerSecond => int.MaxValue;
        public int ConnectTimeoutMilliseconds => 35000;
        public bool Connected { get { lock (gate) return connected && !closed.IsSet; } }
        static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

        public SteamRelaySocket(CSteamID remote)
        {
            this.remote = remote;
            Name = SteamFriends.GetFriendPersonaName(remote);
        }

        internal SteamRelaySocket(CSteamID remote, HSteamNetConnection accepted) : this(remote)
        { handle = accepted; connected = true; }

        public async Task ConnectAsync()
        {
            lock (gate)
            {
                if (closed.IsSet) throw new IOException("Steam connection was canceled.");
                var identity = new SteamNetworkingIdentity(); identity.SetSteamID(remote);
                handle = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, null);
                if (handle == HSteamNetConnection.Invalid) throw new IOException("Steam could not start the connection. Check that Steam is online and the host is still hosting.");
            }
            double deadline = Now + 30;
            while (true)
            {
                lock (gate)
                {
                    var info = Info();
                    if (info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
                    { connected = true; return; }
                }
                if (Now >= deadline) throw new IOException("Steam could not find a route to the host. Check that both players are online in Steam and try the invite again.");
                await Task.Delay(20).ConfigureAwait(false);
            }
        }

        SteamNetConnectionInfo_t Info()
        {
            if (closed.IsSet || handle == HSteamNetConnection.Invalid ||
                !SteamNetworkingSockets.GetConnectionInfo(handle, out var info))
                throw new IOException("Steam connection is closed.");
            if (info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ||
                info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally ||
                info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None)
                throw new IOException("Steam connection ended: " + info.m_szEndDebug);
            return info;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            Validate(buffer, offset, count);
            if (count == 0) return 0;
            // The TimberNet reader is the sole consumer. Native receive polling continues
            // while Unity is saving/loading, without a per-frame managed packet queue.
            while (!closed.IsSet)
            {
                if (packet != null)
                {
                    int size = Math.Min(count, packet.Length - position);
                    Buffer.BlockCopy(packet, position, buffer, offset, size);
                    position += size;
                    if (position == packet.Length) { packet = null; position = 0; }
                    return size;
                }
                lock (gate)
                {
                    if (closed.IsSet) return 0;
                    int received = SteamNetworkingSockets.ReceiveMessagesOnConnection(handle, incoming, 1);
                    if (received < 0) throw new IOException("Steam could not receive data from the host.");
                    if (received == 1)
                    {
                        try
                        {
                            var message = SteamNetworkingMessage_t.FromIntPtr(incoming[0]);
                            if (message.m_cbSize <= 0 || message.m_cbSize > MaxChunkSize)
                                throw new IOException("Steam received an invalid multiplayer packet size.");
                            packet = new byte[message.m_cbSize];
                            Marshal.Copy(message.m_pData, packet, 0, packet.Length);
                        }
                        finally { SteamNetworkingMessage_t.Release(incoming[0]); incoming[0] = IntPtr.Zero; }
                    }
                    else Info();
                }
                if (packet == null) closed.Wait(10);
            }
            return 0;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            Validate(buffer, offset, count);
            if (count > MaxChunkSize) throw new IOException("Steam packet exceeds the transport limit.");
            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                double deadline = Now + 30;
                while (true)
                {
                    EResult result;
                    lock (gate)
                    {
                        Info();
                        // Reliable messages on the default lane preserve stream order.
                        result = SteamNetworkingSockets.SendMessageToConnection(handle,
                            IntPtr.Add(pinned.AddrOfPinnedObject(), offset), (uint)count,
                            Constants.k_nSteamNetworkingSend_Reliable, out _);
                    }
                    if (result == EResult.k_EResultOK) return;
                    if (result != EResult.k_EResultLimitExceeded || Now >= deadline)
                        throw new IOException("Steam could not send multiplayer data: " + result);
                    // Backpressure stays on the sender worker. Never drop reliable commands
                    // or grow another queue when Steam's bounded send buffer fills up.
                    if (closed.Wait(10)) throw new IOException("Steam connection was closed while sending.");
                }
            }
            finally { pinned.Free(); }
        }

        static void Validate(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        }

        public void Close()
        {
            lock (gate)
            {
                if (closed.IsSet) return;
                closed.Set(); connected = false;
                if (handle != HSteamNetConnection.Invalid)
                    SteamNetworkingSockets.CloseConnection(handle, 0, "BeaverBuddies session ended", true);
                handle = HSteamNetConnection.Invalid;
            }
        }

        public async Task FlushAsync()
        {
            double deadline = Now + 10;
            while (true)
            {
                lock (gate)
                {
                    // A guest may already have consumed ResyncReload and closed its side.
                    if (closed.IsSet) return;
                    SteamNetworkingSockets.FlushMessagesOnConnection(handle);
                    var status = new SteamNetConnectionRealTimeStatus_t();
                    var lane = new SteamNetConnectionRealTimeLaneStatus_t();
                    var result = SteamNetworkingSockets.GetConnectionRealTimeStatus(handle, ref status, 0, ref lane);
                    if (result != EResult.k_EResultOK || status.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer) return;
                    if (status.m_cbPendingReliable == 0 && status.m_cbSentUnackedReliable == 0) return;
                }
                if (Now >= deadline) throw new IOException("Steam timed out delivering the recovery notice.");
                await Task.Delay(20).ConfigureAwait(false);
            }
        }
    }
}
