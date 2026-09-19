using System;

namespace BeaverBuddies.Steam
{
    public enum LinkState
    {
        Connecting,
        Connected,
        /// <summary>The other side closed. Messages it sent before closing can still be read.</summary>
        ClosedByPeer,
        /// <summary>Steam gave up on the connection (usually a timeout or no route).</summary>
        Failed,
        /// <summary>The handle no longer exists.</summary>
        Gone,
    }

    public enum LinkSend { Ok, BufferFull, Failed }

    /// <summary>
    /// The few Steam networking calls the transport needs. Connections are plain numbers so the
    /// transport itself has no dependency on Steamworks or Unity and can be tested with a fake.
    /// <para>
    /// Every member is called from the game thread only. Steam documents callbacks as delivered when
    /// callbacks are pumped there, and does not document these calls as safe from other threads, so
    /// nothing here relies on that.
    /// </para>
    /// </summary>
    public interface ISteamLinkBackend
    {
        /// <returns>A listen-socket handle, or 0 on failure.</returns>
        ulong CreateListenSocket();
        void CloseListenSocket(ulong listen);
        /// <returns>A connection handle, or 0 if Steam could not start connecting.</returns>
        ulong Connect(ulong remoteSteamId);
        bool Accept(ulong connection);
        /// <summary>Best-effort tuning of buffers, rates and timeouts for one connection.</summary>
        void Configure(ulong connection);
        LinkState GetState(ulong connection, out int endReason, out string endDebug);
        LinkSend Send(ulong connection, byte[] data, int offset, int count);
        /// <summary>Delivers up to <paramref name="max"/> messages. Returns how many, or -1 if the connection cannot be read.</summary>
        int Receive(ulong connection, Action<byte[]> deliver, int max);
        void Close(ulong connection, int reason, string debug, bool linger);
    }

    /// <summary>Steam's connection end reasons, in words a player can act on.</summary>
    public static class SteamEndReasons
    {
        // Application-defined reasons live in Steam's 1000-1999 range.
        public const int SessionEnded = 1000;
        public const int Rejected = 1001;
        public const int NotAccepting = 1002;

        public static string Describe(int reason, string debug)
        {
            string text;
            switch (reason)
            {
                case 3001: text = "Steam is in offline mode."; break;
                case 4001: text = "The other player stopped responding."; break;
                case 5003: text = "The connection timed out. Steam could not find a working route between you."; break;
                case 5009: text = "A firewall or router blocked the connection."; break;
                default:
                    if (reason >= 1000 && reason < 2000) text = "The other player ended the connection.";
                    else if (reason >= 2000 && reason < 3000) text = "The other player's game ended the connection unexpectedly.";
                    else if (reason >= 3000 && reason < 4000) text = "Steam reported a problem with this computer's connection.";
                    else if (reason >= 4000 && reason < 5000) text = "Steam reported a problem reaching the other player.";
                    else if (reason >= 5000 && reason < 6000) text = "Steam could not keep the connection open.";
                    else text = "The Steam connection ended.";
                    break;
            }
            string detail = string.IsNullOrWhiteSpace(debug) ? "" : ": " + debug.Trim();
            return $"{text} (Steam code {reason}{detail})";
        }
    }
}
