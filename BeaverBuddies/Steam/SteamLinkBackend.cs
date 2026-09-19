using System;
using System.Runtime.InteropServices;
using Steamworks;

namespace BeaverBuddies.Steam
{
    /// <summary>
    /// The real Steam networking calls behind <see cref="ISteamLinkBackend"/>. Game thread only.
    /// Connections use Valve's <c>ISteamNetworkingSockets</c> peer-to-peer API, which is relayed over
    /// Steam's network when a direct route is not possible, so no port forwarding is needed.
    /// </summary>
    internal sealed class SteamLinkBackend : ISteamLinkBackend
    {
        // Valve: an app with a single listen socket should use virtual port zero.
        const int VirtualPort = 0;

        // Steam's defaults are conservative (a fixed 256 KB/s send rate and a 512 KB send buffer), which
        // would make the initial save transfer needlessly slow.
        const int SendBufferBytes = 4 * 1024 * 1024;
        const int RecvBufferBytes = 8 * 1024 * 1024;
        const int SendRateMaxBytes = 8 * 1024 * 1024;
        // A relayed connection can take a while to find a route, and a brief network hiccup should not end a session.
        const int TimeoutInitialMs = 30000;
        const int TimeoutConnectedMs = 20000;

        readonly IntPtr[] messages = new IntPtr[64];
        bool loggedConfigFailure;

        static HSteamNetConnection Conn(ulong connection) => new HSteamNetConnection((uint)connection);

        public ulong CreateListenSocket()
        {
            var socket = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, null);
            return socket.m_HSteamListenSocket;
        }

        public void CloseListenSocket(ulong listen)
        {
            SteamNetworkingSockets.CloseListenSocket(new HSteamListenSocket((uint)listen));
        }

        public ulong Connect(ulong remoteSteamId)
        {
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(remoteSteamId);
            return SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, null).m_HSteamNetConnection;
        }

        public bool Accept(ulong connection)
        {
            var result = SteamNetworkingSockets.AcceptConnection(Conn(connection));
            if (result != EResult.k_EResultOK) Plugin.LogWarning("Steam AcceptConnection returned " + result);
            return result == EResult.k_EResultOK;
        }

        public void Configure(ulong connection)
        {
            SetInt(connection, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, SendBufferBytes);
            SetInt(connection, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, SendRateMaxBytes);
            SetInt(connection, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_RecvBufferSize, RecvBufferBytes);
            SetInt(connection, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial, TimeoutInitialMs);
            SetInt(connection, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, TimeoutConnectedMs);
        }

        // Tuning is best-effort: if Steam declines a value the connection still works with its defaults.
        void SetInt(ulong connection, ESteamNetworkingConfigValue value, int number)
        {
            IntPtr argument = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(argument, number);
                bool ok = SteamNetworkingUtils.SetConfigValue(value,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, new IntPtr((long)connection),
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, argument);
                if (!ok && !loggedConfigFailure)
                {
                    loggedConfigFailure = true;
                    Plugin.LogWarning($"Steam did not accept connection setting {value}; using Steam's defaults.");
                }
            }
            finally { Marshal.FreeHGlobal(argument); }
        }

        public LinkState GetState(ulong connection, out int endReason, out string endDebug)
        {
            endReason = 0; endDebug = "";
            if (!SteamNetworkingSockets.GetConnectionInfo(Conn(connection), out SteamNetConnectionInfo_t info))
                return LinkState.Gone;
            endReason = info.m_eEndReason;
            endDebug = info.m_szEndDebug ?? "";
            switch (info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute:
                    return LinkState.Connecting;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    return LinkState.Connected;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                    return LinkState.ClosedByPeer;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    return LinkState.Failed;
                default:
                    return LinkState.Gone;
            }
        }

        public LinkSend Send(ulong connection, byte[] data, int offset, int count)
        {
            var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                // Reliable and ordered, without Nagle delay: the transport already batches its own writes.
                var result = SteamNetworkingSockets.SendMessageToConnection(Conn(connection),
                    IntPtr.Add(pinned.AddrOfPinnedObject(), offset), (uint)count,
                    Constants.k_nSteamNetworkingSend_ReliableNoNagle, out _);
                if (result == EResult.k_EResultOK) return LinkSend.Ok;
                if (result == EResult.k_EResultLimitExceeded) return LinkSend.BufferFull;
                Plugin.LogWarning("Steam SendMessageToConnection returned " + result);
                return LinkSend.Failed;
            }
            finally { pinned.Free(); }
        }

        public int Receive(ulong connection, Action<byte[]> deliver, int max)
        {
            int total = 0;
            while (total < max)
            {
                int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(Conn(connection), messages, Math.Min(messages.Length, max - total));
                if (count < 0) return -1;
                if (count == 0) break;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var message = SteamNetworkingMessage_t.FromIntPtr(messages[i]);
                        var bytes = new byte[message.m_cbSize];
                        if (message.m_cbSize > 0) Marshal.Copy(message.m_pData, bytes, 0, bytes.Length);
                        deliver(bytes);
                    }
                    finally
                    {
                        // Steam requires every received message to be released.
                        SteamNetworkingMessage_t.Release(messages[i]);
                        messages[i] = IntPtr.Zero;
                    }
                }
                total += count;
            }
            return total;
        }

        public void Close(ulong connection, int reason, string debug, bool linger)
        {
            SteamNetworkingSockets.CloseConnection(Conn(connection), reason, debug, linger);
        }
    }
}
