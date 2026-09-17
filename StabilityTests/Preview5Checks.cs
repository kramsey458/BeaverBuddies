using System.Collections.Concurrent;
using BeaverBuddies;
using TimberNet;

static class Preview5Checks
{
    static void Check(bool value) { if (!value) throw new Exception("assertion failed"); }
    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Matching builds complete handshake before map transfer", () => Session("same", "same", true));
        yield return ("Mismatched builds never request or deliver the map", () => Session("old-build", "new-build", false));
        yield return ("New client rejects legacy map before loading", () =>
        {
            var client = new TimberClient(new ReadStream(new byte[] {0,0,0,4,1,2,3,4})) { CompatibilityIdentity = "new" };
            bool map = false; string error = "";
            client.OnMapReceived += _ => map = true; client.OnError += x => error = x;
            client.Start(); Check(SpinWait.SpinUntil(() => client.IsStopped, 1000)); client.Update();
            Check(!map && error.Contains("older"));
        });
        yield return ("Legacy client sees an upgrade error rather than a fake map", () => Session("new", null, false));
        yield return ("Silent handshake peer times out and releases reader", () =>
        {
            var (a,b) = PipeStream.Pair();
            var task = Task.Run(() => { try { CompatibilityHandshake.Run(a, "x", true, 100); return false; } catch (IOException) { return true; } });
            Check(task.Wait(1500) && task.Result); b.Close();
        });
        yield return ("Client replay failure notifies host on update thread", () => Session("same", "same", true, true));
        yield return ("Host replay failure reaches client before disconnect cleanup", () => Session("same", "same", true, false, true));
        yield return ("Replay stops after partial mutation and restores flag", () =>
        {
            bool active = false; int changes = 0, failures = 0;
            ReplayExecution.Run(new[] {1,2,3}, i => { Check(active); changes++; if (i == 2) throw new Exception("partial"); return true; },
                (i,e) => { Check(i == 2); failures++; }, x => active = x, false);
            Check(changes == 2 && failures == 1 && !active);
        });
        yield return ("Replay cleanup survives failure-handler exception", () =>
        {
            bool active = false;
            try { ReplayExecution.Run(new[] {1}, i => throw new IOException(), (i,e) => throw new InvalidOperationException(), x => active=x, false); }
            catch (InvalidOperationException) { }
            Check(!active);
        });
        yield return ("Replay stop signal prevents later events and preserves outer scope", () =>
        {
            bool active = true; int changes = 0;
            ReplayExecution.Run(new[] {1,2}, i => { changes++; return false; }, (i,e) => throw e, x => active=x, true);
            Check(active && changes == 1);
        });
    }

    static void Session(string hostIdentity, string clientIdentity, bool success, bool fault = false, bool hostFault = false)
    {
        var (hostStream, clientStream) = PipeStream.Pair();
        var listener = new PipeListener(hostStream);
        int requests = 0, maps = 0, errors = 0, faults = 0;
        var host = new TimberServer(listener, () => { Interlocked.Increment(ref requests); return Task.FromResult(new byte[] {7,8,9}); }, null)
            { CompatibilityIdentity = hostIdentity };
        var client = new TimberClient(clientStream) { CompatibilityIdentity = clientIdentity };
        client.OnMapReceived += bytes => { Check(bytes.SequenceEqual(new byte[] {7,8,9})); maps++; };
        client.OnError += _ => errors++;
        host.OnSessionFault += _ => { faults++; host.AbortSession("test"); };
        client.OnSessionFault += _ => faults++;
        try
        {
            host.Start(); client.Start();
            Check(SpinWait.SpinUntil(() => { host.Update(); client.Update(); return maps > 0 || errors > 0; }, 2000));
            if (success) Check(maps == 1 && requests == 1 && errors == 0);
            else Check(maps == 0 && requests == 0 && errors > 0);
            if (fault)
            {
                client.AbortSession("test");
                Check(SpinWait.SpinUntil(() => !hostStream.Connected, 1000));
                Check(faults == 0);
                host.Update(); Check(faults == 1 && host.IsStopped);
            }
            if (hostFault)
            {
                host.AbortSession("test");
                Check(SpinWait.SpinUntil(() => client.IsStopped, 1000));
                Check(faults == 0);
                client.Update(); Check(faults == 1 && host.IsStopped);
            }
        }
        finally { host.Close(); client.Close(); }
    }
}

sealed class PipeListener : ISocketListener
{
    readonly BlockingCollection<ISocketStream> pending = new();
    public PipeListener(ISocketStream stream) => pending.Add(stream);
    public void Start() { }
    public ISocketStream AcceptClient() => pending.Take();
    public void Stop() => pending.CompleteAdding();
}

sealed class PipeStream : ISocketStream
{
    readonly BlockingCollection<byte> input, output;
    volatile bool closed;
    PipeStream(BlockingCollection<byte> input, BlockingCollection<byte> output) { this.input=input; this.output=output; }
    public static (PipeStream,PipeStream) Pair()
    {
        var a = new BlockingCollection<byte>(); var b = new BlockingCollection<byte>();
        return (new PipeStream(a,b),new PipeStream(b,a));
    }
    public bool Connected => !closed;
    public string Name => "test-peer";
    public int MaxChunkSize => 1024;
    public int MaxBytesPerSecond => int.MaxValue;
    public Task ConnectAsync() => Task.CompletedTask;
    public int Read(byte[] buffer,int offset,int count)
    {
        if (count == 0) return 0;
        try { buffer[offset] = input.Take(); return 1; }
        catch (InvalidOperationException) { return 0; }
    }
    public void Write(byte[] buffer,int offset,int count)
    {
        try { for(int i=0;i<count;i++) output.Add(buffer[offset+i]); }
        catch (InvalidOperationException e) { throw new IOException("closed",e); }
    }
    public void Close() { closed=true; input.CompleteAdding(); output.CompleteAdding(); }
}
