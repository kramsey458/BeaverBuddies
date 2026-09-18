using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeaverBuddies.Events;
using BeaverBuddies.IO;
using Newtonsoft.Json.Linq;
using Timberborn.BehaviorSystem;
using Timberborn.EntitySystem;
using Timberborn.InventorySystem;
using Timberborn.WaterSystem;
using Timberborn.WorkSystem;
using TimberNet;

namespace BeaverBuddies.DesyncDetecter
{
    public sealed class RollingDiagnosticsService : RegisteredSingleton, IResettableSingleton
    {
        const int Interval = 20, EntityWindow = 64, WaterWindow = 256;
        readonly EntityRegistry registry;
        readonly ThreadSafeWaterMap water;
        readonly RollingTrace trace = new RollingTrace();
        readonly List<Inventory> inventories = new List<Inventory>();
        readonly string directory;
        static int pendingWrites;
        static readonly object writerGate = new object();
        bool failed, exported, reset;
        int lastTick = -1;
        double maxCaptureMilliseconds;
        static RollingDiagnosticsService Current => SingletonManager.GetSingleton<RollingDiagnosticsService>();
        public RollingDiagnosticsService(EntityRegistry registry, ThreadSafeWaterMap water)
        {
            this.registry = registry; this.water = water;
            directory = Path.Combine(UnityEngine.Application.persistentDataPath, "BeaverBuddiesDiagnostics");
        }
        public void Reset() { reset = true; trace.Clear(); }
        bool Enabled => !reset && !failed && !exported && Settings.RollingDiagnosticsEnabled;
        public static void CaptureBoundary(int tick) => Current?.Capture(tick);
        void Capture(int tick)
        {
            if (!Enabled || tick < 0 || tick % Interval != 0 || tick == lastTick) return;
            try
            {
                long start = Stopwatch.GetTimestamp();
                lastTick = tick;
                var state = UnityEngine.Random.state; // Read only; never draw or reseed RNG.
                var entities = registry.Entities;
                var entitySamples = new JArray();
                int begin = entities.Count == 0 ? 0 : (int)((long)(tick / Interval) * EntityWindow % entities.Count);
                for (int i = 0; i < Math.Min(EntityWindow, entities.Count); i++)
                {
                    var entity = entities[(begin + i) % entities.Count];
                    if (entity == null || entity.Deleted) continue;
                    var worker = entity.GetComponent<Worker>();
                    var behavior = entity.GetComponent<BehaviorManager>();
                    string job = (worker?.Workplace?.GetComponent<EntityComponent>()?.EntityId.ToString("D") ?? "none") + "/" + worker?.JobRunning + "/" +
                        behavior?._runningBehavior?.ComponentName + "/" + behavior?._runningExecutor?.GetType().FullName + "/" +
                        Bits(behavior?._runningExecutorElapsedTime ?? 0);
                    inventories.Clear(); entity.GetComponents(inventories);
                    var stock = new StringBuilder();
                    bool truncated = inventories.Count > 8;
                    foreach (var inventory in inventories.Take(8).OrderBy(v => v.ComponentName, StringComparer.Ordinal))
                    {
                        stock.Append(inventory.ComponentName).Append(':').Append(inventory.Capacity).Append(':').Append(inventory.TotalAmountInStock).Append('|');
                        int count = 0;
                        foreach (var good in inventory.Stock)
                        {
                            if (count++ == 64) { truncated = true; break; }
                            stock.Append(good.GoodId).Append('=').Append(good.Amount).Append(';');
                        }
                        count = 0;
                        foreach (var good in inventory.ReservedCapacity())
                        {
                            if (count++ == 64) { truncated = true; break; }
                            stock.Append("r:").Append(good.GoodId).Append('=').Append(good.Amount).Append(';');
                        }
                        count = 0;
                        foreach (var good in inventory._reservedStock.Goods)
                        {
                            if (count++ == 64) { truncated = true; break; }
                            stock.Append("s:").Append(good.GoodId).Append('=').Append(good.Amount).Append(';');
                        }
                    }
                    entitySamples.Add(new JObject { ["id"] = entity.EntityId.ToString("D"), ["job"] = CompatibilityProfile.Digest(job),
                        ["inventory"] = CompatibilityProfile.Digest(stock.ToString()), ["truncated"] = truncated });
                }
                var columns = water._threadSafeWaterColumns;
                var counts = water._threadSafeColumnCounts;
                var waterSamples = new JArray();
                int waterBegin = columns.Length == 0 ? 0 : (int)((long)(tick / Interval) * WaterWindow % columns.Length);
                for (int i = 0; i < Math.Min(WaterWindow, columns.Length); i++)
                {
                    int index = (waterBegin + i) % columns.Length;
                    var column = columns[index];
                    bool active = water._verticalStride > 0 && index % water._verticalStride < counts.Length && index / water._verticalStride < counts[index % water._verticalStride];
                    waterSamples.Add(new JArray(index, active, Bits(column.WaterDepth), Bits(column.Contamination), Bits(column.Overflow), column.Floor, column.Ceiling));
                }
                var net = ReplayService.Network;
                trace.Snapshot(new JObject {
                    ["tick"] = tick, ["rng"] = new JArray(state.s0, state.s1, state.s2, state.s3),
                    ["entityOrder"] = TEBPatcher.EntityUpdateHash, ["positions"] = TEBPatcher.PositionHash,
                    ["entityCount"] = entities.Count, ["entityWindowStart"] = begin, ["entities"] = entitySamples,
                    ["waterColumnCount"] = columns.Length, ["waterStride"] = water._verticalStride, ["water"] = waterSamples,
                    ["network"] = new JObject { ["hash"] = net?.Hash, ["ticksBehind"] = net?.TicksBehind, ["pending"] = net?.PendingReliableMessages }
                });
                maxCaptureMilliseconds = Math.Max(maxCaptureMilliseconds, (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
            }
            catch (Exception error) { Disable(error); }
        }
        static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
        public static void Record(ReplayEvent replayEvent, string phase) => Current?.RecordEvent(replayEvent, phase);
        void RecordEvent(ReplayEvent replayEvent, string phase)
        {
            if (!Enabled || replayEvent == null || replayEvent is HeartbeatEvent || replayEvent.type.Contains("Trace") || replayEvent.type.Contains("Ping")) return;
            try
            {
                // Restrict to bounded scalar fields; hash strings rather than logging player-entered text.
                var fields = new JObject(); var targets = new JArray(); int count = 0;
                foreach (var field in replayEvent.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    if (count++ >= 24) break;
                    object value = field.GetValue(replayEvent);
                    if (value is string s && s.Length <= 4096)
                    {
                        fields[field.Name] = CompatibilityProfile.Digest(s);
                        if (targets.Count < 8 && Guid.TryParse(s, out var target)) targets.Add(target.ToString("D"));
                    }
                    else if (value is int || value is bool || value is float || value is double || value is Guid || value is Enum)
                        fields[field.Name] = Convert.ToString(value, CultureInfo.InvariantCulture);
                }
                trace.Event(new JObject { ["tick"] = SingletonManager.GetSingleton<ReplayService>()?.TicksSinceLoad,
                    ["eventTick"] = replayEvent.ticksSinceLoad, ["type"] = replayEvent.type, ["phase"] = phase,
                    ["expectedRng"] = replayEvent.randomS0Before, ["actualRng"] = UnityEngine.Random.state.s0,
                    ["targets"] = targets, ["arguments"] = CompatibilityProfile.Digest(fields.ToString(Newtonsoft.Json.Formatting.None)) });
            }
            catch (Exception error) { Disable(error); }
        }
        public static void Trigger(string reason)
        {
            var current = Current;
            if (current == null || current.reset || current.exported) return;
            // RealNewGuid avoids the deterministic gameplay GUID generator.
            string id = GuidPatcher.RealNewGuid().ToString("N");
            current.Export(id, reason);
            var message = TimberNetBase.Control("DiagnosticsCapture"); message["id"] = id;
            try { ReplayService.Network?.SendControl(message); } catch { /* Diagnostics must not disrupt recovery. */ }
        }
        public static void Receive(EventIO owner, JObject message)
        {
            if (owner != EventIO.Get() || (string)message["command"] != "DiagnosticsCapture") return;
            var current = Current;
            if (current == null || current.reset || current.exported || !Guid.TryParseExact((string)message["id"], "N", out var id)) return;
            current.Export(id.ToString("N"), "peer-requested capture");
            if (owner is ServerEventIO host) host.NetBase?.SendControl(message);
        }
        void Export(string id, string reason)
        {
            if (reset || exported) return;
            exported = true;
            if (!Settings.RollingDiagnosticsEnabled) return;
            try
            {
                var net = ReplayService.Network;
                var metadata = new JObject {
                    ["captureId"] = id, ["version"] = Plugin.Version, ["role"] = EventIO.Get() is ServerEventIO ? "host" : "guest",
                    ["snapshotSha256"] = net?.SnapshotDigest, ["triggerTick"] = SingletonManager.GetSingleton<ReplayService>()?.TicksSinceLoad,
                    ["reason"] = reason.Length > 2048 ? reason.Substring(0, 2048) : reason,
                    ["intervalTicks"] = Interval, ["entityWindow"] = EntityWindow, ["waterWindow"] = WaterWindow,
                    ["maxCaptureMilliseconds"] = maxCaptureMilliseconds, ["recorderFailed"] = failed,
                    ["coverage"] = "Rotating samples of completed ticks, not a full-world checksum. No world reads occur at the fault site.",
                    ["compatibility"] = net?.LoadedCompatibilityIdentity ?? net?.CompatibilityIdentity
                };
                var report = trace.Freeze(metadata);
                if (Interlocked.Increment(ref pendingWrites) > 2) { Interlocked.Decrement(ref pendingWrites); Plugin.LogWarning("Rolling diagnostics writer busy; report skipped."); return; }
                string name = "rolling-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + id + ".zip";
                Plugin.LogWarning("Saving local rolling diagnostics: " + Path.Combine(directory, name));
                _ = Task.Run(() =>
                {
                    try { lock (writerGate) report.Write(directory, name); }
                    catch (Exception error) { Plugin.LogWarning("Could not write rolling diagnostics: " + error.Message); }
                    finally { Interlocked.Decrement(ref pendingWrites); }
                });
            }
            catch (Exception error) { Disable(error); }
        }
        void Disable(Exception error)
        {
            failed = true;
            Plugin.LogWarning("Rolling diagnostics disabled for this scene: " + error.Message);
        }
    }
}
