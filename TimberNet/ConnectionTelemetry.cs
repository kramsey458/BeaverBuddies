using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace TimberNet
{
    public sealed class SimulationStatus
    {
        public int Tick { get; }
        // -1 means the two-second measurement window is still warming up.
        public double Rate { get; }
        public bool Loaded { get; }
        public bool Paused { get; }
        public SimulationStatus(int tick, double rate, bool loaded, bool paused)
        { Tick = Math.Max(0, tick); Rate = rate; Loaded = loaded; Paused = paused; }
    }
    public sealed class PeerStatus
    {
        public string Label { get; internal set; } = "Peer";
        public double? RoundTripMilliseconds { get; internal set; }
        public double AgeSeconds { get; internal set; }
        public int LocalTickAtReply { get; internal set; }
        public SimulationStatus? Simulation { get; internal set; }
        public int QueuedMessages { get; internal set; }
        public long QueuedBytes { get; internal set; }
        public bool Fresh => Simulation != null && AgeSeconds < 5;
    }

    // Main-thread state, with a caller-supplied monotonic clock for tests.
    // One outstanding probe per peer. Replaced disposable frames cannot change replay.
    public sealed class ConnectionTelemetry
    {
        public const string MessageType = "ConnectionStatus";
        sealed class Peer
        {
            public int Sequence;
            public double Sent = double.NegativeInfinity, Received = double.NegativeInfinity, Replied = double.NegativeInfinity;
            public bool Pending;
            public double? RoundTrip;
            public SimulationStatus? Simulation;
            public int LocalTick;
        }
        readonly Dictionary<object, Peer> peers = new Dictionary<object, Peer>();
        public static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        public void Clear() => peers.Clear();
        public void Retain(HashSet<object> active)
        { foreach (var key in new List<object>(peers.Keys)) if (!active.Contains(key)) peers.Remove(key); }
        public void Probe(object peer, double now, Action<JObject> send)
        {
            if (!peers.TryGetValue(peer, out var state))
            {
                if (peers.Count >= PlayerActivity.MaxPlayers) return;
                peers.Add(peer, state = new Peer());
            }
            if (now - state.Sent < (state.Pending ? 5 : 1)) return;
            state.Sequence = state.Sequence == int.MaxValue ? 1 : state.Sequence + 1;
            state.Sent = now; state.Pending = true;
            send(new JObject { ["type"] = MessageType, ["reply"] = false, ["sequence"] = state.Sequence });
        }
        public static bool Valid(JObject message)
        {
            try { return Validate(message); }
            catch (Exception error) when (error is OverflowException || error is FormatException || error is InvalidCastException || error is ArgumentException) { return false; }
        }
        static bool Validate(JObject message)
        {
            if ((string?)message["type"] != MessageType || message["reply"]?.Type != JTokenType.Boolean ||
                message["sequence"]?.Type != JTokenType.Integer) return false;
            long sequence = (long)message["sequence"]!;
            if (sequence < 1 || sequence > int.MaxValue) return false;
            if (!(bool)message["reply"]!) return true;
            if (message["tick"]?.Type != JTokenType.Integer || message["loaded"]?.Type != JTokenType.Boolean ||
                message["paused"]?.Type != JTokenType.Boolean || (message["rate"]?.Type != JTokenType.Float && message["rate"]?.Type != JTokenType.Integer)) return false;
            long tick = (long)message["tick"]!; double rate = (double)message["rate"]!;
            return tick >= 0 && tick <= int.MaxValue && !double.IsNaN(rate) && !double.IsInfinity(rate) && rate >= -1 && rate <= 100000;
        }
        public void Receive(object peer, JObject message, double now, SimulationStatus local, Action<JObject> send)
        {
            if (!Valid(message) || !peers.TryGetValue(peer, out var state)) return;
            int sequence = (int)message["sequence"]!;
            if (!(bool)message["reply"]!)
            {
                if (now - state.Replied < .5) return; // A flood must not amplify outgoing traffic.
                state.Replied = now;
                send(new JObject { ["type"] = MessageType, ["reply"] = true, ["sequence"] = sequence,
                    ["tick"] = local.Tick, ["rate"] = local.Rate, ["loaded"] = local.Loaded, ["paused"] = local.Paused });
            }
            else if (state.Pending && sequence == state.Sequence)
            {
                state.Pending = false; state.Received = now;
                state.RoundTrip = Math.Max(0, (now - state.Sent) * 1000);
                state.LocalTick = local.Tick;
                state.Simulation = new SimulationStatus((int)message["tick"]!, (double)message["rate"]!, (bool)message["loaded"]!, (bool)message["paused"]!);
            }
        }
        public PeerStatus Read(object peer, double now)
        {
            if (!peers.TryGetValue(peer, out var state)) return new PeerStatus { AgeSeconds = double.PositiveInfinity };
            return new PeerStatus { AgeSeconds = Math.Max(0, now - state.Received), RoundTripMilliseconds = state.RoundTrip,
                Simulation = state.Simulation, LocalTickAtReply = state.LocalTick };
        }
    }
}
