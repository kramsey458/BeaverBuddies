using BeaverBuddies.IO;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using Timberborn.WaterSystem;

namespace BeaverBuddies.Fixes
{
    // Sort the simulation snapshot, not the live registry. Overlapping sources
    // repeatedly mix contamination; floating-point rounding depends on their order.
    [HarmonyPatch(typeof(WaterSourceRegistry), nameof(WaterSourceRegistry.UpdateThreadSafeRegistry))]
    public static class WaterSourceOrderFix
    {
        private static readonly Comparison<ThreadSafeWaterSource> Comparison = Compare;

        public static void Postfix(WaterSourceRegistry __instance)
        {
            if (EventIO.IsNull) return;
            Canonicalize(__instance._threadSafeWaterSources);
            if (Settings.Debug)
            {
                int hash = 13;
                var details = new StringBuilder();
                unchecked
                {
                    foreach (var source in __instance._threadSafeWaterSources)
                    {
                        details.Append('[');
                        hash = hash * 7 + BitConverter.SingleToInt32Bits(source.CurrentStrength);
                        hash = hash * 7 + BitConverter.SingleToInt32Bits(source.Contamination);
                        hash = hash * 7 + source.Coordinates.Length;
                        foreach (var c in source.Coordinates)
                        {
                            details.Append($"({c.x},{c.y},{c.z})");
                            hash = hash * 7 + c.x;
                            hash = hash * 7 + c.y;
                            hash = hash * 7 + c.z;
                        }
                        details.Append($" strength={BitConverter.SingleToInt32Bits(source.CurrentStrength):X8}" +
                            $" contamination={BitConverter.SingleToInt32Bits(source.Contamination):X8}] ");
                    }
                }
                DesyncDetecter.DesyncDetecterService.Trace(
                    $"Water sources count={__instance._threadSafeWaterSources.Count} hash={hash:X8}");
                DesyncDetecter.DesyncDetecterService.Trace($"Water source values {details}", skipStackTrack: true);
            }
        }

        internal static void Canonicalize(List<ThreadSafeWaterSource> sources)
        {
            sources.Sort(Comparison);
        }

        private static int Compare(ThreadSafeWaterSource a, ThreadSafeWaterSource b)
        {
            var ac = a.Coordinates;
            var bc = b.Coordinates;
            int comparison = ac.Length.CompareTo(bc.Length);
            if (comparison != 0) return comparison;
            for (int i = 0; i < ac.Length; i++)
            {
                comparison = ac[i].x.CompareTo(bc[i].x);
                if (comparison != 0) return comparison;
                comparison = ac[i].y.CompareTo(bc[i].y);
                if (comparison != 0) return comparison;
                comparison = ac[i].z.CompareTo(bc[i].z);
                if (comparison != 0) return comparison;
            }
            comparison = BitConverter.SingleToInt32Bits(a.CurrentStrength)
                .CompareTo(BitConverter.SingleToInt32Bits(b.CurrentStrength));
            if (comparison != 0) return comparison;
            // Raw bits provide a total order even for signed zero/NaN. Equal
            // keys have identical simulation inputs, so no identity tie-break is needed.
            return BitConverter.SingleToInt32Bits(a.Contamination)
                .CompareTo(BitConverter.SingleToInt32Bits(b.Contamination));
        }
    }
}
