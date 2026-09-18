using System;
using Timberborn.CoreUI;
using UnityEngine.UIElements;

namespace BeaverBuddies.Connect
{
    internal static class SnapshotResyncStatus
    {
        static DialogBox box;
        public static void Forget() => box = null;
        public static void Hide()
        {
            var previous = box; box = null;
            previous?.Close();
        }
        public static void Show(DialogBoxShower dialogs, string text, Action cancel)
        {
            if (box == null)
                box = dialogs.Create().SetMessage(text).SetConfirmButton(() =>
                {
                    box = null; cancel();
                }, "Stop recovery").Show();
            else box.GetPanel().Q<Label>("Message").text = text;
        }
    }
}
