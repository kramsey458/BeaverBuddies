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
    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Bounded decompression stops a compressed expansion", () => Throws(()=>CompressionUtils.Decompress(CompressionUtils.Compress(new string('x',50000)),1024)));
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
