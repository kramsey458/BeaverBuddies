using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using BeaverBuddies.Steam;
using Newtonsoft.Json.Linq;
using Steamworks;
using TimberNet;

int failures = 0;
var tests = new (string Name, Action Run)[]
{
    ("Steam preserves the tail across three partial reads", () =>
    {
        using var s = new SocketScope();
        s.Socket.ReceiveData(new byte[] {1,2,3,4,5,6});
        s.Socket.ReceiveData(new byte[] {7,8});
        var bytes = new byte[8];
        Equal(2, s.Socket.Read(bytes, 0, 2));
        Equal(2, s.Socket.Read(bytes, 2, 2));
        Equal(2, s.Socket.Read(bytes, 4, 2));
        Equal(2, s.Socket.Read(bytes, 6, 2));
        Check(bytes.SequenceEqual(new byte[] {1,2,3,4,5,6,7,8}));
    }),
    ("Steam reads a header and body from the same packet", () =>
    {
        using var s = new SocketScope();
        s.Socket.ReceiveData(new byte[] {0,0,0,3,9,8,7});
        s.Socket.ReceiveData(new byte[] {6,5,4}); // original bug incorrectly consumes this
        ISocketStream stream = s.Socket;
        Check(stream.ReadUntilComplete(4).SequenceEqual(new byte[] {0,0,0,3}));
        Check(stream.ReadUntilComplete(3).SequenceEqual(new byte[] {9,8,7}));
    }),
    ("Steam zero-length reads do not consume a packet", () =>
    {
        using var s = new SocketScope(); s.Socket.ReceiveData(new byte[] {9});
        Equal(0, s.Socket.Read(new byte[1], 0, 0));
        var read = Task.Run(() => ((ISocketStream)s.Socket).ReadUntilComplete(1));
        Check(read.Wait(1000), "read stalled after a zero-length read"); Equal((byte)9, read.Result[0]);
    }),
    ("Steam close wakes a blocked reader with EOF", () =>
    {
        using var s = new SocketScope();
        using var started = new ManualResetEventSlim();
        var read = Task.Run(() => { started.Set(); return s.Socket.Read(new byte[1], 0, 1); });
        started.Wait(); s.Socket.Close();
        Check(read.Wait(1000), "blocked reader survived close"); Equal(0, read.Result);
    }),
    ("Steam rejects a failed reliable send", () =>
    {
        using var s = new SocketScope(); SteamNetworking.SendSucceeds = false;
        try { Throws<IOException>(() => s.Socket.Write(new byte[1], 0, 1)); }
        finally { SteamNetworking.SendSucceeds = true; }
    }),
    ("Steam rejects writes after close", () =>
    {
        using var s = new SocketScope(); s.Socket.Close();
        Throws<IOException>(() => s.Socket.Write(new byte[1], 0, 1));
    }),
    ("Closing an old Steam socket preserves its replacement", () =>
    {
        var listener = new SteamPacketListener();
        var first = new SteamSocket(new CSteamID(1), true);
        var second = new SteamSocket(new CSteamID(1), true);
        first.RegisterSteamPacketListener(listener); second.RegisterSteamPacketListener(listener);
        first.Close();
        var sockets = (Dictionary<CSteamID, SteamSocket>)typeof(SteamPacketListener)
            .GetField("sockets", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(listener);
        Check(sockets.TryGetValue(new CSteamID(1), out var current) && ReferenceEquals(current, second));
        second.Close();
    }),
    ("Concurrent sends keep length and payload together", () =>
    {
        var net = new TestNet(); var stream = new RecordingStream();
        var first = Task.Run(() => net.Send(stream, new byte[] {1,1,1,1}));
        Check(stream.HeaderWritten.Wait(1000));
        using var secondStarted = new ManualResetEventSlim();
        var second = Task.Run(() => { secondStarted.Set(); net.Send(stream, new byte[] {2,2,2,2}); });
        secondStarted.Wait();
        // Hold the first header long enough for a competing writer to run.
        Thread.Sleep(100); stream.Continue.Set();
        Check(Task.WaitAll(new[] {first, second}, 2000));
        Check(stream.Bytes.SequenceEqual(new byte[] {0,0,0,4,1,1,1,1,0,0,0,4,2,2,2,2}));
    }),
    ("Failed client writes stop ticking and notify only on Update", () =>
    {
        var stream = new ReadStream(Array.Empty<byte>()) { ThrowOnWrite = true };
        var net = new TimberClient(stream); int errors = 0; int callbackThread = 0;
        net.OnError += _ => { errors++; callbackThread = Environment.CurrentManagedThreadId; };
        Task.Run(() => net.DoUserInitiatedEvent(Message())).GetAwaiter().GetResult();
        Check(net.IsStopped); Check(!stream.Connected); Equal(0, errors);
        net.Update(); Equal(1, errors); Equal(Environment.CurrentManagedThreadId, callbackThread);
        Check(!net.ShouldTick); net.Update(); Equal(1, errors);
    }),
    ("Truncated client payload reports failure instead of hanging silently", () =>
    {
        var stream = new ReadStream(new byte[] {0,0,0,4,1}); var net = new TimberClient(stream);
        int errors = 0; net.OnError += _ => errors++;
        net.Start(); Check(SpinWait.SpinUntil(() => net.IsStopped, 1500));
        Equal(0, errors); net.Update(); Equal(1, errors);
    }),
    ("Backward animation time reselects a valid segment", () =>
    {
        var animator = new Timberborn.CharacterMovementSystem.MovementAnimator();
        Animate(animator, 1.5f); Animate(animator, .5f);
        Equal(.5f, animator.ModelPosition.x); Check(animator.Notified);
    }),
    ("Invalid animation coordinates fall back before water listeners", () =>
    {
        var animator = new Timberborn.CharacterMovementSystem.MovementAnimator();
        animator.Transform.position = new UnityEngine.Vector3(10, 2, 3);
        animator._animatedPathFollower.InvalidPosition = true;
        Animate(animator, .5f); Equal(10f, animator.ModelPosition.x); Check(animator.Notified);
    }),
    ("Single-player animation remains on the original path", () =>
    {
        BeaverBuddies.IO.EventIO.IsNull = true;
        try { Check(Animate(new Timberborn.CharacterMovementSystem.MovementAnimator(), .5f)); }
        finally { BeaverBuddies.IO.EventIO.IsNull = false; }
    })
};
tests = tests.Concat(Preview5Checks.Tests()).Concat(PerformanceChecks.Tests()).Concat(ActivityTransportChecks.Tests()).Concat(CursorPreferencesChecks.Tests()).ToArray();
foreach (var test in tests)
{
    try
    {
        var task = Task.Run(test.Run);
        if (!task.Wait(5000)) throw new TimeoutException("Test exceeded five seconds");
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception e) { failures++; Console.WriteLine($"FAIL {test.Name}: {e.GetBaseException().Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");
static void Throws<T>(Action run) where T : Exception { try { run(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
static JObject Message() => new JObject { [TimberNetBase.TYPE_KEY] = "Heartbeat", [TimberNetBase.TICKS_KEY] = 1 };
static bool Animate(Timberborn.CharacterMovementSystem.MovementAnimator animator, float time)
{
    BeaverBuddies.SingletonManager.Progress.Time = time;
    return (bool)typeof(BeaverBuddies.Fixes.AnimatedPathFollowerUpdatePathcer)
        .GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] {animator, .02f});
}
sealed class SocketScope : IDisposable
{
    public SteamSocket Socket = new(new CSteamID(1), true);
    public void Dispose() => Socket.Close();
}
sealed class TestNet : TimberNetBase { public void Send(ISocketStream stream, byte[] data) => SendDataWithLength(stream, data); }
class ReadStream : ISocketStream
{
    readonly MemoryStream input;
    public bool Connected { get; private set; } = true;
    public bool ThrowOnWrite;
    public string Name => "test";
    public int MaxChunkSize => 2;
    public int MaxBytesPerSecond => int.MaxValue;
    public ReadStream(byte[] bytes) { input = new(bytes); }
    public Task ConnectAsync() => Task.CompletedTask;
    public void Close() => Connected = false;
    public int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, Math.Min(count, 1));
    public virtual void Write(byte[] buffer, int offset, int count) { if (ThrowOnWrite) throw new IOException("Injected write failure"); }
}
sealed class RecordingStream : ReadStream
{
    public ConcurrentQueue<byte> Bytes = new();
    public ManualResetEventSlim HeaderWritten = new(), Continue = new();
    int writes;
    public RecordingStream() : base(Array.Empty<byte>()) { }
    public override void Write(byte[] buffer, int offset, int count)
    {
        for (int i = offset; i < offset + count; i++) Bytes.Enqueue(buffer[i]);
        if (Interlocked.Increment(ref writes) == 1) { HeaderWritten.Set(); if (!Continue.Wait(3000)) throw new TimeoutException(); }
    }
}
