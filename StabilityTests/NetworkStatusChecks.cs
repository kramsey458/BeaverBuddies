#nullable enable
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using TimberNet;

// The connection status feed: RTT measurement, roster frames, and real host/guest sessions.
static class NetworkStatusChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");
    static bool Near(double expected, double? actual, double tolerance) => actual != null && Math.Abs(expected - actual.Value) <= tolerance;

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("RTT tracker smooths samples and reports jitter", () =>
        {
            var t = new RttTracker(0);
            int a = t.BeginProbe(1000); t.OnReply(a, 1042);
            var first = t.Snapshot(1, "Direct", 1042);
            Check(Near(42, first.RttMs, .001) && first.JitterMs == 0, "first sample should be taken as-is");
            int b = t.BeginProbe(2000); t.OnReply(b, 2060);
            var second = t.Snapshot(1, "Direct", 2060);
            Check(Near(42 * .7 + 60 * .3, second.RttMs, .001), "smoothed value " + second.RttMs);
            Check(Near(18 * .3, second.JitterMs, .001), "jitter " + second.JitterMs);
            Equal(1, second.PlayerId); Equal("Direct", second.Transport);
        });
        yield return ("RTT tracker ignores unknown, duplicate and expired replies", () =>
        {
            var t = new RttTracker(0);
            t.OnReply(99, 10);                                   // never sent
            Check(t.Snapshot(1, "", 10).RttMs == null);
            int a = t.BeginProbe(100); t.OnReply(a, 130); t.OnReply(a, 5000);   // duplicate must not skew it
            Check(Near(30, t.Snapshot(1, "", 5000).RttMs, .001), "duplicate reply changed the result");
            // A peer that never answers cannot grow the tracker without bound: old probes are forgotten.
            var quiet = new RttTracker(0);
            int first = quiet.BeginProbe(0);
            for (int i = 0; i < 50; i++) quiet.BeginProbe(i);
            quiet.OnReply(first, 999);                           // long since evicted
            Check(quiet.Snapshot(1, "", 999).RttMs == null, "an evicted probe still produced a sample");
        });
        yield return ("Silence is measured from the last reply, or from connecting if there never was one", () =>
        {
            var t = new RttTracker(1000);
            Check(Near(4, t.Snapshot(1, "", 5000).SilenceSeconds, .001), "silence before any reply");
            int a = t.BeginProbe(5000); t.OnReply(a, 5050);
            Check(Near(2, t.Snapshot(1, "", 7050).SilenceSeconds, .001), "silence after a reply");
        });
        yield return ("Roster frames round-trip and reject anything malformed", () =>
        {
            var peers = new[] { new PeerStatus(1, "Direct", 42.4, 3.1, .5), new PeerStatus(2, "Steam", null, 0, null) };
            Check(StatusFrames.TryParseRoster(StatusFrames.Roster(2, peers), out int you, out var parsed));
            Equal(2, you); Equal(2, parsed.Count);
            Check(Near(42.4, parsed[0].RttMs, .01) && parsed[0].Transport == "Direct" && parsed[1].RttMs == null && parsed[1].Transport == "Steam");

            JObject Good() => StatusFrames.Roster(1, peers);
            bool Parses(Action<JObject> mutate) { var j = Good(); mutate(j); return StatusFrames.TryParseRoster(j, out _, out _); }
            Check(!Parses(j => j["you"] = -1), "negative id");
            Check(!Parses(j => j["you"] = "1"), "string id");
            Check(!Parses(j => j["peers"] = "nope"), "peers not an array");
            Check(!Parses(j => j["extra"] = 1), "extra key");
            Check(!Parses(j => ((JObject)j["peers"]![0]!)["rtt"] = -5), "negative rtt");
            Check(!Parses(j => ((JObject)j["peers"]![0]!)["rtt"] = 1e12), "huge rtt");
            Check(!Parses(j => ((JObject)j["peers"]![0]!)["rtt"] = "fast"), "non-numeric rtt");
            Check(!Parses(j => ((JObject)j["peers"]![0]!)["via"] = "<b>bold</b>"), "markup in transport name");
            Check(!Parses(j => ((JObject)j["peers"]![0]!)["via"] = new string('x', 40)), "long transport name");
            Check(!Parses(j => ((JObject)j["peers"]![0]!)["id"] = -3), "negative peer id");
            var crowd = Enumerable.Range(0, StatusFrames.MaxRosterPeers + 5).Select(i => new PeerStatus(i, "", 1, 0, 0));
            Check(StatusFrames.TryParseRoster(StatusFrames.Roster(1, crowd), out _, out var capped) && capped.Count == StatusFrames.MaxRosterPeers, "roster was not capped when built");
        });
        yield return ("Probe frames validate their sequence number", () =>
        {
            Check(StatusFrames.TryParseSequence(StatusFrames.Probe(7), out int seq) && seq == 7);
            Check(!StatusFrames.TryParseSequence(new JObject { ["type"] = "NetProbe", ["seq"] = -1 }, out _));
            Check(!StatusFrames.TryParseSequence(new JObject { ["type"] = "NetProbe", ["seq"] = "x" }, out _));
            Check(!StatusFrames.TryParseSequence(new JObject { ["type"] = "NetProbe" }, out _));
            Check(!StatusFrames.TryParseSequence(new JObject { ["type"] = "NetProbe", ["seq"] = 1, ["a"] = 1 }, out _));
        });
        yield return ("The host measures each guest's ping, and a slow guest reads slower", () =>
        {
            using var rig = new Rig(guestReplyDelays: new[] { 0, 40 });
            var status = rig.WaitFor(() => rig.Host.GetNetworkStatus(), s => s.Peers.Count == 2 && s.Peers.All(p => p.RttMs != null), "pings never appeared");
            Check(status.IsHost && status.YourPlayerId == 0 && status.HostSilenceSeconds == null);
            var fast = status.Peers.Single(p => p.PlayerId == 1); var slow = status.Peers.Single(p => p.PlayerId == 2);
            Check(slow.RttMs > 60, $"a guest whose replies are about 80 ms late measured {slow.RttMs} ms");
            Check(slow.RttMs > fast.RttMs + 40, $"slow {slow.RttMs} vs fast {fast.RttMs}");
            Check(fast.RttMs < 60, $"a prompt guest measured {fast.RttMs} ms");
            Equal("Direct", fast.Transport);
            Check(fast.SilenceSeconds < 1, "a live guest should not be silent");
        });
        yield return ("Every guest receives the roster with their own id and everyone's ping", () =>
        {
            using var rig = new Rig(guestReplyDelays: new[] { 0, 0 });
            for (int i = 0; i < 2; i++)
            {
                var guest = rig.Guests[i];
                var status = rig.WaitFor(() => guest.GetNetworkStatus(), s => s.YourPlayerId >= 0 && s.Peers.Count == 2 && s.Peers.All(p => p.RttMs != null), "guest never got a complete roster");
                Check(!status.IsHost && status.YourPlayerId == i + 1, $"guest {i} thinks it is player {status.YourPlayerId}");
                Check(status.HostSilenceSeconds < 1.5, "the host feed should be fresh: " + status.HostSilenceSeconds);
            }
        });
        yield return ("Status traffic never changes the hash, the event script or tick progress", () =>
        {
            using var rig = new Rig(guestReplyDelays: new[] { 0 });
            foreach (var g in rig.Guests) { Check(SpinWait.SpinUntil(() => g.HasEventsForTick(0), 2000)); g.ReadEvents(0); }
            int hostHash = rig.Host.Hash, guestHash = rig.Guests[0].Hash;
            rig.WaitFor(() => rig.Host.GetNetworkStatus(), s => s.Peers.Count == 1 && s.Peers[0].RttMs != null, "no ping");
            rig.Run(500);                                       // many more probe/reply/roster rounds
            Equal(hostHash, rig.Host.Hash); Equal(guestHash, rig.Guests[0].Hash);
            Equal(0, rig.Host.ReadEvents(0).Count); Equal(0, rig.Guests[0].ReadEvents(0).Count);
            Check(!rig.Host.HasEventsForTick(0) && !rig.Guests[0].HasEventsForTick(0));
            Equal(0, rig.Host.TicksBehind); Equal(0, rig.Guests[0].TicksBehind);
        });
        yield return ("Malformed or misdirected status frames are ignored without ending the session", () =>
        {
            using var rig = new Rig(guestReplyDelays: new[] { 0 });
            var guest = rig.Guests[0];
            guest.SendRaw(new JObject { ["type"] = StatusFrames.ReplyType, ["seq"] = "x" });
            guest.SendRaw(new JObject { ["type"] = StatusFrames.ReplyType });
            guest.SendRaw(new JObject { ["type"] = StatusFrames.ReplyType, ["seq"] = 999999 });      // never sent
            guest.SendRaw(new JObject { ["type"] = StatusFrames.RosterType, ["you"] = -9, ["peers"] = 3 });
            guest.SendRaw(new JObject { ["type"] = StatusFrames.ProbeType, ["seq"] = 1 });           // a guest cannot probe the host
            rig.Run(200);
            Check(!rig.Host.IsStopped && !guest.IsStopped, "a bad status frame ended the session");
            // Real measurements still work afterwards, and gameplay still flows.
            rig.WaitFor(() => rig.Host.GetNetworkStatus(), s => s.Peers.Count == 1 && s.Peers[0].RttMs != null, "measurement stopped working");
            guest.DoUserInitiatedEvent(new JObject { [TimberNetBase.TYPE_KEY] = "Ping", [TimberNetBase.TICKS_KEY] = 0 });
            Check(SpinWait.SpinUntil(() => { rig.Host.Update(); return rig.Host.HasEventsForTick(0); }, 2000), "gameplay stopped after bad status frames");
        });
        yield return ("A guest that disconnects disappears from the host's roster", () =>
        {
            using var rig = new Rig(guestReplyDelays: new[] { 0, 0 });
            rig.WaitFor(() => rig.Host.GetNetworkStatus(), s => s.Peers.Count == 2, "guests never appeared");
            rig.Guests[1].Close();
            var status = rig.WaitFor(() =>
            {
                rig.Host.DoUserInitiatedEvent(new JObject { [TimberNetBase.TYPE_KEY] = "x", [TimberNetBase.TICKS_KEY] = 0 });   // lets the host notice the drop
                return rig.Host.GetNetworkStatus();
            }, s => s.Peers.Count == 1, "the departed guest stayed in the roster");
            Equal(1, status.Peers[0].PlayerId);
            Check(!rig.Host.IsStopped);
        });
        yield return ("A joining guest gets its save, state and init frames before any status frame, and a silent guest shows as silent", () =>
        {
            using var rig = new Rig(guestReplyDelays: new int[0]);
            var raw = rig.AddRecordingGuest();                 // records everything, answers nothing
            Check(SpinWait.SpinUntil(() => { rig.Pump(); Thread.Sleep(2); return raw.FrameTypes().Any(t => t == StatusFrames.ProbeType); }, 3000),
                "no probe ever reached the guest: " + string.Join(",", raw.FrameTypes()));
            var types = raw.FrameTypes();
            Equal("<map>", types[0]);
            int init = types.IndexOf("InitProbe"), state = types.IndexOf(TimberNetBase.SET_STATE_EVENT), firstStatus = types.FindIndex(StatusFrames.IsStatusType);
            Check(state > 0 && init > state, "join frames out of order: " + string.Join(",", types));
            Check(firstStatus > init, "a status frame arrived before the join finished: " + string.Join(",", types));
            // It never replies, so it has no ping and its silence grows.
            rig.Run(300);
            var peer = rig.Host.GetNetworkStatus().Peers.Single();
            Check(peer.RttMs == null, "a guest that never replies must not have a ping");
            Check(peer.SilenceSeconds > .25, "silence " + peer.SilenceSeconds);
        });
    }

    // A guest stream whose writes are delayed, standing in for a slow return path.
    sealed class DelayedWriteStream : ISocketStream
    {
        readonly ISocketStream inner; readonly int delayMs;
        public DelayedWriteStream(ISocketStream inner, int delayMs) { this.inner = inner; this.delayMs = delayMs; }
        public bool Connected => inner.Connected;
        public string? Name => inner.Name;
        public int MaxChunkSize => inner.MaxChunkSize;
        public int MaxBytesPerSecond => inner.MaxBytesPerSecond;
        public Task ConnectAsync() => inner.ConnectAsync();
        public int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public void Close() => inner.Close();
        public void Write(byte[] buffer, int offset, int count)
        {
            if (delayMs > 0) Thread.Sleep(delayMs);
            inner.Write(buffer, offset, count);
        }
    }

    // The host's view of a guest connection, labelled like a real TCP one.
    sealed class LabelledStream : ISocketStream, ITransportInfo
    {
        readonly ISocketStream inner;
        public LabelledStream(ISocketStream inner) => this.inner = inner;
        public string TransportName => "Direct";
        public bool Connected => inner.Connected;
        public string? Name => inner.Name;
        public int MaxChunkSize => inner.MaxChunkSize;
        public int MaxBytesPerSecond => inner.MaxBytesPerSecond;
        public Task ConnectAsync() => inner.ConnectAsync();
        public int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public void Close() => inner.Close();
        public void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }

    sealed class TestGuest : TimberClient
    {
        public TestGuest(ISocketStream stream) : base(stream) { }
        public void SendRaw(JObject message) => DoUserInitiatedEvent(message);
    }

    sealed class MultiListener : ISocketListener
    {
        public readonly BlockingCollection<ISocketStream> Pending = new();
        public void Start() { }
        public ISocketStream AcceptClient() => Pending.Take();
        public void Stop() => Pending.CompleteAdding();
    }

    // A guest that only records the frames the host writes to it.
    sealed class RecordingGuest
    {
        readonly List<byte> received = new();
        public RecordingGuest(ISocketStream stream)
        {
            Task.Run(() => { var one = new byte[1]; try { while (stream.Read(one, 0, 1) == 1) lock (received) received.Add(one[0]); } catch (IOException) { } });
        }
        // The first frame is the raw map; every later one is compressed JSON.
        public List<string> FrameTypes()
        {
            byte[] copy; lock (received) copy = received.ToArray();
            var types = new List<string>(); int i = 0;
            while (i + 4 <= copy.Length)
            {
                int length = (copy[i] << 24) | (copy[i + 1] << 16) | (copy[i + 2] << 8) | copy[i + 3];
                i += 4; if (i + length > copy.Length) break;
                if (types.Count == 0) types.Add("<map>");
                else
                {
                    string type = "?";
                    try { type = (string?)JObject.Parse(CompressionUtils.Decompress(copy.Skip(i).Take(length).ToArray()))["type"] ?? "?"; } catch { }
                    types.Add(type);
                }
                i += length;
            }
            return types;
        }
    }

    sealed class Rig : IDisposable
    {
        public readonly TimberServer Host;
        public readonly List<TestGuest> Guests = new();
        readonly MultiListener listener = new();
        readonly int previousInterval = TimberServer.StatusIntervalMs;

        public Rig(int[] guestReplyDelays)
        {
            TimberServer.StatusIntervalMs = 100;
            Host = new TimberServer(listener, () => Task.FromResult(new byte[] { 1, 2, 3 }),
                () => new JObject { [TimberNetBase.TYPE_KEY] = "InitProbe", [TimberNetBase.TICKS_KEY] = 0 });
            Host.Start();
            foreach (int delay in guestReplyDelays)
            {
                var (hostSide, guestSide) = PipeStream.Pair();
                listener.Pending.Add(new LabelledStream(hostSide));
                var guest = new TestGuest(new DelayedWriteStream(guestSide, delay));
                bool mapped = false; guest.OnMapReceived += _ => mapped = true;
                guest.Start();
                Guests.Add(guest);
                Check(SpinWait.SpinUntil(() => { Pump(); return mapped; }, 3000), "guest never received the map");
            }
        }

        // The game calls Update on the game thread every frame. Doing it here, on one thread, keeps the
        // test faithful; a background pump would race the test's own reads.
        public void Pump() { Host.Update(); foreach (var g in Guests) g.Update(); }
        public void Run(int milliseconds) => SpinWait.SpinUntil(() => { Pump(); Thread.Sleep(2); return false; }, milliseconds);

        public RecordingGuest AddRecordingGuest()
        {
            var (hostSide, guestSide) = PipeStream.Pair();
            listener.Pending.Add(new LabelledStream(hostSide));
            return new RecordingGuest(guestSide);
        }

        public T WaitFor<T>(Func<T> read, Func<T, bool> done, string failure)
        {
            T value = read();
            Check(SpinWait.SpinUntil(() => { Pump(); Thread.Sleep(2); value = read(); return done(value); }, 4000), failure + " (last: " + Describe(value) + ")");
            return value;
        }
        static string Describe<T>(T value) => value is NetworkStatus s
            ? $"host={s.IsHost} id={s.YourPlayerId} peers=[{string.Join(", ", s.Peers.Select(p => $"{p.PlayerId}:{p.RttMs?.ToString("0") ?? "?"}ms"))}]"
            : value?.ToString() ?? "null";

        public void Dispose()
        {
            TimberServer.StatusIntervalMs = previousInterval;
            foreach (var g in Guests) g.Close();
            Host.Close();
        }
    }
}
