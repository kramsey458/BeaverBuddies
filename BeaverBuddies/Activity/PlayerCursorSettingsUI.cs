using System;
using System.Collections.Generic;
using BeaverBuddies.Connect;
using HarmonyLib;
using Timberborn.CoreUI;
using Timberborn.Localization;
using Timberborn.OptionsGame;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Activity
{
    // Adds a "Player cursors" button to the in-game options menu, next to Settings.
    [HarmonyPatch(typeof(GameOptionsBox), "GetPanel")]
    public class PlayerCursorMenuPatcher
    {
        public static void Postfix(ref VisualElement __result)
        {
            // Only registered in co-op sessions, so single-player menus are untouched.
            SingletonManager.GetSingleton<PlayerCursorSettingsUI>()?.AddButton(__result);
        }
    }

    /// <summary>
    /// A dialog for choosing, per connected player, the color, size and transparency of their cursor.
    /// These choices are local: they change only how you see other players.
    /// </summary>
    public class PlayerCursorSettingsUI : RegisteredSingleton
    {
        readonly DialogBoxShower dialogs;
        readonly ILoc loc;

        public PlayerCursorSettingsUI(DialogBoxShower dialogs, ILoc loc)
        {
            this.dialogs = dialogs;
            this.loc = loc;
        }

        public void AddButton(VisualElement optionsRoot)
        {
            try
            {
                ButtonInserter.DuplicateOrGetButton(optionsRoot, "SettingsButton", "PlayerCursorsButton", button =>
                {
                    button.text = loc.T("BeaverBuddies.Cursors.Button");
                    button.clicked += Show;
                });
            }
            catch (Exception error)
            {
                // The menu must still open if the game's layout differs from what we expect.
                Plugin.LogWarning("Could not add the player cursors button: " + error.Message);
            }
        }

        void Show()
        {
            var service = SingletonManager.GetSingleton<PlayerActivityService>();
            if (service == null) return;
            var panel = new CursorPanel(service, loc);
            dialogs.Create()
                .SetMessage(loc.T("BeaverBuddies.Cursors.Title") + "\n" + loc.T("BeaverBuddies.Cursors.Subtitle"))
                .SetMaxWidth(620)
                .AddContent(panel.Root)
                .SetConfirmButton(panel.Commit, loc.T("BeaverBuddies.Cursors.Done"))
                .Show();
        }
    }

    // Built entirely from inline styles so it stays readable whatever the game's stylesheet does.
    sealed class CursorPanel
    {
        static readonly Color Ink = new Color(.95f, .91f, .82f);
        static readonly Color Muted = new Color(.75f, .71f, .63f);
        static readonly Color CardBackground = new Color(0, 0, 0, .28f);
        static readonly Color CardBorder = new Color(1, 1, 1, .12f);
        static readonly Color[] Presets =
        {
            Hex("FF4D4D"), Hex("FF9F1C"), Hex("FFD93D"), Hex("6BCB77"), Hex("2EC4B6"),
            Hex("4D96FF"), Hex("9B5DE5"), Hex("F15BB5"), Hex("FFFFFF"), Hex("9CA3AF"),
        };

        readonly PlayerActivityService service;
        readonly ILoc loc;
        readonly ScrollView list;
        int shownVersion = -1;

        public VisualElement Root { get; }

        public CursorPanel(PlayerActivityService service, ILoc loc)
        {
            this.service = service; this.loc = loc;
            Root = new VisualElement();
            Root.style.marginTop = 8;
            list = new ScrollView(ScrollViewMode.Vertical);
            list.style.maxHeight = 440;
            Root.Add(list);
            Rebuild();
            // Players can join or leave while the dialog is open.
            Root.schedule.Execute(() =>
            {
                try { if (service.PlayersVersion != shownVersion) Rebuild(); }
                catch (Exception error) { Plugin.LogWarning("Could not refresh the player cursors list: " + error.Message); }
            }).Every(500);
        }

        /// <summary>Called when the dialog is confirmed. Every edit is also saved as it is made.</summary>
        public void Commit() => Save();

        void Save()
        {
            if (!PlayerActivityService.Preferences.Save())
                Plugin.LogWarning("Could not save player cursor settings; they apply until the game closes.");
        }

        void Rebuild()
        {
            shownVersion = service.PlayersVersion;
            list.Clear();
            var players = service.Players();
            if (players.Count == 0)
            {
                list.Add(Text(loc.T("BeaverBuddies.Cursors.None"), 14, Muted));
                return;
            }
            foreach (var player in players) list.Add(BuildCard(player));
        }

        VisualElement BuildCard(PlayerCursorEntry player)
        {
            var prefs = PlayerActivityService.Preferences;
            string key = player.StyleKey;
            // Work on a copy; the saved entry is replaced each time a control changes.
            var style = prefs.Get(key).Clone();

            var card = new VisualElement();
            card.style.marginBottom = 10; card.style.paddingTop = 8; card.style.paddingBottom = 8;
            card.style.paddingLeft = 10; card.style.paddingRight = 10;
            card.style.backgroundColor = CardBackground;
            Border(card, 1, CardBorder, 4);

            // Header: a live preview of the cursor colour and transparency, the name, and Reset.
            var header = Horizontal(); header.style.marginBottom = 6; header.style.alignItems = Align.Center;
            var preview = new VisualElement();
            preview.style.width = 18; preview.style.height = 18; preview.style.marginRight = 8;
            Border(preview, 1, new Color(1, 1, 1, .5f), 3);
            var name = Text(player.Label, 16, Ink); name.style.flexGrow = 1; name.style.unityFontStyleAndWeight = FontStyle.Bold;
            var reset = new Button { text = loc.T("BeaverBuddies.Cursors.Reset") };
            header.Add(preview); header.Add(name); header.Add(reset);
            card.Add(header);

            // Colour: presets, then exact channels.
            var swatches = Horizontal(); swatches.style.flexWrap = Wrap.Wrap; swatches.style.marginBottom = 4;
            var swatchViews = new List<(VisualElement View, string Hex)>();
            card.Add(swatches);

            var red = new CursorSlider(0, 255, 0, new Color(.85f, .25f, .25f));
            var green = new CursorSlider(0, 255, 0, new Color(.3f, .75f, .35f));
            var blue = new CursorSlider(0, 255, 0, new Color(.3f, .5f, .95f));
            var size = new CursorSlider(PlayerCursorStyle.MinSize, PlayerCursorStyle.MaxSize, 1, new Color(.7f, .6f, .35f));
            // The panel talks about transparency; the style stores its opposite, opacity.
            var transparency = new CursorSlider(0, 1 - PlayerCursorStyle.MinOpacity, 0, new Color(.7f, .6f, .35f));
            var redValue = ValueLabel(); var greenValue = ValueLabel(); var blueValue = ValueLabel();
            var sizeValue = ValueLabel(); var transparencyValue = ValueLabel();
            card.Add(SliderRow(loc.T("BeaverBuddies.Cursors.Red"), red, redValue));
            card.Add(SliderRow(loc.T("BeaverBuddies.Cursors.Green"), green, greenValue));
            card.Add(SliderRow(loc.T("BeaverBuddies.Cursors.Blue"), blue, blueValue));
            card.Add(SliderRow(loc.T("BeaverBuddies.Cursors.Size"), size, sizeValue));
            card.Add(SliderRow(loc.T("BeaverBuddies.Cursors.Transparency"), transparency, transparencyValue));

            Color EffectiveColor()
            {
                if (style.ColorHex != null && ColorUtility.TryParseHtmlString("#" + style.ColorHex, out var custom)) return custom;
                return player.AdvertisedColor;
            }

            void Refresh(bool updateSliders)
            {
                Color color = EffectiveColor();
                var shown = color; shown.a = style.Opacity;
                preview.style.backgroundColor = shown;
                foreach (var (view, hex) in swatchViews)
                    Border(view, string.Equals(hex, style.ColorHex, StringComparison.OrdinalIgnoreCase) ? 2 : 1,
                        string.Equals(hex, style.ColorHex, StringComparison.OrdinalIgnoreCase) ? Color.white : new Color(0, 0, 0, .7f), 3);
                redValue.text = Mathf.RoundToInt(color.r * 255).ToString();
                greenValue.text = Mathf.RoundToInt(color.g * 255).ToString();
                blueValue.text = Mathf.RoundToInt(color.b * 255).ToString();
                sizeValue.text = Mathf.RoundToInt(style.Size * 100) + "%";
                transparencyValue.text = Mathf.RoundToInt((1 - style.Opacity) * 100) + "%";
                if (!updateSliders) return;
                red.SetValueWithoutNotify(color.r * 255); green.SetValueWithoutNotify(color.g * 255); blue.SetValueWithoutNotify(color.b * 255);
                size.SetValueWithoutNotify(style.Size); transparency.SetValueWithoutNotify(1 - style.Opacity);
            }

            // Keep the overlay in step while a slider is dragged; write the file once on release.
            void Store(bool save, bool updateSliders)
            {
                prefs.Set(key, style);
                if (save) Save();
                Refresh(updateSliders);
            }

            void SetColor(string hex) { style.ColorHex = hex; Store(true, true); }
            void SetChannels(bool save)
            {
                style.ColorHex = ColorUtility.ToHtmlStringRGB(new Color(
                    Mathf.Round(red.Value) / 255f, Mathf.Round(green.Value) / 255f, Mathf.Round(blue.Value) / 255f));
                Store(save, false);
            }

            foreach (var channel in new[] { red, green, blue })
            {
                channel.Changing += _ => SetChannels(false);
                channel.Committed += _ => SetChannels(true);
            }
            size.Changing += v => { style.Size = v; Store(false, false); };
            size.Committed += v => { style.Size = v; Store(true, false); };
            transparency.Changing += v => { style.Opacity = 1 - v; Store(false, false); };
            transparency.Committed += v => { style.Opacity = 1 - v; Store(true, false); };
            reset.clicked += () => { style = new PlayerCursorStyle(); Store(true, true); };

            // Added last: the swatch handlers use the sliders above.
            swatchViews.Add((AddSwatch(swatches, player.AdvertisedColor, loc.T("BeaverBuddies.Cursors.TheirColor"), () => SetColor(null)), null));
            foreach (var preset in Presets)
            {
                string hex = ColorUtility.ToHtmlStringRGB(preset);
                swatchViews.Add((AddSwatch(swatches, preset, null, () => SetColor(hex)), hex));
            }

            Refresh(true);
            return card;
        }

        VisualElement AddSwatch(VisualElement parent, Color color, string caption, Action onClick)
        {
            var swatch = new VisualElement();
            swatch.style.height = 22; swatch.style.marginRight = 4; swatch.style.marginBottom = 4;
            swatch.style.width = caption == null ? 22 : 64;
            swatch.style.backgroundColor = color;
            swatch.style.justifyContent = Justify.Center; swatch.style.alignItems = Align.Center;
            Border(swatch, 1, new Color(0, 0, 0, .7f), 3);
            if (caption != null)
            {
                // Pick black or white text depending on how bright the swatch is.
                float brightness = color.r * .299f + color.g * .587f + color.b * .114f;
                var text = Text(caption, 11, brightness > .55f ? Color.black : Color.white);
                text.pickingMode = PickingMode.Ignore;
                swatch.Add(text);
            }
            swatch.RegisterCallback<ClickEvent>(_ => onClick());
            parent.Add(swatch);
            return swatch;
        }

        static VisualElement SliderRow(string caption, CursorSlider slider, Label value)
        {
            var row = Horizontal(); row.style.alignItems = Align.Center; row.style.marginTop = 3;
            var label = Text(caption, 13, Muted); label.style.width = 92;
            row.Add(label); row.Add(slider); row.Add(value);
            return row;
        }

        static Label ValueLabel()
        {
            var label = Text("", 13, Ink);
            label.style.width = 46; label.style.unityTextAlign = TextAnchor.MiddleRight;
            return label;
        }

        static Label Text(string text, int size, Color color)
        {
            var label = new Label(text);
            label.style.color = color; label.style.fontSize = size;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        static VisualElement Horizontal()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            return row;
        }

        internal static void Border(VisualElement element, float width, Color color, float radius)
        {
            var style = element.style;
            style.borderTopWidth = width; style.borderBottomWidth = width; style.borderLeftWidth = width; style.borderRightWidth = width;
            style.borderTopColor = color; style.borderBottomColor = color; style.borderLeftColor = color; style.borderRightColor = color;
            style.borderTopLeftRadius = radius; style.borderTopRightRadius = radius;
            style.borderBottomLeftRadius = radius; style.borderBottomRightRadius = radius;
        }

        static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString("#" + hex, out var color);
            return color;
        }
    }

    /// <summary>
    /// A draggable value bar. <see cref="Changing"/> fires for every step of a drag or click and
    /// <see cref="Committed"/> once when the pointer is released.
    /// </summary>
    sealed class CursorSlider : VisualElement
    {
        readonly float min, max;
        readonly VisualElement fill, thumb;
        public float Value { get; private set; }
        public event Action<float> Changing, Committed;

        public CursorSlider(float min, float max, float value, Color fillColor)
        {
            this.min = min; this.max = max;
            style.flexGrow = 1; style.height = 18; style.minWidth = 140; style.marginLeft = 4; style.marginRight = 8;
            style.backgroundColor = new Color(0, 0, 0, .45f);
            style.overflow = Overflow.Visible;
            CursorPanel.Border(this, 1, new Color(1, 1, 1, .18f), 3);

            fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.style.position = Position.Absolute; fill.style.left = 0; fill.style.top = 0; fill.style.bottom = 0;
            fill.style.backgroundColor = fillColor;
            thumb = new VisualElement { pickingMode = PickingMode.Ignore };
            thumb.style.position = Position.Absolute; thumb.style.top = -3; thumb.style.bottom = -3;
            thumb.style.width = 6; thumb.style.marginLeft = -3;
            thumb.style.backgroundColor = new Color(.96f, .93f, .84f);
            Add(fill); Add(thumb);
            SetValueWithoutNotify(value);

            RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                this.CapturePointer(e.pointerId);
                MoveTo(e.localPosition.x);
                e.StopPropagation();
            });
            RegisterCallback<PointerMoveEvent>(e =>
            {
                if (this.HasPointerCapture(e.pointerId)) MoveTo(e.localPosition.x);
            });
            RegisterCallback<PointerUpEvent>(e =>
            {
                if (!this.HasPointerCapture(e.pointerId)) return;
                this.ReleasePointer(e.pointerId);
                Committed?.Invoke(Value);
                e.StopPropagation();
            });
        }

        public void SetValueWithoutNotify(float value)
        {
            Value = Mathf.Clamp(value, min, max);
            float percent = max > min ? (Value - min) / (max - min) * 100f : 0f;
            fill.style.width = Length.Percent(percent);
            thumb.style.left = Length.Percent(percent);
        }

        void MoveTo(float x)
        {
            float width = resolvedStyle.width;
            if (width <= 0) return;
            SetValueWithoutNotify(min + Mathf.Clamp01(x / width) * (max - min));
            Changing?.Invoke(Value);
        }
    }
}
