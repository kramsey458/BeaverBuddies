using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Newtonsoft.Json.Linq;
using TimberNet;

static class ReconnectChecks
{
    static void Check(bool ok,string reason="assertion failed") { if(!ok) throw new Exception(reason); }
    static void Reject(Action run) { try{run();}catch(IOException){return;}throw new Exception("admission unexpectedly accepted"); }
    public static IEnumerable<(string Name,Action Run)> Tests()
    {
        yield return ("Reconnect tickets distinguish sessions, reject strangers and duplicate claims",()=>{
            var tickets=new ReconnectTickets();var a=new Peer();var b=new Peer();
            string ta=(string)tickets.Claim(a,"",true)["ticket"], tb=(string)tickets.Claim(b,"",true)["ticket"];
            tickets.Activate(a);tickets.Activate(b); Check(ta.Length==64 && ta!=tb);
            var recovery=new ReconnectTickets(tickets.Export(),"snapshot-1","sha256");
            Reject(()=>recovery.Claim(new Peer(),"",true));Reject(()=>recovery.Claim(new Peer(),new string('0',64),true));
            var returned=new Peer();var reply=recovery.Claim(returned,ta,true);Check((string)reply["recoveryId"]=="snapshot-1");
            Reject(()=>recovery.Claim(new Peer(),ta,true));returned.Close();recovery.Release(returned);
            Check((string)recovery.Claim(new Peer(),ta,true)["ticket"]==ta);
        });
        yield return ("Closing grace revokes missing tickets but retains already admitted downloads",()=>{
            var recovery=new ReconnectTickets(new[]{"a","b"},"id","hash");var peer=new Peer();
            recovery.Claim(peer,"a",true);Check(recovery.Seal()==1 && recovery.HasClaim(peer));
            Reject(()=>recovery.Claim(new Peer(),"b",true));Check(recovery.Export(true).SequenceEqual(new[]{"a"}));
            Check(recovery.Release(peer));Check(!recovery.Release(peer));
            var expired=new ReconnectTickets(new[]{"a"},"id","hash");expired.SetDeadline(ConnectionTelemetry.Now-1);
            Reject(()=>expired.Claim(new Peer(),"a",true)); // Enforced by the socket worker even while Unity is loading.
        });
        yield return ("Incomplete first joins do not acquire a reconnect place or trigger disconnect recovery",()=>{
            var tickets=new ReconnectTickets();var peer=new Peer();tickets.Claim(peer,"",true);
            Check(tickets.Export().Length==0);Check(!tickets.Release(peer) && tickets.Export(true).Length==0);
            Reject(()=>tickets.Claim(new Peer(),"",false));
        });
        yield return ("Ticket admission is capped and concurrent duplicate claims admit exactly one peer",()=>{
            var fresh=new ReconnectTickets();for(int i=0;i<PlayerActivity.MaxPlayers;i++)fresh.Claim(new Peer(),"",true);
            Reject(()=>fresh.Claim(new Peer(),"",true));var recovery=new ReconnectTickets(new[]{"a"},"id","hash");int accepted=0;
            Parallel.For(0,50,_=>{try{recovery.Claim(new Peer(),"a",true);Interlocked.Increment(ref accepted);}catch(IOException){}});Check(accepted==1);
        });
        yield return ("Real TCP returns the new snapshot only to original ticket holders",()=>{
            var listener=new LocalListener();TimberServer host=new(listener,()=>Task.FromResult(new byte[]{1,2,3}),null){ReconnectTickets=new(),CompatibilityIdentity="build13"};
            TimberClient guest=null, stranger=null;int maps=0, dropped=0;byte[] received=null;int updateThread=Environment.CurrentManagedThreadId;
            try{
                host.OnPeerDisconnected+=_=>{Check(Environment.CurrentManagedThreadId==updateThread);dropped++;};host.Start();
                guest=new(new TCPClientWrapper("127.0.0.1",listener.Port)){UseReconnectHandshake=true,CompatibilityIdentity="build13"};
                guest.OnMapReceived+=b=>{maps++;received=b;};guest.Start();
                void Pump(){host.Update();guest?.Update();stranger?.Update();}
                void Until(Func<bool> done)=>Check(SpinWait.SpinUntil(()=>{Pump();return done();},5000),"loopback timeout");
                Until(()=>maps==1 && host.ReconnectTickets.Export().Length==1);string ticket=guest.ReconnectToken;
                Check(ticket.Length==64 && guest.RecoveryId==null && received.SequenceEqual(new byte[]{1,2,3}));
                host.StopAcceptingClients("running");guest.Close();Until(()=>dropped==1);for(int i=0;i<5;i++)host.Update();Check(dropped==1);
                var roster=host.ReconnectTickets.Export(true);host.Close();listener=new LocalListener();
                host=new(listener,()=>Task.FromResult(new byte[]{4,5,6}),null){ReconnectTickets=new(roster,"recovery-id","digest"),CompatibilityIdentity="build13"};host.Start();
                int rejected=0;stranger=new(new TCPClientWrapper("127.0.0.1",listener.Port)){UseReconnectHandshake=true,CompatibilityIdentity="build13"};stranger.OnError+=_=>rejected++;stranger.Start();
                Until(()=>rejected==1);Check(host.ClientCount==0);
                guest=new(new TCPClientWrapper("127.0.0.1",listener.Port)){UseReconnectHandshake=true,ReconnectToken=ticket,CompatibilityIdentity="build13"};
                guest.OnMapReceived+=b=>{maps++;received=b;};guest.Start();Until(()=>maps==2);
                Check(received.SequenceEqual(new byte[]{4,5,6}) && guest.RecoveryId=="recovery-id" && guest.RecoveryDigest=="digest");
                Until(()=>{guest.ReadEvents(0);return host.Hash==guest.Hash;});Check(host.TickCount==0 && guest.TickCount==0);
            }finally{guest?.Close();stranger?.Close();host.Close();}
        });
        yield return ("Graceful TCP leave removes its ticket and does not request recovery",()=>{
            var listener=new LocalListener();var host=new TimberServer(listener,()=>Task.FromResult(new byte[]{1}),null){ReconnectTickets=new()};TimberClient guest=null;
            try{
                int drops=0,maps=0;host.OnPeerDisconnected+=_=>drops++;host.Start();
                guest=new(new TCPClientWrapper("127.0.0.1",listener.Port)){UseReconnectHandshake=true};guest.OnMapReceived+=_=>maps++;guest.Start();
                void Pump(){host.Update();guest.Update();}
                Check(SpinWait.SpinUntil(()=>{Pump();return maps==1 && host.ReconnectTickets.Export().Length==1;},3000));
                guest.LeaveSession();Check(SpinWait.SpinUntil(()=>{Pump();return host.ReconnectTickets.Export(true).Length==0 && host.ClientCount==0;},3000));
                for(int i=0;i<10;i++)host.Update();Check(drops==0 && host.ClientCount==0,"graceful leave triggered a disconnect callback");
            }finally{guest?.Close();host.Close();}
        });
        yield return ("Transport keepalives survive suspended game updates without entering replay",()=>{
            var listener=new LocalListener();var host=new TimberServer(listener,()=>Task.FromResult(new byte[]{1}),null){ReconnectTickets=new()};TimberClient guest=null;
            try{
                int maps=0;host.Start();guest=new(new TCPClientWrapper("127.0.0.1",listener.Port)){UseReconnectHandshake=true};guest.OnMapReceived+=_=>maps++;guest.Start();
                Check(SpinWait.SpinUntil(()=>{host.Update();guest.Update();return maps==1 && host.ReconnectTickets.Export().Length==1;},3000));
                var field=typeof(TimberNetBase).GetField("lastInbound",BindingFlags.NonPublic|BindingFlags.Instance);
                var hostTimes=(System.Collections.Concurrent.ConcurrentDictionary<ISocketStream,double>)field.GetValue(host);
                var guestTimes=(System.Collections.Concurrent.ConcurrentDictionary<ISocketStream,double>)field.GetValue(guest);
                double start=ConnectionTelemetry.Now;
                // Deliberately do not call either Update(): both Unity threads could be saving/loading.
                Check(SpinWait.SpinUntil(()=>hostTimes.Values.Any(t=>t>start+1) && guestTimes.Values.Any(t=>t>start+1),5000),"keepalive depended on game updates");
                host.Update();guest.Update();guest.ReadEvents(0);Check(host.Hash==guest.Hash && host.ReadEvents(0).Count==0 && guest.ReadEvents(0).Count==0);
            }finally{guest?.Close();host.Close();}
        });
        yield return ("Unresponsive admitted sockets disconnect once on the update thread",()=>{
            var host=new WatchdogHost();var peer=new Peer();host.ReconnectTickets=new();host.ReconnectTickets.Claim(peer,"",true);host.ReconnectTickets.Activate(peer);int dropped=0;host.OnPeerDisconnected+=_=>dropped++;
            var field=typeof(TimberNetBase).GetField("lastInbound",BindingFlags.NonPublic|BindingFlags.Instance);
            var times=(System.Collections.Concurrent.ConcurrentDictionary<ISocketStream,double>)field.GetValue(host);times[peer]=ConnectionTelemetry.Now-21;
            host.CheckPeer(peer);Check(!peer.Connected && dropped==0);host.Update();host.Update();Check(dropped==1);host.Close();
        });
    }
    sealed class WatchdogHost:TimberServer
    {public WatchdogHost():base(new LocalListener(),()=>Task.FromResult(new byte[]{1}),null){}public void CheckPeer(ISocketStream peer)=>CheckConnectionSilence(peer);}
    sealed class LocalListener:ISocketListener
    {readonly TcpListener listener=new(IPAddress.Loopback,0);public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;public void Start()=>listener.Start();public ISocketStream AcceptClient()=>new TCPClientWrapper(listener.AcceptTcpClient());public void Stop()=>listener.Stop();}
    sealed class Peer:ISocketStream
    {public bool Connected{get;private set;}=true;public string Name=>null;public int MaxChunkSize=>8192;public int MaxBytesPerSecond=>int.MaxValue;public void Close()=>Connected=false;public Task ConnectAsync()=>Task.CompletedTask;public int Read(byte[] b,int o,int n)=>0;public void Write(byte[] b,int o,int n){} }
}
