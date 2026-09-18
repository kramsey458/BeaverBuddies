using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TimberNet
{
    public static class CompatibilityProfile
    {
        public const int MaxCharacters = 512 * 1024;
        public static string Digest(string value)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
        public static string Encode(IDictionary<string, string> entries)
        {
            var values = new JObject();
            foreach (var pair in entries.OrderBy(p => p.Key, StringComparer.Ordinal)) values.Add(pair.Key, pair.Value);
            string result = new JObject { ["schema"] = 2, ["entries"] = values }.ToString(Formatting.None);
            Parse(result);
            return result;
        }
        public static Dictionary<string, string> Parse(string identity)
        {
            if (identity.Length > MaxCharacters) throw new IOException("Mod compatibility profile is too large.");
            using var reader = new JsonTextReader(new StringReader(identity)) { MaxDepth = 4, DateParseHandling = DateParseHandling.None };
            var root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if ((int?)root["schema"] != 2 || !(root["entries"] is JObject values) || values.Count > 8192 || reader.Read())
                throw new IOException("Invalid mod compatibility profile.");
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in values.Properties())
            {
                if (pair.Name.Length > 512 || pair.Name.Any(char.IsControl) || pair.Value.Type != JTokenType.String || ((string)pair.Value!).Length > 4096)
                    throw new IOException("Invalid mod compatibility entry.");
                entries.Add(pair.Name, (string)pair.Value!);
            }
            if (!entries.ContainsKey("game") || !entries.ContainsKey("beaverbuddies")) throw new IOException("Incomplete mod compatibility profile.");
            return entries;
        }
        public static string? Difference(string host, string guest, bool completeSettings)
        {
            // Keep the small, opaque identity mode used by existing transport consumers.
            if (!host.StartsWith("{", StringComparison.Ordinal) || !guest.StartsWith("{", StringComparison.Ordinal))
                return host == guest ? null : "Multiplayer build mismatch. Install the same preview and restart both games.";
            var left = Parse(host); var right = Parse(guest);
            var differences = new List<string>();
            foreach (string key in left.Keys.Union(right.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                bool hasLeft = left.TryGetValue(key, out var a), hasRight = right.TryGetValue(key, out var b);
                // Game-only settings are checked again after both maps have loaded.
                if (!completeSettings && key.StartsWith("setting/", StringComparison.Ordinal) && (!hasLeft || !hasRight)) continue;
                if (!hasLeft || !hasRight || a != b)
                    differences.Add(key + (!hasLeft ? " (guest only)" : !hasRight ? " (host only)" : " (different)"));
            }
            if (differences.Count == 0) return null;
            return "Multiplayer mod compatibility mismatch:\n" + string.Join("\n", differences.Take(12)) +
                (differences.Count > 12 ? $"\n...and {differences.Count - 12} more." : "") +
                "\nMatch enabled mods, versions, load order, files and registered settings, then restart both games.";
        }
    }
}
