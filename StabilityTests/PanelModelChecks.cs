using System.Globalization;
using BeaverBuddies.Panel;

// What the connection panel says, tested without a game: the production model over plain data.
static class PanelModelChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected [{expected}], got [{actual}]");

    // The English strings the panel ships with.
    static readonly Dictionary<string, string> English = new()
    {
        ["BeaverBuddies.Panel.Host"] = "Host", ["BeaverBuddies.Panel.Guest"] = "Guest", ["BeaverBuddies.Panel.You"] = "You",
        ["BeaverBuddies.Panel.PlayersOne"] = "{0} player", ["BeaverBuddies.Panel.PlayersMany"] = "{0} players",
        ["BeaverBuddies.Panel.PingValue"] = "{0} ms", ["BeaverBuddies.Panel.Measuring"] = "...", ["BeaverBuddies.Panel.NoResponse"] = "No response",
        ["BeaverBuddies.Panel.StatusInSync"] = "In sync", ["BeaverBuddies.Panel.StatusCatchingUp"] = "Catching up ({0} ticks behind)",
        ["BeaverBuddies.Panel.StatusWaiting"] = "Waiting for host", ["BeaverBuddies.Panel.StatusUnstable"] = "Connection unstable",
        ["BeaverBuddies.Panel.StatusDesynced"] = "Out of sync", ["BeaverBuddies.Panel.StatusDisconnected"] = "Disconnected",
        ["BeaverBuddies.Panel.TickRateValue"] = "{0} ticks/s", ["BeaverBuddies.Panel.SpeedValue"] = "{0}x", ["BeaverBuddies.Panel.Paused"] = "Paused",
        ["BeaverBuddies.Panel.TicksOne"] = "{0} tick", ["BeaverBuddies.Panel.TicksMany"] = "{0} ticks",
        ["BeaverBuddies.Panel.LinkDirect"] = "Direct", ["BeaverBuddies.Panel.LinkSteam"] = "Steam",
    };
    static string T(string key, object[] args) => string.Format(CultureInfo.InvariantCulture, English[key], args);

    static PanelPlayer P(int id, string name, bool you = false, bool host = false, double? rtt = null, double? silence = null, string via = "") =>
        new() { Id = id, Name = name, IsYou = you, IsHost = host, RttMs = rtt, SilenceSeconds = silence, Transport = via };

    static PanelInputs HostView(params PanelPlayer[] guests)
    {
        var input = new PanelInputs { IsHost = true, Speed = 1, TickRate = 1.7 };
        input.Players.Add(P(0, "Kyler", you: true, host: true));
        input.Players.AddRange(guests);
        return input;
    }

    static PanelInputs GuestView(int ticksBehind, params PanelPlayer[] players)
    {
        var input = new PanelInputs { IsHost = false, Speed = 1, TickRate = 1.7, TicksBehind = ticksBehind, HostSilenceSeconds = .4 };
        input.Players.Add(P(0, "Kyler", host: true));
        input.Players.AddRange(players);
        return input;
    }

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Every string the panel asks for exists in the English file", () =>
        {
            // A misspelt key would show up in the game as raw text, so check them all against the CSV.
            string root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "BeaverBuddies.sln"))) root = Path.GetDirectoryName(root)!;
            Check(root != null, "could not find the repository root");
            string csv = File.ReadAllText(Path.Combine(root!, "BeaverBuddies", "Localizations", "enUS_BeaverBuddie.csv"));
            var defined = new HashSet<string>(System.Text.RegularExpressions.Regex.Matches(csv, "^([A-Za-z0-9.]+),", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value));
            var files = Directory.GetFiles(Path.Combine(root!, "BeaverBuddies", "Panel"), "*.cs").Append(Path.Combine(root!, "BeaverBuddies", "Settings.cs"));
            var missing = new List<string>(); int used = 0;
            foreach (string file in files)
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file),
                    "\"(BeaverBuddies\\.(?:Panel|Settings\\.ConnectionPanel|KeyBindings\\.TogglePanel)[A-Za-z0-9.]*)\""))
                { used++; if (!defined.Contains(m.Groups[1].Value)) missing.Add(m.Groups[1].Value); }
            Check(used > 25, "found only " + used + " string keys; the check is not looking in the right place");
            Check(missing.Count == 0, "missing from enUS_BeaverBuddie.csv: " + string.Join(", ", missing.Distinct()));
            // And the keys the tests use as the English table are the real ones.
            foreach (string key in English.Keys) Check(defined.Contains(key), "test table key not in the file: " + key);
        });
        yield return ("Ping quality boundaries and silence", () =>
        {
            Equal(Quality.Good, PingQuality.Classify(80, 0));
            Equal(Quality.Fair, PingQuality.Classify(80.1, 0));
            Equal(Quality.Fair, PingQuality.Classify(160, 0));
            Equal(Quality.Poor, PingQuality.Classify(160.1, 0));
            Equal(Quality.Unknown, PingQuality.Classify(null, 1));
            Equal(Quality.Unknown, PingQuality.Classify(null, null));
            // Someone who stopped replying is not "good" just because their last ping was.
            Equal(Quality.Silent, PingQuality.Classify(20, PingQuality.SilentSeconds));
            Equal(Quality.Good, PingQuality.Classify(20, PingQuality.SilentSeconds - .1));
        });
        yield return ("Tick rate is measured over a sliding window", () =>
        {
            var meter = new TickRateMeter();
            Check(meter.Sample(0, 0) == null, "no rate from a single sample");
            Check(meter.Sample(1, .3) == null, "too little history");
            double? steady = null;
            for (int i = 2; i <= 12; i++) steady = meter.Sample(i, i * .6);            // one tick per 0.6 s
            Check(steady != null && Math.Abs(steady.Value - 1 / .6) < .05, "steady rate " + steady);
            // Paused: time passes, no ticks.
            double? paused = null;
            for (int i = 1; i <= 8; i++) paused = meter.Sample(12, 7.2 + i);
            Check(paused != null && paused.Value < .05, "paused rate " + paused);
            // A new session (tick count goes back) starts over instead of reporting a negative rate.
            Check(meter.Sample(3, 20) == null, "should restart after the tick count dropped");
        });
        yield return ("Host view: worst guest ping on the pill, rows ordered, healthy status", () =>
        {
            var model = PanelModelBuilder.Build(HostView(
                P(2, "Bob", rtt: 190, silence: .3, via: "Direct"), P(1, "Sarah", rtt: 42, silence: .2, via: "Direct")), T);
            Equal(StatusKind.InSync, model.Status); Equal("In sync", model.StatusText);
            Equal("Host", model.Role);
            Equal("3 players  190 ms", model.Summary); Equal(Quality.Poor, model.SummaryQuality);
            Equal("Kyler,Sarah,Bob", string.Join(",", model.Rows.Select(r => r.Name)));     // you first, then by id
            Equal("You / Host", model.Rows[0].Tag); Equal("", model.Rows[0].PingText);
            Equal("42 ms", model.Rows[1].PingText); Equal(Quality.Good, model.Rows[1].Quality);
            Equal("190 ms", model.Rows[2].PingText); Equal(Quality.Poor, model.Rows[2].Quality);
            Check(model.BehindText == null, "the host has no 'behind' figure");
            Equal("Direct", model.LinkText);
        });
        yield return ("Guest view: your own ping on the pill, host first, others listed", () =>
        {
            var model = PanelModelBuilder.Build(GuestView(0,
                P(2, "Me", you: true, rtt: 55, silence: .2, via: "Steam"), P(1, "Sarah", rtt: 30, silence: .2, via: "Direct")), T);
            Equal("Guest", model.Role);
            Equal("3 players  55 ms", model.Summary); Equal(Quality.Good, model.SummaryQuality);
            Equal("Kyler,Sarah,Me", string.Join(",", model.Rows.Select(r => r.Name)));
            Equal("Host", model.Rows[0].Tag); Equal("", model.Rows[0].PingText);
            Equal("You", model.Rows[2].Tag); Equal("55 ms", model.Rows[2].PingText);
            Equal("0 ticks", model.BehindText);
            Equal("Steam", model.LinkText);                    // a guest sees how they themselves are connected
        });
        yield return ("Mixed connection types are all listed", () =>
        {
            var model = PanelModelBuilder.Build(HostView(P(1, "A", rtt: 10, via: "Direct"), P(2, "B", rtt: 10, via: "Steam")), T);
            Equal("Direct, Steam", model.LinkText);
            Check(PanelModelBuilder.Build(HostView(P(1, "A", rtt: 10)), T).LinkText == null, "unknown transport should show nothing");
        });
        yield return ("Status priority: disconnected, out of sync, unstable, waiting, catching up, in sync", () =>
        {
            var guests = new[] { P(1, "Me", you: true, rtt: 40, silence: .1) };
            StatusKind Kind(Action<PanelInputs> change) { var i = GuestView(0, guests); change(i); return PanelModelBuilder.Build(i, T).Status; }
            Equal(StatusKind.InSync, Kind(_ => { }));
            Equal(StatusKind.CatchingUp, Kind(i => i.TicksBehind = PanelModelBuilder.CatchingUpTicks));
            Equal(StatusKind.InSync, Kind(i => i.TicksBehind = PanelModelBuilder.CatchingUpTicks - 1));
            Equal(StatusKind.WaitingForHost, Kind(i => { i.WaitingForHost = true; i.TicksBehind = 10; }));
            Equal(StatusKind.Unstable, Kind(i => { i.HostSilenceSeconds = 6; i.WaitingForHost = true; }));
            Equal(StatusKind.Desynced, Kind(i => { i.Desynced = true; i.HostSilenceSeconds = 6; }));
            Equal(StatusKind.Disconnected, Kind(i => { i.Stopped = true; i.Desynced = true; }));
            Equal("Catching up (5 ticks behind)", PanelModelBuilder.Build(GuestView(5, guests), T).StatusText);
        });
        yield return ("A silent guest makes the host's session unstable and reads as no response", () =>
        {
            var model = PanelModelBuilder.Build(HostView(P(1, "Sarah", rtt: 30, silence: 9), P(2, "Bob", rtt: 40, silence: .2)), T);
            Equal(StatusKind.Unstable, model.Status);
            Equal("No response", model.Rows[1].PingText); Equal(Quality.Silent, model.Rows[1].Quality);
            Equal("40 ms", model.Rows[2].PingText);
            // Waiting-for-host only ever applies to guests.
            var host = HostView(P(1, "Sarah", rtt: 30, silence: .1)); host.WaitingForHost = true; host.TicksBehind = 99;
            Equal(StatusKind.InSync, PanelModelBuilder.Build(host, T).Status);
        });
        yield return ("A silent host is shown on the host row", () =>
        {
            var input = GuestView(0, P(1, "Me", you: true, rtt: 40, silence: .1));
            input.HostSilenceSeconds = 8;
            var model = PanelModelBuilder.Build(input, T);
            Equal(StatusKind.Unstable, model.Status); Equal(Quality.Silent, model.Rows[0].Quality);
        });
        yield return ("A disconnected session says so on the pill", () =>
        {
            var input = HostView(P(1, "Sarah", rtt: 30, silence: .1)); input.Stopped = true;
            var model = PanelModelBuilder.Build(input, T);
            Equal("Disconnected", model.Summary); Equal(Quality.Silent, model.SummaryQuality);
        });
        yield return ("Before anything is measured the panel shows placeholders, not numbers", () =>
        {
            var input = HostView(P(1, "Sarah")); input.TickRate = null;
            var model = PanelModelBuilder.Build(input, T);
            Equal("2 players", model.Summary);                 // no ping yet, so no ping on the pill
            Equal("...", model.Rows[1].PingText); Equal(Quality.Unknown, model.Rows[1].Quality);
            Equal("...", model.TickRateText);
        });
        yield return ("Tick rate, speed and counts are formatted the same in every culture", () =>
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");          // uses a decimal comma
            try
            {
                var input = HostView(P(1, "Sarah", rtt: 12.6)); input.TickRate = 1.666; input.Speed = 2.5f;
                var model = PanelModelBuilder.Build(input, T);
                Equal("1.7 ticks/s", model.TickRateText); Equal("2.5x", model.SpeedText);
                Equal("2 players  13 ms", model.Summary);
            }
            finally { CultureInfo.CurrentCulture = previous; }
            var paused = HostView(P(1, "Sarah", rtt: 5)); paused.Speed = 0;
            Equal("Paused", PanelModelBuilder.Build(paused, T).SpeedText);
            Equal("1x", PanelModelBuilder.Build(HostView(P(1, "S", rtt: 5)), T).SpeedText);
            Equal("1 player", PanelModelBuilder.Build(HostView(), T).Summary);
            Equal("1 tick", PanelModelBuilder.Build(GuestView(1, P(1, "Me", you: true, rtt: 5)), T).BehindText);
        });
    }
}
