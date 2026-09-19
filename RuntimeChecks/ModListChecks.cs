using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

// The join-time mod list warning against the real game types: the game's own Mod objects are described
// correctly, and a list from another player is compared and queued (or ignored) as intended.
internal static class ModListChecks
{
    public static void Run(Assembly mod, Action<string, Action> test)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var modding = Assembly.Load("Timberborn.Modding");
        var versionType = Assembly.Load("Timberborn.Versioning").GetType("Timberborn.Versioning.Version", true);
        var gameModType = modding.GetType("Timberborn.Modding.Mod", true);
        var manifestType = modding.GetType("Timberborn.Modding.ModManifest", true);
        var directoryType = modding.GetType("Timberborn.Modding.ModDirectory", true);
        var versionedModType = modding.GetType("Timberborn.Modding.VersionedMod", true);
        var serviceType = mod.GetType("BeaverBuddies.Connect.ModListService", true);
        var entryType = mod.GetType("BeaverBuddies.Connect.ModEntry", true);
        var compatibilityType = mod.GetType("BeaverBuddies.Connect.ModCompatibility", true);
        var warningsType = mod.GetType("BeaverBuddies.Connect.ModWarnings", true);

        // Plugin.Log* would otherwise reach Unity's native logger.
        var pluginLogger = mod.GetType("BeaverBuddies.Plugin", true).GetField("logger", all);
        var loggerType = mod.GetType("BeaverBuddies.Util.Logging.ILogger", true);
        object previousLogger = pluginLogger.GetValue(null);
        void WithQuietLogger(Action run)
        {
            pluginLogger.SetValue(null, DispatchProxy.Create(loggerType, typeof(QuietLoggerProxy)));
            try { run(); } finally { pluginLogger.SetValue(null, previousLogger); }
        }

        object Version(string text) => versionType.GetMethod("Create").Invoke(null, new object[] { text });
        object GameMod(string id, string name, string version)
        {
            var manifest = Activator.CreateInstance(manifestType, new object[]
            {
                name, "test mod", Version(version), id, Version("1.0.0.0"),
                Array.CreateInstance(versionedModType, 0), Array.CreateInstance(versionedModType, 0),
            });
            var directory = Activator.CreateInstance(directoryType, new object[]
                { new DirectoryInfo(Path.GetTempPath()), true, "test", Version("1.1.0.0"), false });
            return Activator.CreateInstance(gameModType, new object[] { directory, manifest, true });
        }
        object Entry(string id, string name, string version) => Activator.CreateInstance(entryType, new object[] { id, name, version });
        string Prop(object entry, string name) => (string)entryType.GetProperty(name).GetValue(entry);

