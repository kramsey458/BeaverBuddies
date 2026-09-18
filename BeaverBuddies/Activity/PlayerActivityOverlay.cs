using Timberborn.Buildings;
using Timberborn.CameraSystem;
using UnityEngine;

namespace BeaverBuddies.Activity
{
    // Read-only IMGUI overlay: repaint only; no controls, input capture, colliders, or world objects.
    public sealed class PlayerActivityOverlay : MonoBehaviour
    {
        public PlayerActivityService Service;
        public CameraService CameraService;
        public const float CursorOpacity = .5f;
        Texture2D cursor;
        GUIStyle label;

        void OnDestroy() { if (cursor != null) Destroy(cursor); }
        public void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || Service == null || !Settings.PlayerActivityEnabled) return;
            var transform = CameraService?.Transform;
            if (transform == null) return;
            var camera = transform.GetComponent<Camera>();
            if (camera == null || !camera.isActiveAndEnabled) return;
            if (label == null) label = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, richText = false };
            if (cursor == null) cursor = CreateCursor();
            Color previous = GUI.color;
            try
            {
                int index = 0;
                foreach (var player in Service.RemotePlayers)
                {
                    Color color = player.Color;
                    if (player.State.CursorVisible && Project(camera, player.CursorPosition(Time.unscaledTime), out var point))
                    {
                        color.a = CursorOpacity; GUI.color = color;
                        GUI.DrawTexture(new Rect(point.x, point.y, 18, 26), cursor);
                        DrawLabel(point + new Vector2(20, 8), player.Label, player.Color);
                    }
                    var selected = player.Selected;
                    var editing = player.Editing;
                    bool isEditing = editing && !editing.Deleted && editing.HasComponent<Building>();
                    var target = isEditing ? editing : selected;
                    if (target && !target.Deleted && Project(camera, target.Transform.position + Vector3.up, out var location))
                    {
                        string action = isEditing ? "Editing: " : target.HasComponent<Building>() ? "Viewing: " : "Selected: ";
                        DrawLabel(location + new Vector2(14, -24 - 20 * index), action + player.Label, player.Color);
                    }
                    index++;
                }
            }
            finally { GUI.color = previous; }
        }

        static bool Project(Camera camera, Vector3 world, out Vector2 point)
        {
            Vector3 screen = camera.WorldToScreenPoint(world);
            point = new Vector2(screen.x, Screen.height - screen.y);
            return screen.z > 0 && screen.x >= 0 && screen.y >= 0 && screen.x <= Screen.width && screen.y <= Screen.height;
        }
        void DrawLabel(Vector2 point, string text, Color color)
        {
            GUI.color = Color.white;
            float width = Mathf.Min(420, label.CalcSize(new GUIContent(text)).x + 8);
            var rect = new Rect(Mathf.Clamp(point.x, 0, Mathf.Max(0, Screen.width - width)), Mathf.Clamp(point.y, 0, Mathf.Max(0, Screen.height - 22)), width, 22);
            label.normal.textColor = Color.black;
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, label);
            color.a = .95f; label.normal.textColor = color;
            GUI.Label(rect, text, label);
        }
        static Texture2D CreateCursor()
        {
            var texture = new Texture2D(18, 26) { filterMode = FilterMode.Point };
            // Arrow silhouette generated once, with transparent surroundings.
            for (int y = 0; y < 26; y++)
            for (int x = 0; x < 18; x++)
            {
                bool inside = y < 19 && x <= y * .7f || y >= 14 && y < 25 && x >= 5 && x <= 8;
                texture.SetPixel(x, 25 - y, inside ? Color.white : Color.clear);
            }
            texture.Apply();
            return texture;
        }
    }
}
