using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using TimberNet;

static class ActivityChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static PlayerActivity State(int id = 0, float x = 10) => new(id, "Player", "12ABEF", true, x, 20, 30, Guid.Empty.ToString());
    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Activity validates roundtrip, strips name markup and bounds text", () =>
        {
            var state = new PlayerActivity(3, "<b>Alex</b>\n" + new string('x', 80), "123456", true, 1, 2, 3);
            Check(PlayerActivity.TryParse(state.ToJson(), out var parsed));
            Check(parsed.Name.Length == 32 && !parsed.Name.Contains('<') && !parsed.Name.Contains('\n'));
            Check(parsed.PlayerId == 3 && parsed.X == 1 && parsed.CursorVisible);
        });
        yield return ("Malformed activity is ignored without admitting nonfinite coordinates or invalid entities", () =>
        {
            foreach (var pair in new (string Key, JToken Value)[] { ("x", float.NaN), ("z", float.PositiveInfinity),
                ("y", 100001), ("color", "oops"), ("selection", "missing"), ("editing", "missing"),
                ("name", new string('x', 65)), ("player", -1), ("cursor", "true"), ("x", new JObject()) })
            {
                var message = State().ToJson(); message[pair.Key] = pair.Value;
                Check(!PlayerActivity.TryParse(message, out _), pair.Key);
            }
        });
        yield return ("Activity inbox retains latest per player and bounds distinct players", () =>
        {
            var inbox = new ActivityMailbox();
            Parallel.For(0, 10000, i => inbox.Put(State(1, i), 0));
            inbox.Put(State(1, 99999), 0);
            var latest = inbox.Take(1); Check(latest.Length == 1 && latest[0].X == 99999);
            for (int i = 0; i < 5000; i++) inbox.Put(State(i), 0);
            Check(inbox.Take(1).Length == PlayerActivity.MaxPlayers);
            Check(inbox.Take(1).Length == 0);
        });
        yield return ("Activity inbox discards stale loading-screen state and clears on reset", () =>
        {
            var inbox = new ActivityMailbox(); inbox.Put(State(1), 0); inbox.Put(State(2), 2);
            Check(inbox.Take(4).Single().PlayerId == 2);
            inbox.Put(State(), 5); inbox.Clear(); Check(inbox.Take(5).Length == 0);
        });
        yield return ("Cursor flood coalesces while reliable commands retain FIFO and priority", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var sent = new ConcurrentQueue<string>();
            var sender = new OrderedSender(text => { if (text == "blocked") { entered.Set(); release.Wait(2500); } sent.Enqueue(text); }, _ => { });
            try
            {
                sender.Enqueue("blocked"); Check(entered.Wait(1000));
                for (int i = 0; i < 10000; i++) sender.EnqueueLatest(1, "cursor" + i);
                sender.Enqueue("command1"); sender.Enqueue("command2");
                release.Set(); Check(SpinWait.SpinUntil(() => sent.Count == 4, 2000));
                Check(sent.SequenceEqual(new[] { "blocked", "command1", "command2", "cursor9999" }));
                Check(sender.Drain().IsCompletedSuccessfully);
            }
            finally { release.Set(); sender.Stop(); }
        });
        yield return ("Disposable budget cannot disconnect or consume reliable queue allowance", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            int failures = 0; var sent = new ConcurrentQueue<string>();
            var sender = new OrderedSender(text => { if (text == "held") { entered.Set(); release.Wait(2500); } sent.Enqueue(text); }, _ => failures++, 8, 2);
            try
            {
                sender.Enqueue("held"); Check(entered.Wait(1000));
                for (int i = 0; i < 10000; i++) sender.EnqueueLatest(i, "p");
                sender.EnqueueLatest(0, new string('x', 4097));
                sender.Enqueue("next"); release.Set();
                Check(SpinWait.SpinUntil(() => sent.Count == 66, 2000)); Check(failures == 0);
                Check(sent.Take(2).SequenceEqual(new[] { "held", "next" }));
                sender.Stop(); sender.EnqueueLatest(1, "ignored");
            }
            finally { release.Set(); sender.Stop(); }
        });
        yield return ("TCP host relays activity between guests while paused without echo, spoofing or hash changes", () =>
        {
            var listener = new LocalListener();
            var host = new TimberServer(listener, () => Task.FromResult(new byte[] { 1, 2 }), null);
            TimberClient one = null, two = null;
            try
            {
                host.Start(); one = new TimberClient(new TCPClientWrapper("127.0.0.1", listener.Port));
                two = new TimberClient(new TCPClientWrapper("127.0.0.1", listener.Port));
                int maps = 0; one.OnMapReceived += _ => maps++; two.OnMapReceived += _ => maps++;
                var h = new List<PlayerActivity>(); var a = new List<PlayerActivity>(); var b = new List<PlayerActivity>();
                int callbackThread = 0, currentThread = Environment.CurrentManagedThreadId;
                host.OnActivity += s => { h.Add(s); callbackThread = Environment.CurrentManagedThreadId; };
                one.OnActivity += a.Add; two.OnActivity += b.Add;
                one.Start(); two.Start();
                void Pump() { host.Update(); one.Update(); two.Update(); }
                void Until(Func<bool> condition) { Check(SpinWait.SpinUntil(() => { Pump(); return condition(); }, 2500), "loopback timeout"); }
                Until(() => maps == 2 && host.ClientCount == 2);
                // SetState is the last reliable join message; consume it before comparing hashes.
                Until(() => { one.ReadEvents(0); two.ReadEvents(0); return one.Hash == host.Hash && two.Hash == host.Hash; });
                int hash = host.Hash;
                one.SendControl(State(999).ToJson()); // Exercise the host's identity check, not just the normal client API.
                Until(() => h.Count > 0 && b.Count > 0);
                Check(a.Count == 0 && h[0].PlayerId > 0 && h[0].PlayerId != 999 && h[0].PlayerId == b[0].PlayerId);
                Check(callbackThread == currentThread);
                two.SendActivity(State(999, 55)); Until(() => a.Count > 0);
                Check(a[0].PlayerId != h[0].PlayerId, "duplicate names/claimed IDs merged two players");
                host.SendActivity(State(987, 88)); Until(() => a.Any(s => s.PlayerId == 0) && b.Any(s => s.PlayerId == 0));
                Check(host.Hash == hash && one.Hash == hash && two.Hash == hash);
                Check(host.TickCount == 0 && one.TickCount == 0 && two.TickCount == 0);
                Check(host.ReadEvents(0).Count == 0 && one.ReadEvents(0).Count == 0 && two.ReadEvents(0).Count == 0);
                host.SendControl(TimberNetBase.Control("ResyncPrepare")); string control = null;
                one.OnControl += (_, m) => control = (string)m["command"];
                Until(() => control != null); Check(control == "ResyncPrepare");
            }
            finally { one?.Close(); two?.Close(); host.Close(); }
        });
    }
    sealed class LocalListener : ISocketListener
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public void Start() => listener.Start();
        public ISocketStream AcceptClient() => new TCPClientWrapper(listener.AcceptTcpClient());
        public void Stop() => listener.Stop();
    }
}