        test("The game's mods are described by ID, name and version", () =>
        {
            var mods = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(gameModType));
            mods.Add(GameMod("Harmony", "Harmony", "2.4.1"));
            mods.Add(GameMod("eMka.ModSettings", "Mod Settings", "1.1.1.0"));
            mods.Add(GameMod("OptimizedLocalHousing", "Optimized Local Housing", "0.1.0"));
            var entries = ((IEnumerable)serviceType.GetMethod("Describe").Invoke(null, new object[] { mods })).Cast<object>().ToList();
            if (entries.Count != 3) throw new Exception("expected 3 entries, got " + entries.Count);
            // The version is shown as the game writes it in the log ("v2.4.1").
            if (Prop(entries[0], "Id") != "Harmony" || Prop(entries[0], "Name") != "Harmony" || Prop(entries[0], "Version") != "v2.4.1")
                throw new Exception($"first entry was {Prop(entries[0], "Id")} / {Prop(entries[0], "Name")} / {Prop(entries[0], "Version")}");
            if (Prop(entries[2], "Display") != "Optimized Local Housing (v0.1.0)") throw new Exception("display was " + Prop(entries[2], "Display"));
        });

        // Every check below runs as this computer having Harmony and Optimized Local Housing.
        void UseLocalMods(params object[] entries)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType));
            foreach (object entry in entries) list.Add(entry);
            var enumerable = typeof(IEnumerable<>).MakeGenericType(entryType);
            var source = Expression.Lambda(typeof(Func<>).MakeGenericType(enumerable), Expression.Constant(list, enumerable)).Compile();
            compatibilityType.GetMethod("SetSource", all).Invoke(null, new object[] { source });
        }
        string Advisory(params object[] entries)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType));
            foreach (object entry in entries) list.Add(entry);
            return (string)mod.GetType("BeaverBuddies.Connect.ModListCodec", true).GetMethod("Serialize").Invoke(null, new object[] { list });
        }
        void FromPeer(string peer, string advisory) => compatibilityType.GetMethod("OnPeerAdvisory", all).Invoke(null, new object[] { peer, advisory });
        IList Pending() => (IList)warningsType.GetMethod("TakeAll").Invoke(null, null);
        void ClearWarnings() => warningsType.GetMethod("Clear").Invoke(null, null);

        test("A different mod list from the other player queues a warning that names them", () => WithQuietLogger(() =>
        {
            UseLocalMods(Entry("Harmony", "Harmony", "v2.4.1"), Entry("OptimizedLocalHousing", "Optimized Local Housing", "v0.1.0"));
            ClearWarnings();
            FromPeer("sarawr", Advisory(Entry("Harmony", "Harmony", "v2.4.1"), Entry("BobHousingOptimize", "Bobingabout's Housing Optimize", "v1.1.1.0")));
            var pending = Pending();
            if (pending.Count != 1) throw new Exception("expected one warning, got " + pending.Count);
            var warning = pending[0];
            if ((string)warning.GetType().GetProperty("PeerName").GetValue(warning) != "sarawr") throw new Exception("the warning does not name the other player");
            var difference = warning.GetType().GetProperty("Difference").GetValue(warning);
            var onlyHere = (IList)difference.GetType().GetProperty("OnlyHere").GetValue(difference);
            var onlyThere = (IList)difference.GetType().GetProperty("OnlyThere").GetValue(difference);
            if (onlyHere.Count != 1 || Prop(onlyHere[0], "Id") != "OptimizedLocalHousing") throw new Exception("only-here was wrong");
            if (onlyThere.Count != 1 || Prop(onlyThere[0], "Id") != "BobHousingOptimize") throw new Exception("only-there was wrong");
        }));
        test("The same mods, or an unreadable list, queue no warning", () => WithQuietLogger(() =>
        {
            UseLocalMods(Entry("Harmony", "Harmony", "v2.4.1"), Entry("OptimizedLocalHousing", "Optimized Local Housing", "v0.1.0"));
            ClearWarnings();
            FromPeer("sarawr", Advisory(Entry("OptimizedLocalHousing", "Optimized Local Housing", "v0.1.0"), Entry("Harmony", "Harmony", "v2.4.1")));
            FromPeer("sarawr", "this is not a mod list");
            FromPeer(null, "{\"v\":99,\"mods\":[]}");
            if (Pending().Count != 0) throw new Exception("a warning was queued when there was nothing to warn about");
        }));
        test("The advisory sent to the other player is this computer's list and is never empty text", () => WithQuietLogger(() =>
        {
            UseLocalMods(Entry("Harmony", "Harmony", "v2.4.1"));
            string advisory = (string)compatibilityType.GetMethod("CreateAdvisory", all).Invoke(null, null);
            if (!advisory.Contains("\"Harmony\"") || !advisory.Contains("v2.4.1")) throw new Exception("advisory was " + advisory);
            // If this computer's own list cannot be read, the other player must not be told everything differs.
            UseLocalMods();
            string none = (string)compatibilityType.GetMethod("CreateAdvisory", all).Invoke(null, null);
            if (string.IsNullOrEmpty(none)) throw new Exception("a computer with no readable mods must still send something");
            var args = new object[] { none, null };
            bool readable = (bool)mod.GetType("BeaverBuddies.Connect.ModListCodec", true).GetMethod("TryParse").Invoke(null, args);
            if (readable) throw new Exception("the other player would read an empty list and report every mod as different");
        }));
        test("With no readable mods of its own, this computer compares nothing and warns no one", () => WithQuietLogger(() =>
        {
            UseLocalMods();
            ClearWarnings();
            FromPeer("sarawr", Advisory(Entry("Harmony", "Harmony", "v2.4.1"), Entry("Other", "Other", "v1")));
            if (Pending().Count != 0) throw new Exception("a false warning was queued");
        }));
    }
}
