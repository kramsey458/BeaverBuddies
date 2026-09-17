using System.Diagnostics;
using System.Reflection;
using Newtonsoft.Json.Linq;
using TimberNet;

static class PerformanceChecks
{
    static void Check(bool condition) { if (!condition) throw new Exception("Performance regression changed event semantics"); }
    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Binary insertion preserves stable order with duplicate and out-of-order ticks", () =>
        {
            var net = new InsertNet(); var expected = new List<JObject>(); var actual = new List<JObject>();
            var random = new Random(41);
            for(int i=0;i<1000;i++)
            {
                var item = new JObject { [TimberNetBase.TICKS_KEY] = random.Next(30), ["id"] = i };
                int index = expected.FindIndex(e => TimberNetBase.GetTick(e) > TimberNetBase.GetTick(item));
                if(index < 0) expected.Add(item); else expected.Insert(index,item);
                net.Insert(item,actual);
            }
            Check(expected.SequenceEqual(actual));
        });
        yield return ("Bulk dequeue preserves due events and leaves future backlog untouched", () =>
        {
            foreach(int cutoff in new[] {-1,0,4,9,10})
            {
                var items = Enumerable.Range(0,1000).Select(i => i/100).ToList();
                var expected = items.Where(t=>t<=cutoff).ToList();
                var future = items.Where(t=>t>cutoff).ToList();
                var ready = TimberNetBase.PopEventsForTick(cutoff,items,x=>x);
                Check(ready.SequenceEqual(expected) && items.SequenceEqual(future));
            }
        });
        yield return ("Incoming queue retains parsed messages without reparsing", () =>
        {
            var queue = typeof(TimberNetBase).GetField("receivedEventQueue",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(new InsertNet())!;
            Check(queue.GetType().GetGenericArguments().Single()==typeof(JObject));
        });
        yield return ("Routine event logging is off but detailed logging can be enabled", () =>
        {
            var net = new InsertNet(); int logs=0; net.OnLog += _ => logs++;
            var item = new JObject { [TimberNetBase.TICKS_KEY]=1, [TimberNetBase.TYPE_KEY]="Heartbeat" };
            net.DoUserInitiatedEvent(item); Check(logs==0);
            net.DetailedLoggingEnabled=()=>true;
            net.DoUserInitiatedEvent(item); Check(logs==1);
        });
        yield return ("Synthetic ordered-backlog benchmark keeps identical results", () =>
        {
            var net = new InsertNet();
            var values = Enumerable.Range(0,4000).Select(i => new JObject{[TimberNetBase.TICKS_KEY]=i}).ToArray();
            var old = new List<JObject>(); var current = new List<JObject>();
            var clock=Stopwatch.StartNew();
            foreach(var item in values)
            {
                int tick=TimberNetBase.GetTick(item);
                int index=old.FindIndex(e=>TimberNetBase.GetTick(e)>tick);
                if(index<0) old.Add(item); else old.Insert(index,item);
            }
            double before=clock.Elapsed.TotalMilliseconds; clock.Restart();
            foreach(var item in values) net.Insert(item,current);
            double after=clock.Elapsed.TotalMilliseconds;
            Check(old.SequenceEqual(current));
            Console.WriteLine($"  Ordered backlog: old {before:F2} ms, optimized {after:F2} ms for 4,000 events (synthetic, not game FPS)");
        });
    }
    sealed class InsertNet : TimberNetBase
    {
        public void Insert(JObject value,List<JObject> list)=>InsertInScript(value,list);
    }
}
