using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace BeaverBuddies.Activity
{
    /// <summary>
    /// How this player draws one other player's cursor. Purely local: nothing here is sent over the
    /// network and it can never affect the simulation.
    /// </summary>
    public sealed class PlayerCursorStyle
    {
        public const float MinSize = .5f, MaxSize = 3f, DefaultSize = 1f;
        public const float MinOpacity = .1f, MaxOpacity = 1f, DefaultOpacity = .5f;

        /// <summary>RRGGBB override, or null to use the color the other player chose for themselves.</summary>
        public string ColorHex;
        public float Size = DefaultSize;
        /// <summary>1 is solid, lower is more transparent. The panel presents this as transparency.</summary>
        public float Opacity = DefaultOpacity;

        public static readonly PlayerCursorStyle Default = new PlayerCursorStyle();

        [JsonIgnore]
        public bool IsDefault => ColorHex == null && Size == DefaultSize && Opacity == DefaultOpacity;

        public PlayerCursorStyle Clone() => new PlayerCursorStyle { ColorHex = ColorHex, Size = Size, Opacity = Opacity };

        /// <summary>Returns a copy with every value forced into its valid range.</summary>
        public PlayerCursorStyle Normalized() => new PlayerCursorStyle
        {
            ColorHex = NormalizeColor(ColorHex),
            Size = Clamp(Size, MinSize, MaxSize, DefaultSize),
            Opacity = Clamp(Opacity, MinOpacity, MaxOpacity, DefaultOpacity),
        };

        public static string NormalizeColor(string hex)
        {
            if (hex == null) return null;
            hex = hex.Trim().TrimStart('#');
            if (hex.Length != 6 || hex.Any(c => !Uri.IsHexDigit(c))) return null;
            return hex.ToUpperInvariant();
        }

        static float Clamp(float value, float min, float max, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return fallback;
            return value < min ? min : value > max ? max : value;
        }
    }

    /// <summary>
    /// Saved per-player cursor styles. Players are remembered by display name, so a friend keeps the
    /// same look between sessions even though their player number changes.
    /// </summary>
    public sealed class PlayerCursorPreferences
    {
        public const int MaxEntries = 256;
        readonly Dictionary<string, PlayerCursorStyle> styles = new Dictionary<string, PlayerCursorStyle>(StringComparer.OrdinalIgnoreCase);
        readonly string path;

        /// <param name="path">JSON file, or null to keep preferences in memory only.</param>
        public PlayerCursorPreferences(string path)
        {
            this.path = path;
            Load();
        }

        public int Count => styles.Count;

        /// <summary>The saved style, or the shared default. Callers must not modify the result.</summary>
        public PlayerCursorStyle Get(string key) =>
            key != null && styles.TryGetValue(key, out var style) ? style : PlayerCursorStyle.Default;

        /// <summary>Stores a normalized copy. Styles equal to the default are dropped rather than saved.</summary>
        public void Set(string key, PlayerCursorStyle style)
        {
            if (string.IsNullOrWhiteSpace(key) || style == null) return;
            var clean = style.Normalized();
            if (clean.IsDefault) { styles.Remove(key); return; }
            if (!styles.ContainsKey(key) && styles.Count >= MaxEntries) return;
            styles[key] = clean;
        }

        public void Reset(string key)
        {
            if (key != null) styles.Remove(key);
        }

        /// <summary>
        /// Name-based key. When several connected players share a name, each also gets its player
        /// number so their styles stay separate.
        /// </summary>
        public static string KeyFor(string name, int playerId, bool nameIsShared)
        {
            string clean = (name ?? "").Trim().ToLowerInvariant();
            if (clean.Length == 0) clean = "player";
            return nameIsShared ? clean + "#" + playerId : clean;
        }

        void Load()
        {
            if (path == null || !File.Exists(path)) return;
            try
            {
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, PlayerCursorStyle>>(File.ReadAllText(path));
                if (loaded == null) return;
                foreach (var pair in loaded.Take(MaxEntries))
                    if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null) Set(pair.Key, pair.Value);
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                // A damaged or unreadable file just means default styles; it must never block joining.
                styles.Clear();
            }
        }

        /// <returns>False if the file could not be written; the in-memory styles still apply.</returns>
        public bool Save()
        {
            if (path == null) return true;
            try
            {
                string json = JsonConvert.SerializeObject(styles, Formatting.Indented);
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                // Write beside the target and swap, so a crash mid-save cannot leave a half-written file.
                string temp = path + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
