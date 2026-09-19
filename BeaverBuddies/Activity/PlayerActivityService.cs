using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeaverBuddies.IO;
using Timberborn.Buildings;
using Timberborn.CameraSystem;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using Timberborn.InputSystem;
using Timberborn.SceneLoading;
using Timberborn.SelectionSystem;
using Timberborn.SingletonSystem;
using Timberborn.TerrainQueryingSystem;
using TimberNet;
using UnityEngine;

namespace BeaverBuddies.Activity
{
    public sealed class RemoteActivity
    {
        public PlayerActivity State;
        public float LastSeen;
        public Vector3 CursorFrom, CursorTo;
        public float CursorChanged;
        /// <summary>The color the other player chose for themselves.</summary>
        public Color AdvertisedColor;
        public string Label;
        /// <summary>Key into the saved per-player styles; see <see cref="PlayerCursorPreferences.KeyFor"/>.</summary>
        public string StyleKey;
        public EntityComponent Selected, Editing;
        public Color HighlightColor;
        public readonly Highlighter Highlighter = new Highlighter();

        string cachedHex;
        Color cachedColor;

        public int PlayerId => State.PlayerId;
        public string Name => State.Name;

        public Vector3 CursorPosition(float now) => Vector3.Lerp(CursorFrom, CursorTo, Mathf.Clamp01((now - CursorChanged) / .1f));

        /// <summary>The style's color override if there is one, otherwise the player's own color.</summary>
        public Color ResolveColor(PlayerCursorStyle style)
        {
            if (style.ColorHex == null) return AdvertisedColor;
            if (!string.Equals(style.ColorHex, cachedHex, StringComparison.Ordinal))
            {
                cachedHex = style.ColorHex;
                cachedColor = ColorUtility.TryParseHtmlString("#" + style.ColorHex, out var parsed) ? parsed : AdvertisedColor;
            }
            return cachedColor;
        }
    }

    /// <summary>What the settings panel needs to know about a connected player.</summary>
    public readonly struct PlayerCursorEntry
    {
        public readonly int PlayerId;
        public readonly string Name, Label, StyleKey;
        public readonly Color AdvertisedColor;
        public PlayerCursorEntry(RemoteActivity player)
        {
            PlayerId = player.PlayerId; Name = player.Name; Label = player.Label;
            StyleKey = player.StyleKey; AdvertisedColor = player.AdvertisedColor;
        }
    }

    // This service reads presentation state only. It never selects an entity, mutates a building,
    // calls simulation RNG, or records replay events.
    public sealed class PlayerActivityService : RegisteredSingleton, IPostLoadableSingleton, IUpdatableSingleton, IResettableSingleton
    {
        public const string PreferencesFileName = "BeaverBuddiesCursorStyles.json";

        static PlayerCursorPreferences preferences;
        /// <summary>Saved per-player cursor styles, shared by the overlay and the settings panel.</summary>
        public static PlayerCursorPreferences Preferences =>
            preferences ??= new PlayerCursorPreferences(Path.Combine(Application.persistentDataPath, PreferencesFileName));

        readonly InputService input;
        readonly CameraService camera;
        readonly TerrainPicker terrain;
        readonly SelectableObjectRaycaster raycaster;
        readonly EntitySelectionService selection;
        readonly EntityRegistry entities;
        readonly LoadingScreen loading;
        readonly Dictionary<int, RemoteActivity> remote = new Dictionary<int, RemoteActivity>();
        readonly List<int> expired = new List<int>();
        TimberNetBase net;
        GameObject overlayHost;
        bool loaded, suspended, failed, keysDirty;
        float nextSend, editingUntil;
        string editingId = "";

        public IEnumerable<RemoteActivity> RemotePlayers => remote.Values;

        /// <summary>Incremented whenever the set of connected players or their style keys change.</summary>
        public int PlayersVersion { get; private set; }

        public PlayerActivityService(InputService input, CameraService camera, TerrainPicker terrain,
            SelectableObjectRaycaster raycaster, EntitySelectionService selection, EntityRegistry entities, LoadingScreen loading)
        {
            this.input = input; this.camera = camera; this.terrain = terrain; this.raycaster = raycaster;
            this.selection = selection; this.entities = entities; this.loading = loading;
        }

