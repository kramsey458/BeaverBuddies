using TimberNet;
namespace UnityEngine
{
    public static class Application { public static string version="1.1.2.4", persistentDataPath; }
    public static class Random { public struct State { public int s0,s1,s2,s3; } public static State state=new(){s0=1,s1=2,s2=3,s3=4}; }
}
namespace Timberborn.SingletonSystem { public interface ILoadableSingleton { void Load(); } }
namespace Timberborn.Modding
{
    public class ModRepository { public List<Mod> EnabledMods=new(); }
    public class Mod { public Manifest Manifest=new(); public ModDirectory ModDirectory=new(); }
    public class ModDirectory { public string Path; }
    public class Manifest { public string Id; public ModVersion Version=new(); }
    public class ModVersion { public string Full="1"; }
}
namespace ModSettings.Core
{
    public class ModSetting { }
    public class NonPersistentSetting : ModSetting { }
    public class ModSetting<T> : ModSetting { public T Value {get;set;} }
    public class ModSettingsOwner { public List<ModSetting> ModSettings=new(); }
    public class ModSettingsOwnerRegistry
    {
        public Dictionary<Timberborn.Modding.Mod,List<ModSettingsOwner>> Owners=new();
        public bool HasModSettings(Timberborn.Modding.Mod mod)=>Owners.ContainsKey(mod);
        public List<ModSettingsOwner> GetModSettingOwners(Timberborn.Modding.Mod mod)=>Owners[mod];
    }
}
namespace Timberborn.EntitySystem
{
    public class BaseComponent
    {
        public readonly List<object> Components=new();
        public T GetComponent<T>() where T:class => Components.OfType<T>().FirstOrDefault();
        public void GetComponents<T>(List<T> result) { result.AddRange(Components.OfType<T>()); }
    }
    public class EntityComponent : BaseComponent { public Guid EntityId; public bool Deleted; }
    public class EntityRegistry { public List<EntityComponent> Entities=new(); }
}
namespace Timberborn.WorkSystem
{
    public class Workplace : Timberborn.EntitySystem.BaseComponent { }
    public class Worker { public Workplace Workplace; public bool JobRunning; }
}
namespace Timberborn.BehaviorSystem
{
    public class Behavior { public string ComponentName="behavior"; }
    public class BehaviorManager { public Behavior _runningBehavior; public object _runningExecutor; public float _runningExecutorElapsedTime; }
}
namespace Timberborn.InventorySystem
{
    public struct GoodAmount { public string GoodId; public int Amount; }
    public class Goods { public List<GoodAmount> GoodsList=new(); public List<GoodAmount> Values=>GoodsList; }
    public class ReservedGoods { public List<GoodAmount> Goods=new(); }
    public class Inventory
    {
        public string ComponentName="inventory"; public int Capacity,TotalAmountInStock;
        public List<GoodAmount> Stock=new(), Reserved=new(); public ReservedGoods _reservedStock=new();
        public List<GoodAmount> ReservedCapacity()=>Reserved;
    }
}
namespace Timberborn.WaterSystem
{
    public struct ReadOnlyWaterColumn { public float WaterDepth,Contamination,Overflow; public byte Floor,Ceiling; }
    public class ThreadSafeWaterMap { public ReadOnlyWaterColumn[] _threadSafeWaterColumns=new ReadOnlyWaterColumn[512]; public byte[] _threadSafeColumnCounts=new byte[512]; public int _verticalStride=512; }
}
namespace BeaverBuddies
{
    public interface IResettableSingleton { void Reset(); }
    public class RegisteredSingleton { public RegisteredSingleton()=>SingletonManager.Objects[GetType()]=this; }
    public static class SingletonManager { public static Dictionary<Type,object> Objects=new(); public static T GetSingleton<T>()=>Objects.TryGetValue(typeof(T),out var value)?(T)value:default; }
    public static class Plugin { public const string ID="beaverbuddies", Version="11"; public static List<string> Warnings=new(); public static void LogWarning(string s) {lock(Warnings)Warnings.Add(s);} }
    public static class Settings { public static bool RollingDiagnosticsEnabled=true,Debug; }
    public static class GuidPatcher { public static Guid RealNewGuid()=>Guid.NewGuid(); }
    public static class TEBPatcher { public static int EntityUpdateHash=17,PositionHash=19; }
    public class ReplayService : RegisteredSingleton { public static TimberNetBase Network; public int TicksSinceLoad; }
    public class HeartbeatEvent : Events.ReplayEvent { }
}
namespace BeaverBuddies.IO
{
    public abstract class EventIO { public static EventIO Current; public static EventIO Get()=>Current; }
    public class ServerEventIO : EventIO { public TimberServer NetBase; }
    public class ClientEventIO : EventIO { }
}
namespace BeaverBuddies.Events
{
    public class ReplayEvent { public int ticksSinceLoad; public int? randomS0Before; public string type=>GetType().Name; }
}
