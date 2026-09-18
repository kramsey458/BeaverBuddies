using BeaverBuddies.IO;
using HarmonyLib;
using Timberborn.OptionsGame;
using UnityEngine.UIElements;

namespace BeaverBuddies.Connect
{
    [HarmonyPatch(typeof(GameOptionsBox), nameof(GameOptionsBox.GetPanel))]
    internal static class SnapshotResyncMenu
    {
        static void Postfix(GameOptionsBox __instance, VisualElement __result)
        {
            if (EventIO.IsNull) return;
            var button = ButtonInserter.DuplicateOrGetButton(__result, "LoadGameButton", "SnapshotResyncButton", created =>
            {
                created.text = "Resync from host";
                created.tooltip = "Ask the host to save and reload a shared snapshot for everyone. The host must enable automatic snapshot recovery.";
                created.clicked += () =>
                {
                    var replay = SingletonManager.GetSingleton<ReplayService>();
                    if (replay == null || SnapshotResyncService.Active || ReplayService.HasReplayFailure) return;
                    __instance.OnUICancelled();
                    SnapshotResyncService.TryRecover(replay);
                };
            });
            button.SetEnabled(ReplayService.IsLoaded && !SnapshotResyncService.Active && !ReplayService.HasReplayFailure);
        }
    }
}
