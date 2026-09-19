using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TimberNet
{
    /// <summary>Optional. A transport that can say how it reaches the other player, for display.</summary>
    public interface ITransportInfo
    {
        /// <summary>A short label such as "Direct" or "Steam".</summary>
        string TransportName { get; }
    }

    /// <summary>How one connected player looks from the host, for display only.</summary>
    public sealed class PeerStatus
    {
        public int PlayerId { get; }
        /// <summary>How they are connected ("Direct", "Steam"), or empty if unknown.</summary>
        public string Transport { get; }
        /// <summary>Smoothed round-trip time to the host in milliseconds, or null before the first reply.</summary>
        public double? RttMs { get; }
        public double JitterMs { get; }
        /// <summary>Seconds since the host last heard a reply from them; null before the first probe.</summary>
        public double? SilenceSeconds { get; }

        public PeerStatus(int playerId, string transport, double? rttMs, double jitterMs, double? silenceSeconds)
        {
            PlayerId = playerId; Transport = transport ?? ""; RttMs = rttMs; JitterMs = jitterMs; SilenceSeconds = silenceSeconds;
        }
    }

    /// <summary>A snapshot of the multiplayer connection. Presentation only; never part of the simulation.</summary>
    public sealed class NetworkStatus
    {
        public bool IsHost { get; }
        public bool IsStopped { get; }
        /// <summary>This player's id: 0 for the host, otherwise the number the host assigned.</summary>
        public int YourPlayerId { get; }
        /// <summary>For a guest: seconds since anything arrived from the host's status feed. Null for the host.</summary>
        public double? HostSilenceSeconds { get; }
        /// <summary>Every connected guest as the host measures them (a guest sees the host's latest list).</summary>
        public IReadOnlyList<PeerStatus> Peers { get; }

        public NetworkStatus(bool isHost, bool isStopped, int yourPlayerId, double? hostSilenceSeconds, IReadOnlyList<PeerStatus> peers)
        {
            IsHost = isHost; IsStopped = isStopped; YourPlayerId = yourPlayerId;
            HostSilenceSeconds = hostSilenceSeconds; Peers = peers;
        }

        public static NetworkStatus None(bool stopped) =>
            new NetworkStatus(false, stopped, -1, null, Array.Empty<PeerStatus>());
    }

    /// <summary>
    /// Measures round-trip time from probe/reply pairs. Thread-safe: replies arrive on a network thread
    /// and snapshots are read on the game thread.
    /// </summary>
    public sealed class RttTracker
    {
        const int MaxOutstanding = 16;
        // Weight of the newest sample. Low enough to smooth spikes, high enough to follow real changes.
        const double Smoothing = 0.3;

        readonly object gate = new object();
        readonly Dictionary<int, double> outstanding = new Dictionary<int, double>();
        readonly double createdAtMs;
        int nextSequence;
        double? smoothed;
        double jitter;
        double? lastReplyAtMs;

        public static double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

        public RttTracker(double nowMs) { createdAtMs = nowMs; }

        /// <summary>Records that a probe is being sent now and returns its sequence number.</summary>
        public int BeginProbe(double nowMs)
        {
            lock (gate)
            {
                int sequence = ++nextSequence;
                outstanding[sequence] = nowMs;
                // A peer that never answers must not grow this without bound: forget the oldest.
                while (outstanding.Count > MaxOutstanding)
                    outstanding.Remove(outstanding.Keys.Min());
                return sequence;
            }
        }

        /// <summary>Records a reply. Unknown, duplicate or expired sequence numbers are ignored.</summary>
        public void OnReply(int sequence, double nowMs)
        {
            lock (gate)
            {
                if (!outstanding.TryGetValue(sequence, out double sentAt)) return;
                outstanding.Remove(sequence);
                double rtt = Math.Max(0, nowMs - sentAt);
                if (smoothed == null) smoothed = rtt;
                else
                {
                    jitter = jitter * (1 - Smoothing) + Math.Abs(rtt - smoothed.Value) * Smoothing;
                    smoothed = smoothed.Value * (1 - Smoothing) + rtt * Smoothing;
                }
                lastReplyAtMs = nowMs;
            }
        }

        public PeerStatus Snapshot(int playerId, string transport, double nowMs)
        {
            lock (gate)
            {
                // A peer that has never answered is measured from when it connected.
                double silence = Math.Max(0, nowMs - (lastReplyAtMs ?? createdAtMs)) / 1000.0;
                return new PeerStatus(playerId, transport, smoothed, jitter, silence);
            }
        }
    }

    /// <summary>The status feed's wire format. Every incoming frame is validated before it is used.</summary>
    public static class StatusFrames
    {
        public const string ProbeType = "NetProbe";
        public const string ReplyType = "NetProbeReply";
        public const string RosterType = "NetRoster";
        public const int MaxRosterPeers = PlayerActivity.MaxPlayers;

        public static bool IsStatusType(string? type) => type == ProbeType || type == ReplyType || type == RosterType;

        public static JObject Probe(int sequence) => new JObject { ["type"] = ProbeType, ["seq"] = sequence };
        public static JObject Reply(int sequence) => new JObject { ["type"] = ReplyType, ["seq"] = sequence };

        public static bool TryParseSequence(JObject message, out int sequence)
        {
            sequence = 0;
            try
            {
                if (message.Count > 2 || message["seq"]?.Type != JTokenType.Integer) return false;
                sequence = (int)message["seq"]!;
                return sequence >= 0;
            }
            catch (Exception e) when (e is OverflowException || e is InvalidCastException) { return false; }
        }

        public static JObject Roster(int you, IEnumerable<PeerStatus> peers)
        {
            var list = new JArray();
            foreach (var peer in peers.Take(MaxRosterPeers))
            {
                var entry = new JObject { ["id"] = peer.PlayerId, ["via"] = peer.Transport, ["jit"] = Math.Round(peer.JitterMs, 1) };
                if (peer.RttMs != null) entry["rtt"] = Math.Round(peer.RttMs.Value, 1);
                if (peer.SilenceSeconds != null) entry["idle"] = Math.Round(peer.SilenceSeconds.Value, 1);
                list.Add(entry);
            }
            return new JObject { ["type"] = RosterType, ["you"] = you, ["peers"] = list };
        }

        public static bool TryParseRoster(JObject message, out int you, out List<PeerStatus> peers)
        {
            you = -1; peers = new List<PeerStatus>();
            try
            {
                if (message.Count > 3 || message["you"]?.Type != JTokenType.Integer || !(message["peers"] is JArray array)) return false;
                you = (int)message["you"]!;
                if (you < 0 || array.Count > MaxRosterPeers) return false;
                foreach (JToken token in array)
                {
                    if (!(token is JObject entry) || entry.Count > 6 || entry["id"]?.Type != JTokenType.Integer) return false;
                    int id = (int)entry["id"]!;
                    if (id < 0 || id > 1_000_000) return false;
                    string via = entry["via"]?.Type == JTokenType.String ? (string)entry["via"]! : "";
                    if (via.Length > 16 || via.Any(c => !(char.IsLetterOrDigit(c) || c == ' ' || c == '-'))) return false;
                    if (!TryNumber(entry["jit"], 600_000, out double jitter)) return false;
                    double? rtt = null, idle = null;
                    if (entry["rtt"] != null) { if (!TryNumber(entry["rtt"], 600_000, out double r)) return false; rtt = r; }
                    if (entry["idle"] != null) { if (!TryNumber(entry["idle"], 86_400, out double i)) return false; idle = i; }
                    peers.Add(new PeerStatus(id, via, rtt, jitter, idle));
                }
                return true;
            }
            catch (Exception e) when (e is OverflowException || e is InvalidCastException || e is FormatException)
            { return false; }
        }

        static bool TryNumber(JToken? token, double max, out double value)
        {
            value = 0;
            if (token == null || (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)) return false;
            value = (double)token;
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value <= max;
        }
    }
}
