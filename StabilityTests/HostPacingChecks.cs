#nullable enable
using BeaverBuddies;
using BeaverBuddies.Panel;
using Newtonsoft.Json.Linq;
using TimberNet;

// The host's speed limit choice, guests reporting their tick, and the host easing off for a guest that cannot keep up.
static class HostPacingChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Speed limit: outside a session a player's own setting applies", () =>
        {
            Check(SpeedLimitChoice.IsRemoved(false, false, null, true));
            Check(!SpeedLimitChoice.IsRemoved(false, false, null, false));
            // A value left over from a session means nothing once it has ended.
            Check(!SpeedLimitChoice.IsRemoved(false, false, true, false));
        });
        yield return ("Speed limit: in a session only the host's choice counts", () =>
        {
            foreach (bool own in new[] { false, true })
            {
                Check(SpeedLimitChoice.IsRemoved(true, true, true, own), "guest must adopt a host that removed it");
                Check(!SpeedLimitChoice.IsRemoved(true, true, false, own), "guest must adopt a host that kept it");
            }
            // Until the host's message arrives (or from an older host that never sends one) a guest keeps the default.
            Check(!SpeedLimitChoice.IsRemoved(true, true, null, true));
            // A host uses what it sends, before and after it has sent it.
            Check(SpeedLimitChoice.IsRemoved(true, false, null, true));
            Check(!SpeedLimitChoice.IsRemoved(true, false, false, true), "a host changing the setting mid-session must not split the players");
        });
        yield return ("Status reply carries the guest's tick and still accepts a reply without one", () =>
        {
            Check(StatusFrames.TryParseReply(StatusFrames.Reply(7, 1234), out int seq, out int? tick)); Equal(7, seq); Equal((int?)1234, tick);
            Check(StatusFrames.TryParseReply(StatusFrames.Reply(7), out seq, out tick)); Equal(7, seq); Check(tick == null);
            Check(StatusFrames.TryParseReply(StatusFrames.Reply(3, -50), out _, out tick) && tick == 0, "a negative tick is sent as zero");
            JObject With(Action<JObject> mutate) { var j = StatusFrames.Reply(1, 10); mutate(j); return j; }
            Check(!StatusFrames.TryParseReply(With(j => j["tick"] = -1), out _, out _), "negative tick");
            Check(!StatusFrames.TryParseReply(With(j => j["tick"] = "10"), out _, out _), "string tick");
            Check(!StatusFrames.TryParseReply(With(j => j["tick"] = 1.5), out _, out _), "fractional tick");
            Check(!StatusFrames.TryParseReply(With(j => j["tick"] = long.MaxValue), out _, out _), "overflowing tick");
            Check(!StatusFrames.TryParseReply(With(j => j["extra"] = 1), out _, out _), "unexpected field");
            Check(!StatusFrames.TryParseReply(new JObject { ["type"] = StatusFrames.ReplyType, ["seq"] = 1, ["other"] = 2 }, out _, out _), "third field that is not a tick");
            Check(!StatusFrames.TryParseReply(With(j => j["seq"] = -1), out _, out _), "negative sequence");
            // A probe is still exactly two fields, so a guest ignores anything else.
            Check(!StatusFrames.TryParseSequence(StatusFrames.Reply(1, 10), out _));
        });
        yield return ("Pacing: a guest that keeps up never slows the host", () =>
        {
            var pacing = new HostPacing();
            foreach (int behind in new[] { 0, 2, 5, 14, 15, 9, 3, 0 }) { pacing.Sample(behind, true); Equal(100, pacing.Percent); }
            Equal(7f, pacing.Apply(7));
        });
        yield return ("Pacing: a guest recovering from a hitch is left to it", () =>
        {
            var pacing = new HostPacing();
            foreach (int behind in new[] { 40, 34, 29, 23, 18, 12, 6, 2 }) { pacing.Sample(behind, true); Equal(100, pacing.Percent); }
        });
        yield return ("Pacing: a guest that stays far behind without gaining eases the host: four samples for the first step, two after", () =>
        {
            var pacing = new HostPacing();
            pacing.Sample(20, true); Equal(100, pacing.Percent);   // first sight: nothing to compare with
            pacing.Sample(22, true); pacing.Sample(25, true); pacing.Sample(27, true);
            Equal(100, pacing.Percent);                            // three samples without gaining are not enough
            pacing.Sample(27, true); Equal(85, pacing.Percent);    // the fourth is (level counts as not gaining)
            pacing.Sample(28, true); Equal(85, pacing.Percent);
            pacing.Sample(28, true); Equal(70, pacing.Percent);    // already easing: two are enough
            pacing.Sample(26, true); Equal(70, pacing.Percent);    // gaining resets the count
            pacing.Sample(26, true); pacing.Sample(26, true); Equal(55, pacing.Percent);
            Check(pacing.IsEasing);
        });
        yield return ("Pacing: never below the floor, never eases speed 1 or below", () =>
        {
            var pacing = new HostPacing();
            for (int i = 0; i < 100; i++) pacing.Sample(50, true);   // far behind, never gaining, but short of the hold
            Equal(HostPacing.MinPercent, pacing.Percent);
            Equal(1f, pacing.Apply(1)); Equal(0f, pacing.Apply(0)); Equal(0.5f, pacing.Apply(0.5f));
            Check(Math.Abs(pacing.Apply(7) - 2.1f) < .001f, "speed 7 at the floor");
            Equal(1f, pacing.Apply(3));   // 3 x 30% would be below 1, which is slow motion: floor at 1
        });
        yield return ("Pacing: climbs back in small steps once guests are close, and holds in between", () =>
        {
            var pacing = new HostPacing();
            foreach (int behind in new[] { 30, 31, 32, 33, 34, 35, 36 }) pacing.Sample(behind, true);
            Equal(70, pacing.Percent);
            pacing.Sample(10, true); Equal(70, pacing.Percent);    // between the marks: hold
            pacing.Sample(4, true); Equal(75, pacing.Percent);
            for (int i = 0; i < 10; i++) pacing.Sample(1, true);
            Equal(100, pacing.Percent);
        });
        yield return ("Pacing: a paused game and an empty session are not held against anyone", () =>
        {
            var pacing = new HostPacing();
            foreach (int behind in new[] { 30, 31, 32, 33, 34 }) pacing.Sample(behind, true);
            Equal(85, pacing.Percent);
            for (int i = 0; i < 10; i++) pacing.Sample(60, false);   // paused: lag says nothing about speed
            Equal(85, pacing.Percent);
            pacing.Sample(61, true); pacing.Sample(62, true); Equal(85, pacing.Percent);   // comparison restarts after a pause
            pacing.Sample(null, true); Equal(100, pacing.Percent);   // every guest left
        });
        yield return ("Hold: the host stands still for a guest more than 60 ticks behind, until it is within 10", () =>
        {
            var pacing = new HostPacing();
            pacing.Sample(60, true); Check(!pacing.IsHolding); Equal(7f, pacing.Apply(7));
            pacing.Sample(61, true); Check(pacing.IsHolding); Equal(0f, pacing.Apply(7));
            Equal(0f, pacing.Apply(1));                       // at every speed, unlike easing
            foreach (int behind in new[] { 61, 61, 61, 61, 61, 40, 11 }) { pacing.Sample(behind, true); Check(pacing.IsHolding); }
            Equal(100, pacing.Percent);                       // a stall says nothing about what the guest can sustain
            pacing.Sample(10, true); Check(!pacing.IsHolding); Equal(7f, pacing.Apply(7));
            // Released while paused too, and at once when the guest leaves, so the host can never wait for nobody.
            pacing.Sample(90, true); pacing.Sample(3, false); Check(!pacing.IsHolding);
            pacing.Sample(90, true); pacing.Sample(null, true); Check(!pacing.IsHolding); Equal(7f, pacing.Apply(7));
        });
        yield return ("Hold model: a guest frozen for 30 s is never more than about 60 ticks behind, and play resumes", () =>
        {
            var held = Simulate(guestTicksPerSecond: _ => 17, hitchEverySeconds: 0, oneStallAtSeconds: 20, stallSeconds: 30);
            Check(held.WorstBehindOverall <= HostPacing.StopTicks + 12, $"worst lag {held.WorstBehindOverall}");
            Check(held.FinalBehind <= CatchUpSpeed.BufferTicks + 1 && held.FinalPercent == 100, $"behind {held.FinalBehind}, {held.FinalPercent}%");
            Check(held.HostTicksPerSecondLateOn > 11, $"host at {held.HostTicksPerSecondLateOn:0.0} ticks/s afterwards");
        });
        yield return ("Pacing model: a fast guest with hitches never slows the host", () =>
        {
            var run = Simulate(guestTicksPerSecond: _ => 17, hitchEverySeconds: 5);
            Equal(100, run.LowestPercent);
            Check(run.WorstBehindLateOn < 12, $"lag {run.WorstBehindLateOn}");
        });
        yield return ("Pacing model: one long stall on a fast guest does not slow the host", () =>
        {
            foreach (double stall in new[] { 1.0, 3.0, 4.0 })
            {
                var run = Simulate(guestTicksPerSecond: _ => 17, hitchEverySeconds: 0, oneStallAtSeconds: 20, stallSeconds: stall);
                Check(run.LowestPercent == 100, $"a {stall} s stall eased the host to {run.LowestPercent}%");
                Check(run.FinalBehind <= CatchUpSpeed.BufferTicks + 1, $"a {stall} s stall left the guest {run.FinalBehind} behind");
            }
        });
        yield return ("Pacing model: without easing a slow guest falls behind without limit", () =>
        {
            var run = Simulate(guestTicksPerSecond: _ => 8, hitchEverySeconds: 0, easing: false);
            Check(run.FinalBehind > 300, $"expected runaway lag, got {run.FinalBehind}");
        });
        yield return ("Pacing model: with easing a slow guest stays within reach and the host runs near the guest's pace", () =>
        {
            var run = Simulate(guestTicksPerSecond: _ => 8, hitchEverySeconds: 0);
            Check(run.WorstBehindLateOn < 60, $"lag late on {run.WorstBehindLateOn}");
            Check(run.HostTicksPerSecondLateOn <= 8 * 1.05, $"host {run.HostTicksPerSecondLateOn:0.0}/s should not outrun a guest that manages 8/s");
            Check(run.HostTicksPerSecondLateOn >= 8 * .75, $"host {run.HostTicksPerSecondLateOn:0.0}/s gave up more speed than it needed to");
        });
        yield return ("Pacing model: full speed returns once a slow guest speeds up", () =>
        {
            var run = Simulate(guestTicksPerSecond: t => t < 90 ? 7 : 17, hitchEverySeconds: 0, totalSeconds: 240);
            Check(run.LowestPercent < 100, "the slow spell should have eased the host");
            Equal(100, run.FinalPercent);
            Check(run.FinalBehind <= CatchUpSpeed.BufferTicks + 1, $"final lag {run.FinalBehind}");
        });
        yield return ("The host learns how far behind each guest is from their replies", () =>
        {
            int previousInterval = TimberServer.StatusIntervalMs;
            TimberServer.StatusIntervalMs = 50;
            var listener = new QueueListener();
            var host = new TimberServer(listener, () => Task.FromResult(new byte[] { 1 }),
                () => new JObject { [TimberNetBase.TYPE_KEY] = "Init", [TimberNetBase.TICKS_KEY] = 0 });
            var (hostSide, guestSide) = PipeStream.Pair();
            listener.Pending.Add(hostSide);
            var guest = new TimberClient(guestSide);
            try
            {
                host.Start();
                bool mapped = false; guest.OnMapReceived += _ => mapped = true;
                guest.Start();
                Check(SpinWait.SpinUntil(() => { host.Update(); guest.Update(); return mapped; }, 3000), "guest never joined");
                Check(host.WorstGuestTicksBehind == null || host.WorstGuestTicksBehind == 0, "nothing reported yet");
                host.ReadEvents(140); guest.ReadEvents(100);   // the game tells each side which tick it has reached
                Check(SpinWait.SpinUntil(() => { host.Update(); guest.Update(); Thread.Sleep(2); return host.WorstGuestTicksBehind == 40; }, 3000),
                    $"host saw {host.WorstGuestTicksBehind}");
                Equal((int?)40, host.GetNetworkStatus().Peers.Single().TicksBehind);
                guest.ReadEvents(139);
                Check(SpinWait.SpinUntil(() => { host.Update(); guest.Update(); Thread.Sleep(2); return host.WorstGuestTicksBehind == 1; }, 3000),
                    $"host saw {host.WorstGuestTicksBehind} after the guest caught up");
                guest.Close();
                Check(SpinWait.SpinUntil(() => { host.Update(); Thread.Sleep(2); return host.WorstGuestTicksBehind == null; }, 3000),
                    "a guest that left is still counted");
            }
            finally { TimberServer.StatusIntervalMs = previousInterval; guest.Close(); host.Close(); }
        });
        yield return ("Panel: the host sees the slowest guest's lag, and a line while it is easing off", () =>
        {
            string T(string key, object[] args) => args.Length == 0 ? key : key + ":" + string.Join(",", args);
            var input = new PanelInputs { IsHost = true, Speed = 7, TickRate = 11.7 };
            input.Players.Add(new PanelPlayer { Id = 0, Name = "Host", IsYou = true, IsHost = true });
            input.Players.Add(new PanelPlayer { Id = 1, Name = "A", RttMs = 30, SilenceSeconds = .1, TicksBehind = 3 });
            input.Players.Add(new PanelPlayer { Id = 2, Name = "B", RttMs = 30, SilenceSeconds = .1, TicksBehind = 22 });
            var model = PanelModelBuilder.Build(input, T);
            Equal("BeaverBuddies.Panel.TicksMany:22", model.GuestsBehindText); Check(model.PacingText == null);
            input.HostPacingPercent = 70;
            Equal("BeaverBuddies.Panel.PacingValue:70", PanelModelBuilder.Build(input, T).PacingText);
            input.HostPacingHolding = true;
            Equal("BeaverBuddies.Panel.PacingHolding", PanelModelBuilder.Build(input, T).PacingText);
            input.HostPacingHolding = false;
            // A guest sees neither, and a guest on an older build reports no lag.
            var guestView = new PanelInputs { IsHost = false, HostPacingPercent = 70 };
            guestView.Players.Add(new PanelPlayer { Id = 1, Name = "A", IsYou = true, TicksBehind = 9 });
            var guestModel = PanelModelBuilder.Build(guestView, T);
            Check(guestModel.GuestsBehindText == null && guestModel.PacingText == null);
            input.Players.ForEach(p => p.TicksBehind = null);
            Check(PanelModelBuilder.Build(input, T).GuestsBehindText == null);
        });
    }

    // Host and one guest, 60 frames a second, speed 7. The guest runs the real catch-up rule but cannot exceed
    // what its computer manages. The host samples the guest's lag once a second, as the status feed does.
    static (int LowestPercent, int FinalPercent, int FinalBehind, int WorstBehindLateOn, double HostTicksPerSecondLateOn, int WorstBehindOverall) Simulate(
        Func<double, double> guestTicksPerSecond, double hitchEverySeconds, bool easing = true,
        double oneStallAtSeconds = -1, double stallSeconds = 0, double totalSeconds = 180)
    {
        const double frame = 1 / 60.0, secondsPerTick = .6; const float target = 7;
        var pacing = new HostPacing();
        double hostTicks = 0, guestTicks = 0, nextSample = 1, nextHitch = hitchEverySeconds > 0 ? hitchEverySeconds : double.MaxValue, stalledUntil = -1;
        float guestSpeed = target; int lowest = 100, worstLate = 0, worstOverall = 0; double hostTicksAtHalf = 0;
        for (double time = 0; time < totalSeconds; time += frame)
        {
            if (Math.Abs(time - totalSeconds / 2) < frame / 2) hostTicksAtHalf = hostTicks;
            hostTicks += frame * (easing ? pacing.Apply(target) : target) / secondsPerTick;
            if (time >= nextHitch) { stalledUntil = time + .3; nextHitch += hitchEverySeconds; }
            if (oneStallAtSeconds >= 0 && time >= oneStallAtSeconds && time < oneStallAtSeconds + frame) stalledUntil = time + stallSeconds;
            if (time >= stalledUntil)
                guestTicks = Math.Min((int)hostTicks, guestTicks + frame * Math.Min(guestSpeed / secondsPerTick, guestTicksPerSecond(time)));
            int behind = (int)hostTicks - (int)guestTicks;
            guestSpeed = CatchUpSpeed.For(target, behind, guestSpeed);
            if (time >= nextSample) { nextSample += 1; pacing.Sample(behind, true); lowest = Math.Min(lowest, pacing.Percent); }
            if (time > totalSeconds / 2) worstLate = Math.Max(worstLate, behind);
            worstOverall = Math.Max(worstOverall, behind);
        }
        return (lowest, pacing.Percent, (int)hostTicks - (int)guestTicks, worstLate, (hostTicks - hostTicksAtHalf) / (totalSeconds / 2), worstOverall);
    }

    sealed class QueueListener : ISocketListener
    {
        public readonly System.Collections.Concurrent.BlockingCollection<ISocketStream> Pending = new();
        public void Start() { }
        public ISocketStream AcceptClient() => Pending.Take();
        public void Stop() => Pending.CompleteAdding();
    }
}
