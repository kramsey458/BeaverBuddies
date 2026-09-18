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
        private const int MaxIdentityBytes = 128 * 1024;

        public static void Run(ISocketStream stream, string identity, bool server, int timeoutMilliseconds = 15000)
        {
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
                    string? difference = CompatibilityProfile.Difference(identity, remote, false);
                    if (difference != null) throw new IOException(difference);
                    Write(stream, "OK", false);
                }
                else
                {
                    string hello = Read(stream, true);
                    if (!hello.StartsWith(Prefix, StringComparison.Ordinal)) throw new IOException(hello);
                    string remote = hello.Substring(Prefix.Length);
                    string? difference = CompatibilityProfile.Difference(remote, identity, false);
                    if (difference != null) throw new IOException(difference);
                    Write(stream, identity, false);
                    if (Read(stream, false) != "OK") throw new IOException("Host did not accept multiplayer compatibility.");
                }
                // Retire the timer before handing the stream to map transfer.
                if (Interlocked.CompareExchange(ref timedOut, 2, 0) == 1)
                    throw new IOException("Compatibility check timed out.");
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

        private static string Read(ISocketStream stream, bool marker)
        {
            if (marker && ReadLength(stream) != 0)
                throw new IOException("The host is running an older BeaverBuddies build without compatibility checking. Update both players to the same preview and restart.");
            int length = ReadLength(stream);
            if (length <= 0 || length > MaxIdentityBytes) throw new IOException("Invalid multiplayer compatibility response.");
            return CompressionUtils.Decompress(stream.ReadUntilComplete(length), CompatibilityProfile.MaxCharacters + 1024);
        }

        private static void Write(ISocketStream stream, string message, bool marker)
        {
            byte[] bytes = CompressionUtils.Compress(message);
            if (bytes.Length > MaxIdentityBytes) throw new IOException("Multiplayer compatibility identity is too large.");
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