        public void PostLoad()
        {
            loaded = true;
            overlayHost = new GameObject("BeaverBuddies_PlayerActivity");
            var overlay = overlayHost.AddComponent<PlayerActivityOverlay>();
            overlay.Service = this; overlay.CameraService = camera;
            loading.LoadingScreenEnabled += OnLoading;
        }
        void OnLoading(object sender, EventArgs args) => Reset();

        public void Reset()
        {
            loaded = false;
            loading.LoadingScreenEnabled -= OnLoading;
            net?.ClearActivity();
            net = null;
            ClearRemote(); editingId = "";
            if (overlayHost != null) UnityEngine.Object.Destroy(overlayHost);
            overlayHost = null;
        }

        void ClearRemote()
        {
            foreach (var player in remote.Values)
            {
                try { player.Highlighter.UnhighlightAllSecondary(); }
                catch (Exception error) { Plugin.LogWarning("Could not clear a remote highlight: " + error.Message); }
            }
            if (remote.Count > 0) { PlayersVersion++; }
            remote.Clear();
        }

        static TimberNetBase CurrentNetwork() => EventIO.Get() is ServerEventIO host ? host.NetBase :
            EventIO.Get() is ClientEventIO guest ? guest.NetBase : null;

        /// <summary>The style used to draw this player's cursor, selection outline and labels.</summary>
        public PlayerCursorStyle StyleOf(RemoteActivity player) => Preferences.Get(player.StyleKey);

        public Color ColorOf(RemoteActivity player) => player.ResolveColor(StyleOf(player));

        /// <summary>Connected players in a stable order (host first), for the settings panel.</summary>
        public List<PlayerCursorEntry> Players() =>
            remote.Values.OrderBy(p => p.PlayerId).Select(p => new PlayerCursorEntry(p)).ToList();

        public void UpdateSingleton()
        {
            if (!loaded || failed) return;
            try { UpdateActivity(); }
            catch (Exception error)
            {
                // An optional overlay must not break replay or crash the simulation.
                failed = true;
                Reset();
                Plugin.LogWarning("Player activity display disabled for this scene: " + error.Message);
            }
        }

        void UpdateActivity()
        {
            var current = CurrentNetwork();
            if (!ReferenceEquals(current, net))
            {
                net?.ClearActivity();
                ClearRemote(); net = current; editingId = ""; nextSend = 0;
            }
            bool hide = !Settings.PlayerActivityEnabled || !ReplayService.IsLoaded || ReplayService.HasReplayFailure;
            if (net == null || net.IsStopped || hide)
            {
                ClearRemote();
                net?.ClearActivity();
                if (!suspended && net != null && !net.IsStopped) net.SendActivity(HiddenState());
                suspended = true;
                return;
            }
            suspended = false;
            float now = Time.unscaledTime;
            foreach (var state in net.TakeActivity()) Apply(state, now);
            expired.Clear();
            foreach (var pair in remote)
            {
                if (now - pair.Value.LastSeen > PlayerActivity.LifetimeSeconds) expired.Add(pair.Key);
            }
            foreach (int id in expired) { remote[id].Highlighter.UnhighlightAllSecondary(); remote.Remove(id); keysDirty = true; }
            if (keysDirty) RefreshStyleKeys();
            if (now < nextSend) return;
            // No catch-up bursts at low FPS; wall-clock rate is independent of the simulation speed.
            nextSend = now + .1f;
            net.SendActivity(Capture(now));
        }

        PlayerActivity HiddenState() => new PlayerActivity(0, Settings.PingDisplayName,
            ColorUtility.ToHtmlStringRGB(Settings.PingColorValue), false, 0, 0, 0);

