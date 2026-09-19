// Only the external Unity/game APIs are doubled. Tests compile the production Steam transport
// core and animation patch source directly.
namespace Timberborn.BuildingsUI { }
namespace Timberborn.Workshops { }
namespace UnityEngine.PlayerLoop { }
namespace UnityEngine
{
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; } }
    public class Transform { public Vector3 position; }
    public static class Time { public static float time; public static float fixedDeltaTime = 1; }
}
namespace HarmonyLib
{
    public class HarmonyPatch : Attribute { public HarmonyPatch(Type t, string name, Type parameter) { } }
}
namespace BeaverBuddies
{
    public class ManualMethodOverwrite : Attribute { }
    public static class Plugin { public static void Log(string s) { } public static void LogWarning(string s) { } }
    public static class SingletonManager
    {
        public static TickProgressService Progress = new();
        public static T GetSingleton<T>() where T : class => Progress as T;
    }
    public class TickProgressService
    {
        public float Time;
        public float TimeAtLastTick(Timberborn.EntitySystem.EntityComponent e) => Time;
        public float PercentTicked(Timberborn.EntitySystem.EntityComponent e) => 0;
    }
}
namespace BeaverBuddies.IO { public static class EventIO { public static bool IsNull; } }
namespace Timberborn.EntitySystem { public class EntityComponent { } }
namespace Timberborn.CharacterMovementSystem
{
    // Deliberately forward-only, like the game's follower. A segment already
    // passed at t=2 must not be extrapolated when the mod next requests t=.5.
    public class TestFollower
    {
        public int _nextCornerIndex;
        public UnityEngine.Vector3 CurrentPosition;
        public bool Stopped;
        public bool InvalidPosition;
        public void Update(float time)
        {
            while (_nextCornerIndex < 3 && time >= _nextCornerIndex) _nextCornerIndex++;
            float x = _nextCornerIndex <= 1 ? time : 1 + (time - 1) * 100;
            CurrentPosition = new(InvalidPosition ? float.NaN : x, 0, 0);
        }
    }
    public class MovementAnimator
    {
        public TestFollower _animatedPathFollower = new();
        public UnityEngine.Transform Transform = new();
        public UnityEngine.Vector3 ModelPosition;
        public bool Notified;
        public T GetComponent<T>() where T : new() => new();
        public void Update(float delta) { }
        public void UpdateTransform(float delta) => ModelPosition = _animatedPathFollower.CurrentPosition;
        public void UpdateAnimationSpeed() { }
        public void UpdateGroupId() { }
        public void NotifyAnimationUpdated()
        {
            if (!float.IsFinite(ModelPosition.x) || ModelPosition.x < 0) throw new IndexOutOfRangeException("Invalid water-map coordinate");
            Notified = true;
        }
        public void UpdateRotation() { }
    }
}

namespace BeaverBuddies { public static class Settings { public static bool Debug; public static bool VerboseLogging = true; } }
