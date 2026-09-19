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
    /// <summary>
    /// Drains a connection's incoming messages in batches. Steamworks.NET refuses a receive call whose
    /// requested count differs from the length of the buffer it is given ("ppOutMessages must be the same
    /// size as nMaxMessages"), so every call asks for exactly one full buffer. 1.0.4 asked for
    /// "whatever is left of the limit" instead, which made the last call of a pump ask for fewer than the
    /// buffer holds whenever between 193 and 255 messages were waiting, as they are after a long load, and
    /// the connection was dropped. The limit is a soft one: the pump may overshoot it by less than one buffer.
    /// </summary>
    public static class ReceiveBatching
    {
        /// <param name="bufferLength">Length of the buffer every batch is received into.</param>
        /// <param name="max">Stop starting new batches once this many messages were handled.</param>
        /// <param name="receiveBatch">Receives up to the given count (always bufferLength); negative on failure.</param>
        /// <param name="handle">Handles, and must release, the message at the given buffer index.</param>
        /// <returns>The number of messages handled, or -1 if receiving failed.</returns>
        public static int Drain(int bufferLength, int max, Func<int, int> receiveBatch, Action<int> handle)
        {
            int total = 0;
            while (total < max)
            {
                int count = receiveBatch(bufferLength);
                if (count < 0) return -1;
                if (count == 0) break;
                for (int i = 0; i < count; i++) handle(i);
                total += count;
            }
            return total;
        }
    }

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
