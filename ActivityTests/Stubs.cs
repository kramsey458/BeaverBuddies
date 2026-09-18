// Only Unity/game boundaries are substituted. The production activity service is compiled unchanged.
using TimberNet;
namespace UnityEngine
{
    public class Object { public static void Destroy(Object value) { } }
    public class GameObject : Object
    {
        public GameObject(string name) { }
        public T AddComponent<T>() where T : new() => new T();
    }
    public class Transform { public Vector3 position; }
    public struct Ray { }
    public struct RaycastHit { public Vector3 point; }
    public record struct Color(float r, float g, float b, float a = 1);
    public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x=x; this.y=y; } }
    public struct Vector3
    {
        public float x,y,z; public Vector3(float x,float y,float z) { this.x=x; this.y=y; this.z=z; }
        public static Vector3 zero => new();
        public static Vector3 Lerp(Vector3 a,Vector3 b,float t) => new(a.x+(b.x-a.x)*t,a.y+(b.y-a.y)*t,a.z+(b.z-a.z)*t);
    }
    public static class Mathf { public static float Clamp01(float x) => Math.Clamp(x,0,1); }
    public static class Time { public static float unscaledTime; }
    public static class Screen { public static int width=1920,height=1080; }
    public static class Application { public static bool isFocused=true; }
    public static class ColorUtility
    {
        public static string ToHtmlStringRGB(Color c) => "12ABEF";
        public static bool TryParseHtmlString(string s,out Color c) { c=new(1,0,0); return true; }
    }
}
namespace Timberborn.BaseComponentSystem
{
    public class BaseComponent
    {
        public Dictionary<Type,BaseComponent> Components = new();
        public UnityEngine.Transform Transform = new();
        public T GetComponent<T>() where T : class => Components.GetValueOrDefault(typeof(T)) as T;
        public bool HasComponent<T>() => Components.ContainsKey(typeof(T));
        public static implicit operator bool(BaseComponent b) => b is not null;
    }
}
namespace Timberborn.Buildings { public class Building : Timberborn.BaseComponentSystem.BaseComponent { } }
namespace Timberborn.EntitySystem
{
    public class EntityComponent : Timberborn.BaseComponentSystem.BaseComponent { public Guid EntityId; public bool Deleted; }
    public class EntityRegistry
    {
        public readonly Dictionary<Guid,EntityComponent> Entities = new();
        public EntityComponent GetEntity(Guid id) => Entities.GetValueOrDefault(id);
    }
}
namespace Timberborn.SelectionSystem
{
    public class SelectableObject : Timberborn.BaseComponentSystem.BaseComponent { }
    public class HighlightableObject : Timberborn.BaseComponentSystem.BaseComponent { }
    public class Highlighter
    {
        public readonly HashSet<Timberborn.BaseComponentSystem.BaseComponent> Targets=new();
        public void HighlightSecondary(Timberborn.BaseComponentSystem.BaseComponent target,UnityEngine.Color c) => Targets.Add(target);
        public void UnhighlightAllSecondary() => Targets.Clear();
    }
    public class EntitySelectionService { public SelectableObject SelectedObject; }
    public class SelectableObjectRaycaster
    {
        public bool Hit; public UnityEngine.Vector3 Position;
        public bool TryHitSelectableObjectIncludeTerrainStump(UnityEngine.Ray ray,out SelectableObject selected,out UnityEngine.RaycastHit hit)
        { selected=null; hit=new() {point=Position}; return Hit; }
    }
}
namespace Timberborn.CameraSystem
{
    public class CameraService
    {
        public UnityEngine.Ray ScreenPointToRayInWorldSpace(UnityEngine.Vector2 p) => new();
        public UnityEngine.Ray ScreenPointToRayInGridSpace(UnityEngine.Vector2 p) => new();
    }
}
namespace Timberborn.TerrainQueryingSystem
{
    public struct Hit { public UnityEngine.Vector3 Intersection; }
    public class TerrainPicker
    {
        public Hit? Hit;
        public Hit? PickTerrainCoordinates(UnityEngine.Ray r) => Hit;
    }
}
namespace Timberborn.Coordinates { public static class CoordinateSystem { public static UnityEngine.Vector3 GridToWorld(UnityEngine.Vector3 p) => p; } }
namespace Timberborn.InputSystem { public class InputService { public bool MouseOverUI; public UnityEngine.Vector2 MousePosition = new(100,100); } }
namespace Timberborn.SceneLoading
{
    public class LoadingScreen { public event EventHandler LoadingScreenEnabled; public void Show() => LoadingScreenEnabled?.Invoke(this,EventArgs.Empty); }
}
namespace Timberborn.SingletonSystem
{
    public interface IPostLoadableSingleton { void PostLoad(); }
    public interface IUpdatableSingleton { void UpdateSingleton(); }
}
namespace BeaverBuddies
{
    public class RegisteredSingleton { protected RegisteredSingleton() { SingletonManager.Instance=this; } }
    public interface IResettableSingleton { void Reset(); }
    public static class SingletonManager { public static object Instance; public static T GetSingleton<T>() => (T)Instance; }
    public static class Settings { public static bool PlayerActivityEnabled=true; public static string PingDisplayName="Alex"; public static UnityEngine.Color PingColorValue=new(1,0,0); }
    public static class Plugin { public static void LogWarning(string value) => throw new Exception(value); }
    public static class ReplayService { public static bool IsLoaded=true,HasReplayFailure,IsReplayingEvents; }
    public static class DeterminismService { public static bool IsTicking; }
}
namespace BeaverBuddies.Connect { public static class SnapshotResyncService { public static bool Active; } }
namespace BeaverBuddies.IO
{
    public class EventIO { public static object Current; public static object Get() => Current; }
    public class ServerEventIO { public TimberNetBase NetBase; }
    public class ClientEventIO { public TimberNetBase NetBase; }
}
namespace BeaverBuddies.Activity
{
    public class PlayerActivityOverlay { public PlayerActivityService Service; public Timberborn.CameraSystem.CameraService CameraService; }
}
