using System;
using System.Collections.Generic;
using System.Globalization;
using BeaverBuddies.Activity;
using BeaverBuddies.IO;
using Timberborn.InputSystem;
using Timberborn.Localization;
using Timberborn.SingletonSystem;
using Timberborn.TimeSystem;
using Timberborn.UILayoutSystem;
using TimberNet;
using UnityEngine;

namespace BeaverBuddies.Panel
{
    /// <summary>
    /// Shows connected players, ping, tick rate and sync state in a small HUD panel during multiplayer.
    /// It only reads: it never sends a gameplay event, never touches the simulation, and any failure
    /// disables just the panel.
    /// </summary>
    public sealed class ConnectionPanelService : RegisteredSingleton, IPostLoadableSingleton, IUpdatableSingleton, IInputProcessor, IResettableSingleton
    {
        public const string ToggleKeyBindingId = "BeaverBuddies.KeyBind.TogglePanel";
        // Later than the game's own panels, so this one sits after them in its corner.
        const int LayoutOrder = 1000;
        const float RefreshSeconds = .5f, TickSampleSeconds = .25f;
        // A guest is briefly out of events between every tick; only a longer wait means it is waiting on the host.
        const float WaitingDebounceSeconds = 1.5f;

        readonly UILayout layout;
        readonly InputService input;
        readonly ILoc loc;
        readonly SpeedManager speed;
        readonly TickRateMeter tickMeter = new TickRateMeter();
        ConnectionPanelView view;
        bool loaded, failed;
        PanelCorner placedIn = (PanelCorner)(-1);
        PanelDisplayMode lastVisibleMode = PanelDisplayMode.Expanded;
        float nextRefresh, nextTickSample, waitingSince = -1;
        double? tickRate;

        public ConnectionPanelService(UILayout layout, InputService input, ILoc loc, SpeedManager speed)
        {
            this.layout = layout; this.input = input; this.loc = loc; this.speed = speed;
        }

        public void PostLoad()
        {
            try
            {
                view = new ConnectionPanelView(loc);
                view.HeaderClicked += OnHeaderClicked;
                view.SetVisible(false);
                input.AddInputProcessor(this);
                loaded = true;
            }
            catch (Exception error) { Disable("could not be created", error); }
        }

        public void Reset()
        {
            loaded = false;
            try { view?.Root.RemoveFromHierarchy(); } catch (Exception) { }
            try { input.RemoveInputProcessor(this); } catch (Exception) { }
            placedIn = (PanelCorner)(-1);
        }

        void Disable(string what, Exception error)
        {
            failed = true;
            Plugin.LogWarning($"The connection panel {what} and is disabled for this scene: {error.Message}");
            Reset();
        }

        // ---- input: an optional key to show or hide the panel ----

        public bool ProcessInput()
        {
            if (!loaded || failed || !input.IsKeyDown(ToggleKeyBindingId)) return false;
            var mode = Settings.ConnectionPanelDisplayMode;
            if (mode == PanelDisplayMode.Hidden) Settings.SetConnectionPanelDisplayMode(lastVisibleMode);
            else { lastVisibleMode = mode; Settings.SetConnectionPanelDisplayMode(PanelDisplayMode.Hidden); }
            nextRefresh = 0;
            // Never swallow the key press for anyone else.
            return false;
        }

        void OnHeaderClicked()
        {
            var mode = Settings.ConnectionPanelDisplayMode;
            Settings.SetConnectionPanelDisplayMode(mode == PanelDisplayMode.Expanded ? PanelDisplayMode.Collapsed : PanelDisplayMode.Expanded);
            nextRefresh = 0;
        }

        // ---- per frame ----

        public void UpdateSingleton()
        {
            if (!loaded || failed) return;
            try { Tick(); }
            catch (Exception error) { Disable("stopped working", error); }
        }

        void Tick()
        {
            var mode = Settings.ConnectionPanelDisplayMode;
            var net = CurrentNetwork();
            if (mode == PanelDisplayMode.Hidden || net == null)
            {
                view.SetVisible(false);
                tickMeter.Reset(); tickRate = null; waitingSince = -1;
                return;
            }
            PlaceIfNeeded();

            float now = Time.unscaledTime;
            var replay = SingletonManager.GetSingleton<ReplayService>();
            if (now >= nextTickSample && replay != null)
            {
                nextTickSample = now + TickSampleSeconds;
                tickRate = tickMeter.Sample(replay.TicksSinceLoad, now);
            }
            if (now < nextRefresh) return;
            nextRefresh = now + RefreshSeconds;

            var model = PanelModelBuilder.Build(Collect(net, replay, now), Translate);
            view.Show(model, mode == PanelDisplayMode.Expanded);
            view.SetVisible(true);
        }

