using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BeaverBuddies.Connect;
using TimberNet;

// The join-time "your mods differ" warning: the list format, the comparison, the message, the exchange during
// the handshake, and real host and guest sessions. None of it needs a game.
static class ModWarningChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected [{expected}], got [{actual}]");

    static ModEntry M(string id, string name, string version) => new ModEntry(id, name, version);

    // The two mod lists from the desync that motivated this: the friend had one extra housing mod.
    static readonly ModEntry[] Host =
    {
        M("Harmony", "Harmony", "v2.4.1"), M("eMka.ModSettings", "Mod Settings", "v1.1.1.0"),
        M("OptimizedLocalHousing", "Optimized Local Housing", "v0.1.0"), M("beaverbuddies", "BeaverBuddies - Stability Fork", "v1.0.3"),
    };
    static readonly ModEntry[] Guest =
    {
        M("Harmony", "Harmony", "v2.4.1"), M("eMka.ModSettings", "Mod Settings", "v1.1.1.0"),
        M("BobHousingOptimize", "Bobingabout's Housing Optimize", "v1.1.1.0"), M("beaverbuddies", "BeaverBuddies - Stability Fork", "v1.0.3"),
    };

    // The English strings as they are in the real file, so a misspelt key or a missing string fails here.
    static Dictionary<string, string> English()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "BeaverBuddies.sln"))) root = Path.GetDirectoryName(root)!;
        Check(root != null, "could not find the repository root");
        string csv = File.ReadAllText(Path.Combine(root!, "BeaverBuddies", "Localizations", "enUS_BeaverBuddie.csv"));
        var strings = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(csv, "^(BeaverBuddies\\.Mods\\.[A-Za-z0-9.]+),\"(.*)\",\"\"\\s*$", RegexOptions.Multiline))
            strings[m.Groups[1].Value] = m.Groups[2].Value;
        return strings;
    }

    static Func<string, object[], string> Translator(Dictionary<string, string> strings) => (key, args) =>
    {
        Check(strings.ContainsKey(key), "missing from enUS_BeaverBuddie.csv: " + key);
        return args.Length == 0 ? strings[key] : string.Format(CultureInfo.InvariantCulture, strings[key], args);
    };

    static string Build(string peer, ModDifference difference) =>
        ModWarningText.Build(new[] { new PendingModWarning(peer, difference) }, Translator(English()));

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        // ---------------------------------------------------------------- the list format
        yield return ("Mod lists survive the round trip", () =>
        {
            Check(ModListCodec.TryParse(ModListCodec.Serialize(Host), out var back));
            Equal(Host.Length, back.Count);
            for (int i = 0; i < Host.Length; i++)
            {
                Equal(Host[i].Id, back[i].Id); Equal(Host[i].Name, back[i].Name); Equal(Host[i].Version, back[i].Version);
            }
            Check(ModListCodec.TryParse(ModListCodec.Serialize(new ModEntry[0]), out var none) && none.Count == 0);
        });
        yield return ("Unreadable mod lists from the other player are ignored, never fatal", () =>
        {
            foreach (string bad in new[] { null, "", "  ", "not json", "[]", "{}", "{\"v\":2,\"mods\":[]}", "{\"v\":1}", "{\"v\":1,\"mods\":5}", "{\"v\":\"x\",\"mods\":[]}", "{\"v\":1,\"mods\":{\"id\":\"a\"}}" })
            {
                bool parsed = ModListCodec.TryParse(bad, out var mods);
                Check(!parsed && mods.Count == 0, "accepted: " + (bad ?? "null"));
            }
            // Entries that are not objects, or have no ID, are skipped.
            Check(ModListCodec.TryParse("{\"v\":1,\"mods\":[1,\"a\",null,{\"name\":\"no id\"},{\"id\":\"ok\",\"name\":\"Fine\",\"version\":\"v1\"}]}", out var some));
            Equal(1, some.Count); Equal("ok", some[0].Id);
        });
        yield return ("A list marked unavailable is unreadable, so it can never cause a warning", () =>
        {
            Check(!ModListCodec.TryParse(ModListCodec.Unavailable, out var none) && none.Count == 0);
        });
        yield return ("Text from the other player is cleaned and limited", () =>
        {
            string nasty = "Line one\r\nLine two\u2028three\t\u0007end";
            Check(ModListCodec.TryParse(ModListCodec.Serialize(new[] { M("id", nasty, new string('9', 500)) }), out var back));
            Check(back[0].Name.IndexOfAny(new[] { '\r', '\n', '\t', '\u2028', '\u0007' }) < 0, "control characters survived: " + back[0].Name);
            Equal("Line one Line two three end", back[0].Name);
            Equal(80, back[0].Version.Length);
            var many = Enumerable.Range(0, 500).Select(i => M("mod" + i, "Mod " + i, "v1")).ToArray();
            Check(ModListCodec.TryParse(ModListCodec.Serialize(many), out var capped));
            Equal(ModListCodec.MaxMods, capped.Count);
        });

        // ---------------------------------------------------------------- the comparison
        yield return ("Comparing finds mods only here, only there and at different versions", () =>
        {
            var here = Host.Append(M("Extra", "Extra Mod", "v1.0")).Append(M("Shared", "Shared Mod", "v1.0")).ToArray();
            var there = Guest.Append(M("Shared", "Shared Mod", "v1.1")).ToArray();
            var difference = ModListComparer.Compare(here, there);
            Equal("Extra Mod|Optimized Local Housing", string.Join("|", difference.OnlyHere.Select(m => m.Name)));
            Equal("Bobingabout's Housing Optimize", string.Join("|", difference.OnlyThere.Select(m => m.Name)));
            Equal(1, difference.VersionsDiffer.Count);
            Equal("v1.0", difference.VersionsDiffer[0].Key.Version); Equal("v1.1", difference.VersionsDiffer[0].Value.Version);
            Check(!difference.IsEmpty);
            // The other player sees the same difference from their side.
            var mirror = ModListComparer.Compare(there, here);
            Equal(difference.OnlyThere.Count, mirror.OnlyHere.Count); Equal(difference.OnlyHere.Count, mirror.OnlyThere.Count);
            Equal("v1.1", mirror.VersionsDiffer[0].Key.Version);
        });
        yield return ("Identical mod lists give no difference, whatever the order or letter case", () =>
        {
            var shuffled = Host.Reverse().Select(m => M(m.Id.ToUpperInvariant(), m.Name, m.Version)).ToArray();
            Check(ModListComparer.Compare(Host, shuffled).IsEmpty);
            Check(ModListComparer.Compare(new ModEntry[0], new ModEntry[0]).IsEmpty);
        });
        yield return ("A mod ID that appears twice counts once, the first entry", () =>
        {
            var twice = new[] { M("A", "A", "v1"), M("a", "A again", "v2") };
            Check(ModListComparer.Compare(twice, new[] { M("A", "A", "v1") }).IsEmpty);
            Equal(1, ModListComparer.Compare(twice, new ModEntry[0]).OnlyHere.Count);
        });

        // ---------------------------------------------------------------- the message
        yield return ("The warning names the other player and lists each kind of difference", () =>
        {
            var difference = ModListComparer.Compare(Host.Append(M("Shared", "Shared Mod", "v1.0")), Guest.Append(M("Shared", "Shared Mod", "v1.1")));
            string text = Build("sarawr", difference);
            Check(text.Contains("Your mods and sarawr's mods are not the same"), text);
            Check(text.Contains("Only on your computer:\n- Optimized Local Housing (v0.1.0)"), text);
            Check(text.Contains("Only on sarawr's computer:\n- Bobingabout's Housing Optimize (v1.1.1.0)"), text);
            Check(text.Contains("Different versions:\n- Shared Mod: you have v1.0, sarawr has v1.1"), text);
            Check(text.TrimEnd().EndsWith("usually harmless."), "the advice should close the message: " + text);
            // Without a name the message still reads properly.
            Check(Build(null, difference).Contains("Your mods and the other player's mods"), "no fallback for a missing name");
            // Only the sections that apply are shown.
            string onlyOne = Build("sarawr", ModListComparer.Compare(Host, Host.Take(2)));
            Check(onlyOne.Contains("Only on your computer:") && !onlyOne.Contains("Only on sarawr's computer:") && !onlyOne.Contains("Different versions:"), onlyOne);
        });
        yield return ("A long list is capped, and hostile names cannot break the message", () =>
        {
            var many = Enumerable.Range(0, 25).Select(i => M("m" + i, "Mod " + i.ToString("D2"), "v1")).ToArray();
            string text = Build("them", ModListComparer.Compare(many, new ModEntry[0]));
            Equal(ModWarningText.MaxPerSection, Regex.Matches(text, "\n- Mod \\d\\d").Count);
            Check(text.Contains("...and 15 more"), text);
            // A player name with line breaks and a huge length stays a short single line.
            string hostile = Build("evil\r\nname" + new string('x', 500), ModListComparer.Compare(many.Take(1), new ModEntry[0]));
            var name = Regex.Match(hostile, "^Your mods and (.*)'s mods are not the same").Groups[1].Value;
            Check(name.StartsWith("evil name") && name.Length <= 40 && name.IndexOfAny(new[] { '\r', '\n' }) < 0, "name: [" + name + "]");
        });
        yield return ("Every string the warning asks for exists in the English file", () =>
        {
            var strings = English();
            // Build asks for every key when all sections and the "more" line appear; a missing one throws.
            var many = Enumerable.Range(0, 15).Select(i => M("m" + i, "Mod " + i, "v1")).ToArray();
            var difference = ModListComparer.Compare(many.Append(M("Shared", "S", "v1")), new[] { M("Only", "O", "v1"), M("Shared", "S", "v2") });
            Check(Build("someone", difference).Length > 0);
            Check(Build(null, difference).Length > 0);
            Equal(8, strings.Count);
        });
        yield return ("Warnings are kept in order until taken, and Clear empties the queue", () =>
        {
            ModWarnings.Clear();
            Check(!ModWarnings.HasPending);
            var difference = ModListComparer.Compare(Host, Guest);
            ModWarnings.Add(new PendingModWarning("one", difference)); ModWarnings.Add(new PendingModWarning("two", difference));
            Check(ModWarnings.HasPending);
            Equal("one,two", string.Join(",", ModWarnings.TakeAll().Select(w => w.PeerName)));
            Check(!ModWarnings.HasPending && ModWarnings.TakeAll().Count == 0);
            ModWarnings.Add(new PendingModWarning("three", difference)); ModWarnings.Clear();
            Check(!ModWarnings.HasPending);
        });

        // ---------------------------------------------------------------- the exchange during the handshake
        yield return ("Both players receive each other's mod list during the handshake", () =>
        {
            var (a, b) = PipeStream.Pair();
            var host = Task.Run(() => CompatibilityHandshake.Run(a, "same", true, 3000, "host mods"));
            var guest = Task.Run(() => CompatibilityHandshake.Run(b, "same", false, 3000, "guest mods"));
            Check(Task.WaitAll(new Task[] { host, guest }, 4000));
            Equal("guest mods", host.Result); Equal("host mods", guest.Result);
            a.Close();
        });
        yield return ("Players without a mod list still handshake exactly as before", () =>
        {
            var (a, b) = PipeStream.Pair();
            var host = Task.Run(() => CompatibilityHandshake.Run(a, "same", true, 3000));
            var guest = Task.Run(() => CompatibilityHandshake.Run(b, "same", false, 3000));
            Check(Task.WaitAll(new Task[] { host, guest }, 4000));
            Check(host.Result == null && guest.Result == null);
            a.Close();
        });
        yield return ("A different build is still refused, and no mod list is exchanged", () =>
        {
            var (a, b) = PipeStream.Pair();
            var host = Task.Run(() => { try { CompatibilityHandshake.Run(a, "build A", true, 3000, "host mods"); return "joined"; } catch (IOException) { return "refused"; } });
            var guest = Task.Run(() => { try { CompatibilityHandshake.Run(b, "build B", false, 3000, "guest mods"); return "joined"; } catch (IOException) { return "refused"; } });
            Check(Task.WaitAll(new Task[] { host, guest }, 4000));
            Equal("refused", host.Result); Equal("refused", guest.Result);
            a.Close();
        });
        yield return ("A mod list too large to send is refused instead of flooding the other player", () =>
        {
            var (a, b) = PipeStream.Pair();
            string incompressible = Convert.ToBase64String(RandomNumberGenerator.GetBytes(60_000));
            var host = Task.Run(() => { try { CompatibilityHandshake.Run(a, "same", true, 3000, incompressible); return "joined"; } catch (IOException) { return "refused"; } });
            var guest = Task.Run(() => { try { CompatibilityHandshake.Run(b, "same", false, 3000, "guest mods"); return "joined"; } catch (IOException) { return "refused"; } });
            Check(Task.WaitAll(new Task[] { host, guest }, 4000));
            Equal("refused", host.Result); Equal("refused", guest.Result);
            a.Close();
        });
        yield return ("Compressed data cannot expand past the limit the reader allows", () =>
        {
            byte[] bomb = CompressionUtils.Compress(new string('a', 2_000_000));
            Check(bomb.Length < 10_000, "the test data should be tiny when compressed");
            bool refused = false;
            try { CompressionUtils.Decompress(bomb, 256 * 1024); } catch (IOException) { refused = true; }
            Check(refused);
            Equal(2_000_000, CompressionUtils.Decompress(bomb, 4_000_000).Length);
        });

        // ---------------------------------------------------------------- real sessions
        yield return ("Host and guest each receive the other's mod list on the update thread, and the map still arrives", () =>
        {
            var (hostStream, guestStream) = PipeStream.Pair();
            var host = new TimberServer(new PipeListener(hostStream), () => Task.FromResult(new byte[] { 7, 8, 9 }), null)
                { CompatibilityIdentity = "same", CompatibilityAdvisory = "host mods" };
            var guest = new TimberClient(guestStream) { CompatibilityIdentity = "same", CompatibilityAdvisory = "guest mods" };
            string hostGot = null, guestGot = null, hostPeer = null, guestPeer = null; int maps = 0, errors = 0;
            host.OnPeerAdvisory += (peer, advisory) => { hostPeer = peer; hostGot = advisory; };
            guest.OnPeerAdvisory += (peer, advisory) => { guestPeer = peer; guestGot = advisory; };
            guest.OnMapReceived += bytes => { Check(bytes.SequenceEqual(new byte[] { 7, 8, 9 })); maps++; };
            guest.OnError += _ => errors++;
            try
            {
                host.Start(); guest.Start();
                Check(SpinWait.SpinUntil(() => { host.Update(); guest.Update(); return hostGot != null && guestGot != null && (maps > 0 || errors > 0); }, 4000));
                Check(maps == 1 && errors == 0, $"maps={maps} errors={errors}");
                Equal("guest mods", hostGot); Equal("host mods", guestGot);
                // The name comes from the connection, which is what the warning shows.
                Equal("test-peer", hostPeer); Equal("test-peer", guestPeer);
            }
            finally { host.Close(); guest.Close(); }
        });
        yield return ("A mod list handler that fails never ends the session", () =>
        {
            var (hostStream, guestStream) = PipeStream.Pair();
            var host = new TimberServer(new PipeListener(hostStream), () => Task.FromResult(new byte[] { 7, 8, 9 }), null)
                { CompatibilityIdentity = "same", CompatibilityAdvisory = "host mods" };
            var guest = new TimberClient(guestStream) { CompatibilityIdentity = "same", CompatibilityAdvisory = "guest mods" };
            int maps = 0, errors = 0, calls = 0;
            guest.OnPeerAdvisory += (_, _) => { calls++; throw new InvalidOperationException("handler bug"); };
            guest.OnMapReceived += _ => maps++;
            guest.OnError += _ => errors++;
            try
            {
                host.Start(); guest.Start();
                Check(SpinWait.SpinUntil(() => { host.Update(); guest.Update(); return maps > 0 || errors > 0; }, 4000));
                Check(calls == 1 && maps == 1 && errors == 0 && !guest.IsStopped, $"calls={calls} maps={maps} errors={errors}");
            }
            finally { host.Close(); guest.Close(); }
        });
        yield return ("A handler that throws (a dialog with no UI behind it) never escapes Update, and the others still run", () =>
        {
            var (hostStream, guestStream) = PipeStream.Pair();
            var host = new TimberServer(new PipeListener(hostStream), () => Task.FromResult(new byte[] { 7, 8, 9 }), null)
                { CompatibilityIdentity = "same" };
            var guest = new TimberClient(guestStream) { CompatibilityIdentity = "same" };
            int goodMaps = 0; var errors = new List<string>(); var logs = new List<string>();
            guest.OnLog += logs.Add;
            guest.OnMapReceived += _ => throw new NullReferenceException("no panel stack");
            guest.OnMapReceived += _ => goodMaps++;
            guest.OnError += _ => throw new NullReferenceException("no panel stack");
            guest.OnError += errors.Add;
            try
            {
                host.Start(); guest.Start();
                // Update itself must never throw; the failed load is reported through OnError on a later update.
                Check(SpinWait.SpinUntil(() => { host.Update(); guest.Update(); return errors.Count > 0; }, 4000), "the failed load was never reported");
                guest.Update();
                Check(goodMaps == 1, "the second map handler did not run");
                Check(errors.Count == 1 && errors[0].Contains("could not be loaded"), string.Join(" | ", errors));
                Check(logs.Any(l => l.Contains("Ignoring an error in a handler")), "the failure was not logged");
            }
            finally { host.Close(); guest.Close(); }
        });
        yield return ("A session between different builds never shares a mod list", () =>
        {
            var (hostStream, guestStream) = PipeStream.Pair();
            var host = new TimberServer(new PipeListener(hostStream), () => Task.FromResult(new byte[] { 7, 8, 9 }), null)
                { CompatibilityIdentity = "build A", CompatibilityAdvisory = "host mods" };
            var guest = new TimberClient(guestStream) { CompatibilityIdentity = "build B", CompatibilityAdvisory = "guest mods" };
            int advisories = 0, maps = 0, errors = 0;
            host.OnPeerAdvisory += (_, _) => advisories++; guest.OnPeerAdvisory += (_, _) => advisories++;
            guest.OnMapReceived += _ => maps++; guest.OnError += _ => errors++;
            try
            {
                host.Start(); guest.Start();
                Check(SpinWait.SpinUntil(() => { host.Update(); guest.Update(); return errors > 0; }, 4000));
                for (int i = 0; i < 10; i++) { host.Update(); guest.Update(); Thread.Sleep(10); }
                Check(advisories == 0 && maps == 0);
            }
            finally { host.Close(); guest.Close(); }
        });
    }
}
