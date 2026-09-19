using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using TimberNet;

int failures = 0;
var tests = new (string Name, Action Run)[]
{
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
        Check(SpinWait.SpinUntil(() => net.IsStopped && !stream.Connected, 1000)); Equal(0, errors);
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
tests = tests.Concat(Preview5Checks.Tests()).Concat(PerformanceChecks.Tests()).Concat(SendingChecks.Tests()).Concat(TcpRecoveryChecks.Tests()).Concat(ActivityChecks.Tests()).Concat(CompatibilityDiagnosticsChecks.Tests()).Concat(TelemetryChecks.Tests()).Concat(ManualSessionChecks.Tests()).ToArray();
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
