using BeaverBuddies.Connect;
using Timberborn.CoreUI;
using Timberborn.SingletonSystem;

namespace BeaverBuddies.IO
{
    public sealed class CompatibilityStatus : RegisteredSingleton, IUpdatableSingleton, IResettableSingleton
    {
        readonly DialogBoxShower dialogs;
        DialogBox box;
        public CompatibilityStatus(DialogBoxShower dialogs) { this.dialogs = dialogs; }
        public void UpdateSingleton()
        {
            if (!ReplayService.IsLoaded || EventIO.IsNull || ReplayService.HasReplayFailure || ReplayService.CompatibilityReady || SnapshotResyncService.Active)
            { Hide(); return; }
            if (box == null) box = dialogs.Create()
                .SetMessage("Waiting for all players to load and verify their mod settings. Simulation will remain paused until the check passes.\n\nIf you joined first, wait for the host to choose Start game.")
                .SetConfirmButton(() => { box = null; SingletonManager.GetSingleton<ReplayService>()?.AbortReplay("Mod compatibility check canceled."); }, "Stop session").Show();
        }
        void Hide() { var old = box; box = null; old?.Close(); }
        public void Reset() { box = null; }
    }
}
