using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static class SnapshotGuardChecks
{
    public static void Run(Assembly mod, Action<string, Action> test)
    {
        const BindingFlags fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var coordinator = mod.GetType("BeaverBuddies.Connect.SnapshotResyncService", true);
        var stateField = coordinator.GetField("recovery", fields);
        var replay = mod.GetType("BeaverBuddies.ReplayService", true);
        var loaded = replay.GetProperty("IsLoaded", fields);
        object oldState = stateField.GetValue(null); bool oldLoaded = (bool)loaded.GetValue(null);
        try
        {
            stateField.SetValue(null, RuntimeHelpers.GetUninitializedObject(stateField.FieldType));
            loaded.SetValue(null, true);
            var instance = RuntimeHelpers.GetUninitializedObject(replay);
            test("Snapshot barrier prevents new simulation ticks in the compiled mod", () =>
            {
                if ((bool)replay.GetProperty("IsReadyToStartTick").GetValue(instance)) throw new Exception("New tick was allowed");
            });
            test("Snapshot barrier drops new player commands before touching game state", () =>
            {
                replay.GetMethod("RecordEvent").Invoke(instance, new object[] {null});
            });
            test("Snapshot barrier blocks the original action prefix outside simulation", () =>
            {
                var method = mod.GetType("BeaverBuddies.Events.ReplayEvent", true).GetMethod("DoPrefix");
                if ((bool)method.Invoke(null, new object[] {null})) throw new Exception("Original action was allowed");
            });
            test("Incomplete simulation buckets cannot run a deferred save callback", () =>
            {
                var ticking = mod.GetType("BeaverBuddies.TickingService",true);
                var tick = RuntimeHelpers.GetUninitializedObject(ticking);
                int calls=0; var callbacks=new List<Action> { () => calls++ };
                ticking.GetField("onCompletedFullTick",fields).SetValue(tick,callbacks);
                ticking.GetProperty("ShouldCompleteFullTick",fields).SetValue(tick,true);
                ticking.GetProperty("NextBucket",fields).SetValue(tick,3);
                ticking.GetMethod("OnTickingCompleted",fields).Invoke(tick,null);
                if(calls!=0 || callbacks.Count!=1 || !(bool)ticking.GetProperty("ShouldCompleteFullTick").GetValue(tick))
                    throw new Exception("Save ran before the tick completed, or callback was lost");
            });
        }
        finally { stateField.SetValue(null,oldState); loaded.SetValue(null,oldLoaded); }
    }
}
