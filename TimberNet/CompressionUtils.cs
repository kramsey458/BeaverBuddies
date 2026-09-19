using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Text;

namespace TimberNet
{
    public static class CompressionUtils
    {
        public static byte[] Compress(string text)
        {
            byte[] inputBytes = Encoding.UTF8.GetBytes(text);

            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                {
                    gzip.Write(inputBytes, 0, inputBytes.Length);
                }
                return output.ToArray();
            }
        }

        /// <summary>
        /// Decompresses data from another player, refusing to produce more than maxBytes, so a tiny
        /// crafted payload cannot expand into something huge.
        /// </summary>
        public static string Decompress(byte[] compressedData, int maxBytes)
        {
            using (var input = new MemoryStream(compressedData))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > maxBytes) throw new IOException("Compressed message is too large.");
                    output.Write(buffer, 0, read);
                }
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }

        public static string Decompress(byte[] compressedData)
        {
            using (var input = new MemoryStream(compressedData))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gzip.CopyTo(output);
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }
    }
}
