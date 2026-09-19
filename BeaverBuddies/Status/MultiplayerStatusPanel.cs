using System;
using BeaverBuddies.Connect;
using BeaverBuddies.IO;
using HarmonyLib;
using Timberborn.OptionsGame;
using Timberborn.SceneLoading;
using Timberborn.SingletonSystem;
using Timberborn.UILayoutSystem;
using TimberNet;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Status
{
    public sealed class MultiplayerStatusPanel : RegisteredSingleton, IPostLoadableSingleton, IUpdatableSingleton, IResettableSingleton
    {
        readonly UILayout layout;
        readonly LoadingScreen loading;
        readonly TickRateMeter rate = new TickRateMeter();
        VisualElement panel, body, peerList;
        Label title, state, connection, latency, simulation, outgoing, incoming, hint;
        Button collapse;
        TimberNetBase network;
        double nextUpdate;
        bool loaded, failed;
        bool host;
        public MultiplayerStatusPanel(UILayout layout, LoadingScreen loading) { this.layout = layout; this.loading = loading; }
        public void PostLoad()
        {
            try
            {
                Build(); layout.AddTopRight(panel, 100);
                loaded = true; loading.LoadingScreenEnabled += OnLoading;
                ApplyVisibility();
            }
            catch (Exception error) { Fail(error); }
        }
        void OnLoading(object sender, EventArgs args) => Reset();
        public void Reset()
        {
            loaded = false;
            loading.LoadingScreenEnabled -= OnLoading;
            panel?.RemoveFromHierarchy(); panel = null; network = null;
        }
        public void Show()
        {
            Settings.SetStatusPanelVisible(true);
            ApplyVisibility(); nextUpdate = 0;
        }
        void ApplyVisibility()
        {
            if (panel == null) return;
            panel.style.display = Settings.StatusPanelEnabled ? DisplayStyle.Flex : DisplayStyle.None;
            body.style.display = Settings.StatusPanelCollapsed ? DisplayStyle.None : DisplayStyle.Flex;
            state.style.display = Settings.StatusPanelCollapsed ? DisplayStyle.None : DisplayStyle.Flex;
            collapse.text = Settings.StatusPanelCollapsed ? "+" : "−";
            collapse.tooltip = Settings.StatusPanelCollapsed ? "Expand connection status" : "Collapse to the status header";
        }
        public void UpdateSingleton()
        {
            if (!loaded || failed) return;
            try
            {
                var current = ReplayService.Network;
                if (current != null) { network = current; host = current is TimberServer; }
                var replay = SingletonManager.GetSingleton<ReplayService>();
                double now = ConnectionTelemetry.Now;
                bool running = ReplayService.IsLoaded && !ReplayService.HasReplayFailure && replay?.TargetSpeed > 0;
                rate.Observe(replay?.TicksSinceLoad ?? 0, running, now);
                if (now < nextUpdate) return;
                nextUpdate = now + .25; // UI and published measurements update at most four times a second.
                current?.PublishSimulationStatus(new SimulationStatus(replay?.TicksSinceLoad ?? 0, rate.Rate ?? -1, ReplayService.IsLoaded, !running));
                ApplyVisibility();
                if (!Settings.StatusPanelEnabled) return;
                var peers = current?.GetPeerStatus() ?? new System.Collections.Generic.List<PeerStatus>();
                var facts = new StatusFacts {
                    Host = host, Connected = current != null && current.Started && !current.IsStopped,
                    ConnectedPeers = current is TimberServer server ? server.ClientCount : peers.Count,
                    Loaded = ReplayService.IsLoaded,
                    Paused = replay?.TargetSpeed == 0, Failed = ReplayService.HasReplayFailure,
                    Transport = current is TimberClient guest ? guest.TransportName : "Host",
                    Tick = replay?.TicksSinceLoad ?? 0, Rate = rate.Rate,
                    BufferedTicks = current?.TicksBehind ?? 0, ReceivedEvents = current?.BufferedEventCount ?? 0,
                    QueuedBytes = current?.PendingReliableBytes ?? 0, QueuedMessages = current?.PendingReliableMessages ?? 0, Peers = peers
                };
                Render(StatusText.From(facts));
            }
            catch (Exception error) { Fail(error); }
        }
        void Fail(Exception error)
        {
            failed = true; Reset();
            Plugin.LogWarning("Multiplayer status panel disabled for this scene: " + error.Message);
        }
        void Build()
        {
            panel = new VisualElement { name = "BeaverBuddiesStatus", pickingMode = PickingMode.Position };
            panel.style.width = 304; panel.style.maxWidth = new Length(100, LengthUnit.Percent);
            panel.style.flexShrink = 0;
            panel.style.marginTop = 8; panel.style.paddingLeft = 12; panel.style.paddingRight = 12;
            panel.style.paddingTop = 8; panel.style.paddingBottom = 8;
            panel.style.backgroundColor = new Color(.055f, .085f, .095f, .96f);
            panel.style.borderTopLeftRadius = panel.style.borderTopRightRadius = panel.style.borderBottomLeftRadius = panel.style.borderBottomRightRadius = 8;
            panel.style.borderLeftWidth = 3; panel.style.borderLeftColor = new Color(.35f, .78f, .68f);
            panel.style.color = new Color(.91f, .95f, .95f); panel.style.fontSize = 13;
            panel.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            panel.RegisterCallback<WheelEvent>(e => e.StopPropagation());
            var header = new VisualElement(); header.style.flexDirection = FlexDirection.Row; header.style.alignItems = Align.Center;
            header.style.minHeight = 25; header.style.flexShrink = 0;
            title = Text("CO-OP"); title.style.unityFontStyleAndWeight = FontStyle.Bold; title.style.fontSize = 11;
            title.style.color = new Color(.5f, .85f, .75f); title.style.flexGrow = 1; header.Add(title);
            collapse = SmallButton("−", () => { Settings.SetStatusPanelCollapsed(!Settings.StatusPanelCollapsed); ApplyVisibility(); nextUpdate = 0; });
            header.Add(collapse);
            var hide = SmallButton("×", () => { Settings.SetStatusPanelVisible(false); ApplyVisibility(); });
            hide.tooltip = "Hide. Restore with Multiplayer status in the pause menu."; header.Add(hide); panel.Add(header);
            state = Text("Connecting…"); state.style.unityFontStyleAndWeight = FontStyle.Bold;
            state.style.marginTop = 2; panel.Add(state);
            body = new VisualElement(); body.style.marginTop = 8; body.style.flexShrink = 0; panel.Add(body);
            connection = Row("Session", "Your role and the number of connected guests.");
            latency = Row("Response", "Application round-trip time, including send queues and peer processing. Not a pure network ping.");
            simulation = Row("Simulation", "Local simulation ticks per wall-clock second, averaged over two seconds. Not rendered FPS.");
            outgoing = Row("Send queue", "Reliable event count and estimated uncompressed UTF-16 payload memory, including an in-flight frame. Not wire bytes; excludes disposable activity/status traffic.");
            incoming = Row("Received", "Events already received and, on a guest, host ticks waiting in its replay buffer. A sustained buffer suggests simulation catch-up work.");
            var scroll = new ScrollView(ScrollViewMode.Vertical); scroll.style.maxHeight = 144;
            peerList = new VisualElement(); peerList.style.marginTop = 6; scroll.Add(peerList); body.Add(scroll);
            hint = Text(""); hint.style.whiteSpace = WhiteSpace.Normal; hint.style.fontSize = 11;
            hint.style.color = new Color(.65f, .74f, .77f); hint.style.marginTop = 8; body.Add(hint);
        }
        static Label Text(string value)
        {
            var label = new Label(value) { enableRichText = false, pickingMode = PickingMode.Ignore };
            // Explicit metrics keep inherited game label styles from collapsing our rows.
            label.style.position = Position.Relative;
            label.style.height = StyleKeyword.Auto;
            label.style.minHeight = 20;
            label.style.minWidth = 0;
            label.style.flexShrink = 0;
            label.style.marginTop = label.style.marginBottom = 0;
            label.style.marginLeft = label.style.marginRight = 0;
            label.style.paddingTop = label.style.paddingBottom = 0;
            label.style.paddingLeft = label.style.paddingRight = 0;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }
        static Button SmallButton(string text, Action action)
        {
            var button = new Button(action) { text = text, focusable = false };
            button.style.width = 25; button.style.height = 23; button.style.marginLeft = 4;
            button.style.minWidth = 25; button.style.minHeight = 23; button.style.flexShrink = 0;
            button.style.paddingLeft = button.style.paddingRight = 0;
            button.style.fontSize = 16; button.style.color = new Color(.85f, .94f, .93f);
            button.style.backgroundColor = new Color(.13f, .22f, .24f); return button;
        }
        Label Row(string name, string tooltip)
        {
            var row = new VisualElement { tooltip = tooltip }; row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 3; row.style.minHeight = 22; row.style.flexShrink = 0;
            row.style.alignItems = Align.Center;
            var label = Text(name); label.style.width = 78; label.style.color = new Color(.65f, .74f, .77f); row.Add(label);
            var value = Text("—"); value.style.flexGrow = 1; value.style.unityTextAlign = TextAnchor.MiddleRight; value.style.fontSize = 12;
            value.style.flexShrink = 1;
            row.Add(value); body.Add(row); return value;
        }
        void Render(StatusText text)
        {
            title.text = Settings.StatusPanelCollapsed ? "CO-OP · " + text.State : host ? "CO-OP · HOST" : "CO-OP · GUEST";
            state.text = text.State; state.style.color = text.Warning ? new Color(1, .76f, .4f) : new Color(.6f, .88f, .76f);
            if (Settings.StatusPanelCollapsed) return;
            connection.text = text.Connection; latency.text = text.Latency; simulation.text = text.Simulation;
            outgoing.text = text.Outgoing; incoming.text = text.Incoming; hint.text = text.Hint;
            while (peerList.childCount > text.Peers.Length) peerList.RemoveAt(peerList.childCount - 1);
            while (peerList.childCount < text.Peers.Length)
            {
                var row = Text(""); row.style.fontSize = 11; row.style.whiteSpace = WhiteSpace.Normal;
                row.style.marginTop = 5; row.style.paddingTop = 5; row.style.borderTopWidth = 1;
                row.style.borderTopColor = new Color(.19f, .28f, .3f); peerList.Add(row);
            }
            for (int i = 0; i < text.Peers.Length; i++) ((Label)peerList[i]).text = text.Peers[i];
        }
    }
    [HarmonyPatch(typeof(GameOptionsBox), nameof(GameOptionsBox.GetPanel))]
    internal static class StatusPanelMenu
    {
        static void Postfix(GameOptionsBox __instance, VisualElement __result)
        {
            var service = SingletonManager.GetSingleton<MultiplayerStatusPanel>();
            if (service == null) return;
            ButtonInserter.DuplicateOrGetButton(__result, "LoadGameButton", "MultiplayerStatusButton", button => {
                button.text = "Multiplayer status";
                button.tooltip = "Show the connection and simulation status panel.";
                button.clicked += () => { service.Show(); __instance.OnUICancelled(); };
            });
        }
    }
}
