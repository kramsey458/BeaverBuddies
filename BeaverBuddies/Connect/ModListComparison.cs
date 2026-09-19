using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

// Pure logic for the join-time "your mods differ" warning: no Unity or Timberborn types, so it can be tested
// on its own. Everything read from the other player is treated as untrusted.
namespace BeaverBuddies.Connect
{
    /// <summary>One enabled mod, as compared between two players.</summary>
    public sealed class ModEntry
    {
        public string Id { get; }
        public string Name { get; }
        public string Version { get; }

        public ModEntry(string id, string name, string version)
        {
            Id = id ?? "";
            Name = string.IsNullOrEmpty(name) ? Id : name;
            Version = version ?? "";
        }

        /// <summary>For example "Harmony (v2.4.1)".</summary>
        public string Display => Version.Length == 0 ? Name : Name + " (" + Version + ")";
    }

    /// <summary>Turns a mod list into the text sent to the other player, and back.</summary>
    public static class ModListCodec
    {
        public const int MaxMods = 300;
        const int MaxIdLength = 100;
        const int MaxTextLength = 80;
        const int FormatVersion = 1;

        /// <summary>
        /// Sent when this computer's own mod list could not be read. It has no mods entry, so the other
        /// player cannot read it and compares nothing, instead of reporting that every mod differs.
        /// </summary>
        public const string Unavailable = "{\"v\":1}";

        public static string Serialize(IEnumerable<ModEntry> mods)
        {
            var array = new JArray();
            foreach (ModEntry mod in mods.Take(MaxMods))
            {
                array.Add(new JObject
                {
                    ["id"] = Clean(mod.Id, MaxIdLength),
                    ["name"] = Clean(mod.Name, MaxTextLength),
                    ["version"] = Clean(mod.Version, MaxTextLength),
                });
            }
            return new JObject { ["v"] = FormatVersion, ["mods"] = array }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Reads the other player's list. Returns false, and an empty list, for anything that is not a list
        /// in the expected format: a bad list is ignored, never an error.
        /// </summary>
        public static bool TryParse(string payload, out List<ModEntry> mods)
        {
            mods = new List<ModEntry>();
            try
            {
                if (string.IsNullOrEmpty(payload)) return false;
                if (!(JToken.Parse(payload) is JObject root)) return false;
                if ((int?)root["v"] != FormatVersion) return false;
                if (!(root["mods"] is JArray array)) return false;
                foreach (JToken token in array.Take(MaxMods))
                {
                    if (!(token is JObject item)) continue;
                    string id = Clean((string)item["id"], MaxIdLength);
                    if (id.Length == 0) continue;
                    mods.Add(new ModEntry(id, Clean((string)item["name"], MaxTextLength), Clean((string)item["version"], MaxTextLength)));
                }
                return true;
            }
            catch (Exception)
            {
                mods.Clear();
                return false;
            }
        }

        /// <summary>Removes control characters and line breaks and limits the length, so shown text stays one tidy line.</summary>
        public static string Clean(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var clean = new StringBuilder(Math.Min(text.Length, maxLength));
            foreach (char c in text)
            {
                if (clean.Length >= maxLength) break;
                if (char.IsControl(c) || c == '\u2028' || c == '\u2029')
                {
                    if (clean.Length > 0 && clean[clean.Length - 1] != ' ') clean.Append(' ');
                    continue;
                }
                clean.Append(c);
            }
            return clean.ToString().Trim();
        }
    }

    /// <summary>What differs between this computer's mods ("here") and the other player's ("there").</summary>
    public sealed class ModDifference
    {
        public List<ModEntry> OnlyHere { get; } = new List<ModEntry>();
        public List<ModEntry> OnlyThere { get; } = new List<ModEntry>();
        /// <summary>Mods both have, at different versions: Key is the entry here, Value the entry there.</summary>
        public List<KeyValuePair<ModEntry, ModEntry>> VersionsDiffer { get; } = new List<KeyValuePair<ModEntry, ModEntry>>();

        public bool IsEmpty => OnlyHere.Count == 0 && OnlyThere.Count == 0 && VersionsDiffer.Count == 0;
    }

    public static class ModListComparer
    {
        public static ModDifference Compare(IEnumerable<ModEntry> here, IEnumerable<ModEntry> there)
        {
            Dictionary<string, ModEntry> mine = Index(here), theirs = Index(there);
            var difference = new ModDifference();
            foreach (var pair in mine)
            {
                if (!theirs.TryGetValue(pair.Key, out ModEntry other)) difference.OnlyHere.Add(pair.Value);
                else if (!string.Equals(pair.Value.Version, other.Version, StringComparison.Ordinal))
                    difference.VersionsDiffer.Add(new KeyValuePair<ModEntry, ModEntry>(pair.Value, other));
            }
            foreach (var pair in theirs)
                if (!mine.ContainsKey(pair.Key)) difference.OnlyThere.Add(pair.Value);

            difference.OnlyHere.Sort(ByName);
            difference.OnlyThere.Sort(ByName);
            difference.VersionsDiffer.Sort((a, b) => ByName(a.Key, b.Key));
            return difference;
        }

