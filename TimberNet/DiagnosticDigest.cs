using System;
using System.Security.Cryptography;
using System.Text;

namespace TimberNet
{
    // Local diagnostic fingerprints, never used to admit or reject a player.
    public static class DiagnosticDigest
    {
        public static string Compute(string value)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
    }
}
