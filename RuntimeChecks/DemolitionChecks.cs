using System.Reflection;

internal static class DemolitionChecks
{
    public static void Run(Assembly mod, Action<string, Action> test)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var fix = mod.GetType("BeaverBuddies.Fixes.DemolitionSelectionFix", true);
        var compare = fix.GetMethod("Prefer", flags);
        bool Prefer(float distance, Guid id, float best, Guid bestId, bool hasBest) =>
            (bool)compare.Invoke(null, new object[] { distance, id, best, bestId, hasBest });
        var ids = new[] {
            Guid.Parse("00000001-0000-0000-0000-000000000000"),
            Guid.Parse("7fffffff-0000-0000-0000-000000000000"),
            Guid.Parse("ffffffff-0000-0000-0000-000000000000") };
        void Require(bool value) { if (!value) throw new Exception("Unexpected demolition selection"); }
        Guid Select(IEnumerable<(Guid id, float distance)> candidates)
        {
            Guid chosen = Guid.Empty;
            float distance = float.MaxValue;
            bool found = false;
            foreach (var candidate in candidates)
                if (Prefer(candidate.distance, candidate.id, distance, chosen, found))
                { chosen = candidate.id; distance = candidate.distance; found = true; }
            return chosen;
        }
        var permutations = new[] { new[]{0,1,2}, new[]{0,2,1}, new[]{1,0,2}, new[]{1,2,0}, new[]{2,0,1}, new[]{2,1,0} };
        test("Demolition equal-distance selection is independent of every candidate permutation", () =>
        {
            foreach (var order in permutations)
                Require(Select(order.Select(i => (ids[i], 10f))) == ids[0]);
        });
        test("Demolition nearer job wins regardless of ID and candidate order", () =>
        {
            foreach (var order in permutations)
                Require(Select(order.Select(i => (ids[i], i == 2 ? 9f : 10f))) == ids[2]);
        });
        test("Demolition does not round almost-equal distances into ties", () =>
        {
            float closer = MathF.BitDecrement(10f);
            Require(Select(new[] { (ids[0], 10f), (ids[2], closer) }) == ids[2]);
            Require(Select(new[] { (ids[2], closer), (ids[0], 10f) }) == ids[2]);
        });
        test("Demolition preserves vanilla no-selection behavior for invalid or sentinel distances", () =>
        {
            foreach (float distance in new[] { float.NaN, float.PositiveInfinity, float.MaxValue })
                Require(!Prefer(distance, ids[0], float.MaxValue, Guid.Empty, false));
            Require(Select(Array.Empty<(Guid,float)>()) == Guid.Empty);
        });
        test("Demolition duplicate candidate does not replace itself", () =>
            Require(!Prefer(10f, ids[0], 10f, ids[0], true)));
        test("Demolition single-player prefix delegates to vanilla without accessing jobs", () =>
        {
            var field = mod.GetType("BeaverBuddies.IO.EventIO", true).GetField("instance", flags);
            object previous = field.GetValue(null);
            try
            {
                field.SetValue(null, null);
                var prefix = fix.GetMethod("Prefix", flags);
                var priority = Activator.CreateInstance(prefix.GetParameters()[3].ParameterType);
                Require((bool)prefix.Invoke(null, new object[] { null, null, null, priority, null }));
            }
            finally { field.SetValue(null, previous); }
        });
    }
}
