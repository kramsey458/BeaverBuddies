using BeaverBuddies.IO;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Timberborn.WaterSourceSystem;
using UnityEngine;

namespace BeaverBuddies.Fixes
{
    // This gameplay modifier is evaluated by WaterSource.Tick, but the vanilla
    // fade uses render-frame duration. Replace only that clock read, preserving
    // the game's depth thresholds, hysteresis, fade speed and clamping.
    [HarmonyPatch(typeof(WaterDepthStrengthModifier), nameof(WaterDepthStrengthModifier.GetStrengthModifier))]
    public static class WaterSourceTimingFix
    {
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var result = new List<CodeInstruction>(instructions);
            var original = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            var replacement = AccessTools.Method(typeof(WaterSourceTimingFix), nameof(GetDeltaTime));
            int replaced = 0;
            foreach (var instruction in result)
            {
                if (instruction.Calls(original))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    replaced++;
                }
            }
            if (replaced != 1)
                throw new InvalidOperationException($"Expected one water-source frame-clock read, found {replaced}. Game version may be incompatible.");
            return result;
        }

        internal static float GetDeltaTime()
        {
            if (EventIO.IsNull) return GetFrameDeltaTime();
            var buffer = SingletonManager.GetSingleton<LateTickableBuffer>();
            if (buffer == null)
                throw new InvalidOperationException("Water-source tick service is not initialized.");
            return buffer.TickIntervalInSeconds;
        }

        // Keep the Unity native call outside the multiplayer path.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float GetFrameDeltaTime() => Time.deltaTime;
    }
}
