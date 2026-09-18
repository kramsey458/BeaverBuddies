using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using TimberNet;

static class TcpRecoveryChecks
{
    static void Check(bool value, string reason = "assertion failed") { if (!value) throw new Exception(reason); }
    static void Until(Func<bool> done) => Check(SpinWait.SpinUntil(done, 3000), "TCP operation timed out");
    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Real TCP carries ordered commands then reloads a fresh snapshot on the same port", () =>
        {
            int port=0;
            for(int generation=0;generation<2;generation++)
            {
                var listener=new LocalListener(port);
                byte[] map=Enumerable.Range(0,65536).Select(i=>(byte)(i+generation)).ToArray();
                int mainThread=Environment.CurrentManagedThreadId, initThread=0;
                var host=new TimberServer(listener,()=>Task.FromResult(map),()=>
                {
                    initThread=Environment.CurrentManagedThreadId;
                    return new JObject { ["type"]="InitializeClient", ["ticksSinceLoad"]=0 };
                }) { CompatibilityIdentity="preview9" };
                TimberClient client=null;
                try
                {
                    host.Start(); port=listener.Port;
                    client=new TimberClient(new TCPClientWrapper("127.0.0.1",port)) { CompatibilityIdentity="preview9" };
                    byte[] received=null; int errors=0;
                    client.OnMapReceived+=bytes=>received=bytes; client.OnError+=_=>errors++;
                    client.Start(); Until(()=> { host.Update(); client.Update(); return received!=null; });
                    Check(received.SequenceEqual(map));
                    Until(()=> { host.Update(); client.Update(); return initThread!=0; });
                    Check(initThread==mainThread,"join callback accessed game settings on worker");
                    var replayed=new List<JObject>();
                    Until(()=> { replayed.AddRange(client.ReadEvents(0)); return replayed.Count>0; });
                    for(int i=0;i<250;i++) host.DoUserInitiatedEvent(new JObject { ["type"]="Action",["ticksSinceLoad"]=1,["n"]=i });
                    Until(()=> { replayed.AddRange(client.ReadEvents(1)); return replayed.Count==251; });
                    Check(replayed.Skip(1).Select(x=>(int)x["n"]).SequenceEqual(Enumerable.Range(0,250)));
                    Check(client.Hash==host.Hash,"event stream hash differs");
                    string receivedControl=null;
                    client.OnControl+=(_,m)=>receivedControl=(string)m["command"];
                    host.SendControl(TimberNetBase.Control("ResyncReload"));
                    host.CloseAfterFlush(); host.Close();
                    Until(()=> { client.Update(); return receivedControl!=null; });
                    Check(receivedControl=="ResyncReload");
                    Until(()=>listener.Stopped);
                }
                finally { client?.Close(); host.Close(); }
            }
        });
        yield return ("Stopping combined listeners wakes the server and child accept workers", () =>
        {
            var first=new LocalListener(0); var second=new LocalListener(0);
            var combined=new MultiSocketListener(first,second); combined.Start();
            var accept=Task.Run(()=> { try { combined.AcceptClient(); return false; } catch(IOException) { return true; } });
            combined.Stop(); combined.Stop();
            Check(accept.Wait(1000) && accept.Result && first.Stopped && second.Stopped);
        });
        yield return ("Invalid frame length closes connection before allocating a snapshot", () =>
        {
            var client=new TimberClient(new ReadStream(new byte[]{0x7f,0xff,0xff,0xff}));
            int maps=0, errors=0; client.OnMapReceived+=_=>maps++; client.OnError+=_=>errors++;
            client.Start(); Until(()=>client.IsStopped); client.Update();
            Check(maps==0 && errors==1); client.Close();
        });
    }
    sealed class LocalListener : ISocketListener
    {
        readonly TcpListener listener;
        public volatile bool Stopped;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public LocalListener(int port) => listener=new TcpListener(IPAddress.Loopback,port);
        public void Start()=>listener.Start();
        public ISocketStream AcceptClient()=>new TCPClientWrapper(listener.AcceptTcpClient());
        public void Stop() { listener.Stop(); Stopped=true; }
    }
}
