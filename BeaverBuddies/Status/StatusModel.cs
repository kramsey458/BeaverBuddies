using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TimberNet;

namespace BeaverBuddies.Status
{
    // Presentation-only measurements. No tick requests or simulation state changes.
    public sealed class TickRateMeter
    {
        int startTick;
        double startTime;
        bool started, active;
        public double? Rate { get; private set; }
        public void Observe(int tick, bool running, double now)
        {
            if (!started || running != active || tick < startTick || now < startTime)
            { started = true; active = running; startTick = tick; startTime = now; Rate = running ? (double?)null : 0; return; }
            if (!running) { Rate = 0; startTick = tick; startTime = now; return; }
            if (now - startTime >= 2)
            { Rate = Math.Max(0, (tick - startTick) / (now - startTime)); startTime = now; startTick = tick; }
        }
    }
    public sealed class StatusFacts
    {
        public bool Host, Connected, Loaded, Paused, Failed;
        public string Transport = "Direct IP";
        public int Tick, BufferedTicks, ReceivedEvents, QueuedMessages, ConnectedPeers;
        public long QueuedBytes;
        public double? Rate;
        public List<PeerStatus> Peers = new List<PeerStatus>();
    }
    public sealed class StatusText
    {
        public string State, Hint, Connection, Latency, Simulation, Outgoing, Incoming;
        public bool Warning;
        public string[] Peers;
        public static StatusText From(StatusFacts f)
        {
            var fresh = f.Peers.Where(p => p.Fresh).ToArray();
            var result = new StatusText {
                State = "Running", Hint = "No backlog warning.",
                Connection = f.Host ? $"Host · {f.ConnectedPeers} guest{(f.ConnectedPeers == 1 ? "" : "s")}" : "Guest · " + f.Transport,
                Latency = fresh.Length == 0 ? "Measuring…" : Math.Round(fresh.Max(p => p.RoundTripMilliseconds ?? 0)).ToString(CultureInfo.InvariantCulture) + " ms" + (fresh.Length > 1 ? " worst" : ""),
                Simulation = (f.Rate.HasValue ? f.Rate.Value.ToString("0.0", CultureInfo.InvariantCulture) : "—") + " ticks/s · tick " + f.Tick.ToString("N0", CultureInfo.InvariantCulture),
                Outgoing = Size(f.QueuedBytes) + " · " + f.QueuedMessages + " events",
                Incoming = f.Host ? f.ReceivedEvents + " events" : f.BufferedTicks + " ticks buffered · " + f.ReceivedEvents + " events",
                Peers = f.Peers.Take(8).Select(p => DescribePeer(f.Host, p)).ToArray()
            };
            if (f.Failed) { result.State = "Session stopped"; result.Hint = "Reload or rehost before continuing."; result.Warning = true; }
            else if (!f.Connected) { result.State = "Disconnected"; result.Hint = "No active multiplayer connection."; result.Warning = true; }
            else if (!f.Loaded) { result.State = "Loading"; result.Hint = "Waiting for the map to finish loading."; }
            else if (f.Host && f.ConnectedPeers == 0) { result.State = "No guests connected"; result.Hint = "No remote player is connected."; }
            else if (f.Peers.Any(p => !p.Fresh)) { result.State = "Waiting for response"; result.Hint = "Peer may be loading, busy, or delayed by the connection."; result.Warning = true; }
            else if (f.QueuedBytes >= 256 * 1024 || fresh.Any(p => p.RoundTripMilliseconds >= 250))
            { result.State = "Connection delayed"; result.Hint = "Response or outgoing queue is high; includes peer processing time."; result.Warning = true; }
            else if (f.Paused) { result.State = "Paused"; result.Hint = "Simulation is paused. Connection checks continue."; }
            else if (!f.Host && f.BufferedTicks >= 5)
            { result.State = "Catching up"; result.Hint = "Host ticks have arrived; this computer is processing the backlog."; result.Warning = true; }
            else if (f.Host && fresh.Any(p => p.Simulation?.Loaded == true && p.LocalTickAtReply - p.Simulation.Tick >=
                Math.Max(10, Math.Ceiling(Math.Max(f.Rate ?? 0, p.Simulation.Rate) * (.5 + (p.RoundTripMilliseconds ?? 0) / 1000)))))
            { result.State = "Guest behind"; result.Hint = "A guest is behind in the latest sample; compare its tick rate below."; result.Warning = true; }
            else if (!f.Host && fresh.Any(p => p.Simulation?.Loaded == true && !p.Simulation.Paused && p.Simulation.Rate == 0))
            { result.State = "Host not advancing"; result.Hint = "Host replied, but reported no simulation progress in its last sample."; result.Warning = true; }
            if (!f.Connected || f.Failed)
            { result.Latency = "—"; result.Simulation = "—"; result.Outgoing = "—"; result.Incoming = "—"; result.Peers = Array.Empty<string>(); }
            else if (f.Host && f.ConnectedPeers == 0) result.Latency = "—";
            if (result.Peers.Length == 8 && f.Peers.Count > 8)
                result.Peers = result.Peers.Concat(new[] { "+ " + (f.Peers.Count - 8) + " more peers; response summary includes them" }).ToArray();
            return result;
        }
        static string DescribePeer(bool host, PeerStatus peer)
        {
            if (!peer.Fresh) return peer.Label + " · awaiting response";
            var state = peer.Simulation;
            if (!state.Loaded) return peer.Label + " · loading";
            int behind = host ? Math.Max(0, peer.LocalTickAtReply - state.Tick) : Math.Max(0, state.Tick - peer.LocalTickAtReply);
            return peer.Label + " · " + Math.Round(peer.RoundTripMilliseconds ?? 0).ToString(CultureInfo.InvariantCulture) + " ms · " +
                (state.Paused ? "paused" : state.Rate < 0 ? "measuring…" : state.Rate.ToString("0.0", CultureInfo.InvariantCulture) + " ticks/s") +
                "\n" + (host ? "Guest" : "You") + " ~" + behind + " ticks behind at reply" +
                (peer.QueuedBytes > 0 ? " · " + Size(peer.QueuedBytes) + " queued" : "");
        }
        static string Size(long bytes) => bytes < 1024 ? bytes + " B" : (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KiB";
    }
}
