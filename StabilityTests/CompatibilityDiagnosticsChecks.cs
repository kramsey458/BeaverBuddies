using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using TimberNet;

static class CompatibilityDiagnosticsChecks
{
    static void Check(bool value, string reason = "assertion failed") { if (!value) throw new Exception(reason); }
    static void Throws(Action run) { try { run(); } catch { return; } throw new Exception("Expected failure"); }
    static void Until(Func<bool> done) => Check(SpinWait.SpinUntil(done, 3000), "TCP operation timed out");
    static string Profile(params (string Key, string Value)[] extra)
    {
        var entries = new Dictionary<string,string> { ["game"]="1.1.2.4", ["beaverbuddies"]="11", ["mod/housing"]="1", ["order/0000"]="housing" };
        foreach(var pair in extra) entries[pair.Key]=pair.Value;
        return CompatibilityProfile.Encode(entries);
    }
    static JObject Loaded(string profile) { var message=TimberNetBase.Control("CompatibilityLoaded"); message["profile"]=profile; return message; }
    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Compatibility identifies missing mods, differing versions, order and code", () =>
        {
            foreach(var key in new[]{"mod/housing","order/0000","files/housing","code/housing/lib.dll","game","beaverbuddies","session/detailedTracing"})
                Check(CompatibilityProfile.Difference(Profile(),Profile((key,"changed")),false)?.Contains(key)==true,key);
        });
        yield return ("Compatibility defers scene-only settings but rejects shared differing settings", () =>
        {
            string menu=Profile(), game=Profile(("setting/housing/Capacity","hash-a"));
            Check(CompatibilityProfile.Difference(menu,game,false)==null);
            Check(CompatibilityProfile.Difference(menu,game,true)?.Contains("guest only")==true);
            Check(CompatibilityProfile.Difference(game,Profile(("setting/housing/Capacity","hash-b")),false)!=null);
        });
        yield return ("Compatibility rejects oversized malformed duplicate and incomplete profiles", () =>
        {
            foreach(string text in new[]{new string('x',CompatibilityProfile.MaxCharacters+1),"{}","{\"schema\":2,\"entries\":{\"game\":\"x\",\"game\":\"y\",\"beaverbuddies\":\"z\"}}",Profile()+"{}"}) Throws(()=>CompatibilityProfile.Parse(text));
        });
        yield return ("Compatibility messages expose keys but no setting values", () =>
        {
            string secret="private-address-or-token";
            string difference=CompatibilityProfile.Difference(Profile(("setting/test/Secret",CompatibilityProfile.Digest(secret))),Profile(("setting/test/Secret","other")),true);
            Check(difference.Contains("setting/test/Secret") && !difference.Contains(secret) && !difference.Contains(CompatibilityProfile.Digest(secret)));
        });
        yield return ("Bounded decompression stops a compressed expansion", () => Throws(()=>CompressionUtils.Decompress(CompressionUtils.Compress(new string('x',50000)),1024)));
        yield return ("Admission waits for host and every joining peer, independent of load order", () =>
        {
            foreach(bool guestFirst in new[]{true,false})
            {
                object first=new(),second=new(); int responses=0,failures=0;
                var gate=new CompatibilityAdmission(true,(_,m)=> { Check((string)m["command"]=="CompatibilityAccepted"); responses++; },_=>failures++);
                Check(!gate.Ready(new[]{first,second}));
                if(!guestFirst) gate.Loaded(Profile());
                gate.Receive(first,Loaded(Profile())); Check(!gate.Ready(new[]{first,second}));
                if(guestFirst) gate.Loaded(Profile());
                Check(gate.Ready(new[]{first}) && !gate.Ready(new[]{first,second}));
                gate.Receive(second,Loaded(Profile())); Check(gate.Ready(new[]{first,second}) && responses==2 && failures==0);
            }
        });
        yield return ("Admission fails closed for loaded settings mismatch and duplicate submissions", () =>
        {
            object peer=new(); int failures=0; var sent=new List<JObject>();
            var gate=new CompatibilityAdmission(true,(_,m)=>sent.Add(m),_=>failures++);
            gate.Loaded(Profile()); gate.Receive(peer,Loaded(Profile(("setting/test/gameOnly","different"))));
            Check(!gate.Ready(new[]{peer}) && failures==1 && (string)sent.Single()["command"]=="CompatibilityRejected");
            failures=0; gate=new CompatibilityAdmission(true,(_,_)=>{},_=>failures++); gate.Loaded(Profile());
            gate.Receive(peer,Loaded(Profile())); gate.Receive(peer,Loaded(Profile()));
            Check(failures==1 && !gate.Ready(new[]{peer}));
        });
        yield return ("Fresh admission on snapshot reload requires a new settings acknowledgement", () =>
        {
            for(int scene=0;scene<2;scene++)
            {
                int sent=0; var gate=new CompatibilityAdmission(false,(_,_)=>sent++,_=>throw new Exception());
                Check(!gate.Ready(Array.Empty<object>())); gate.Loaded(Profile()); Check(!gate.Ready(Array.Empty<object>()) && sent==1);
                gate.Receive(new object(),TimberNetBase.Control("CompatibilityAccepted")); Check(gate.Ready(Array.Empty<object>()));
            }
        });
        yield return ("Real TCP gates replay until post-load check and preserves snapshot and command hashes", () =>
        {
            var listener=new LocalListener(); var bytes=new byte[]{1,2,3,4};
            var host=new TimberServer(listener,()=>Task.FromResult(bytes),()=>new JObject { ["type"]="InitializeClient",["ticksSinceLoad"]=0 }) { CompatibilityIdentity=Profile() };
            TimberClient guest=null;
            try
            {
                host.Start(); guest=new TimberClient(new TCPClientWrapper("127.0.0.1",listener.Port)) { CompatibilityIdentity=Profile() };
                bool loaded=false; guest.OnMapReceived+=_=>loaded=true; guest.Start();
                Until(()=> {host.Update();guest.Update();return loaded && host.ClientCount==1;});
                Check(!host.ShouldTick && !guest.CompatibilityVerified && guest.ReadEvents(0).Count==0);
                guest.SubmitLoadedCompatibility(Profile(("setting/game/value","a")));
                for(int i=0;i<20;i++){host.Update();guest.Update();Thread.Sleep(1);}
                Check(!host.ShouldTick && !guest.CompatibilityVerified);
                host.SubmitLoadedCompatibility(Profile(("setting/game/value","a")));
                Until(()=> {host.Update();guest.Update();return host.CompatibilityVerified && guest.CompatibilityVerified;});
                var replayed=new List<JObject>(); Until(()=>{replayed.AddRange(guest.ReadEvents(0));return replayed.Count==1;});
                Check(host.Hash==guest.Hash && host.SnapshotDigest==guest.SnapshotDigest && host.SnapshotDigest?.Length==64);
                host.DoUserInitiatedEvent(new JObject { ["type"]="Action",["ticksSinceLoad"]=1 });
                Until(()=>{replayed.AddRange(guest.ReadEvents(1));return replayed.Count==2;}); Check(host.Hash==guest.Hash);
            }
            finally {guest?.Close();host.Close();}
        });
        yield return ("Real TCP rejects loaded-only mismatch without replaying initialization", () =>
        {
            var listener=new LocalListener(); var host=new TimberServer(listener,()=>Task.FromResult(new byte[]{1}),null) {CompatibilityIdentity=Profile()}; TimberClient guest=null;
            try
            {
                host.Start(); guest=new TimberClient(new TCPClientWrapper("127.0.0.1",listener.Port)){CompatibilityIdentity=Profile()}; bool loaded=false; string fault=null;
                guest.OnMapReceived+=_=>loaded=true; guest.OnSessionFault+=s=>fault=s; guest.Start();
                Until(()=>{host.Update();guest.Update();return loaded;});
                host.SubmitLoadedCompatibility(Profile()); guest.SubmitLoadedCompatibility(Profile(("setting/test/gameOnly","bad")));
                Until(()=>{host.Update();guest.Update();return fault!=null;});
                Check(!guest.CompatibilityVerified && guest.ReadEvents(0).Count==0 && fault.Contains("setting/test/gameOnly"), fault);
            }
            finally {guest?.Close();host.Close();}
        });
        yield return ("Rolling recorder retains bounded latest history and freezes independently of reset", () =>
        {
            string directory=Path.Combine(Path.GetTempPath(),"bb-rolling-test-"+Guid.NewGuid().ToString("N"));
            try
            {
                var recorder=new RollingTrace();
                for(int i=0;i<500;i++){recorder.Snapshot(new JObject{["tick"]=i});recorder.Event(new JObject{["n"]=i});}
                var frozen=recorder.Freeze(new JObject{["role"]="test"}); recorder.Clear();
                using var zip=ZipFile.OpenRead(frozen.Write(directory,"rolling-test.zip")); using var reader=new StreamReader(zip.GetEntry("report.json").Open());
                var report=JObject.Parse(reader.ReadToEnd());
                Check(report["snapshots"].Count()==120 && (int)report["snapshots"][0]["tick"]==380);
                Check(report["events"].Count()==256 && (int)report["events"][0]["n"]==244);
            }
            finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
        });
        yield return ("Rolling retention keeps ten archives and preserves unrelated diagnostic files", () =>
        {
            string directory=Path.Combine(Path.GetTempPath(),"bb-retention-test-"+Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory,"water-snapshot.zip"),"keep");
                var report=new RollingTrace().Freeze(new JObject());
                for(int i=0;i<12;i++)report.Write(directory,$"rolling-{i}.zip");
                Check(Directory.GetFiles(directory,"rolling-*.zip").Length==10 && File.Exists(Path.Combine(directory,"water-snapshot.zip")));
                Throws(()=>report.Write(directory,"../escape.zip"));
            }
            finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
        });
        yield return ("Rolling recorder rejects unbounded records and writer propagates IO failure", () =>
        {
            var recorder=new RollingTrace(); Throws(()=>recorder.Event(new JObject{["bad"]=new string('x',RollingTrace.EventCharacters+1)}));
            string file=Path.GetTempFileName();try {Throws(()=>recorder.Freeze(new JObject()).Write(file,"rolling-error.zip"));}finally{File.Delete(file);}
        });
    }
    sealed class LocalListener : ISocketListener
    {
        readonly TcpListener listener=new(IPAddress.Loopback,0);
        public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
        public void Start()=>listener.Start();
        public ISocketStream AcceptClient()=>new TCPClientWrapper(listener.AcceptTcpClient());
        public void Stop()=>listener.Stop();
    }
}
