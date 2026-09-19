using BeaverBuddies.Activity;

// Per-player cursor styles: the production PlayerCursorPreferences source, compiled directly.
static class CursorPreferencesChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    sealed class TempFile : IDisposable
    {
        public readonly string Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bb-cursor-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "styles.json");
        public void Dispose() { try { System.IO.Directory.Delete(Directory, true); } catch { } }
    }

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Cursor style defaults match the documented look", () =>
        {
            var style = new PlayerCursorStyle();
            Equal(1f, style.Size); Equal(.5f, style.Opacity); Check(style.ColorHex == null && style.IsDefault);
            Check(ReferenceEquals(new PlayerCursorPreferences(null).Get("nobody"), PlayerCursorStyle.Default));
        });
        yield return ("Out-of-range and invalid style values are forced valid", () =>
        {
            var prefs = new PlayerCursorPreferences(null);
            prefs.Set("a", new PlayerCursorStyle { Size = 99, Opacity = -4, ColorHex = "zzzzzz" });
            var a = prefs.Get("a");
            Equal(PlayerCursorStyle.MaxSize, a.Size); Equal(PlayerCursorStyle.MinOpacity, a.Opacity); Check(a.ColorHex == null);
            prefs.Set("b", new PlayerCursorStyle { Size = float.NaN, Opacity = float.PositiveInfinity, ColorHex = "  #ff8800 " });
            var b = prefs.Get("b");
            Equal(PlayerCursorStyle.DefaultSize, b.Size); Equal(PlayerCursorStyle.DefaultOpacity, b.Opacity); Equal("FF8800", b.ColorHex);
            prefs.Set("c", new PlayerCursorStyle { Size = .01f, Opacity = 5 });
            Equal(PlayerCursorStyle.MinSize, prefs.Get("c").Size); Equal(PlayerCursorStyle.MaxOpacity, prefs.Get("c").Opacity);
        });
        yield return ("Styles equal to the default are not stored", () =>
        {
            var prefs = new PlayerCursorPreferences(null);
            prefs.Set("a", new PlayerCursorStyle { Size = 2 });
            Equal(1, prefs.Count);
            prefs.Set("a", new PlayerCursorStyle());
            Equal(0, prefs.Count);
            prefs.Set("b", new PlayerCursorStyle { ColorHex = "112233" });
            prefs.Reset("b"); Equal(0, prefs.Count);
        });
        yield return ("Stored styles are copies, so later edits never leak in", () =>
        {
            var prefs = new PlayerCursorPreferences(null);
            var style = new PlayerCursorStyle { Size = 2 };
            prefs.Set("a", style);
            style.Size = 3;
            Equal(2f, prefs.Get("a").Size);
        });
        yield return ("Styles survive a save and reload, keyed case-insensitively", () =>
        {
            using var file = new TempFile();
            var prefs = new PlayerCursorPreferences(file.Path);
            prefs.Set("sarah", new PlayerCursorStyle { ColorHex = "112233", Size = 1.5f, Opacity = .8f });
            Check(prefs.Save());
            Check(!File.Exists(file.Path + ".tmp"), "temp file left behind");
            var reloaded = new PlayerCursorPreferences(file.Path).Get("SARAH");
            Equal("112233", reloaded.ColorHex); Equal(1.5f, reloaded.Size); Equal(.8f, reloaded.Opacity);
            // Saving again replaces the existing file.
            prefs.Set("sarah", new PlayerCursorStyle { Size = 2 });
            Check(prefs.Save());
            Equal(2f, new PlayerCursorPreferences(file.Path).Get("sarah").Size);
        });
        yield return ("A damaged or hostile file falls back to defaults and is normalized", () =>
        {
            using var file = new TempFile();
            Directory.CreateDirectory(file.Directory);
            File.WriteAllText(file.Path, "{ not json");
            Equal(0, new PlayerCursorPreferences(file.Path).Count);
            File.WriteAllText(file.Path, "null");
            Equal(0, new PlayerCursorPreferences(file.Path).Count);
            File.WriteAllText(file.Path, "{\"x\":{\"ColorHex\":\"zz\",\"Size\":1e9,\"Opacity\":-1},\"y\":null,\"\":{\"Size\":2}}");
            var prefs = new PlayerCursorPreferences(file.Path);
            Equal(1, prefs.Count);
            Equal(PlayerCursorStyle.MaxSize, prefs.Get("x").Size); Check(prefs.Get("x").ColorHex == null);
        });
        yield return ("A file with a huge number of entries is capped", () =>
        {
            using var file = new TempFile();
            Directory.CreateDirectory(file.Directory);
            var entries = string.Join(",", Enumerable.Range(0, 1000).Select(i => $"\"p{i}\":{{\"Size\":2}}"));
            File.WriteAllText(file.Path, "{" + entries + "}");
            Equal(PlayerCursorPreferences.MaxEntries, new PlayerCursorPreferences(file.Path).Count);
        });
        yield return ("An unwritable location reports failure instead of throwing", () =>
        {
            using var file = new TempFile();
            Directory.CreateDirectory(file.Directory);
            string blocker = System.IO.Path.Combine(file.Directory, "blocker");
            File.WriteAllText(blocker, "a file, not a folder");
            var prefs = new PlayerCursorPreferences(System.IO.Path.Combine(blocker, "styles.json"));
            prefs.Set("a", new PlayerCursorStyle { Size = 2 });
            Check(!prefs.Save());
            Equal(2f, prefs.Get("a").Size); // still applies for this session
        });
        yield return ("Style keys use the name, plus the player number only when names collide", () =>
        {
            Equal("sarah", PlayerCursorPreferences.KeyFor("  Sarah ", 3, false));
            Equal("sarah#3", PlayerCursorPreferences.KeyFor("Sarah", 3, true));
            Equal("player", PlayerCursorPreferences.KeyFor("", 2, false));
            Equal("player", PlayerCursorPreferences.KeyFor(null, 2, false));
        });
    }
}
