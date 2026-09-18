using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using TimberNet;

static class SendingChecks
{
    static void Check(bool condition, string message = "assertion failed") { if (!condition) throw new Exception(message); }
    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Blocked writer does not block enqueue and preserves FIFO order", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var received = new ConcurrentQueue<string>();
            var sender = new OrderedSender(text => { entered.Set(); Check(release.Wait(2000)); received.Enqueue(text); }, e => throw e);
            sender.Enqueue("first"); Check(entered.Wait(1000));
            var enqueue = Task.Run(() => { for (int i=0;i<100;i++) sender.Enqueue(i.ToString()); });
            Check(enqueue.Wait(500), "enqueue waited on blocked socket");
            Check(!sender.Drain().IsCompleted); release.Set();
            Check(sender.Drain().Wait(2000));
            Check(received.SequenceEqual(new[] { "first" }.Concat(Enumerable.Range(0,100).Select(i => i.ToString()))));
            sender.Stop();
        });
        yield return ("Queue memory limit includes active frame and rejects overflow", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var sender = new OrderedSender(_ => { entered.Set(); release.Wait(1000); }, _ => { }, 5, 10);
            sender.Enqueue("12345"); Check(entered.Wait(1000));
            bool rejected = false;
            try { sender.Enqueue("x"); } catch (IOException) { rejected = true; }
            Check(rejected); release.Set(); Check(sender.Drain().Wait(1000));
            for (int i=0;i<100;i++) Check(sender.Enqueue("12345").Wait(1000));
            sender.Stop();
        });
        yield return ("Queue message limit rejects a slow peer without dropping older commands", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var output = new ConcurrentQueue<string>();
            var sender = new OrderedSender(x => { entered.Set(); release.Wait(1000); output.Enqueue(x); }, _ => { }, 100, 2);
            sender.Enqueue("1"); Check(entered.Wait(1000)); sender.Enqueue("2");
            bool rejected = false; try { sender.Enqueue("3"); } catch (IOException) { rejected = true; }
            Check(rejected); release.Set(); Check(sender.Drain().Wait(1000)); Check(output.SequenceEqual(new[] {"1","2"}));
        });
        yield return ("Failed writer cancels queued work and reports failure once", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            int failures = 0, writes = 0;
            var sender = new OrderedSender(_ => { Interlocked.Increment(ref writes); entered.Set(); release.Wait(1000); throw new IOException(); }, _ => Interlocked.Increment(ref failures));
            sender.Enqueue("1"); Check(entered.Wait(1000)); var pending = sender.Enqueue("2"); release.Set();
            Check(SpinWait.SpinUntil(() => failures == 1, 1000)); Check(pending.IsCanceled && writes == 1);
        });
        yield return ("Closing a blocked connection discards pending frames and wakes writer", () =>
        {
            var stream = new BlockingWriteStream(); var client = new TimberClient(stream);
            client.DoUserInitiatedEvent(Event(1)); Check(stream.Entered.Wait(1000));
            client.DoUserInitiatedEvent(Event(2)); client.Close();
            Check(!stream.Connected && client.IsStopped);
            Check(SpinWait.SpinUntil(() => client.FlushAsync().IsCompleted, 1000));
        });
        yield return ("Event JSON is immutable after enqueue despite caller mutation", () =>
        {
            var stream = new CaptureStream(); var client = new TimberClient(stream);
            var first = Event(1); client.DoUserInitiatedEvent(first);
            Check(stream.Entered.Wait(1000)); first["value"] = "changed";
            client.DoUserInitiatedEvent(Event(2)); stream.Release.Set();
            Check(client.FlushAsync().Wait(2000));
            var frames = Frames(stream.Bytes.ToArray());
            Check(frames.Count == 2 && (int)frames[0]["value"] == 1 && (int)frames[1]["value"] == 2);
            client.Close();
        });
        yield return ("Transport controls bypass replay queues and execute on Update", () =>
        {
            var (a,b) = PipeStream.Pair(); var host = new TimberServer(new PipeListener(a), () => Task.FromResult(new byte[]{1,2}), null);
            var client = new TimberClient(b); bool loaded = false; int controls = 0, callbackThread = 0;
            client.OnMapReceived += _ => loaded = true;
            host.OnControl += (_, message) => { Check((string)message["command"] == "ResyncRequest"); controls++; callbackThread = Environment.CurrentManagedThreadId; };
            try
            {
                host.Start(); client.Start();
                Check(SpinWait.SpinUntil(() => { host.Update(); client.Update(); return loaded; },2000));
                client.SendControl(TimberNetBase.Control("ResyncRequest")); Check(client.FlushAsync().Wait(1000));
                Check(controls == 0);
                Check(SpinWait.SpinUntil(() => { host.Update(); return controls == 1; },1000));
                Check(callbackThread == Environment.CurrentManagedThreadId && host.ReadEvents(0).Count == 0);
            }
            finally { client.Close(); host.Close(); }
        });
        yield return ("A blocked host peer cannot stall another peer's command stream", () =>
        {
            var (a,ca)=PipeStream.Pair(); var (b,cb)=PipeStream.Pair();
            var slow=new GatedStream(a); var listener=new ManyListener(slow,b);
            int initialized=0;
            var host=new TimberServer(listener,()=>Task.FromResult(new byte[]{1}),()=> { initialized++; return Event(0); });
            var first=new TimberClient(ca); var second=new TimberClient(cb); int maps=0;
            first.OnMapReceived+=_=>maps++; second.OnMapReceived+=_=>maps++;
            try
            {
                host.Start(); first.Start(); second.Start();
                Check(SpinWait.SpinUntil(()=> { host.Update(); first.Update(); second.Update(); return maps==2; },2000));
                Check(SpinWait.SpinUntil(()=> { host.Update(); return initialized==2 && host.FlushAsync().IsCompleted; },1000));
                first.ReadEvents(0); second.ReadEvents(0);
                slow.Block=true;
                var send=Task.Run(()=> { for(int n=1;n<=50;n++) host.DoUserInitiatedEvent(new JObject { ["type"]="Action", ["ticksSinceLoad"]=1, ["n"]=n }); });
                Check(send.Wait(500),"host broadcast blocked on one peer"); Check(slow.Entered.Wait(1000));
                var output=new List<JObject>();
                Check(SpinWait.SpinUntil(()=> { output.AddRange(second.ReadEvents(1)); return output.Count(x=>(string)x["type"]=="Action")==50; },2000));
                Check(output.Where(x=>(string)x["type"]=="Action").Select(x=>(int)x["n"]).SequenceEqual(Enumerable.Range(1,50)));
                Check(first.ReadEvents(1).All(x=>(string)x["type"]!="Action"));
                slow.Release.Set();
                var catchup=new List<JObject>();
                Check(SpinWait.SpinUntil(()=> { catchup.AddRange(first.ReadEvents(1)); return catchup.Count(x=>(string)x["type"]=="Action")==50; },2000));
                Check(host.Hash==first.Hash && host.Hash==second.Hash);
            }
            finally { slow.Release.Set(); first.Close(); second.Close(); host.Close(); }
        });
        yield return ("Connecting to a slow host never waits on the calling thread", () =>
        {
            var stream = new SlowConnectStream(); var client = new TimberClient(stream);
            var start = Task.Run(client.Start); Check(start.Wait(500)); client.Close(); stream.Connect.TrySetResult(true);
        });
        yield return ("Graceful disconnect drains final control even when Close follows immediately", () =>
        {
            var stream = new CaptureStream(); var client = new TimberClient(stream);
            client.SendControl(TimberNetBase.Control("ResyncReload")); Check(stream.Entered.Wait(1000));
            client.CloseAfterFlush(); client.Close(); Check(stream.Connected);
            stream.Release.Set(); Check(SpinWait.SpinUntil(() => !stream.Connected,1000));
            Check((string)Frames(stream.Bytes.ToArray()).Single()["command"] == "ResyncReload");
        });
    }
    static JObject Event(int n) => new() { ["type"] = "Heartbeat", ["ticksSinceLoad"] = n, ["value"] = n };
    static List<JObject> Frames(byte[] bytes)
    {
        var result = new List<JObject>(); int offset = 0;
        while(offset < bytes.Length)
        {
            var header = bytes.Skip(offset).Take(4).Reverse().ToArray(); int length = BitConverter.ToInt32(header);
            result.Add(JObject.Parse(CompressionUtils.Decompress(bytes.Skip(offset+4).Take(length).ToArray()))); offset += 4+length;
        }
        return result;
    }
    class BlockingWriteStream : ISocketStream
    {
        public ManualResetEventSlim Entered = new(), Release = new();
        public bool Connected { get; private set; } = true;
        public string Name => "slow-peer";
        public int MaxChunkSize => 32768;
        public int MaxBytesPerSecond => int.MaxValue;
        public virtual Task ConnectAsync() => Task.CompletedTask;
        public void Close() { Connected=false; Release.Set(); }
        public int Read(byte[] b,int o,int n) => 0;
        public virtual void Write(byte[] b,int o,int n) { Entered.Set(); if (!Release.Wait(2000)) throw new TimeoutException(); if (!Connected) throw new IOException(); }
    }
    sealed class CaptureStream : BlockingWriteStream
    {
        public ConcurrentQueue<byte> Bytes = new();
        public override void Write(byte[] b,int o,int n) { base.Write(b,o,n); for(int i=o;i<o+n;i++) Bytes.Enqueue(b[i]); }
    }
    sealed class ManyListener : ISocketListener
    {
        readonly BlockingCollection<ISocketStream> pending=new();
        public ManyListener(params ISocketStream[] sockets) { foreach(var s in sockets) pending.Add(s); }
        public void Start() { }
        public ISocketStream AcceptClient()=>pending.Take();
        public void Stop()=>pending.CompleteAdding();
    }
    sealed class GatedStream : ISocketStream
    {
        readonly ISocketStream inner;
        public volatile bool Block;
        public ManualResetEventSlim Entered=new(), Release=new();
        public GatedStream(ISocketStream inner)=>this.inner=inner;
        public bool Connected=>inner.Connected;
        public string Name=>inner.Name;
        public int MaxChunkSize=>inner.MaxChunkSize;
        public int MaxBytesPerSecond=>inner.MaxBytesPerSecond;
        public Task ConnectAsync()=>inner.ConnectAsync();
        public void Close() { Release.Set(); inner.Close(); }
        public int Read(byte[] b,int o,int n)=>inner.Read(b,o,n);
        public void Write(byte[] b,int o,int n)
        {
            if(Block) { Entered.Set(); if(!Release.Wait(3000)) throw new IOException("slow peer"); }
            inner.Write(b,o,n);
        }
    }
    sealed class SlowConnectStream : BlockingWriteStream
    {
        public TaskCompletionSource<bool> Connect = new();
        public override Task ConnectAsync() => Connect.Task;
    }
}
