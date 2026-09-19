using BeaverBuddies;

static class CatchUpSpeedChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    // The rule this replaces, kept here as the reference.
    static float Original(float targetSpeed, int ticksBehind) =>
        ticksBehind > targetSpeed ? Math.Min(ticksBehind, 10) : targetSpeed;

    static readonly float[] Speeds = { 0, 1, 2, 3, 7, 10 };

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Catch-up: a guest within the buffer runs at the chosen speed", () =>
        {
            foreach (float target in new float[] { 3, 7 })
                for (int behind = 0; behind <= CatchUpSpeed.BufferTicks; behind++)
                    Equal(target, CatchUpSpeed.For(target, behind, target));
        });
        yield return ("Catch-up: at speed 7 a guest 3 to 5 ticks behind now speeds up", () =>
        {
            Equal(7f, Original(7, 3)); Equal(7f, Original(7, 5));
            Equal(8f, CatchUpSpeed.For(7, 3, 7));
            Equal(9f, CatchUpSpeed.For(7, 4, 7));
            Equal(10f, CatchUpSpeed.For(7, 5, 7));
            Equal(10f, CatchUpSpeed.For(7, 40, 7));
        });
        yield return ("Catch-up: never slower than the original rule, and never above its cap", () =>
        {
            foreach (float target in Speeds)
                foreach (float current in new[] { 0, target, target + 1, 10 })
                    for (int behind = 0; behind <= 30; behind++)
                    {
                        float speed = CatchUpSpeed.For(target, behind, current);
                        Check(speed >= Original(target, behind), $"slower than original at target {target}, behind {behind}");
                        Check(speed <= Math.Max(target, CatchUpSpeed.MaxSpeed), $"above cap at target {target}, behind {behind}");
                    }
        });
        yield return ("Catch-up: a paused game keeps the original rule exactly", () =>
        {
            foreach (float current in new float[] { 0, 1, 5, 10 })
                for (int behind = 0; behind <= 30; behind++)
                    Equal(Original(0, behind), CatchUpSpeed.For(0, behind, current));
        });
        yield return ("Catch-up: a player who is not behind is never changed (the host)", () =>
        {
            foreach (float target in Speeds)
                foreach (float current in new[] { 0, target, 10 })
                    Equal(target, CatchUpSpeed.For(target, 0, current));
        });
        yield return ("Catch-up: once started it continues to the release mark, then stops", () =>
        {
            // Not yet catching up: 2 behind is inside the buffer.
            Equal(7f, CatchUpSpeed.For(7, 2, 7));
            // Already catching up: 2 behind keeps going, 1 behind stops.
            Equal(8f, CatchUpSpeed.For(7, 2, 8));
            Equal(7f, CatchUpSpeed.For(7, 1, 8));
            // While catching up the speed holds or rises as the lag flickers; it never steps down early.
            Equal(10f, CatchUpSpeed.For(7, 3, 10));
            Equal(9f, CatchUpSpeed.For(7, 3, 9));
            Equal(10f, CatchUpSpeed.For(7, 6, 9));
            Equal(7f, CatchUpSpeed.For(7, 1, 10));
        });
        yield return ("Catch-up: speeds are whole steps", () =>
        {
            foreach (float target in Speeds)
                for (int behind = 0; behind <= 30; behind++)
                {
                    float speed = CatchUpSpeed.For(target, behind, target);
                    Equal(speed, (float)Math.Round(speed));
                }
        });
        yield return ("Catch-up: a guest that hitches settles near the buffer instead of drifting to seven", () =>
        {
            var original = Simulate(7, (target, behind, _) => Original(target, behind));
            var updated = Simulate(7, CatchUpSpeed.For);
            Check(original.AverageBehind > 3, $"reference model should drift, got {original.AverageBehind:0.0}");
            Check(updated.AverageBehind <= CatchUpSpeed.BufferTicks + .5, $"average lag {updated.AverageBehind:0.0}");
            Check(updated.AverageBehind < original.AverageBehind / 2, "lag should at least halve");
        });
        yield return ("Catch-up: speed changes stay rare", () =>
        {
            var updated = Simulate(7, CatchUpSpeed.For);
            // Each change notifies every animated building, so a rule that flips every tick would cost more
            // than the lag it removes. A hitch may take a few steps up and back down, and no more.
            Check(updated.SpeedChanges <= updated.Hitches * 8, $"{updated.SpeedChanges} changes for {updated.Hitches} hitches");
            var smooth = Simulate(7, CatchUpSpeed.For, hitchEverySeconds: 0);
            Equal(0, smooth.SpeedChanges);
        });
        yield return ("Catch-up: lower speeds behave as before or better", () =>
        {
            foreach (float target in new float[] { 1, 3 })
            {
                var original = Simulate(target, (t, behind, _) => Original(t, behind));
                var updated = Simulate(target, CatchUpSpeed.For);
                Check(updated.AverageBehind <= original.AverageBehind + .01, $"speed {target}: {updated.AverageBehind:0.00} vs {original.AverageBehind:0.00}");
            }
        });
    }

    // A guest receiving the host's ticks with no network delay, losing 0.3 s every few seconds (a garbage
    // collection, a save, a slow frame). One tick is 0.6 s of game time, so a speed of 7 is about 11.7 ticks/s.
    static (double AverageBehind, int SpeedChanges, int Hitches) Simulate(float target, Func<float, int, float, float> rule,
        double hitchEverySeconds = 5, double hitchSeconds = .3, double totalSeconds = 120)
    {
        const double frame = 1 / 60.0, secondsPerTick = .6;
        double guestTicks = 0, behindSum = 0; float speed = target; int changes = 0, hitches = 0, samples = 0;
        double nextHitch = hitchEverySeconds > 0 ? hitchEverySeconds : double.MaxValue, stalledUntil = -1;
        for (double time = 0; time < totalSeconds; time += frame)
        {
            int hostTick = (int)(time * target / secondsPerTick);
            if (time >= nextHitch) { stalledUntil = time + hitchSeconds; nextHitch += hitchEverySeconds; hitches++; }
            if (time >= stalledUntil) guestTicks = Math.Min(hostTick, guestTicks + frame * speed / secondsPerTick);
            int behind = hostTick - (int)guestTicks;
            float next = rule(target, behind, speed);
            if (next != speed) { changes++; speed = next; }
            if (time > totalSeconds / 2) { behindSum += behind; samples++; }
        }
        return (behindSum / samples, changes, hitches);
    }
}
