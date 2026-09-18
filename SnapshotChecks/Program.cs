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
        Settings.SnapshotResyncEnabled=true; SingletonManager.Replay=new() { DeferFinish=true }; EventIO.Set(Host);
        Service=new(Saves,Loader,new GameSaveRepository(),Dialogs,Connection);
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
