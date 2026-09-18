using BeaverBuddies.DesyncDetecter;
using BeaverBuddies.IO;
using HarmonyLib;
using System;
using System.Globalization;
using System.Text;
using Timberborn.BehaviorSystem;
using Timberborn.Demolishing;
using Timberborn.EntitySystem;
using Timberborn.Navigation;
using Priority = Timberborn.PrioritySystem.Priority;

namespace BeaverBuddies.Fixes
{
    [HarmonyPatch(typeof(DemolishJobProvider), nameof(DemolishJobProvider.GetJob))]
    internal static class DemolitionSelectionFix
    {
        // Exact equality only: never replace a genuinely nearer job with a
        // farther one. Entity IDs are persisted and shared by multiplayer peers.
        internal static bool Prefer(float distance, Guid id, float bestDistance, Guid bestId, bool hasBest)
        {
            return distance < bestDistance ||
                (hasBest && distance == bestDistance && id.CompareTo(bestId) < 0);
        }

        private static bool Prefix(DemolishJobProvider __instance, Accessible start,
            BehaviorAgent agent, Priority priority, ref (Behavior, Decision) __result)
        {
            if (EventIO.IsNull) return true;

            var demolisher = agent.GetComponent<Demolisher>();
            DemolishJob best = null;
            float bestDistance = float.MaxValue;
            Guid bestId = Guid.Empty;
            int eligible = 0;
            // Candidate order is useful locally, but must NOT be part of the
            // compared trace: differing order is harmless after this fix.
            var candidates = Settings.Debug && Settings.VerboseLogging ? new StringBuilder() : null;
            foreach (var job in __instance._demolishJobs.GetJobs(priority))
            {
                if (!job.CanStartJob(demolisher) ||
                    !DemolishJobProvider.IsReachable(job, start, out float distance)) continue;
                var id = job.GetComponent<EntityComponent>().EntityId;
                if (candidates != null && eligible < 8)
                    candidates.Append(id).Append('@').Append(distance.ToString("R", CultureInfo.InvariantCulture)).Append(' ');
                eligible++;
                if (Prefer(distance, id, bestDistance, bestId, best != null))
                {
                    best = job;
                    bestId = id;
                    bestDistance = distance;
                }
            }

            if (best)
            {
                if (Settings.Debug)
                {
                    var builder = agent.GetComponent<EntityComponent>().EntityId;
                    DesyncDetecterService.Trace($"Demolition selection builder={builder} target={bestId} distance={bestDistance.ToString("R", CultureInfo.InvariantCulture)}");
                    if (candidates != null)
                        Plugin.Log($"Demolition candidates builder={builder} eligible={eligible} first8=[{candidates}] selected={bestId}");
                }
                var result = best.StartBuilderJob(demolisher);
                if (!result.Item2.ShouldReleaseNow)
                {
                    __result = result;
                    return false;
                }
            }
            __result = (null, Decision.ReleaseNow());
            return false;
        }
    }
}
