using BeaverBuddies;
using BeaverBuddies.Activity;
using BeaverBuddies.Connect;
using BeaverBuddies.IO;
using Timberborn.Buildings;
using Timberborn.CameraSystem;
using Timberborn.EntitySystem;
using Timberborn.InputSystem;
using Timberborn.SceneLoading;
using Timberborn.SelectionSystem;
using Timberborn.TerrainQueryingSystem;
using TimberNet;
using UnityEngine;

int failures=0;
var tests = new (string Name, Action<World> Run)[]
{
    ("Cursor samples world hits, falls back to terrain, and hides off-map", w =>
    {
        w.Raycaster.Hit=true; w.Raycaster.Position=new(8,9,10); w.Step();
        Check(w.Last.CursorVisible && w.Last.X==8 && w.Last.Y==9);
        w.Raycaster.Hit=false; w.Terrain.Hit=new Hit {Intersection=new(1,2,3)}; w.Step();
        Check(w.Last.CursorVisible && w.Last.X==1);
        w.Terrain.Hit=null; w.Step(); Check(!w.Last.CursorVisible);
    }),
    ("UI hides world cursor but retains selection; lost focus clears both", w =>
    {
        w.Input.MouseOverUI=true; w.Step(); Check(!w.Last.CursorVisible && w.Last.Selection==w.Entity.EntityId.ToString());
        Application.isFocused=false; w.Step(); Check(!w.Last.CursorVisible && w.Last.Selection=="");
    }),
    ("Sampling caps at ten per second without low-FPS catch-up bursts", w =>
    {
        w.Step(); int count=w.Net.Sent.Count;
        for(int i=0;i<100;i++) w.Service.UpdateSingleton(); Check(w.Net.Sent.Count==count);
        Time.unscaledTime+=60; w.Service.UpdateSingleton(); Check(w.Net.Sent.Count==count+1);
    }),
    ("Native outline ownership preserves local highlights and releases changed selections", w =>
    {
        var local=new Highlighter(); local.HighlightSecondary(w.Entity,new Color());
        w.Receive(); var remote=w.Service.RemotePlayers.Single(); Check(remote.Highlighter.Targets.Contains(w.Entity));
        w.Net.Inject(new PlayerActivity(1,"Alex","12ABEF",false,0,0,0));
        Check(remote.Highlighter.Targets.Count==0 && local.Targets.Contains(w.Entity));
    }),
    ("Disconnected peer expires cursor, selection and edit badge", w =>
    {
        w.Receive(); var remote=w.Service.RemotePlayers.Single(); Time.unscaledTime+=4; w.Service.UpdateSingleton();
        Check(!w.Service.RemotePlayers.Any() && remote.Highlighter.Targets.Count==0);
    }),
    ("Deleted or unavailable entity does not become a remote selection", w =>
    {
        w.Entity.Deleted=true; w.Receive(); Check(w.Service.RemotePlayers.Single().Selected==null);
    }),
    ("Switching session removes old highlights and ignores the old network", w =>
    {
        w.Receive(); var old=w.Service.RemotePlayers.Single();
        EventIO.Current=new ClientEventIO {NetBase=new TestNet()}; w.Step(); w.Receive();
        Check(!w.Service.RemotePlayers.Any() && old.Highlighter.Targets.Count==0);
    }),
    ("Loading removes overlay state and network subscription before scene replacement", w =>
    {
        w.Receive(); var old=w.Service.RemotePlayers.Single(); w.Loading.Show(); w.Receive();
        Check(!w.Service.RemotePlayers.Any() && old.Highlighter.Targets.Count==0);
    }),
    ("Disabling activity sends a clear once and stops sharing or receiving", w =>
    {
        w.Receive(); Settings.PlayerActivityEnabled=false; w.Step(); int count=w.Net.Sent.Count;
        Check(!w.Last.CursorVisible && w.Last.Selection=="" && !w.Service.RemotePlayers.Any());
        w.Step(); w.Receive(); Check(w.Net.Sent.Count==count && !w.Service.RemotePlayers.Any());
        Settings.PlayerActivityEnabled=true; w.Step(); Check(w.Net.Sent.Count==count+1);
    }),
    ("Recovery suppresses activity until the session is ready", w =>
    {
        w.Receive(); SnapshotResyncService.Active=true; w.Step(); w.Receive();
        Check(!w.Service.RemotePlayers.Any() && w.Last.Selection=="");
    }),
    ("Only local edits show an editing badge, expiring after three seconds", w =>
    {
        ReplayService.IsReplayingEvents=true; PlayerActivityService.NotifyLocalEdit(w.Entity.EntityId.ToString()); w.Step();
        Check(w.Last.Editing==""); ReplayService.IsReplayingEvents=false;
        DeterminismService.IsTicking=true; PlayerActivityService.NotifyLocalEdit(w.Entity.EntityId.ToString()); w.Step();
        Check(w.Last.Editing==""); DeterminismService.IsTicking=false;
        PlayerActivityService.NotifyLocalEdit(w.Entity.EntityId.ToString()); w.Step(); Check(w.Last.Editing==w.Last.Selection);
        Time.unscaledTime+=4; w.Step(); Check(w.Last.Editing=="");
    }),
    ("Remote cursor interpolation progresses between world positions", w =>
    {
        w.Receive(); var p=w.Service.RemotePlayers.Single();
        w.Net.Inject(new PlayerActivity(1,"Alex","12ABEF",true,20,0,0));
        Check(Math.Abs(p.CursorPosition(Time.unscaledTime+.05f).x-15)<.01);
    })
};
foreach (var (name,run) in tests)
{
    using var world=new World();
    try { run(world); Console.WriteLine("PASS "+name); }
    catch(Exception e) { failures++; Console.WriteLine("FAIL "+name+": "+e); }
}
Console.WriteLine($"{tests.Length-failures}/{tests.Length} passed");
return failures==0?0:1;
static void Check(bool value) { if(!value) throw new Exception("assertion failed"); }

