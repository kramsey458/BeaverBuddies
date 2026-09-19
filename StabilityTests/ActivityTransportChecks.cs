#nullable enable
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using TimberNet;

// Player activity transport: production TimberServer/TimberClient over in-memory streams.
static class ActivityTransportChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static readonly string Id1 = Guid.NewGuid().ToString(), Id2 = Guid.NewGuid().ToString();

    static PlayerActivity State(int player, string name = "Ann", string color = "FF8800", bool cursor = true,
        float x = 1, float y = 2, float z = 3, string selection = "", string editing = "") =>
        new PlayerActivity(player, name, color, cursor, x, y, z, selection, editing);

    static JObject Json(Action<JObject>? mutate = null)
    {
        var json = State(1, selection: Id1, editing: Id2).ToJson();
        mutate?.Invoke(json);
        return json;
    }

    static bool Parses(Action<JObject>? mutate = null) => PlayerActivity.TryParse(Json(mutate), out _);

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Activity JSON round-trips through validation", () =>
        {
            Check(PlayerActivity.TryParse(Json(), out var a) && a != null);
            Check(a!.PlayerId == 1 && a.Name == "Ann" && a.Color == "FF8800" && a.CursorVisible);
            Check(a.X == 1 && a.Y == 2 && a.Z == 3 && a.Selection == Id1 && a.Editing == Id2);
        });
        yield return ("Malformed activity is rejected", () =>
        {
            Check(!Parses(j => j["color"] = "GG0000"), "bad hex");
            Check(!Parses(j => j["color"] = "FF88"), "short color");
            Check(!Parses(j => j["x"] = float.NaN), "NaN");
            Check(!Parses(j => j["y"] = 1e9), "huge coordinate");
            Check(!Parses(j => j["player"] = -1), "negative id");
            Check(!Parses(j => j["player"] = "1"), "string id");
            Check(!Parses(j => j["cursor"] = 1), "int cursor");
            Check(!Parses(j => j["selection"] = "not-a-guid"), "bad selection");
            Check(!Parses(j => j["editing"] = "not-a-guid"), "bad editing");
            Check(!Parses(j => j["name"] = new string('a', 65)), "long name");
            Check(!Parses(j => { for (int i = 0; i < 13; i++) j["k" + i] = i; }), "too many keys");
            Check(!Parses(j => j.Remove("x")), "missing x");
            Check(!Parses(j => j["type"] = "Other"), "wrong type");
        });
        yield return ("Activity names are sanitized", () =>
        {
            Equal("Player", State(0, name: "").Name);
            Equal("Player", State(0, name: "<>\n").Name);
            Equal("bBob", State(0, name: " <b>Bob\n").Name); // angle brackets and control characters removed
            Check(!State(0, name: "<b>hi</b>").Name.Contains('<'));
            Equal(32, State(0, name: new string('x', 100)).Name.Length);
        });
        yield return ("Mailbox keeps only the newest state per player and drops stale ones", () =>
        {
            var box = new ActivityMailbox();
            box.Put(State(1, x: 1), 0); box.Put(State(1, x: 2), 1); box.Put(State(2, x: 9), 1);
            box.Put(State(3, x: 5), -10); // older than the lifetime
            var taken = box.Take(1.5);
            Equal(2, taken.Length);
            Check(taken.Single(s => s.PlayerId == 1).X == 2);
            Equal(0, box.Take(1.5).Length);
        });
        yield return ("Mailbox refuses more than the maximum number of players", () =>
        {
            var box = new ActivityMailbox();
            for (int i = 0; i < PlayerActivity.MaxPlayers + 10; i++) box.Put(State(i), 0);
            Equal(PlayerActivity.MaxPlayers, box.Take(0).Length);
        });
        yield return ("Channel coalesces to the newest state while a write is stalled", () =>
        {
            var gate = new ManualResetEventSlim(); var started = new ManualResetEventSlim();
            var written = new ConcurrentQueue<float>();
            var channel = new ActivityChannel(new ReadStream(Array.Empty<byte>()), (s, m) =>
            {
                written.Enqueue((float)m["x"]!);
                started.Set();
                if (!gate.Wait(3000)) throw new TimeoutException();
            }, (s, e) => throw new Exception(e));
            channel.Post(State(1, x: 0));
            Check(started.Wait(1000));
            for (int i = 1; i <= 500; i++) channel.Post(State(1, x: i));
            gate.Set();
            Check(SpinWait.SpinUntil(() => written.Count == 2, 2000), "writes: " + written.Count);
            Thread.Sleep(100);
            Equal(2, written.Count);
            Check(written.ToArray().SequenceEqual(new float[] { 0, 500 }), string.Join(",", written));
        });
        yield return ("Channel reports a failed write once and stops", () =>
        {
            int failures = 0;
            var channel = new ActivityChannel(new ReadStream(Array.Empty<byte>()),
                (s, m) => throw new IOException("boom"), (s, e) => Interlocked.Increment(ref failures));
            channel.Post(State(1));
            Check(SpinWait.SpinUntil(() => failures == 1, 2000));
            channel.Post(State(1)); Thread.Sleep(100);
            Equal(1, failures);
        });
        yield return ("Host assigns identity, relays to other guests, and never echoes", () =>
        {
            using var s = new Session(2);
            // The guest lies about its id; the host must ignore that.
            s.Guests[0].SendActivity(State(99, "One", "112233", x: 10));
            var atHost = s.WaitForActivity(s.Host, 1);
            Check(atHost.Single().PlayerId == 1 && atHost.Single().X == 10, "host saw " + atHost.Single().PlayerId);
            var atGuest2 = s.WaitForActivity(s.Guests[1], 1);
            Check(atGuest2.Single().PlayerId == 1 && atGuest2.Single().Name == "One" && atGuest2.Single().Color == "112233");
            Thread.Sleep(200);
            Equal(0, s.Guests[0].TakeActivity().Length);

            s.Guests[1].SendActivity(State(1, "Two", x: 20)); // claims to be player 1
            var two = s.WaitForActivity(s.Host, 1).Single();
            Check(two.PlayerId == 2 && two.Name == "Two", "second guest id " + two.PlayerId);
            Check(s.WaitForActivity(s.Guests[0], 1).Single().PlayerId == 2);

            s.Host.SendActivity(State(7, "Host", x: 30));
            Check(s.WaitForActivity(s.Guests[0], 1).Single().PlayerId == 0);
            Check(s.WaitForActivity(s.Guests[1], 1).Single().PlayerId == 0);
        });
        yield return ("Activity never changes the hash, tick progress or replay script", () =>
        {
            using var s = new Session(2);
            // The host sends its init event to every connected guest each time someone joins, so a
            // guest can legitimately hold several. Consume them all before taking the baseline.
            foreach (var g in s.Guests)
            {
                Check(SpinWait.SpinUntil(() => g.HasEventsForTick(0), 2000), "init event never arrived");
                g.ReadEvents(0); Thread.Sleep(150); g.ReadEvents(0);
            }
            int hostHash = s.Host.Hash, guestHash = s.Guests[0].Hash;
            for (int i = 0; i < 200; i++)
            {
                s.Guests[0].SendActivity(State(0, x: i));
                s.Host.SendActivity(State(0, x: i));
            }
            s.WaitForActivity(s.Host, 1); s.WaitForActivity(s.Guests[1], 1);
            Equal(hostHash, s.Host.Hash); Equal(guestHash, s.Guests[0].Hash);
            Equal(0, s.Host.ReadEvents(0).Count); Equal(0, s.Guests[0].ReadEvents(0).Count);
            Check(!s.Host.HasEventsForTick(0) && !s.Guests[0].HasEventsForTick(0));
            Equal(0, s.Host.TicksBehind); Equal(0, s.Guests[0].TicksBehind);
        });
        yield return ("Malformed activity frames from a guest are dropped without ending the session", () =>
        {
            using var s = new Session(1);
            s.Guests[0].SendRaw(new JObject { ["type"] = PlayerActivity.MessageType, ["player"] = "x" });
            s.Guests[0].SendRaw(new JObject { ["type"] = PlayerActivity.MessageType });
            s.Guests[0].SendActivity(State(0, x: 5));
            Check(s.WaitForActivity(s.Host, 1).Single().X == 5);
            Check(!s.Host.IsStopped && !s.Guests[0].IsStopped);
            // Gameplay still works afterwards.
            s.Guests[0].DoUserInitiatedEvent(new JObject { [TimberNetBase.TYPE_KEY] = "Ping", [TimberNetBase.TICKS_KEY] = 0 });
            Check(SpinWait.SpinUntil(() => { s.Host.Update(); return s.Host.HasEventsForTick(0); }, 2000));
        });
        yield return ("Gameplay events keep their order under an activity flood", () =>
        {
            using var s = new Session(2);
            var stop = new ManualResetEventSlim();
            var flood = Task.Run(() => { for (int i = 0; !stop.IsSet; i++) { s.Guests[1].SendActivity(State(0, x: i % 100)); s.Host.SendActivity(State(0, x: i % 100)); Thread.Sleep(0); } });
            try
            {
                for (int i = 0; i < 50; i++)
                    s.Host.DoUserInitiatedEvent(new JObject { [TimberNetBase.TYPE_KEY] = "Seq", [TimberNetBase.TICKS_KEY] = 0, ["n"] = i });
                var received = new List<int>();
                Check(SpinWait.SpinUntil(() =>
                {
                    foreach (var e in s.Guests[0].ReadEvents(0)) if ((string?)e["type"] == "Seq") received.Add((int)e["n"]!);
                    return received.Count == 50;
                }, 4000), "received " + received.Count);
                Check(received.SequenceEqual(Enumerable.Range(0, 50)), "events reordered or lost");
                Check(!s.Guests[0].IsStopped && !s.Host.IsStopped);
            }
            finally { stop.Set(); flood.Wait(); }
        });
        yield return ("A joining guest gets map, state and init frames before any activity", () =>
        {
            using var s = new Session(1);
            // The second guest joins while its init event is held back by the host.
            var initGate = new ManualResetEventSlim();
            s.InitGate = initGate;
            var tap = s.AddGuestWithTap();
            Check(SpinWait.SpinUntil(() => s.InitBlocked, 2000), "join never reached the init event");
            for (int i = 0; i < 20; i++) { s.Guests[0].SendActivity(State(0, x: i)); s.Host.SendActivity(State(0, x: i)); Thread.Sleep(10); }
            Thread.Sleep(150);
            initGate.Set();
            Check(SpinWait.SpinUntil(() => tap.FrameTypes().Contains("InitProbe"), 2000), "init never sent: " + string.Join(",", tap.FrameTypes()));
            s.Host.SendActivity(State(0, x: 99));
            Check(SpinWait.SpinUntil(() => tap.FrameTypes().Contains(PlayerActivity.MessageType), 2000), "no activity after join");
            var types = tap.FrameTypes();
            Equal("<map>", types[0]);
            Check(types.IndexOf(PlayerActivity.MessageType) > types.IndexOf("InitProbe"),
                "activity arrived before the init event: " + string.Join(",", types));
            Check(types.IndexOf(TimberNetBase.SET_STATE_EVENT) < types.IndexOf("InitProbe"), "state after init");
        });
        yield return ("A guest that disconnects stops receiving activity", () =>
        {
            using var s = new Session(2);
            s.Guests[1].Close();
            Check(SpinWait.SpinUntil(() => { s.Host.SendActivity(State(0)); s.Host.DoUserInitiatedEvent(new JObject { [TimberNetBase.TYPE_KEY] = "x", [TimberNetBase.TICKS_KEY] = 0 }); Thread.Sleep(20); return s.Host.ClientCount == 1; }, 3000));
            s.Guests[0].SendActivity(State(0, x: 4));
            Check(s.WaitForActivity(s.Host, 1).Single().PlayerId == 1);
            Check(!s.Host.IsStopped);
        });
    }

    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    sealed class TestGuest : TimberClient
    {
        readonly ISocketStream stream;
        public TestGuest(ISocketStream stream) : base(stream) { this.stream = stream; }
        public void SendRaw(JObject message) => DoUserInitiatedEvent(message);
    }

    // Records every byte the host writes to a guest so the frame order can be checked.
    sealed class TapStream : ISocketStream
    {
        readonly ISocketStream inner; readonly object gate = new();
        readonly List<byte> bytes = new();
        public TapStream(ISocketStream inner) => this.inner = inner;
        public bool Connected => inner.Connected;
        public string? Name => inner.Name;
        public int MaxChunkSize => inner.MaxChunkSize;
        public int MaxBytesPerSecond => inner.MaxBytesPerSecond;
        public Task ConnectAsync() => inner.ConnectAsync();
        public int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public void Close() => inner.Close();
        public void Write(byte[] buffer, int offset, int count)
        {
            lock (gate) bytes.AddRange(buffer.Skip(offset).Take(count));
            inner.Write(buffer, offset, count);
        }
        // The first frame is the raw map; every later frame is a compressed JSON message.
        public List<string> FrameTypes()
        {
            byte[] copy; lock (gate) copy = bytes.ToArray();
            var types = new List<string>(); int i = 0;
            while (i + 4 <= copy.Length)
            {
                int length = (copy[i] << 24) | (copy[i + 1] << 16) | (copy[i + 2] << 8) | copy[i + 3];
                i += 4;
                if (i + length > copy.Length) break;
                if (types.Count == 0) types.Add("<map>");
                else types.Add((string?)JObject.Parse(CompressionUtils.Decompress(copy.Skip(i).Take(length).ToArray()))["type"] ?? "?");
                i += length;
            }
            return types;
        }
    }

    sealed class MultiListener : ISocketListener
    {
        public readonly BlockingCollection<ISocketStream> Pending = new();
        public void Start() { }
        public ISocketStream AcceptClient() => Pending.Take();
        public void Stop() => Pending.CompleteAdding();
    }

    sealed class Session : IDisposable
    {
        public readonly TimberServer Host;
        public readonly List<TestGuest> Guests = new();
        public ManualResetEventSlim? InitGate;
        public volatile bool InitBlocked;
        readonly MultiListener listener = new();
        int mapCalls;

        public Session(int guests)
        {
            Host = new TimberServer(listener, () => { Interlocked.Increment(ref mapCalls); return Task.FromResult(new byte[] { 7, 8, 9 }); }, () =>
            {
                var gate = InitGate;
                if (gate != null) { InitBlocked = true; gate.Wait(3000); }
                return new JObject { [TimberNetBase.TYPE_KEY] = "InitProbe", [TimberNetBase.TICKS_KEY] = 0 };
            });
            Host.Start();
            for (int i = 0; i < guests; i++) AddGuest(null);
        }

        public TapStream AddGuestWithTap()
        {
            TapStream? tap = null;
            AddGuest(host => tap = new TapStream(host), wait: false);
            return tap!;
        }

        TestGuest AddGuest(Func<ISocketStream, TapStream>? tapFactory, bool wait = true)
        {
            var (hostSide, guestSide) = PipeStreamFactory.Pair();
            listener.Pending.Add(tapFactory != null ? tapFactory(hostSide) : hostSide);
            var guest = new TestGuest(guestSide);
            bool mapReceived = false;
            guest.OnMapReceived += _ => mapReceived = true;
            guest.Start();
            Guests.Add(guest);
            if (wait) Check(SpinWait.SpinUntil(() => { Host.Update(); guest.Update(); return mapReceived; }, 3000), "guest never received the map");
            // The host opens the activity lane once the join has finished.
            if (wait) Thread.Sleep(50);
            return guest;
        }

        public PlayerActivity[] WaitForActivity(TimberNetBase net, int count)
        {
            var got = new List<PlayerActivity>();
            Check(SpinWait.SpinUntil(() => { got.AddRange(net.TakeActivity()); return got.Count >= count; }, 3000),
                "activity never arrived");
            return got.ToArray();
        }

        public void Dispose()
        {
            foreach (var g in Guests) g.Close();
            Host.Close();
        }
    }
}

// Same in-memory pipe as the production-transport tests, exposed as a factory.
static class PipeStreamFactory
{
    public static (ISocketStream Host, ISocketStream Guest) Pair()
    {
        var (a, b) = PipeStream.Pair();
        return (a, b);
    }
}
