using System;
using System.IO;
using System.Threading;

namespace TimberNet
{
    // A zero first frame is also a legacy error response: old clients display
    // the explanation instead of trying to load handshake bytes as a save.
    public static class CompatibilityHandshake
    {
        private const string Prefix = "BeaverBuddies requires matching Preview 5 or newer builds. Restart both games after updating.\nBB-HANDSHAKE-1\n";
        private const int MaxIdentityBytes = 4096;
        // The advisory is optional information both sides send once their builds are known to match
        // (for example the list of enabled mods). It never decides whether a player may join.
        private const string AdvisoryPrefix = "BB-ADVISORY-1\n";
        private const int MaxAdvisoryBytes = 32 * 1024;
        private const int MaxAdvisoryTextBytes = 256 * 1024;

        /// <summary>
        /// Checks that both players run the same build, then, if an advisory is given, swaps advisories
        /// with the other player and returns theirs. Both sides must pass one or neither must, which is
        /// always true between two copies of the same build.
        /// </summary>
        public static string? Run(ISocketStream stream, string identity, bool server, int timeoutMilliseconds = 15000, string? advisory = null)
        {
            string? remoteAdvisory = null;
            int timedOut = 0;
            using var timeout = new Timer(_ =>
            {
                if (Interlocked.CompareExchange(ref timedOut, 1, 0) == 0)
                    try { stream.Close(); } catch { }
            }, null, timeoutMilliseconds, Timeout.Infinite);
            try
            {
                if (server)
                {
                    Write(stream, Prefix + identity, true);
                    string remote = Read(stream, false);
                    if (!string.Equals(remote, identity, StringComparison.Ordinal))
                        throw new IOException("Multiplayer build mismatch. Both players must install the same archive and restart Timberborn.");
                    Write(stream, "OK", false);
                    if (advisory != null)
                    {
                        Write(stream, AdvisoryPrefix + advisory, false, MaxAdvisoryBytes);
                        remoteAdvisory = ReadAdvisory(stream);
                    }
                }
                else
                {
                    string hello = Read(stream, true);
                    if (!hello.StartsWith(Prefix, StringComparison.Ordinal)) throw new IOException(hello);
                    string remote = hello.Substring(Prefix.Length);
                    if (!string.Equals(remote, identity, StringComparison.Ordinal))
                        throw new IOException($"Multiplayer build mismatch. Install the same archive and restart both games.\nHost: {remote}\nClient: {identity}");
                    Write(stream, identity, false);
                    if (Read(stream, false) != "OK") throw new IOException("Host did not accept multiplayer compatibility.");
                    if (advisory != null)
                    {
                        remoteAdvisory = ReadAdvisory(stream);
                        Write(stream, AdvisoryPrefix + advisory, false, MaxAdvisoryBytes);
                    }
                }
                // Retire the timer before handing the stream to map transfer.
                if (Interlocked.CompareExchange(ref timedOut, 2, 0) == 1)
                    throw new IOException("Compatibility check timed out.");
                return remoteAdvisory;
            }
            catch (Exception error)
            {
                stream.Close();
                if (Volatile.Read(ref timedOut) == 1)
                    throw new IOException("Multiplayer compatibility check timed out. Make sure both players use the same preview and restart Timberborn.", error);
                throw;
            }
        }

        private static int ReadLength(ISocketStream stream)
        {
            byte[] bytes = stream.ReadUntilComplete(4);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToInt32(bytes, 0);
        }

        private static string ReadAdvisory(ISocketStream stream)
        {
            string message = Read(stream, false, MaxAdvisoryBytes, MaxAdvisoryTextBytes);
            if (!message.StartsWith(AdvisoryPrefix, StringComparison.Ordinal))
                throw new IOException("Invalid multiplayer compatibility response.");
            return message.Substring(AdvisoryPrefix.Length);
        }

        private static string Read(ISocketStream stream, bool marker, int maxBytes = MaxIdentityBytes, int maxTextBytes = 64 * 1024)
        {
            if (marker && ReadLength(stream) != 0)
                throw new IOException("The host is running an older BeaverBuddies build without compatibility checking. Update both players to the same preview and restart.");
            int length = ReadLength(stream);
            if (length <= 0 || length > maxBytes) throw new IOException("Invalid multiplayer compatibility response.");
            return CompressionUtils.Decompress(stream.ReadUntilComplete(length), maxTextBytes);
        }

        private static void Write(ISocketStream stream, string message, bool marker, int maxBytes = MaxIdentityBytes)
        {
            byte[] bytes = CompressionUtils.Compress(message);
            if (bytes.Length > maxBytes) throw new IOException("Multiplayer compatibility identity is too large.");
            lock (stream)
            {
                if (marker) stream.Write(new byte[4], 0, 4);
                byte[] length = BitConverter.GetBytes(bytes.Length);
                if (BitConverter.IsLittleEndian) Array.Reverse(length);
                stream.Write(length, 0, 4);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