sealed class TestNet : TimberNetBase
{
    public readonly List<PlayerActivity> Sent=new();
    public override void SendActivity(PlayerActivity state) => Sent.Add(state);
    public void Inject(PlayerActivity state) => ProcessActivity(state);
}
sealed class World : IDisposable
{
    public readonly TestNet Net=new();
    public readonly InputService Input=new();
    public readonly SelectableObjectRaycaster Raycaster=new();
    public readonly TerrainPicker Terrain=new();
    public readonly LoadingScreen Loading=new();
    public readonly EntityComponent Entity=new() {EntityId=Guid.NewGuid()};
    public readonly PlayerActivityService Service;
    public PlayerActivity Last=>Net.Sent.Last();
    public World()
    {
        Time.unscaledTime=0; Application.isFocused=true; Settings.PlayerActivityEnabled=true;
        ReplayService.IsLoaded=true; ReplayService.HasReplayFailure=false; ReplayService.IsReplayingEvents=false;
        SnapshotResyncService.Active=false; DeterminismService.IsTicking=false;
        Entity.Components[typeof(EntityComponent)]=Entity; Entity.Components[typeof(Building)]=new Building();
        Entity.Components[typeof(HighlightableObject)]=new HighlightableObject();
        var selected=new SelectableObject {Components=Entity.Components};
        var registry=new EntityRegistry(); registry.Entities.Add(Entity.EntityId,Entity);
        EventIO.Current=new ServerEventIO {NetBase=Net}; Net.Start();
        Service=new PlayerActivityService(Input,new CameraService(),Terrain,Raycaster,new EntitySelectionService {SelectedObject=selected},registry,Loading);
        Service.PostLoad(); Step();
    }
    public void Receive() => Net.Inject(new PlayerActivity(1,"Alex","12ABEF",true,10,0,0,Entity.EntityId.ToString(),Entity.EntityId.ToString()));
    public void Step() {Time.unscaledTime+=.11f; Service.UpdateSingleton();}
    public void Dispose() { Service.Reset(); Net.Close(); }
}