        // A mod is identified by its ID, ignoring case; if an ID appears twice the first entry counts.
        static Dictionary<string, ModEntry> Index(IEnumerable<ModEntry> mods)
        {
            var index = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ModEntry mod in mods)
                if (mod != null && mod.Id.Length > 0 && !index.ContainsKey(mod.Id)) index.Add(mod.Id, mod);
            return index;
        }

        static int ByName(ModEntry a, ModEntry b)
        {
            int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A difference found with one player, waiting to be shown.</summary>
    public sealed class PendingModWarning
    {
        public string PeerName { get; }
        public ModDifference Difference { get; }

        public PendingModWarning(string peerName, ModDifference difference)
        {
            PeerName = peerName;
            Difference = difference;
        }
    }

    /// <summary>
    /// Warnings found during the handshake, on the update thread, and shown by the game scene's warning service.
    /// A guest finds its warning in the main menu but only sees the game after loading, so the queue outlives scenes.
    /// </summary>
    public static class ModWarnings
    {
        static readonly ConcurrentQueue<PendingModWarning> queue = new ConcurrentQueue<PendingModWarning>();

        public static bool HasPending => !queue.IsEmpty;

        public static void Add(PendingModWarning warning) => queue.Enqueue(warning);

        public static List<PendingModWarning> TakeAll()
        {
            var taken = new List<PendingModWarning>();
            while (queue.TryDequeue(out PendingModWarning warning)) taken.Add(warning);
            return taken;
        }

        public static void Clear()
        {
            while (queue.TryDequeue(out _)) { }
        }
    }

    public static class ModWarningText
    {
        public const int MaxPerSection = 10;
        const int MaxPeerNameLength = 40;
        static readonly object[] NoArguments = new object[0];

        /// <summary>
        /// Builds the message. <paramref name="translate"/> turns a localization key and its arguments into text.
        /// </summary>
        public static string Build(IReadOnlyList<PendingModWarning> warnings, Func<string, object[], string> translate)
        {
            var text = new StringBuilder();
            for (int i = 0; i < warnings.Count; i++)
            {
                if (i > 0) text.Append("\n\n");
                AppendOne(text, warnings[i], translate);
            }
            text.Append("\n\n").Append(translate("BeaverBuddies.Mods.Advice", NoArguments));
            return text.ToString();
        }

        static void AppendOne(StringBuilder text, PendingModWarning warning, Func<string, object[], string> t)
        {
            string peer = ModListCodec.Clean(warning.PeerName, MaxPeerNameLength);
            if (peer.Length == 0) peer = t("BeaverBuddies.Mods.OtherPlayer", NoArguments);
            ModDifference difference = warning.Difference;

            text.Append(t("BeaverBuddies.Mods.Intro", new object[] { peer }));
            if (difference.OnlyHere.Count > 0)
            {
                text.Append("\n\n").Append(t("BeaverBuddies.Mods.OnlyHere", NoArguments));
                AppendList(text, difference.OnlyHere.Select(mod => mod.Display).ToList(), t);
            }
            if (difference.OnlyThere.Count > 0)
            {
                text.Append("\n\n").Append(t("BeaverBuddies.Mods.OnlyThere", new object[] { peer }));
                AppendList(text, difference.OnlyThere.Select(mod => mod.Display).ToList(), t);
            }
            if (difference.VersionsDiffer.Count > 0)
            {
                text.Append("\n\n").Append(t("BeaverBuddies.Mods.VersionsDiffer", NoArguments));
                AppendList(text, difference.VersionsDiffer.Select(pair => t("BeaverBuddies.Mods.VersionLine",
                    new object[] { pair.Key.Name, pair.Key.Version, peer, pair.Value.Version })).ToList(), t);
            }
        }

        static void AppendList(StringBuilder text, IReadOnlyList<string> lines, Func<string, object[], string> t)
        {
            foreach (string line in lines.Take(MaxPerSection)) text.Append("\n- ").Append(line);
            if (lines.Count > MaxPerSection)
                text.Append("\n").Append(t("BeaverBuddies.Mods.More", new object[] { lines.Count - MaxPerSection }));
        }
    }
}
