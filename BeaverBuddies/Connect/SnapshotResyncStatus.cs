using System;
using Timberborn.CoreUI;
using UnityEngine.UIElements;

namespace BeaverBuddies.Connect
{
    internal static class SnapshotResyncStatus
    {
        static DialogBox box;
        static bool hasContinue;
        public static void Forget() => box = null;
        public static void Hide()
        {
            var previous = box; box = null;
            previous?.Close();
        }
        public static void Show(DialogBoxShower dialogs, string text, Action cancel, Action continueWithout = null)
        {
            if (box != null && hasContinue != (continueWithout != null)) Hide();
            if (box == null)
            {
                hasContinue = continueWithout != null;
                var created = dialogs.Create().SetMessage(text).SetConfirmButton(() =>
                {
                    box = null; (continueWithout ?? cancel)();
                }, continueWithout != null ? "Continue without player" : "Cancel recovery");
                if (continueWithout != null) created.SetCancelButton(() => { box = null; cancel(); }, "Cancel recovery");
                box = created.Show();
            }
            else box.GetPanel().Q<Label>("Message").text = text;
        }
    }
}
