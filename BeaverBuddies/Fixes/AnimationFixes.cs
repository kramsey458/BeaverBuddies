//# define NO_SMOOTH_ANIMATION

using BeaverBuddies.IO;
using HarmonyLib;
using Timberborn.CharacterMovementSystem;
using Timberborn.EntitySystem;
using UnityEngine;

namespace BeaverBuddies.Fixes
{
#if !NO_SMOOTH_ANIMATION
    [ManualMethodOverwrite]
    /*
4/19/2025
private void Update(float deltaTime)
{
    _animatedPathFollower.Update(Time.time);
    if (!_animatedPathFollower.Stopped)
    {
        UpdateTransform(deltaTime);
        UpdateAnimationSpeed();
        UpdateGroupId();
    }

    NotifyAnimationUpdated();
    UpdateRotation();
}
     */
    [HarmonyPatch(typeof(MovementAnimator), nameof(MovementAnimator.Update), typeof(float))]
    public class AnimatedPathFollowerUpdatePathcer
    {
        static bool Prefix(MovementAnimator __instance, float deltaTime)
        {
            if (EventIO.IsNull) return true;
            var tickProgressService = SingletonManager.GetSingleton<TickProgressService>();
            if (tickProgressService == null) return true;

            //Vector3 position = Vector3.zero;

            float time = Time.time;
            EntityComponent entity = __instance.GetComponent<EntityComponent>();
            if (entity != null)
            {
                // For the movement animation, use interpolated time based on
                // how many buckets we've ticked (i.e. how close to the next
                // time update).
                time = tickProgressService.TimeAtLastTick(entity) +
                    Time.fixedDeltaTime *
                    tickProgressService.PercentTicked(entity);

                //if (entity.EntityId.ToString() == "00355d1d-36fd-f115-9c90-6a54dda73a85")
                //{
                //    Plugin.Log($"{entity.EntityId} (${entity.GetComponent<Character>().FirstName}) :\n" +
                //        $"index: {tickProgressService.GetEntityBucketIndex(entity)}\n" +
                //        $"nextTick: {tickProgressService.TickableBucketService._nextBucketIndex}\n" +
                //        $"ticked: {tickProgressService.HasTicked(entity)}\n" +
                //        $"last: {tickProgressService.TimeAtLastTick(entity)}\n" +
                //        $"perc: {tickProgressService.PercentTicked(entity)}\n" +
                //        $"time: {Time.time} -> {time}");
                //    position = __instance._animatedPathFollower.CurrentPosition;
                //}
            }
            else
            {
                Plugin.LogWarning("missing entity component!");
            }

            // Use the interpolated time
            // The game's follower only searches forward from its cached corner.
            // Our interpolated clock can move backwards at a tick boundary, so
            // reselect the segment instead of extrapolating from a later corner.
            __instance._animatedPathFollower._nextCornerIndex = 0;
            __instance._animatedPathFollower.Update(time);

            Vector3 position = __instance._animatedPathFollower.CurrentPosition;
            if (float.IsNaN(position.x) || float.IsInfinity(position.x) ||
                float.IsNaN(position.y) || float.IsInfinity(position.y) ||
                float.IsNaN(position.z) || float.IsInfinity(position.z))
            {
                // Do not pass an invalid visual position to swimming/water-map
                // listeners. Restore the simulation position, leaving its path
                // and the simulation itself unchanged.
                __instance._animatedPathFollower.CurrentPosition = __instance.Transform.position;
            }

            // Otherwise, update as usual
            if (!__instance._animatedPathFollower.Stopped)
            {
                __instance.UpdateTransform(deltaTime);
                __instance.UpdateAnimationSpeed();
                __instance.UpdateGroupId();
            }
            __instance.NotifyAnimationUpdated();
            __instance.UpdateRotation();

            //if (position != Vector3.zero)
            //{
            //    Plugin.Log($"pos: {position} -> {__instance._animatedPathFollower.CurrentPosition}");
            //    Vector3 dir = __instance._animatedPathFollower.CurrentPosition - position;
            //    Plugin.Log($"XDir: {Mathf.Sign(dir.x)}, ZDir: {Mathf.Sign(dir.z)}");
            //}

            // We've replaced the original method, so skip it
            return false;
        }
    }
#endif
}
