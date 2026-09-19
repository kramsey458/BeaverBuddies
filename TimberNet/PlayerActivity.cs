using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TimberNet
{
    // Presentation only: never enters the replay script, tick ordering, or state hash.
    public sealed class PlayerActivity
    {
        public const string MessageType = "PlayerActivity";
        public const int MaxPlayers = 64;
        public const double LifetimeSeconds = 3;
        public int PlayerId { get; }
        public string Name { get; }
        public string Color { get; }
        public bool CursorVisible { get; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public string Selection { get; }
        public string Editing { get; }

        public PlayerActivity(int playerId, string name, string color, bool cursorVisible,
            float x, float y, float z, string selection = "", string editing = "")
        {
            PlayerId = playerId;
            Name = CleanName(name);
            Color = color;
            CursorVisible = cursorVisible;
            X = x; Y = y; Z = z;
            Selection = selection; Editing = editing;
        }

        public PlayerActivity WithPlayerId(int id) => new PlayerActivity(id, Name, Color, CursorVisible, X, Y, Z, Selection, Editing);

        public JObject ToJson() => new JObject
        {
            ["type"] = MessageType, ["player"] = PlayerId, ["name"] = Name, ["color"] = Color,
            ["cursor"] = CursorVisible, ["x"] = X, ["y"] = Y, ["z"] = Z,
            ["selection"] = Selection, ["editing"] = Editing
        };

        static string CleanName(string? name)
        {
            var clean = new string((name ?? "").Where(c => !char.IsControl(c) && c != '<' && c != '>').Take(32).ToArray()).Trim();
            return clean.Length == 0 ? "Player" : clean;
        }

        // Anything a peer sends is validated before it can reach the game thread.
        public static bool TryParse(JObject message, out PlayerActivity? activity)
        {
            activity = null;
            try
            {
                if ((string?)message["type"] != MessageType || message.Count > 12) return false;
                if (message["player"]?.Type != JTokenType.Integer || message["cursor"]?.Type != JTokenType.Boolean) return false;
                int id = (int)message["player"]!;
                if (id < 0) return false;
                foreach (string key in new[] { "name", "color", "selection", "editing" })
                    if (message[key]?.Type != JTokenType.String || ((string)message[key]!).Length > 64) return false;
                string name = (string)message["name"]!, color = (string)message["color"]!;
                if (color.Length != 6 || color.Any(c => !Uri.IsHexDigit(c))) return false;
                string selected = (string)message["selection"]!, editing = (string)message["editing"]!;
                if (selected.Length != 0 && !Guid.TryParseExact(selected, "D", out _)) return false;
                if (editing.Length != 0 && !Guid.TryParseExact(editing, "D", out _)) return false;
                float[] position = new float[3];
                int index = 0;
                foreach (string key in new[] { "x", "y", "z" })
                {
                    if (message[key]?.Type != JTokenType.Float && message[key]?.Type != JTokenType.Integer) return false;
                    float value = (float)message[key]!;
                    if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 100000) return false;
                    position[index++] = value;
                }
                activity = new PlayerActivity(id, name, color, (bool)message["cursor"]!, position[0], position[1], position[2], selected, editing);
                return true;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException || e is OverflowException || e is InvalidCastException)
            { return false; }
        }
    }

    // Bounded latest-wins inbox. Receiving while the main thread loads a map cannot build a cursor backlog.
    public sealed class ActivityMailbox
    {
        readonly object gate = new object();
        readonly Dictionary<int, (PlayerActivity State, double Time)> latest = new Dictionary<int, (PlayerActivity, double)>();
        public static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        public void Put(PlayerActivity state, double now)
        {
            lock (gate)
            {
                if (!latest.ContainsKey(state.PlayerId) && latest.Count >= PlayerActivity.MaxPlayers) return;
                latest[state.PlayerId] = (state, now);
            }
        }
        public PlayerActivity[] Take(double now)
        {
            lock (gate)
            {
                var result = latest.Values.Where(v => now - v.Time <= PlayerActivity.LifetimeSeconds).Select(v => v.State).ToArray();
                latest.Clear();
                return result;
            }
        }
        public void Clear() { lock (gate) latest.Clear(); }
    }

    // Per-connection outgoing lane for presentation frames. It keeps only the newest state for each
    // player, is drained by at most one pooled task at a time, and never blocks the game thread on a
    // slow socket. Frames are written through the same framed, stream-locked path as gameplay events,
    // so an activity frame can never split a reliable event.
    public sealed class ActivityChannel
    {
        readonly ISocketStream stream;
        readonly Action<ISocketStream, JObject> write;
        readonly Action<ISocketStream, string> fail;
        readonly object gate = new object();
        // Latest frame per key. Frames are built when they are written, so a timestamp inside one is accurate.
        readonly Dictionary<string, Func<JObject>> pending = new Dictionary<string, Func<JObject>>();
        const int MaxPending = PlayerActivity.MaxPlayers + 8;
        bool running, closed;

        public ISocketStream Stream => stream;

        public ActivityChannel(ISocketStream stream, Action<ISocketStream, JObject> write, Action<ISocketStream, string> fail)
        {
            this.stream = stream; this.write = write; this.fail = fail;
        }

        public void Post(PlayerActivity state) => PostFrame("activity:" + state.PlayerId, () => state.ToJson());

        /// <summary>Queues a frame, replacing any older frame with the same key.</summary>
        public void PostFrame(string key, Func<JObject> frame)
        {
            lock (gate)
            {
                if (closed) return;
                if (!pending.ContainsKey(key) && pending.Count >= MaxPending) return;
                pending[key] = frame;
                if (running) return;
                running = true;
            }
            System.Threading.Tasks.Task.Run(Pump);
        }

        public void Close()
        {
            lock (gate) { closed = true; pending.Clear(); }
        }

        void Pump()
        {
            while (true)
            {
                Func<JObject>[] batch;
                lock (gate)
                {
                    if (closed || pending.Count == 0) { running = false; return; }
                    batch = pending.Values.ToArray();
                    pending.Clear();
                }
                foreach (var frame in batch)
                {
                    try { write(stream, frame()); }
                    catch (Exception e)
                    {
                        // A failed write may leave a partial frame, so this connection is finished.
                        Close();
                        fail(stream, "Error sending activity: " + e.Message);
                        return;
                    }
                }
            }
        }
    }
}
