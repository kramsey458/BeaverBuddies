using Newtonsoft.Json.Linq;
using TimberNet;
// Controlled engine boundaries: the production recovery coordinator is compiled unchanged.
namespace Timberborn.SingletonSystem { public interface IUpdatableSingleton { void UpdateSingleton(); } }
namespace Timberborn.GameSaveRepositorySystem
{
    public class SaveReference { }
    public class GameSaveRepository { }
}
namespace Timberborn.GameSceneLoading
{
    public class GameSceneLoader
    {
        public int Loads;
        public void StartSaveGame(Timberborn.GameSaveRepositorySystem.SaveReference reference) { Loads++; }
    }
}
namespace Timberborn.CoreUI
{
    public class DialogBoxShower
    {
        public string Message; public int Shown; public Action Confirm;
        public DialogBoxShower Create() => this;
        public DialogBoxShower SetMessage(string message) { Message=message; return this; }
        public DialogBoxShower SetConfirmButton(Action action,string text) { Confirm=action; return this; }
        public DialogBoxShower SetDefaultCancelButton() => this;
        public void Show() { Shown++; }
    }
}
namespace BeaverBuddies
{
    public static class GuidPatcher { public static Guid RealNewGuid() => Guid.NewGuid(); }
    public static class Plugin { public static void LogWarning(string s) { } public static void LogError(string s) { } }
    public static class Settings { public static bool SnapshotResyncEnabled = true; }
    public static class DeterminismService { public static byte[] Seed; public static void InitGameStartState(byte[] b) => Seed=b; }
    public static class SingletonManager
    {
        public static ReplayService Replay = new();
        public static T GetSingleton<T>() => (T)(object)Replay;
        public static void Reset() { ReplayService.IsLoaded=false; Replay=new(); }
    }
    public class ReplayService
    {
        public static bool HasReplayFailure, IsLoaded;
        public float TargetSpeed = 3;
        public Action Finish;
        public bool DeferFinish;
        public bool Frozen;
        public List<Events.SpeedSetEvent> Recorded = new();
        public void FreezeForSnapshot() { Frozen=true; TargetSpeed=0; }
        public void FinishTickForSnapshot(Action completed) { if (DeferFinish) Finish=completed; else completed(); }
        public void RecordEvent(Events.SpeedSetEvent value) { Recorded.Add(value); }
    }
}
namespace BeaverBuddies.DesyncDetecter { public static class WaterDiagnostics { public static void WriteOnDesync() { } } public static class RollingDiagnosticsService { public static void Trigger(string reason) { } } }
namespace BeaverBuddies.Events { public class SpeedSetEvent { public float speed; } }
namespace BeaverBuddies.IO
{
    public class TestNet
    {
        public int ClientCount=2;
        public bool IsStopped;
        public List<JObject> Sent = new();
        public TaskCompletionSource<bool> Flush = new();
        public void StopAcceptingClients(string text) { }
        public void SendControl(JObject message) => Sent.Add((JObject)message.DeepClone());
        public Task FlushAsync() => Flush.Task;
    }
    public abstract class EventIO
    {
        static EventIO instance;
        public static EventIO Get() => instance;
        public static void Set(EventIO io) { instance?.Close(); instance=io; }
        public int Closed;
        public virtual void Close() { Closed++; }
        public virtual void Update() { }
    }
    public class ServerEventIO : EventIO
    {
        public TestNet NetBase = new();
        public byte[] Map;
        public bool HasSteamClients;
        public void Start(byte[] bytes) => Map=bytes;
    }
    public class ClientEventIO : EventIO { public TestNet NetBase = new(); }
}
namespace BeaverBuddies.Connect
{
    public class RehostingService
    {
        public bool Succeeds = true;
        public int Saves, Rehosts;
        public Action<Timberborn.GameSaveRepositorySystem.SaveReference> Saved;
        public bool SaveRehostFile(Action<Timberborn.GameSaveRepositorySystem.SaveReference> callback, bool waitUntilAccessible)
        { Saves++; if (!waitUntilAccessible) throw new Exception("stream still open"); Saved=callback; return Succeeds; }
        public void RehostGame() => Rehosts++;
    }
    public class ClientConnectionService
    {
        public int Reconnects;
        public void BeginSnapshotReconnect() => Reconnects++;
        public void ConnectOrShowFailureMessage() => Reconnects++;
    }
    public static class ServerHostingUtils
    {
        public static byte[] Bytes = new byte[]{4,5,6};
        public static byte[] GetMapBtyes(Timberborn.GameSaveRepositorySystem.GameSaveRepository r,Timberborn.GameSaveRepositorySystem.SaveReference s) => Bytes;
    }
}

namespace BeaverBuddies.Connect
{
    static class SnapshotResyncStatus
    {
        public static void Forget() { }
        public static void Hide() { }
        public static void Show(Timberborn.CoreUI.DialogBoxShower d,string text,Action cancel) { }
    }
}
