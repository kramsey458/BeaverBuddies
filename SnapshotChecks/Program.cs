using System.Reflection;
using BeaverBuddies;
using BeaverBuddies.Connect;
using BeaverBuddies.IO;
using Newtonsoft.Json.Linq;
using Timberborn.CoreUI;
using Timberborn.GameSaveRepositorySystem;
using Timberborn.GameSceneLoading;
using TimberNet;

int total=0, failed=0;
void Check(bool condition,string message="assertion failed") { if(!condition) throw new Exception(message); }
void Test(string name, Action run)
{
    total++;
    try { run(); Console.WriteLine("PASS " + name); }
    catch(Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
}
JObject Control(string command,string id) { var m=TimberNetBase.Control(command); m["id"]=id; m["snapshotSha256"]=BitConverter.ToString(System.Security.Cryptography.SHA256.HashData(ServerHostingUtils.Bytes)); return m; }
object State() => typeof(SnapshotResyncService).GetField("recovery",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
void Expire() => State().GetType().GetField("Deadline").SetValue(State(),double.NegativeInfinity);

Test("Host waits for a complete tick and closed save before replacing the session", () =>
{
    var f=new Fixture(); var old=f.Host; SnapshotResyncService.TryRecover(SingletonManager.Replay);
    Check(f.Saves.Saves==0 && old.NetBase.Sent.Count==1 && SnapshotResyncService.Active);
    f.Service.UpdateSingleton(); Check(f.Saves.Saves==0);
    SingletonManager.Replay.Finish(); Check(SingletonManager.Replay.Frozen);
    f.Service.UpdateSingleton(); Check(f.Saves.Saves==1 && f.Loader.Loads==0);
    f.Saves.Saved(new SaveReference()); f.Service.UpdateSingleton(); Check(f.Loader.Loads==0 && old.Closed==0);
    old.NetBase.Flush.SetResult(true); f.Service.UpdateSingleton();
    Check(old.Closed==1 && f.Loader.Loads==1 && !ReplayService.IsLoaded);
    Check(((ServerEventIO)EventIO.Get()).Map.SequenceEqual(DeterminismService.Seed));
});
Test("Two-player readiness barrier rejects stale IDs and duplicate acknowledgements", () =>
{
    var f=new Fixture(); string id=f.ReloadHost(); var net=((ServerEventIO)EventIO.Get()).NetBase;
    var one=new Peer(); var two=new Peer(); ReplayService.IsLoaded=true;
    SnapshotResyncService.Receive(EventIO.Get(),one,Control("ResyncReady","stale")); f.Service.UpdateSingleton();
    Check(SnapshotResyncService.Active);
    SnapshotResyncService.Receive(EventIO.Get(),one,Control("ResyncReady",id));
    SnapshotResyncService.Receive(EventIO.Get(),one,Control("ResyncReady",id)); f.Service.UpdateSingleton();
    Check(SnapshotResyncService.Active && SingletonManager.Replay.Recorded.Count==0);
    SnapshotResyncService.Receive(EventIO.Get(),two,Control("ResyncReady",id)); f.Service.UpdateSingleton();
    Check(!SnapshotResyncService.Active && SingletonManager.Replay.Recorded.Single().speed==3);
    Check((string)net.Sent.Single()["command"]=="ResyncComplete");
});
Test("Readiness requires the host scene to load and every acknowledged connection to remain open", () =>
{
    var f=new Fixture(); string id=f.ReloadHost(); var one=new Peer(); var two=new Peer();
    SnapshotResyncService.Receive(EventIO.Get(),one,Control("ResyncReady",id));
    SnapshotResyncService.Receive(EventIO.Get(),two,Control("ResyncReady",id)); f.Service.UpdateSingleton();
    Check(SnapshotResyncService.Active); two.Close(); ReplayService.IsLoaded=true; f.Service.UpdateSingleton();
    Check(SnapshotResyncService.Active && SingletonManager.Replay.Recorded.Count==0);
});
Test("Guest reloads once and only acknowledges after scene load plus initialization replay", () =>
{
    var f=new Fixture(); var old=new ClientEventIO(); EventIO.Set(old);
    Check(SnapshotResyncService.TryRecover(SingletonManager.Replay)); Check(SingletonManager.Replay.Frozen);
    SnapshotResyncService.Receive(old,new Peer(),Control("ResyncPrepare","abc"));
    SnapshotResyncService.Receive(old,new Peer(),Control("ResyncReload","wrong")); Check(f.Connection.Reconnects==0);
    SnapshotResyncService.Receive(old,new Peer(),Control("ResyncReload","abc"));
    SnapshotResyncService.Receive(old,new Peer(),Control("ResyncReload","abc")); Check(f.Connection.Reconnects==1);
    SnapshotResyncService.ConnectionFailed("connection refused"); Check(f.Dialogs.Shown==0);
    var fresh=new ClientEventIO(); EventIO.Set(fresh); SnapshotResyncService.MapLoading(fresh,ServerHostingUtils.Bytes);
    ReplayService.IsLoaded=false; SnapshotResyncService.InitializedClient(); f.Service.UpdateSingleton(); Check(fresh.NetBase.Sent.Count==0);
    ReplayService.IsLoaded=true; f.Service.UpdateSingleton(); f.Service.UpdateSingleton(); Check(fresh.NetBase.Sent.Count==1);
    SnapshotResyncService.Receive(old,new Peer(),Control("ResyncComplete","abc")); Check(SnapshotResyncService.Active);
    SnapshotResyncService.Receive(fresh,new Peer(),Control("ResyncComplete","abc")); Check(!SnapshotResyncService.Active);
});
Test("Snapshot save failure keeps the world frozen and never restarts hosting", () =>
{
    var f=new Fixture(); f.Saves.Succeeds=false;
    SnapshotResyncService.TryRecover(SingletonManager.Replay); SingletonManager.Replay.Finish(); f.Service.UpdateSingleton();
    Check(f.Dialogs.Shown==1 && SnapshotResyncService.Active && SingletonManager.Replay.Frozen && f.Loader.Loads==0);
    f.Service.UpdateSingleton(); Check(f.Dialogs.Shown==1 && f.Saves.Saves==1);
});
Test("Canceled flush cannot start a new session", () =>
{
    var f=new Fixture(); SnapshotResyncService.TryRecover(SingletonManager.Replay); SingletonManager.Replay.Finish();
    f.Service.UpdateSingleton(); f.Saves.Saved(new SaveReference()); f.Host.NetBase.Flush.SetCanceled(); f.Service.UpdateSingleton();
    Check(f.Loader.Loads==0 && f.Dialogs.Shown==1 && SingletonManager.Replay.Frozen);
});
Test("Recovery timeout stays paused and shows one actionable error", () =>
{
    var f=new Fixture(); f.ReloadHost(); ReplayService.IsLoaded=true; Expire(); f.Service.UpdateSingleton(); f.Service.UpdateSingleton();
    Check(f.Dialogs.Shown==1 && SnapshotResyncService.Active && SingletonManager.Replay.Frozen && SingletonManager.Replay.Recorded.Count==0);
});
Test("Failed replay never saves or reloads potentially partially mutated state", () =>
{
    var f=new Fixture(); ReplayService.HasReplayFailure=true;
    Check(!SnapshotResyncService.TryRecover(SingletonManager.Replay)); f.Service.UpdateSingleton();
    Check(f.Saves.Saves==0 && f.Loader.Loads==0);
});
Test("Duplicate desync requests do not create multiple snapshots", () =>
{
    var f=new Fixture(); var request=Control("ResyncRequest","");
    for(int i=0;i<10;i++) SnapshotResyncService.Receive(f.Host,new Peer(),request);
    SingletonManager.Replay.Finish(); for(int i=0;i<10;i++) f.Service.UpdateSingleton();
    Check(f.Saves.Saves==1 && f.Host.NetBase.Sent.Count==1);
});
Test("Leaving for the main menu invalidates deferred save callbacks", () =>
{
    var f=new Fixture(); SnapshotResyncService.TryRecover(SingletonManager.Replay); SingletonManager.Replay.Finish();
    f.Service.UpdateSingleton(); SnapshotResyncService.Reset(); f.Saves.Saved(new SaveReference()); f.Service.UpdateSingleton();
    Check(!SnapshotResyncService.Active && f.Loader.Loads==0);
});
Test("Host can opt out without entering an automatic reload loop", () =>
{
    var f=new Fixture(); Settings.SnapshotResyncEnabled=false;
    SnapshotResyncService.Receive(f.Host,new Peer(),Control("ResyncRequest","")); SingletonManager.Replay.Finish(); f.Service.UpdateSingleton();
    Check(f.Saves.Saves==0 && f.Loader.Loads==0 && f.Dialogs.Shown==1);
});
Test("Mismatched snapshot bytes are rejected before loading the guest scene", () =>
{
    var f=new Fixture(); var old=new ClientEventIO(); EventIO.Set(old);
    SnapshotResyncService.Receive(old,new Peer(),Control("ResyncPrepare","abc"));
    SnapshotResyncService.Receive(old,new Peer(),Control("ResyncReload","abc"));
    var fresh=new ClientEventIO(); EventIO.Set(fresh);
    Check(!SnapshotResyncService.MapLoading(fresh,new byte[]{9,9,9}));
    Check(f.Dialogs.Shown==1 && f.Loader.Loads==0 && SnapshotResyncService.Active);
});
Test("A repeat desync immediately after recovery stops automatic reload loops", () =>
{
    var f=new Fixture(); string id=f.ReloadHost(); ReplayService.IsLoaded=true;
    SnapshotResyncService.Receive(EventIO.Get(),new Peer(),Control("ResyncReady",id));
    SnapshotResyncService.Receive(EventIO.Get(),new Peer(),Control("ResyncReady",id)); f.Service.UpdateSingleton();
    Check(!SnapshotResyncService.Active);
    SnapshotResyncService.TryRecover(SingletonManager.Replay); f.Service.UpdateSingleton();
    Check(f.Dialogs.Shown==1 && f.Saves.Saves==1 && f.Loader.Loads==1 && SingletonManager.Replay.Frozen);
});
Test("Steam invite sessions fall back explicitly instead of entering a broken reconnect", () =>
{
    var f=new Fixture(); f.Host.HasSteamClients=true;
    SnapshotResyncService.TryRecover(SingletonManager.Replay); SingletonManager.Replay.Finish(); f.Service.UpdateSingleton();
    Check(f.Dialogs.Shown==1 && f.Dialogs.Message.Contains("Steam") && f.Saves.Saves==0);
});
Test("A direct-IP drop pauses at a full tick and includes the missing guest in recovery", () =>
{
    var f=new Fixture(); f.BeginGrace(); Check(SnapshotResyncService.Active && f.Saves.Saves==0);
    Check((bool)f.Host.NetBase.Sent.Single()["connectionRecovery"]);
    SingletonManager.Replay.Finish(); f.Service.UpdateSingleton(); Check(f.Saves.Saves==1 && SingletonManager.Replay.Frozen);
});
Test("Reconnect timeout continues with connected players only after snapshot readiness", () =>
{
    var f=new Fixture(); var id=f.ReloadGrace(); var host=(ServerEventIO)EventIO.Get(); var peer=new Peer();
    host.NetBase.ReconnectTickets.Claim(peer,f.Tickets[0],true); host.NetBase.ClientCount=1;
    ReplayService.IsLoaded=true; State().GetType().GetField("ReconnectDeadline").SetValue(State(),double.NegativeInfinity);
    f.Service.UpdateSingleton(); Check(SnapshotResyncService.Active && SingletonManager.Replay.Recorded.Count==0);
    SnapshotResyncService.Receive(host,peer,Control("ResyncReady",id)); f.Service.UpdateSingleton();
    Check(!SnapshotResyncService.Active && SingletonManager.Replay.Recorded.Single().speed==3);
    Check(host.NetBase.ReconnectTickets.Export(true).Length==1);
});
Test("Grace expiration with no guests reloads safely then resumes host alone", () =>
{
    var f=new Fixture(); f.ReloadGrace(); var host=(ServerEventIO)EventIO.Get(); host.NetBase.ClientCount=0;
    State().GetType().GetField("ReconnectDeadline").SetValue(State(),double.NegativeInfinity);
    f.Service.UpdateSingleton(); Check(SnapshotResyncService.Active); ReplayService.IsLoaded=true; f.Service.UpdateSingleton();
    Check(!SnapshotResyncService.Active && SingletonManager.Replay.Recorded.Single().speed==3 && f.Loader.Loads==1);
});
Test("Continue without player closes admission but waits for connected guests to load", () =>
{
    var f=new Fixture(); var id=f.ReloadGrace(); var host=(ServerEventIO)EventIO.Get(); var peer=new Peer();
    host.NetBase.ReconnectTickets.Claim(peer,f.Tickets[0],true); host.NetBase.ClientCount=1; ReplayService.IsLoaded=true;
    SnapshotResyncService.ContinueWithoutMissingPlayers(); f.Service.UpdateSingleton(); Check(SnapshotResyncService.Active);
    bool rejected=false; try { host.NetBase.ReconnectTickets.Claim(new Peer(),f.Tickets[1],true); } catch(IOException) { rejected=true; } Check(rejected);
    SnapshotResyncService.Receive(host,peer,Control("ResyncReady",id)); f.Service.UpdateSingleton(); Check(!SnapshotResyncService.Active);
});
Test("Guests reconnecting in time get a separate load deadline", () =>
{
    var f=new Fixture(); var id=f.ReloadGrace(); var host=(ServerEventIO)EventIO.Get(); var a=new Peer(); var b=new Peer();
    host.NetBase.ReconnectTickets.Claim(a,f.Tickets[0],true);host.NetBase.ReconnectTickets.Claim(b,f.Tickets[1],true);
    State().GetType().GetField("ReconnectDeadline").SetValue(State(),double.NegativeInfinity); ReplayService.IsLoaded=true;
    f.Service.UpdateSingleton(); Check(SnapshotResyncService.Active && f.Dialogs.Shown==0);
    SnapshotResyncService.Receive(host,a,Control("ResyncReady",id)); f.Service.UpdateSingleton(); Check(SnapshotResyncService.Active);
    SnapshotResyncService.Receive(host,b,Control("ResyncReady",id)); f.Service.UpdateSingleton(); Check(!SnapshotResyncService.Active);
});
Test("Cancel recovery leaves the world paused and invalidates pending admission", () =>
{
    var f=new Fixture(); f.ReloadGrace(); ReplayService.IsLoaded=true; f.Service.UpdateSingleton();
    SnapshotResyncStatus.Cancel(); Check(SnapshotResyncService.Active && f.Dialogs.Shown==1 && SingletonManager.Replay.Frozen);
    var host=(ServerEventIO)EventIO.Get(); bool rejected=false;try{host.NetBase.ReconnectTickets.Claim(new Peer(),f.Tickets[0],true);}catch(IOException){rejected=true;}Check(rejected);
    f.Service.UpdateSingleton();Check(SingletonManager.Replay.Recorded.Count==0);
});
Test("Guest transport loss uses ticket admission metadata when reload notice was missed", () =>
{
    var f=new Fixture();var old=new ClientEventIO();EventIO.Set(old);
    var net=new TimberClient(new TCPClientWrapper("127.0.0.1",1)){ReconnectToken=new string('A',64)};
    Check(SnapshotResyncService.TryConnectionLost(old,net)); Check(f.Connection.Reconnects==1 && SingletonManager.Replay.Frozen);
    var fresh=new ClientEventIO(); fresh.NetBase.RecoveryId="new-session";fresh.NetBase.RecoveryDigest=(string)Control("x","x")["snapshotSha256"];EventIO.Set(fresh);
    Check(SnapshotResyncService.MapLoading(fresh,ServerHostingUtils.Bytes)); SnapshotResyncService.InitializedClient(); f.Service.UpdateSingleton();
    Check((string)fresh.NetBase.Sent.Single()["id"]=="new-session");
    SnapshotResyncService.Receive(fresh,new Peer(),Control("ResyncComplete","new-session"));Check(!SnapshotResyncService.Active);
});
Test("Guest refuses old initial map when host never started snapshot recovery", () =>
{
    var f=new Fixture();var old=new ClientEventIO();EventIO.Set(old);
    var net=new TimberClient(new TCPClientWrapper("127.0.0.1",1)){ReconnectToken=new string('A',64)};
    Check(SnapshotResyncService.TryConnectionLost(old,net));var fresh=new ClientEventIO();EventIO.Set(fresh);
    Check(!SnapshotResyncService.MapLoading(fresh,ServerHostingUtils.Bytes) && f.Dialogs.Shown==1);
});
Test("Replay failures, initial joins and opt-out never start automatic reconnect", () =>
{
    var f=new Fixture();var old=new ClientEventIO();EventIO.Set(old);var net=new TimberClient(new TCPClientWrapper("127.0.0.1",1)){ReconnectToken="ticket"};
    ReplayService.HasReplayFailure=true;Check(!SnapshotResyncService.TryConnectionLost(old,net));ReplayService.HasReplayFailure=false;
    ReplayService.IsLoaded=false;Check(!SnapshotResyncService.TryConnectionLost(old,net));ReplayService.IsLoaded=true;
    Settings.ReconnectGraceEnabled=false;Check(!SnapshotResyncService.TryConnectionLost(old,net));Settings.ReconnectGraceEnabled=true;
});
Test("A second dropped loading connection after admission closes remains paused", () =>
{
    var f=new Fixture(); f.ReloadGrace(); var host=(ServerEventIO)EventIO.Get(); var peer=new Peer();
    host.NetBase.ReconnectTickets.Claim(peer,f.Tickets[0],true);host.NetBase.ClientCount=1;ReplayService.IsLoaded=true;
    SnapshotResyncService.ContinueWithoutMissingPlayers();peer.Close();SnapshotResyncService.PeerDisconnected(host,peer);
    Check(f.Dialogs.Shown==1 && SnapshotResyncService.Active && SingletonManager.Replay.Recorded.Count==0);
});
Test("Loss after prepare and during snapshot load retries instead of canceling recovery", () =>
{
    var f=new Fixture(); var old=new ClientEventIO();EventIO.Set(old);
    var prepare=Control("ResyncPrepare","abc");prepare["connectionRecovery"]=true;
    SnapshotResyncService.Receive(old,new Peer(),prepare);SnapshotResyncService.ConnectionFailed("lost before reload");
    Check(SnapshotResyncService.IsReconnecting && f.Connection.Reconnects==1 && f.Dialogs.Shown==0);
    var fresh=new ClientEventIO();fresh.NetBase.RecoveryId="abc";fresh.NetBase.RecoveryDigest=(string)Control("x","x")["snapshotSha256"];EventIO.Set(fresh);
    Check(SnapshotResyncService.MapLoading(fresh,ServerHostingUtils.Bytes));SnapshotResyncService.ConnectionFailed("lost while loading");
    Check(SnapshotResyncService.IsReconnecting && f.Connection.Reconnects==2 && f.Dialogs.Shown==0);
});
Test("Paused hosts remain paused after the reconnect grace period", () =>
{
    var f=new Fixture();SingletonManager.Replay.TargetSpeed=0;f.ReloadGrace();var host=(ServerEventIO)EventIO.Get();host.NetBase.ClientCount=0;
    ReplayService.IsLoaded=true;SnapshotResyncService.ContinueWithoutMissingPlayers();f.Service.UpdateSingleton();
    Check(!SnapshotResyncService.Active && SingletonManager.Replay.Recorded.Single().speed==0);
});
Test("Cancel during an asynchronous save invalidates its late completion", () =>
{
    var f=new Fixture();f.BeginGrace();SingletonManager.Replay.Finish();f.Service.UpdateSingleton();Check(f.Saves.Saves==1);
    SnapshotResyncStatus.Cancel();f.Saves.Saved(new SaveReference());f.Host.NetBase.Flush.SetResult(true);f.Service.UpdateSingleton();
    Check(f.Loader.Loads==0 && f.Host.NetBase.Sent.All(m=>(string)m["command"]!="ResyncReload") && f.Dialogs.Shown==1);
});
Test("Disk or scene-load failures stop recovery rather than retrying as a network hiccup", () =>
{
    var f=new Fixture();var old=new ClientEventIO();EventIO.Set(old);var net=new TimberClient(new TCPClientWrapper("127.0.0.1",1)){ReconnectToken="ticket"};
    Check(SnapshotResyncService.TryConnectionLost(old,net));SnapshotResyncService.ConnectionFailed("disk full",false);f.Service.UpdateSingleton();
    Check(!SnapshotResyncService.IsReconnecting && f.Dialogs.Shown==1 && f.Connection.Reconnects==1);
});
Console.WriteLine($"{total-failed}/{total} passed (production coordinator; mocked Unity scene/save boundaries)");
return failed==0 ? 0 : 1;

sealed class Fixture
{
    public SnapshotResyncService Service;
    public RehostingService Saves = new(); public ClientConnectionService Connection=new();
    public GameSceneLoader Loader=new(); public DialogBoxShower Dialogs=new();
    public ServerEventIO Host=new();
    public Fixture()
    {
        SnapshotResyncService.Reset(); ReplayService.HasReplayFailure=false; ReplayService.IsLoaded=true;
        Settings.SnapshotResyncEnabled=true; Settings.ReconnectGraceEnabled=true; ReplayService.CompatibilityReady=true; SingletonManager.Replay=new() { DeferFinish=true }; EventIO.Set(Host);
        Service=new(Saves,Loader,new GameSaveRepository(),Dialogs,Connection);
    }
    public string[] Tickets;
    public void BeginGrace()
    {
        Host.NetBase.ReconnectTickets=new ReconnectTickets(); var a=new Peer();var b=new Peer();
        Host.NetBase.ReconnectTickets.Claim(a,"",true);Host.NetBase.ReconnectTickets.Activate(a);
        Host.NetBase.ReconnectTickets.Claim(b,"",true);Host.NetBase.ReconnectTickets.Activate(b);
        Tickets=Host.NetBase.ReconnectTickets.Export(); a.Close();Host.NetBase.ReconnectTickets.Release(a);Host.NetBase.ClientCount=1;
        SnapshotResyncService.PeerDisconnected(Host,new TCPClientWrapper("127.0.0.1",1));
    }
    public string ReloadGrace()
    {
        BeginGrace();SingletonManager.Replay.Finish();Service.UpdateSingleton();Saves.Saved(new SaveReference());
        Host.NetBase.Flush.SetResult(true);Service.UpdateSingleton();return (string)Host.NetBase.Sent.First()["id"];
    }
    public string ReloadHost()
    {
        SnapshotResyncService.TryRecover(SingletonManager.Replay); SingletonManager.Replay.Finish();
        Service.UpdateSingleton(); Saves.Saved(new SaveReference()); Host.NetBase.Flush.SetResult(true); Service.UpdateSingleton();
        return (string)Host.NetBase.Sent.First()["id"];
    }
}
sealed class Peer : ISocketStream
{
    public bool Connected { get; private set; }=true;
    public string Name=>"guest";
    public int MaxChunkSize=>1024; public int MaxBytesPerSecond=>int.MaxValue;
    public Task ConnectAsync()=>Task.CompletedTask;
    public void Close()=>Connected=false;
    public int Read(byte[] b,int o,int c)=>0;
    public void Write(byte[] b,int o,int c) { }
}
