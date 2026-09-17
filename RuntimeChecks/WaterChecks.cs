using System.Collections;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;

// Exercise the installed game's water task and the compiled mod, without Unity.
// No copied game implementation or game assemblies are distributed with tests.
internal static class WaterChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    public static void Run(Assembly mod, Action<string, Action> test)
    {
        var water = Assembly.Load("Timberborn.WaterSystem");
        Type W(string name) => water.GetType("Timberborn.WaterSystem." + name, true);
        var sourceType = W("ThreadSafeWaterSource");
        var columnType = W("WaterColumn");
        var readonlyColumnType = W("ReadOnlyWaterColumn");
        var vecType = Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.Vector3Int", true);
        var listType = typeof(List<>).MakeGenericType(sourceType);
        var fix = mod.GetType("BeaverBuddies.Fixes.WaterSourceOrderFix", true);
        var canonicalize = fix.GetMethod("Canonicalize", All);
        var diagnostics = mod.GetType("BeaverBuddies.DesyncDetecter.WaterDiagnostics", true);
        object InvokeDiagnostic(string name, params object[] arguments) => diagnostics.GetMethod(name, All).Invoke(null, arguments);
        void Set(object instance, string field, object value) =>
            (instance.GetType().GetField(field, All) ?? instance.GetType().GetField("<" + field + ">k__BackingField", All)
                ?? throw new Exception("Missing field " + field)).SetValue(instance, value);
        object CreateSource(float strength, float contamination, int x = 0)
        {
            var coordinates = Array.CreateInstance(vecType, 1);
            coordinates.SetValue(Activator.CreateInstance(vecType, x, 0, 0), 0);
            var create = typeof(ImmutableArray).GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition &&
                m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsArray).MakeGenericMethod(vecType);
            var proxy = (WaterSourceProxy)DispatchProxy.Create(W("IWaterSource"), typeof(WaterSourceProxy));
            proxy.Coordinates = create.Invoke(null, new object[] {coordinates});
            proxy.Strength = strength;
            proxy.Contamination = contamination;
            return Activator.CreateInstance(sourceType, proxy);
        }
        IList Sources(IEnumerable<object> values)
        {
            var list = (IList)Activator.CreateInstance(listType);
            foreach (var v in values) list.Add(v);
            return list;
        }
        string SourceKey(object s)
        {
            var coords = sourceType.GetProperty("Coordinates").GetValue(s);
            var first = coords.GetType().GetProperty("Item").GetValue(coords, new object[] {0});
            return vecType.GetProperty("x").GetValue(first) + ":" +
                BitConverter.SingleToInt32Bits((float)sourceType.GetProperty("CurrentStrength").GetValue(s)) + ":" +
                BitConverter.SingleToInt32Bits((float)sourceType.GetProperty("Contamination").GetValue(s));
        }
        string Simulate(IList sources)
        {
            var mapType = Assembly.Load("Timberborn.MapIndexSystem").GetType("Timberborn.MapIndexSystem.MapIndexService", true);
            var map = RuntimeHelpers.GetUninitializedObject(mapType);
            Set(map, "<Stride>k__BackingField", 3);
            var columns = Array.CreateInstance(columnType, 9);
            var column = Activator.CreateInstance(columnType);
            Set(column, "Ceiling", (byte)32);
            Set(column, "WaterDepth", 0.25f);
            Set(column, "Contamination", 0.25f);
            columns.SetValue(column, 4);
            var counts = new byte[9]; counts[4] = 1;
            var common = Assembly.Load("Timberborn.Common");
            var readonlyCounts = Activator.CreateInstance(common.GetType("Timberborn.Common.ReadOnlyArray`1").MakeGenericType(typeof(byte)), counts);
            var readonlySources = Activator.CreateInstance(common.GetType("Timberborn.Common.ReadOnlyList`1").MakeGenericType(sourceType),
                All, null, new object[]{sources}, null);
            // This fixture stays below its ceiling; no overflow calculation is invoked.
            var setter = Activator.CreateInstance(W("WaterDepthSetter"), RuntimeHelpers.GetUninitializedObject(W("WaterOverflowCalculator")));
            var task = Activator.CreateInstance(W("UpdateWaterSourcesTask"), map, setter,
                Activator.CreateInstance(W("MutableWaterColumnRetriever")), columns, readonlyCounts, readonlySources, 9, 1f, 1f, 1f);
            task.GetType().GetMethod("Run").Invoke(task, null);
            column = columns.GetValue(4);
            return string.Join(":", new[] {"WaterDepth", "Contamination", "Overflow"}
                .Select(n => BitConverter.SingleToInt32Bits((float)columnType.GetField(n).GetValue(column))));
        }
        var inputs = new[] {CreateSource(0.1f, 0.99f), CreateSource(0.3f, 0.01f), CreateSource(0.7f, 0.8f)};
        var permutations = new[] {new[]{0,1,2}, new[]{0,2,1}, new[]{1,0,2}, new[]{1,2,0}, new[]{2,0,1}, new[]{2,1,0}};
        test("Real game water task reproduces order-dependent contamination/depth", () =>
        {
            var outcomes = permutations.Select(p => Simulate(Sources(p.Select(i => inputs[i])))).Distinct().ToArray();
            if (outcomes.Length < 2) throw new Exception("Fixture did not reproduce order sensitivity");
            Console.WriteLine($"  Unsorted game task produced {outcomes.Length} distinct bit patterns across six source orders");
        });
        test("Canonical source order gives identical real-game water results for all six permutations", () =>
        {
            var outcomes = permutations.Select(p =>
            {
                var sources = Sources(p.Select(i => inputs[i]));
                canonicalize.Invoke(null, new object[] {sources});
                return Simulate(sources);
            }).Distinct().ToArray();
            if (outcomes.Length != 1) throw new Exception("Water results still depend on registration order");
        });
        test("Source ordering preserves every input and handles location and signed-zero ties", () =>
        {
            var values = inputs.Concat(new[] {CreateSource(-0f, 0f), CreateSource(0f, 0f), CreateSource(0.1f, 0.99f, 1)}).ToArray();
            var first = Sources(values); var second = Sources(values.Reverse());
            canonicalize.Invoke(null, new object[]{first}); canonicalize.Invoke(null, new object[]{second});
            var a = first.Cast<object>().Select(SourceKey).ToArray();
            if (!a.SequenceEqual(second.Cast<object>().Select(SourceKey)) ||
                !a.Order().SequenceEqual(values.Select(SourceKey).Order())) throw new Exception("Inputs changed or ordering differs");
        });
        Array Columns(float depth)
        {
            var array = Array.CreateInstance(readonlyColumnType, 2);
            var c = Activator.CreateInstance(readonlyColumnType);
            Set(c, "WaterDepth", depth); Set(c, "Ceiling", (byte)32);
            array.SetValue(c, 0);
            return array;
        }
        test("Water diagnostics distinguish active contamination from inactive storage", () =>
        {
            var columns = Columns(0.5f); var counts = new byte[]{1,0};
            var before = (string)InvokeDiagnostic("Describe", columns, counts, 2);
            var c = columns.GetValue(0); Set(c, "Contamination", 0.8f); columns.SetValue(c, 0);
            var after = (string)InvokeDiagnostic("Describe", columns, counts, 2);
            if (before == after || before.Split("contamination=")[0] != after.Split("contamination=")[0])
                throw new Exception("Contamination was not isolated");
            c = columns.GetValue(1); Set(c, "WaterDepth", 10f); columns.SetValue(c, 1);
            var inactive = (string)InvokeDiagnostic("Describe", columns, counts, 2);
            if (inactive == after || inactive.Split("inactive=")[0] != after.Split("inactive=")[0])
                throw new Exception("Inactive slot contaminated active hashes");
        });
        test("Water archive retains four immutable snapshots with exact field bits", () =>
        {
            InvokeDiagnostic("Reset");
            var columns = Columns(0.125f); var counts = new byte[]{1,0};
            for (int tick = 1; tick <= 6; tick++)
            {
                var changing = columns.GetValue(0); Set(changing,"WaterDepth",tick * 0.125f); columns.SetValue(changing,0);
                InvokeDiagnostic("CaptureData", columns, counts, tick, 2, 2);
            }
            var c = columns.GetValue(0); Set(c, "WaterDepth", 99f); columns.SetValue(c, 0); counts[0] = 0;
            string dir = Path.Combine(Path.GetTempPath(), "BeaverBuddies-water-tests-" + Guid.NewGuid().ToString("N"));
            string path = (string)InvokeDiagnostic("WriteArchive", dir);
            using (var zip = ZipFile.OpenRead(path))
            {
                if (zip.Entries.Count != 4) throw new Exception("Snapshot retention is not bounded");
                foreach (var entry in zip.Entries)
                {
                    using var reader = new BinaryReader(entry.Open());
                    if (reader.ReadInt32() != 0x42575731) throw new Exception("Invalid archive schema");
                    int tick = reader.ReadInt32();
                    if (tick < 3 || tick > 6) throw new Exception("Wrong retained ticks");
                    if (reader.ReadInt32()!=2 || reader.ReadInt32()!=2 || reader.ReadInt32()!=2 || reader.ReadInt32()!=2)
                        throw new Exception("Wrong dimensions");
                    if (reader.ReadByte()!=1 || reader.ReadByte()!=0 || reader.ReadByte()!=0 || reader.ReadByte()!=32 ||
                        reader.ReadInt32()!=BitConverter.SingleToInt32Bits(tick * 0.125f)) throw new Exception("Snapshot changed after capture");
                }
            }
            File.Delete(path); Directory.Delete(dir);
            InvokeDiagnostic("Reset");
        });
        test("Steady-state diagnostic captures reuse arrays and bound allocations", () =>
        {
            InvokeDiagnostic("Reset");
            var columns = Array.CreateInstance(readonlyColumnType,262144);
            var counts = new byte[65536];
            object[] captureArgs = {columns,counts,1,256,65536};
            var capture=diagnostics.GetMethod("CaptureData",All);
            for(int i=0;i<4;i++) capture.Invoke(null,captureArgs);
            long start=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<16;i++) capture.Invoke(null,captureArgs);
            long optimized=GC.GetAllocatedBytesForCurrentThread()-start;
            start=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<16;i++) { GC.KeepAlive(columns.Clone()); GC.KeepAlive(counts.Clone()); }
            long baseline=GC.GetAllocatedBytesForCurrentThread()-start;
            Console.WriteLine($"  Snapshot allocation, 16 captures after warm-up: old clones {baseline:N0} bytes; optimized {optimized:N0} bytes (262,144 columns)");
            if(optimized > 65536 || baseline < 1048576) throw new Exception("Map-sized steady-state allocations remain");
            InvokeDiagnostic("Reset");
        });
        test("Snapshot recycling handles map size changes within retention budget", () =>
        {
            InvokeDiagnostic("Reset");
            foreach(int size in new[] {2,2,2,2,4,4,2,4})
                InvokeDiagnostic("CaptureData",Array.CreateInstance(readonlyColumnType,size),new byte[size],size,2,size);
            var retained=(IEnumerable)diagnostics.GetField("snapshots",All).GetValue(null);
            var entries=retained.Cast<object>().ToArray();
            if(entries.Length!=4) throw new Exception("Snapshot count changed");
            foreach(var entry in entries)
            {
                int tick=(int)entry.GetType().GetField("Tick",All).GetValue(entry);
                var columns=(Array)entry.GetType().GetField("Columns",All).GetValue(entry);
                if(columns.Length!=tick) throw new Exception("Reused incompatible buffer");
            }
            long bytes=(long)diagnostics.GetField("bytes",All).GetValue(null);
            if(bytes>64L*1024*1024) throw new Exception("Budget exceeded");
            InvokeDiagnostic("Reset");
        });
    }
}

public class WaterSourceProxy : DispatchProxy
{
    public object Coordinates;
    public float Strength, Contamination;
    protected override object Invoke(MethodInfo method, object[] args) => method.Name switch
    {
        "get_Coordinates" => Coordinates,
        "get_CurrentStrength" => Strength,
        "get_Contamination" => Contamination,
        _ => throw new NotSupportedException(method.Name)
    };
}