        void PlaceIfNeeded()
        {
            var corner = Settings.ConnectionPanelCornerValue;
            if (corner == placedIn) return;
            view.Root.RemoveFromHierarchy();
            switch (corner)
            {
                case PanelCorner.TopRight: layout.AddTopRight(view.Root, LayoutOrder); break;
                case PanelCorner.BottomLeft: layout.AddBottomLeft(view.Root, LayoutOrder); break;
                case PanelCorner.BottomRight: layout.AddBottomRight(view.Root, LayoutOrder); break;
                default: layout.AddTopLeft(view.Root, LayoutOrder); break;
            }
            view.SetAlignment(corner == PanelCorner.TopRight || corner == PanelCorner.BottomRight);
            placedIn = corner;
        }

        static TimberNetBase CurrentNetwork() => EventIO.Get() is ServerEventIO host ? host.NetBase :
            EventIO.Get() is ClientEventIO guest ? guest.NetBase : null;

        PanelInputs Collect(TimberNetBase net, ReplayService replay, float now)
        {
            var io = EventIO.Get();
            NetworkStatus status = net.GetNetworkStatus();

            // A guest is only "waiting" if it has been out of events for a while, not between ticks.
            bool outOfEvents = !status.IsHost && io != null && io.IsOutOfEvents;
            if (!outOfEvents) waitingSince = -1;
            else if (waitingSince < 0) waitingSince = now;

            var result = new PanelInputs
            {
                IsHost = status.IsHost,
                Stopped = status.IsStopped,
                Desynced = replay?.IsDesynced == true || ReplayService.HasReplayFailure,
                WaitingForHost = waitingSince >= 0 && now - waitingSince >= WaitingDebounceSeconds,
                TicksBehind = io?.TicksBehind ?? 0,
                HostSilenceSeconds = status.HostSilenceSeconds,
                TickRate = tickRate,
                Speed = speed.CurrentSpeed,
                HostPacingPercent = replay?.HostPacingPercent ?? 100,
            };

            // Names come from player activity (the same names other players chose for pings and cursors).
            var names = new Dictionary<int, string>();
            var activity = SingletonManager.GetSingleton<PlayerActivityService>();
            if (activity != null) foreach (var player in activity.RemotePlayers) names[player.PlayerId] = player.Name;
            string me = Settings.PingDisplayName;

            if (status.IsHost)
            {
                result.Players.Add(new PanelPlayer { Id = 0, Name = me, IsYou = true, IsHost = true });
                foreach (var peer in status.Peers) result.Players.Add(FromPeer(peer, NameOf(peer.PlayerId, names), false));
            }
            else
            {
                result.Players.Add(new PanelPlayer { Id = 0, Name = NameOf(0, names), IsHost = true });
                bool foundYou = false;
                foreach (var peer in status.Peers)
                {
                    bool isYou = peer.PlayerId == status.YourPlayerId;
                    foundYou |= isYou;
                    result.Players.Add(FromPeer(peer, isYou ? me : NameOf(peer.PlayerId, names), isYou));
                }
                // Before the host's first update arrives we still know we are here.
                if (!foundYou) result.Players.Add(new PanelPlayer { Id = status.YourPlayerId, Name = me, IsYou = true });
            }
            return result;
        }

        static PanelPlayer FromPeer(PeerStatus peer, string name, bool isYou) => new PanelPlayer
        {
            Id = peer.PlayerId, Name = name, IsYou = isYou,
            RttMs = peer.RttMs, SilenceSeconds = peer.SilenceSeconds, Transport = peer.Transport,
            TicksBehind = peer.TicksBehind,
        };

        string NameOf(int id, Dictionary<int, string> names)
        {
            if (names.TryGetValue(id, out string name)) return name;
            return id == 0 ? Translate("BeaverBuddies.Panel.Host", Array.Empty<object>())
                : Translate("BeaverBuddies.Panel.PlayerNumber", new object[] { id });
        }

        // ILoc translates a key; the placeholders are filled in here, in a fixed culture.
        string Translate(string key, object[] args)
        {
            string text = loc.T(key);
            return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
        }
    }
}
