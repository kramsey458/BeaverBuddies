using System;
using System.Globalization;
using Timberborn.CoreUI;
using Timberborn.Localization;
using Timberborn.SingletonSystem;

namespace BeaverBuddies.Connect
{
    /// <summary>
    /// Shows the "your mods differ" warning found when a player joined. The host sees it in the lobby, before
    /// choosing Start Game; a guest sees it as soon as the game has loaded.
    /// </summary>
    public class ModMismatchWarningService : IUpdatableSingleton
    {
        private readonly DialogBoxShower _dialogBoxShower;
        private readonly ILoc _loc;

        public ModMismatchWarningService(DialogBoxShower dialogBoxShower, ILoc loc)
        {
            _dialogBoxShower = dialogBoxShower;
            _loc = loc;
        }

        public void UpdateSingleton()
        {
            if (!ModWarnings.HasPending) return;
            var pending = ModWarnings.TakeAll();
            if (pending.Count == 0) return;
            try
            {
                string message = ModWarningText.Build(pending, Translate);
                _dialogBoxShower.Create().SetMessage(message).SetDefaultCancelButton().Show();
            }
            catch (Exception error)
            {
                // A warning that cannot be shown must never disturb the game.
                Plugin.LogError("Could not show the mod list warning: " + error);
            }
        }

        // ILoc translates a key; the placeholders are filled in here, in a fixed culture.
        private string Translate(string key, object[] args)
        {
            string text = _loc.T(key);
            return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
        }
    }
}
