using System.Reflection;
using BeaverBuddies;
using BeaverBuddies.Status;
using TimberNet;
using UnityEngine.UIElements;
using Timberborn.SceneLoading;
using Timberborn.UILayoutSystem;
using Newtonsoft.Json.Linq;

int passed=0,failed=0;
void Check(bool condition,string why="assertion failed"){if(!condition)throw new Exception(why);}
void Test(string name,Action action){try{action();Console.WriteLine("PASS "+name);passed++;}catch(Exception e){Console.WriteLine("FAIL "+name+": "+e.GetBaseException().Message);failed++;}}
StatusFacts Facts()=>new(){Connected=true,Loaded=true,Compatible=true,Rate=20,ConnectedPeers=1};
PeerStatus Peer(int remote=100,int local=100,double rate=20,double milliseconds=25,double age=0,bool paused=false)
{
    var telemetry=new ConnectionTelemetry();object peer=new();telemetry.Probe(peer,0,_=>{});
    telemetry.Receive(peer,new JObject{["type"]=ConnectionTelemetry.MessageType,["reply"]=true,["sequence"]=1,["tick"]=remote,["rate"]=rate,["loaded"]=true,["paused"]=paused},milliseconds/1000,new(local,20,true,false),_=>{});
    return telemetry.Read(peer,milliseconds/1000+age);
}
Test("Tick rate follows simulation ticks, not rendering frequency",()=>{
    var slow=new TickRateMeter();var fast=new TickRateMeter();slow.Observe(0,true,0);fast.Observe(0,true,0);
    for(int i=1;i<=120;i++)fast.Observe(i/3,true,i/60.0);slow.Observe(40,true,2);
    Check(slow.Rate==20 && fast.Rate==20);
});
Test("Rate restarts cleanly across pause and map tick reset",()=>{
    var rate=new TickRateMeter();rate.Observe(100,true,0);rate.Observe(120,true,2);Check(rate.Rate==10);
    rate.Observe(120,false,3);Check(rate.Rate==0);rate.Observe(120,true,4);Check(rate.Rate==null);
    rate.Observe(0,true,5);Check(rate.Rate==null);rate.Observe(20,true,7);Check(rate.Rate==10);
});
Test("A received backlog is identified as local simulation catch-up",()=>{
    var f=Facts();f.Peers.Add(Peer());f.BufferedTicks=15;Check(StatusText.From(f).State=="Catching up");
});
Test("Paused simulation is never diagnosed as a slow computer",()=>{
    var f=Facts();f.Peers.Add(Peer(rate:0,paused:true));f.Paused=true;f.BufferedTicks=100;Check(StatusText.From(f).State=="Paused");
});
Test("Delayed response is distinguished from low simulation progress",()=>{
    var f=Facts();f.Peers.Add(Peer(milliseconds:400));Check(StatusText.From(f).State=="Connection delayed");
    f.Peers.Clear();f.Peers.Add(Peer(rate:0));Check(StatusText.From(f).State=="Host not advancing");
    f.Peers.Clear();f.Peers.Add(Peer(rate:-1));Check(StatusText.From(f).State=="Running");
});
Test("A stale sample never claims a current latency or a current simulation rate",()=>{
    var f=Facts();f.Peers.Add(Peer(age:6));var result=StatusText.From(f);Check(result.State=="Waiting for response" && result.Latency=="Measuring…" && result.Peers[0].Contains("awaiting response"));
});
Test("Host estimates guest lag at reply time rather than adding sample age",()=>{
    var f=Facts();f.Host=true;f.Tick=99999;f.Peers.Add(Peer(remote:98,local:100,age:3));Check(StatusText.From(f).Peers[0].Contains("~2 ticks behind"));
});
Test("Host warning allows for sample cadence and transit at high simulation speed",()=>{
    var f=Facts();f.Host=true;f.Rate=100;f.Peers.Add(Peer(remote:80,local:100,rate:100));Check(StatusText.From(f).State=="Running");
    f.Peers.Clear();f.Peers.Add(Peer(remote:20,local:100,rate:100));Check(StatusText.From(f).State=="Guest behind");
});
Test("Queued memory is explicitly separate from received simulation backlog",()=>{
    var f=Facts();f.Peers.Add(Peer());f.QueuedBytes=300*1024;f.QueuedMessages=50;f.BufferedTicks=0;
    var result=StatusText.From(f);Check(result.State=="Connection delayed" && result.Outgoing.Contains("300 KiB") && result.Incoming.Contains("0 ticks buffered"));
});
Test("Recovery, compatibility and disconnection supersede normal metrics",()=>{
    var f=Facts();f.Peers.Add(Peer());f.Recovering=true;f.Recovery="Saving";var recovering=StatusText.From(f);Check(recovering.State=="Recovering" && recovering.Latency=="—" && recovering.Peers.Length==0);
    f.Recovering=false;f.Compatible=false;Check(StatusText.From(f).State=="Checking mods");f.Connected=false;Check(StatusText.From(f).State=="Disconnected");f.Failed=true;Check(StatusText.From(f).State=="Session stopped");
});
Test("Host without guests has no fictitious ping",()=>{
    var f=Facts();f.Host=true;f.ConnectedPeers=0;Check(StatusText.From(f).State=="No guests connected" && StatusText.From(f).Latency=="—");
});
Test("HUD starts readable and collapse leaves just a compact header",()=>{
    var f=Fixture();f.Panel.UpdateSingleton();var root=f.Layout.Root[0];Check(root.style.width.Value==304 && root.style.display==DisplayStyle.Flex);
    ((Button)root[0][1]).Click();Check(Settings.StatusPanelCollapsed && root[2].style.display==DisplayStyle.None && root[1].style.display==DisplayStyle.None);
    ((Button)root[0][1]).Click();Check(!Settings.StatusPanelCollapsed && root[2].style.display==DisplayStyle.Flex);f.Panel.Reset();
});
Test("Hide removes HUD hit area and pause-menu restore preserves collapse preference",()=>{
    var f=Fixture();var root=f.Layout.Root[0];((Button)root[0][1]).Click();((Button)root[0][2]).Click();
    Check(!Settings.StatusPanelEnabled && root.style.display==DisplayStyle.None);f.Panel.Show();Check(Settings.StatusPanelEnabled && Settings.StatusPanelCollapsed && root.style.display==DisplayStyle.Flex);f.Panel.Reset();
});
Test("Reload removes the old HUD and respects saved local display preferences",()=>{
    var f=Fixture();Settings.SetStatusPanelVisible(false);Settings.SetStatusPanelCollapsed(true);f.Loading.Begin();Check(f.Layout.Root.childCount==0);f.Panel.UpdateSingleton();Check(f.Layout.Root.childCount==0);
    var next=new MultiplayerStatusPanel(f.Layout,new LoadingScreen());next.PostLoad();Check(f.Layout.Root.childCount==1 && f.Layout.Root[0].style.display==DisplayStyle.None);next.Reset();
});
Test("Panel consumes pointer and wheel events only inside its native HUD rectangle",()=>{
    var f=Fixture();var root=f.Layout.Root[0];var pointer=new PointerDownEvent();var wheel=new WheelEvent();root.Callbacks[typeof(PointerDownEvent)].DynamicInvoke(pointer);root.Callbacks[typeof(WheelEvent)].DynamicInvoke(wheel);
    Check(pointer.Stopped && wheel.Stopped && f.Layout.Root.Callbacks.Count==0);f.Panel.Reset();
});
Test("HUD updates do not tick or alter the network replay hash",()=>{
    var f=Fixture();var net=ReplayService.Network;int hash=net.Hash;for(int i=0;i<1000;i++)f.Panel.UpdateSingleton();Check(net.Hash==hash && net.TickCount==0);f.Panel.Reset();net.Close();
});
Test("Hiding the panel still publishes this player's simulation measurements",()=>{
    var f=Fixture();Settings.SetStatusPanelVisible(false);SingletonManager.GetSingleton<ReplayService>().TicksSinceLoad=42;f.Panel.UpdateSingleton();
    var sample=(SimulationStatus)typeof(TimberNetBase).GetField("simulationStatus",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(ReplayService.Network);
    Check(sample.Loaded && sample.Tick==42 && f.Layout.Root[0].style.display==DisplayStyle.None);f.Panel.Reset();
});
Test("Frequent HUD updates respect the quarter-second refresh budget",()=>{
    var f=Fixture();f.Panel.UpdateSingleton();var field=typeof(MultiplayerStatusPanel).GetField("nextUpdate",BindingFlags.Instance|BindingFlags.NonPublic);
    double scheduled=(double)field.GetValue(f.Panel);for(int i=0;i<100;i++)f.Panel.UpdateSingleton();Check(scheduled==(double)field.GetValue(f.Panel));f.Panel.Reset();
});
Console.WriteLine($"{passed}/{passed+failed} passed (production status code; mocked native UI)");return failed==0?0:1;

(MultiplayerStatusPanel Panel,UILayout Layout,LoadingScreen Loading) Fixture()
{
    SingletonManager.Items.Clear();Settings.StatusPanelEnabled=true;Settings.StatusPanelCollapsed=false;ReplayService.IsLoaded=true;ReplayService.CompatibilityReady=true;ReplayService.HasReplayFailure=false;
    BeaverBuddies.Connect.SnapshotResyncService.Active=false;new ReplayService();ReplayService.Network=new FakeNet();ReplayService.Network.Start();
    var layout=new UILayout();var loading=new LoadingScreen();var panel=new MultiplayerStatusPanel(layout,loading);panel.PostLoad();return(panel,layout,loading);
}
sealed class FakeNet:TimberNetBase { }