        PlayerActivity Capture(float now)
        {
            if (!Application.isFocused) return HiddenState();
            var selected = selection.SelectedObject;
            var entity = selected ? selected.GetComponent<EntityComponent>() : null;
            string selectedId = entity && !entity.Deleted ? entity.EntityId.ToString() : "";
            Vector3 position = Vector3.zero;
            Vector2 mouse = input.MousePosition;
            bool visible = !input.MouseOverUI && mouse.x >= 0 && mouse.y >= 0 && mouse.x < Screen.width && mouse.y < Screen.height;
            if (visible)
            {
                Ray ray = camera.ScreenPointToRayInWorldSpace(mouse);
                if (raycaster.TryHitSelectableObjectIncludeTerrainStump(ray, out _, out var hit)) position = hit.point;
                else
                {
                    var ground = terrain.PickTerrainCoordinates(camera.ScreenPointToRayInGridSpace(mouse));
                    if (ground.HasValue) position = CoordinateSystem.GridToWorld(ground.Value.Intersection);
                    else visible = false;
                }
            }
            if (now >= editingUntil) editingId = "";
            return new PlayerActivity(0, Settings.PingDisplayName, ColorUtility.ToHtmlStringRGB(Settings.PingColorValue),
                visible, position.x, position.y, position.z, selectedId, editingId);
        }

        // Called only for a locally initiated building command, never during event replay/ticks.
        public static void NotifyLocalEdit(string entityId)
        {
            if (DeterminismService.IsTicking || ReplayService.IsReplayingEvents || !Settings.PlayerActivityEnabled) return;
            var service = SingletonManager.GetSingleton<PlayerActivityService>();
            if (service == null || !service.loaded || service.failed) return;
            service.editingId = entityId;
            service.editingUntil = Time.unscaledTime + 3f;
        }

        EntityComponent Resolve(string id)
        {
            if (!Guid.TryParse(id, out var guid)) return null;
            var entity = entities.GetEntity(guid);
            return entity && !entity.Deleted ? entity : null;
        }

        void Apply(PlayerActivity state, float now)
        {
            if (!loaded || failed) return;
            try
            {
                if (!remote.TryGetValue(state.PlayerId, out var player))
                {
                    if (remote.Count >= PlayerActivity.MaxPlayers) return;
                    player = new RemoteActivity(); remote.Add(state.PlayerId, player);
                    keysDirty = true;
                }
                if (player.State != null && player.State.Name != state.Name) keysDirty = true;
                Vector3 target = new Vector3(state.X, state.Y, state.Z);
                player.CursorFrom = player.State?.CursorVisible == true && state.CursorVisible ? player.CursorPosition(now) : target;
                player.CursorTo = target; player.CursorChanged = now; player.LastSeen = now;
                ColorUtility.TryParseHtmlString("#" + state.Color, out var advertised);
                player.State = state;
                player.AdvertisedColor = advertised;
                player.Label = state.Name + (state.PlayerId == 0 ? " (Host)" : " (P" + state.PlayerId + ")");
                if (keysDirty) RefreshStyleKeys();
                Color color = ColorOf(player);
                var selected = Resolve(state.Selection);
                if (player.Selected != selected || player.HighlightColor != color) player.Highlighter.UnhighlightAllSecondary();
                player.Selected = selected; player.Editing = Resolve(state.Editing);
                player.HighlightColor = color;
                // Secondary highlights leave the local player's primary selection/hover color intact.
                if (selected && selected.HasComponent<HighlightableObject>()) player.Highlighter.HighlightSecondary(selected, color);
            }
            catch (Exception error)
            {
                failed = true; Reset();
                Plugin.LogWarning("Player activity display disabled for this scene: " + error.Message);
            }
        }

        // Players are remembered by name; two connected players with the same name are told apart by number.
        void RefreshStyleKeys()
        {
            keysDirty = false;
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var player in remote.Values)
            {
                if (player.State == null) continue;
                counts[player.Name] = counts.TryGetValue(player.Name, out int n) ? n + 1 : 1;
            }
            foreach (var player in remote.Values)
            {
                if (player.State == null) continue;
                player.StyleKey = PlayerCursorPreferences.KeyFor(player.Name, player.PlayerId, counts[player.Name] > 1);
            }
            PlayersVersion++;
        }
    }
}
