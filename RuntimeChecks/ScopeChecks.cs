using System.Collections;
using System.Reflection;

static class ScopeChecks
{
    public static void Run(Assembly assembly, Action<string,Action> test)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var service = assembly.GetType("BeaverBuddies.DeterminismService", true)!;
        foreach (string name in new[] {"InputPatcher", "SoundsPatcher", "SoundEmitterPatcher", "DateSalterPatcher", "BeaverNameServiceRandomNamePatcher", "PlantableDescriberPatcher", "StockpileGoodPileVisualizerPatcher", "LoopingSoundPlayerPatcher", "BotManufactoryAnimationControllerPatcher", "TerrainBlockRandomizerPickVariationPatcher"})
        {
            test(name + " retains nested scope and cleans up after exception", () =>
            {
                var type = assembly.GetType("BeaverBuddies." + name, true)!;
                var prefix = type.GetMethod("Prefix",flags)!;
                var finalizer = type.GetMethod("Finalizer",flags) ?? throw new Exception("Missing exception cleanup");
                var field = service.GetField(name == "BeaverNameServiceRandomNamePatcher" ? "activeGamePatchers" : "activeNonGamePatchers",flags)!;
                var active = (IDictionary)field.GetValue(null)!;
                active.Clear();
                object[] outer = {false}, inner = {false};
                prefix.Invoke(null,outer); prefix.Invoke(null,inner);
                if(active.Count != 1 || (int)active.Values.Cast<object>().Single() != 2) throw new Exception("Nested scope missing");
                try { throw new IOException("injected visual failure"); }
                catch(IOException) { finalizer.Invoke(null,inner); }
                if(active.Count != 1 || (int)active.Values.Cast<object>().Single() != 1) throw new Exception("Outer scope lost");
                finalizer.Invoke(null,new object[] {false});
                if(active.Count != 1) throw new Exception("Skipped prefix removed outer scope");
                finalizer.Invoke(null,outer);
                if(active.Count != 0) throw new Exception("Leaked scope");
            });
        }
        foreach(bool initial in new[] {false,true})
        {
            test("Ticker cleanup restores previous flag " + initial, () =>
            {
                var type = assembly.GetType("BeaverBuddies.TickerPatcher",true)!;
                var field = service.GetField("IsTicking",flags)!;
                field.SetValue(null,initial);
                object[] state = {false};
                type.GetMethod("Prefix",flags)!.Invoke(null,state);
                if(!(bool)field.GetValue(null)!) throw new Exception("Ticker scope not active");
                type.GetMethod("Finalizer",flags)!.Invoke(null,state);
                if((bool)field.GetValue(null)! != initial) throw new Exception("Ticker scope leaked");
                field.SetValue(null,false);
            });
        }
        test("Aborted tick blocks simulation and discards deferred save callbacks", () =>
        {
            var replay = assembly.GetType("BeaverBuddies.ReplayService",true)!;
            var failure = replay.GetProperty("HasReplayFailure",flags)!;
            var ticking = assembly.GetType("BeaverBuddies.TickingService",true)!;
            var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(ticking);
            int calls = 0;
            var callbacks = new List<Action> { () => calls++ };
            ticking.GetField("onCompletedFullTick",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(instance,callbacks);
            try
            {
                failure.SetValue(null,true);
                ticking.GetMethod("OnTickingCompleted",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(instance,null);
                var patch = assembly.GetType("BeaverBuddies.TickableBucketServiceTickUpdatePatcher",true)!;
                bool tick = (bool)patch.GetMethod("Prefix",flags)!.Invoke(null,new object[] {null,10})!;
                if(calls != 0 || callbacks.Count != 0 || tick) throw new Exception("Failed session continued ticking or saving");
            }
            finally { failure.SetValue(null,false); }
        });
        // The game's installed MonoMod dependency cannot install detours under
        // every .NET test runtime. Keep this integration check opt-in; the
        // production prefix/finalizer behavior above is always tested.
        if (Environment.GetEnvironmentVariable("BEAVERBUDDIES_TEST_HARMONY") != "1")
        {
            Console.WriteLine("NOT RUN: Harmony patch installation (opt-in; requires a compatible detour runtime)");
            return;
        }
        test("Installed Harmony finalizer restores RNG after a real nested throw", () =>
        {
            var patcher = assembly.GetType("BeaverBuddies.InputPatcher", true)!;
            var active = (IDictionary)service.GetField("activeNonGamePatchers", flags)!.GetValue(null)!;
            active.Clear();
            var harmonyAssembly = Assembly.Load("0Harmony");
            var harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", true)!;
            var methodType = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod", true)!;
            const string id = "beaverbuddies.preview5.runtimechecks";
            var harmony = Activator.CreateInstance(harmonyType, id)!;
            object PatchMethod(string name) => Activator.CreateInstance(methodType, patcher.GetMethod(name,flags)!)!;
            var original = typeof(ScopeChecks).GetMethod(nameof(InvokeBody),flags)!;
            try
            {
                harmonyType.GetMethod("Patch")!.Invoke(harmony, new object[] {original, PatchMethod("Prefix"), null, null, PatchMethod("Finalizer")});
                InvokeBody(() =>
                {
                    if(active.Count != 1) throw new Exception("Prefix not installed");
                    try { InvokeBody(() => { if((int)active.Values.Cast<object>().Single() != 2) throw new Exception("Missing depth"); throw new IOException("injected"); }); }
                    catch(IOException) { }
                    if(active.Count != 1 || (int)active.Values.Cast<object>().Single() != 1) throw new Exception("Nested finalizer lost outer scope");
                    throw new IOException("outer injected");
                });
                throw new Exception("Original exception was swallowed");
            }
            catch(IOException) { if(active.Count != 0) throw new Exception("Exception leaked RNG scope"); }
            finally { harmonyType.GetMethod("UnpatchAll")!.Invoke(harmony, new object[] {id}); }
        });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void InvokeBody(Action body) => body();
}
