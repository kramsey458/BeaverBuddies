using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using TimberNet;

static class TelemetryChecks
{
    static void Check(bool value,string reason="assertion failed") {if(!value)throw new Exception(reason);}
    static JObject Reply(int sequence,int tick=10,double rate=20)=>new(){["type"]=ConnectionTelemetry.MessageType,["reply"]=true,["sequence"]=sequence,["tick"]=tick,["rate"]=rate,["loaded"]=true,["paused"]=false};
    public static IEnumerable<(string Name,Action Run)> Tests()
    {
        yield return ("Telemetry measures round trip without relying on peer clocks",()=>{
            var t=new ConnectionTelemetry();object peer=new();JObject sent=null;t.Probe(peer,100,m=>sent=m);
            t.Receive(peer,Reply((int)sent["sequence"],90),100.04,new SimulationStatus(100,20,true,false),_=>throw new Exception());
            var result=t.Read(peer,100.1);Check(Math.Abs(result.RoundTripMilliseconds.Value-40)<.001 && result.LocalTickAtReply==100 && result.Simulation.Tick==90 && result.Fresh);
            Check(!t.Read(peer,106).Fresh);
        });
        yield return ("Telemetry caps outstanding probes, retries after timeout and ignores obsolete replies",()=>{
            var t=new ConnectionTelemetry();object peer=new();var sent=new List<JObject>();t.Probe(peer,0,sent.Add);
            for(int i=0;i<400;i++)t.Probe(peer,i/100.0,sent.Add);Check(sent.Count==1);
            t.Probe(peer,5,sent.Add);Check(sent.Count==2);t.Receive(peer,Reply(1),5.1,new(0,0,true,true),_=>{});Check(t.Read(peer,5.1).Simulation==null);
            t.Receive(peer,Reply(2),5.2,new(0,0,true,true),_=>{});Check(t.Read(peer,5.2).Fresh);
        });
        yield return ("Telemetry rate limits replies and bounds peer tracking",()=>{
            var t=new ConnectionTelemetry();int sent=0;object peer=new();JObject probe=null;t.Probe(peer,0,m=>probe=m);
            for(int i=0;i<100;i++)t.Receive(peer,probe,.1,new(0,0,true,true),_=>sent++);Check(sent==1);
            t.Clear();sent=0;for(int i=0;i<1000;i++)t.Probe(new object(),0,_=>sent++);Check(sent==PlayerActivity.MaxPlayers);
            t.Retain(new HashSet<object>());Check(t.Read(peer,1).Simulation==null);
        });
        yield return ("Telemetry rejects malformed and nonfinite metrics without throwing",()=>{
            foreach(var key in new[]{"sequence","tick","rate","loaded","paused"}) {var m=Reply(1);m[key]=new JObject();Check(!ConnectionTelemetry.Valid(m));}
            foreach(double rate in new[]{double.NaN,double.PositiveInfinity,-2,100001})Check(!ConnectionTelemetry.Valid(Reply(1,10,rate)));
            Check(!ConnectionTelemetry.Valid(Reply(-1)) && !ConnectionTelemetry.Valid(Reply(1,-1)) && ConnectionTelemetry.Valid(Reply(1,0,-1)));
            var huge=JObject.Parse("{\"type\":\"ConnectionStatus\",\"reply\":false,\"sequence\":999999999999999999999999999999999999999999}");Check(!ConnectionTelemetry.Valid(huge));
        });
        yield return ("Status frames share disposable queues and cannot overtake waiting gameplay",()=>{
            using var entered=new ManualResetEventSlim();using var resume=new ManualResetEventSlim();var writes=new List<string>();
            var sender=new OrderedSender(s=>{lock(writes)writes.Add(s);if(s=="first"){entered.Set();resume.Wait(2000);}},_=>throw new Exception());
            sender.Enqueue("first");Check(entered.Wait(1000));for(int i=0;i<1000;i++)sender.EnqueueLatest(-1,"probe"+i);var final=sender.Enqueue("gameplay");
            Check(sender.PendingData.Messages==2 && sender.PendingData.Bytes==26);resume.Set();Check(final.Wait(2000));
            Check(SpinWait.SpinUntil(()=>{lock(writes)return writes.Count==3;},1000));lock(writes)Check(writes.SequenceEqual(new[]{"first","gameplay","probe999"}));sender.Stop();
        });
        yield return ("Real TCP exchanges paused telemetry for two guests without replay, hash or control changes",()=>{
            var listener=new LocalListener();var host=new TimberServer(listener,()=>Task.FromResult(new byte[]{1,2,3}),null);TimberClient one=null,two=null;
            try {
                host.Start();one=new(new TCPClientWrapper("127.0.0.1",listener.Port));two=new(new TCPClientWrapper("127.0.0.1",listener.Port));int maps=0,controls=0;
                one.OnMapReceived+=_=>maps++;two.OnMapReceived+=_=>maps++;host.OnControl+=(_,_)=>controls++;one.OnControl+=(_,_)=>controls++;
                one.Start();two.Start();host.PublishSimulationStatus(new(100,0,true,true));one.PublishSimulationStatus(new(98,0,true,true));two.PublishSimulationStatus(new(90,0,true,true));
                void Pump(){host.Update();one.Update();two.Update();}
                void Until(Func<bool> condition)=>Check(SpinWait.SpinUntil(()=>{Pump();return condition();},3000),"loopback timeout");
                Until(()=>maps==2);Until(()=>{one.ReadEvents(0);two.ReadEvents(0);return host.Hash==one.Hash && host.Hash==two.Hash;});int hash=host.Hash;
                Until(()=>host.GetPeerStatus().Count==2 && host.GetPeerStatus().All(p=>p.Fresh) && one.GetPeerStatus().Single().Fresh && two.GetPeerStatus().Single().Fresh);
                Check(host.GetPeerStatus().Select(p=>p.Simulation.Tick).OrderBy(x=>x).SequenceEqual(new[]{90,98}));
                Check(host.GetPeerStatus().Select(p=>p.Label).Distinct().Count()==2 && one.GetPeerStatus().Single().Simulation.Paused);
                Check(host.Hash==hash && one.Hash==hash && two.Hash==hash && controls==0);
                Check(host.ReadEvents(0).Count==0 && one.ReadEvents(0).Count==0 && two.ReadEvents(0).Count==0);
                Check(host.TickCount==0 && one.TickCount==0);one.Close();Until(()=>host.ClientCount==1);Check(host.GetPeerStatus().Count==1);
            }finally{one?.Close();two?.Close();host.Close();}
        });
    }
    sealed class LocalListener:ISocketListener
    {
        readonly TcpListener listener=new(IPAddress.Loopback,0);public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
        public void Start()=>listener.Start();public ISocketStream AcceptClient()=>new TCPClientWrapper(listener.AcceptTcpClient());public void Stop()=>listener.Stop();
    }
}
