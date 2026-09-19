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

        // Base cursor size in pixels at a style size of 1.
        const float CursorWidth = 18, CursorHeight = 26;
        // The arrow is authored on this grid, then drawn at whatever size the player asked for.
        const int TextureScale = 2;

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
                    if (player.State == null) continue;
                    var style = Service.StyleOf(player);
                    Color color = Service.ColorOf(player);
                    // Text follows the cursor's transparency, but never fades out completely.
                    float textAlpha = Mathf.Clamp(style.Opacity + .35f, .5f, .95f);
                    if (player.State.CursorVisible && Project(camera, player.CursorPosition(Time.unscaledTime), out var point))
                    {
                        var size = new Vector2(CursorWidth, CursorHeight) * style.Size;
                        Color tint = color; tint.a = style.Opacity; GUI.color = tint;
                        GUI.DrawTexture(new Rect(point.x, point.y, size.x, size.y), cursor);
                        DrawLabel(point + new Vector2(size.x + 2, size.y * .3f), player.Label, color, textAlpha);
                    }
                    var selected = player.Selected;
                    var editing = player.Editing;
                    bool isEditing = editing && !editing.Deleted && editing.HasComponent<Building>();
                    var target = isEditing ? editing : selected;
                    if (target && !target.Deleted && Project(camera, target.Transform.position + Vector3.up, out var location))
                    {
                        string action = isEditing ? "Editing: " : target.HasComponent<Building>() ? "Viewing: " : "Selected: ";
                        DrawLabel(location + new Vector2(14, -24 - 20 * index), action + player.Label, color, textAlpha);
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

        void DrawLabel(Vector2 point, string text, Color color, float alpha)
        {
            GUI.color = Color.white;
            float width = Mathf.Min(420, label.CalcSize(new GUIContent(text)).x + 8);
            var rect = new Rect(Mathf.Clamp(point.x, 0, Mathf.Max(0, Screen.width - width)), Mathf.Clamp(point.y, 0, Mathf.Max(0, Screen.height - 22)), width, 22);
            label.normal.textColor = new Color(0, 0, 0, alpha);
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, label);
            color.a = alpha; label.normal.textColor = color;
            GUI.Label(rect, text, label);
        }

        // Arrow silhouette generated once at twice the base resolution so it stays smooth when enlarged.
        static Texture2D CreateCursor()
        {
            int width = (int)CursorWidth * TextureScale, height = (int)CursorHeight * TextureScale;
            var texture = new Texture2D(width, height) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (int py = 0; py < height; py++)
            for (int px = 0; px < width; px++)
            {
                float x = (px + .5f) / TextureScale, y = (py + .5f) / TextureScale;
                bool inside = y < 19 && x <= y * .7f || y >= 14 && y < 25 && x >= 5 && x <= 8;
                texture.SetPixel(px, height - 1 - py, inside ? Color.white : Color.clear);
            }
            texture.Apply();
            return texture;
        }
    }
}
