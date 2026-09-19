using BeaverBuddies.IO;
using HarmonyLib;
using Timberborn.TimeSystem;

namespace BeaverBuddies
{
    // Applies SpeedLimitChoice to the game. The game's GameSpeedThrottler asks SpeedManager for a scale below 1 as
    // the population grows; when the limit is removed, that request is turned into 1 (no scaling).
    //
    // This only changes how fast ticks are worked through, never what happens in them.
    public static class LargeColonySpeedLimit
    {
        // The host's choice, and the session it belongs to. A value left over from an earlier session is ignored
        // because the session object is different.
        private static EventIO _sessionIo;
        private static bool _sessionRemoved;

        private static SpeedManager _speedManager;
        private static float _requestedScale = 1f;

        public static bool IsRemoved
        {
            get
            {
                EventIO io = EventIO.Get();
                bool? sessionValue = io != null && ReferenceEquals(io, _sessionIo) ? _sessionRemoved : (bool?)null;
                return SpeedLimitChoice.IsRemoved(io != null, io is ClientEventIO, sessionValue, Settings.RemoveSpeedLimit);
            }
        }

        // Host: called as the session's start message is built. Returns the value to send.
        // It can run on a network thread, so it only records the value: the host's own game picks it up when
        // it next asks for the speed scale, which it does as the save finishes loading.
        public static bool BeginHostSession()
        {
            _sessionIo = EventIO.Get();
            _sessionRemoved = Settings.RemoveSpeedLimit;
            return _sessionRemoved;
        }

        // Guest: called when the host's start message is replayed.
        public static void AdoptHostChoice(bool removed)
        {
            _sessionIo = EventIO.Get();
            _sessionRemoved = removed;
            Reapply();
            Plugin.Log($"Large colony speed limit for this session: {(removed ? "removed" : "game default")} (the host's choice)");
        }

        // Re-applies the current choice to the game's speed, for instance after the setting changed.
        public static void Reapply()
        {
            if (_speedManager != null)
            {
                _speedManager.ChangeSpeedScale(_requestedScale);
            }
        }

        internal static void OnScaleRequested(SpeedManager speedManager, ref float speedScale)
        {
            _speedManager = speedManager;
            _requestedScale = speedScale;
            if (IsRemoved)
            {
                speedScale = 1f;
            }
        }
    }

    [HarmonyPatch(typeof(SpeedManager), nameof(SpeedManager.ChangeSpeedScale))]
    static class SpeedManagerChangeSpeedScalePatcher
    {
        static void Prefix(SpeedManager __instance, ref float speedScale)
        {
            LargeColonySpeedLimit.OnScaleRequested(__instance, ref speedScale);
        }
    }
}
