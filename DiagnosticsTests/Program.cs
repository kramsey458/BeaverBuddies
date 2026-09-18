using System.Reflection;
using System.Globalization;
using System.IO.Compression;
using BeaverBuddies;
using BeaverBuddies.IO;
using BeaverBuddies.DesyncDetecter;
using ModSettings.Core;
using Newtonsoft.Json.Linq;
using Timberborn.Modding;
using Timberborn.EntitySystem;
using Timberborn.InventorySystem;
using Timberborn.WaterSystem;
using TimberNet;

int passed=0,failed=0;
void Check(bool condition,string reason="assertion failed") {if(!condition)throw new Exception(reason);}
void Test(string name,Action action) {try {action();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.GetBaseException().Message);}}
string root=Path.Combine(Path.GetTempPath(),"bb-diagnostics-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
var repository=new ModRepository();var owners=new ModSettingsOwnerRegistry();
Mod AddMod(string id){var mod=new Mod();mod.Manifest.Id=id;mod.ModDirectory.Path=Path.Combine(root,id);Directory.CreateDirectory(mod.ModDirectory.Path);repository.EnabledMods.Add(mod);return mod;}
var own=AddMod(Plugin.ID);var housing=AddMod("housing");var owner=new SampleSettings();owners.Owners[housing]=new(){owner};owners.Owners[own]=new(){new SampleSettings()};
var compatibility=new BuildCompatibility(repository,owners);compatibility.Load();
try
{
    Test("Profile scans enabled mods, preserves load order and reads registered game settings",()=>{
        var entries=CompatibilityProfile.Parse(BuildCompatibility.CreateIdentity());
        Check(entries["order/0001"]=="housing" && entries["mod/housing"]=="1");
        Check(entries.Keys.Any(k=>k.EndsWith("/Capacity")) && !entries.Keys.Any(k=>k.StartsWith("setting/beaverbuddies/")));
        Check(!entries.Keys.Any(k=>k.EndsWith("/Button")));
    });
    Test("Profile notices modified and newly added configuration files",()=>{
        string before=BuildCompatibility.CreateIdentity();File.WriteAllText(Path.Combine(housing.ModDirectory.Path,"config.json"),"{\"n\":1}");
        string added=BuildCompatibility.CreateIdentity();Check(CompatibilityProfile.Difference(before,added,true).Contains("files/housing"));
        File.WriteAllText(Path.Combine(housing.ModDirectory.Path,"config.json"),"{\"n\":100}");Check(CompatibilityProfile.Difference(added,BuildCompatibility.CreateIdentity(),true)!=null);
    });
    Test("Profile fingerprints loaded assembly modules as well as disk files",()=>{
        string path=Path.Combine(housing.ModDirectory.Path,"TimberNet.dll");File.Copy(typeof(TimberNetBase).Assembly.Location,path);
        var entries=CompatibilityProfile.Parse(BuildCompatibility.CreateIdentity());Check(entries["code/housing/TimberNet.dll"].Contains(typeof(TimberNetBase).Module.ModuleVersionId.ToString("D")));
    });
    Test("Setting values are hashed and culture independent",()=>{
        var previous=CultureInfo.CurrentCulture;
        try {CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("en-US");string first=BuildCompatibility.CreateIdentity();CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");Check(first==BuildCompatibility.CreateIdentity());Check(!first.Contains("private text"));}
        finally{CultureInfo.CurrentCulture=previous;}
    });
    Test("Gameplay settings differ while local diagnostics preferences do not",()=>{
        string before=BuildCompatibility.CreateIdentity();Settings.RollingDiagnosticsEnabled=false;Check(before==BuildCompatibility.CreateIdentity());Settings.RollingDiagnosticsEnabled=true;
        owner.Capacity.Value=7;Check(CompatibilityProfile.Difference(before,BuildCompatibility.CreateIdentity(),true).Contains("Capacity"));
    });
    Test("Detailed trace mode is checked because it changes replay traffic",()=>{
        string before=BuildCompatibility.CreateIdentity();Settings.Debug=true;Check(CompatibilityProfile.Difference(before,BuildCompatibility.CreateIdentity(),true).Contains("session/detailedTracing"));Settings.Debug=false;
    });
    Test("Unsupported registered setting fails visibly instead of silently skipping",()=>{
        owner.ModSettings.Add(new ModSetting<DateTime>{Value=DateTime.UtcNow}); // IFormattable is supported, but duplicate custom IDs remain explicit
        owner.ModSettings.Add(new ModSetting<object>{Value=new object()});
        bool threw=false;try{BuildCompatibility.CreateIdentity();}catch(IOException){threw=true;}Check(threw);owner.ModSettings.RemoveRange(owner.ModSettings.Count-2,2);
    });
    Test("Rolling sampler reads bounded windows without altering water, inventory or RNG",()=>{
        var fixture=Fixture();var recorder=fixture.Recorder;var rng=UnityEngine.Random.state;
        RollingDiagnosticsService.CaptureBoundary(0);RollingDiagnosticsService.CaptureBoundary(0);RollingDiagnosticsService.CaptureBoundary(1);RollingDiagnosticsService.CaptureBoundary(20);
        var report=Report(recorder);Check(report["snapshots"].Count()==2);
        var sample=report["snapshots"][0];Check(sample["entities"].Count()==64 && sample["water"].Count()==256);
        Check(UnityEngine.Random.state.Equals(rng) && fixture.Water._threadSafeWaterColumns[0].WaterDepth==4 && fixture.Inventory.Stock[0].Amount==10);
        Check((int)report["snapshots"][1]["water"][0][0]==256);
    });
    Test("Rolling samples detect jobs, inventory reservations and water contamination changes",()=>{
        var f=Fixture(1);RollingDiagnosticsService.CaptureBoundary(0);var first=Report(f.Recorder)["snapshots"][0];
        f.Inventory._reservedStock.Goods.Add(new(){GoodId="Log",Amount=2});f.Water._threadSafeWaterColumns[0].Contamination=.5f;
        // 40 returns to the same water window.
        RollingDiagnosticsService.CaptureBoundary(40);var second=Report(f.Recorder)["snapshots"][1];
        Check((string)first["entities"][0]["inventory"]!=(string)second["entities"][0]["inventory"]);
        Check((int)first["water"][0][3]!=(int)second["water"][0][3]);
    });
    Test("Recent action records include target IDs but omit private text and heavy trace events",()=>{
        var f=Fixture();var target=Guid.NewGuid();RollingDiagnosticsService.Record(new RenameEvent{entityID=target.ToString(),name="private player text"},"before");RollingDiagnosticsService.Record(new HeartbeatEvent(),"before");
        var report=Report(f.Recorder);Check(report["events"].Count()==1 && !report.ToString().Contains("private player text"));Check((string)report["events"][0]["targets"][0]==target.ToString());
    });
    Test("Diagnostic sampling failures disable only the recorder",()=>{
        var f=Fixture();f.Water._threadSafeWaterColumns=null;RollingDiagnosticsService.CaptureBoundary(0);RollingDiagnosticsService.CaptureBoundary(20);
        Check((bool)typeof(RollingDiagnosticsService).GetField("failed",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f.Recorder));
    });
    Test("Disabled diagnostics and scene reset leave no new records",()=>{
        var f=Fixture();Settings.RollingDiagnosticsEnabled=false;RollingDiagnosticsService.CaptureBoundary(0);Check(Report(f.Recorder)["snapshots"].Count()==0);
        Settings.RollingDiagnosticsEnabled=true;RollingDiagnosticsService.CaptureBoundary(20);f.Recorder.Reset();RollingDiagnosticsService.CaptureBoundary(40);Check(Report(f.Recorder)["snapshots"].Count()==0);
    });
    Test("Automatic export survives scene reset and sends one shared capture notice",()=>{
        var f=Fixture();RollingDiagnosticsService.CaptureBoundary(0);RollingDiagnosticsService.Trigger("test fault");RollingDiagnosticsService.Trigger("duplicate");f.Recorder.Reset();
        Check(f.Network.Controls==1);WaitWrites();string directory=Path.Combine(UnityEngine.Application.persistentDataPath,"BeaverBuddiesDiagnostics");
        Check(Directory.GetFiles(directory,"rolling-*.zip").Length==1);using var zip=ZipFile.OpenRead(Directory.GetFiles(directory,"rolling-*.zip")[0]);using var reader=new StreamReader(zip.GetEntry("report.json").Open());Check(JObject.Parse(reader.ReadToEnd())["snapshots"].Count()==1);
    });
}
finally {WaitWrites();Directory.Delete(root,true);}
Console.WriteLine($"{passed}/{passed+failed} passed");return failed==0?0:1;

void WaitWrites(){Check(SpinWait.SpinUntil(()=>(int)typeof(RollingDiagnosticsService).GetField("pendingWrites",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null)==0,3000),"writer timed out");}
(RollingDiagnosticsService Recorder,ThreadSafeWaterMap Water,Inventory Inventory,FakeNet Network) Fixture(int count=100)
{
    SingletonManager.Objects.Clear();new ReplayService();Settings.RollingDiagnosticsEnabled=true;var net=new FakeNet();ReplayService.Network=net;EventIO.Current=new ClientEventIO();
    UnityEngine.Application.persistentDataPath=Path.Combine(root,Guid.NewGuid().ToString("N"));var entities=new EntityRegistry();var stock=new Inventory{Capacity=50,TotalAmountInStock=10};stock.Stock.Add(new(){GoodId="Log",Amount=10});
    for(int i=0;i<count;i++){var entity=new EntityComponent{EntityId=Guid.NewGuid()};entity.Components.Add(stock);entities.Entities.Add(entity);}
    var water=new ThreadSafeWaterMap();water._threadSafeWaterColumns[0].WaterDepth=4;water._threadSafeColumnCounts[0]=1;
    return(new RollingDiagnosticsService(entities,water),water,stock,net);
}
JObject Report(RollingDiagnosticsService recorder)
{
    var trace=(RollingTrace)typeof(RollingDiagnosticsService).GetField("trace",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(recorder);
    string path=trace.Freeze(new JObject()).Write(Path.Combine(root,"inspection"),"rolling-"+Guid.NewGuid().ToString("N")+".zip");
    using var zip=ZipFile.OpenRead(path);using var reader=new StreamReader(zip.GetEntry("report.json").Open());return JObject.Parse(reader.ReadToEnd());
}
sealed class SampleSettings:ModSettingsOwner
{
    public ModSetting<int> Capacity{get;}=new(){Value=5};public ModSetting<float> Rate{get;}=new(){Value=1.25f};public ModSetting<bool> Enabled{get;}=new(){Value=true};public ModSetting<string> Name{get;}=new(){Value="private text"};public NonPersistentSetting Button{get;}=new();
    public SampleSettings(){ModSettings.AddRange(new ModSetting[]{Capacity,Rate,Enabled,Name,Button});}
}
sealed class RenameEvent:BeaverBuddies.Events.ReplayEvent { public string entityID,name; }
sealed class FakeNet:TimberNetBase {public int Controls;public override void SendControl(JObject m)=>Controls++;}
