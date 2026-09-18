using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BeaverBuddies.DesyncDetecter;
using BeaverBuddies.Events;
using BeaverBuddies.IO;
using Newtonsoft.Json.Linq;
using Timberborn.CoreUI;
using Timberborn.GameSaveRepositorySystem;
using Timberborn.GameSceneLoading;
using Timberborn.SingletonSystem;
using TimberNet;

namespace BeaverBuddies.Connect
{
    // Scene-independent recovery state; every transition is made on the Unity update thread.
    // A fresh transport session deliberately discards all commands from the divergent world.
    public sealed class SnapshotResyncService : IUpdatableSingleton
    {
        enum Phase { FinishingTick, Saving, Draining, HostLoading, WaitingForHost, Connecting, ClientLoading, Failed }
        sealed class Recovery
        {
            public string Id, Digest;
            public Phase Phase;
            public EventIO Owner;
            public double Deadline;
            public int Expected;
            public float Speed;
            public readonly HashSet<ISocketStream> Ready = new HashSet<ISocketStream>();
            public bool SaveStarted, Initialized, ReadySent;
            public Task Drain;
            public SaveReference Save;
            public byte[] Bytes;
        }
        static Recovery recovery;
        static SnapshotResyncService current;
        static double lastRecovery = double.NegativeInfinity;
        static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        public static bool Active => recovery != null;
        public static bool FreezingOldSession => recovery != null &&
            recovery.Phase != Phase.HostLoading && recovery.Phase != Phase.ClientLoading;
        public static bool IsReconnecting => recovery?.Phase == Phase.Connecting;
        readonly RehostingService rehosting;
        readonly GameSceneLoader loader;
        readonly GameSaveRepository repository;
        readonly DialogBoxShower dialogs;
        readonly ClientConnectionService connection;

        public SnapshotResyncService(RehostingService rehosting, GameSceneLoader loader,
            GameSaveRepository repository, DialogBoxShower dialogs, ClientConnectionService connection)
        {
            this.rehosting = rehosting; this.loader = loader; this.repository = repository;
            this.dialogs = dialogs; this.connection = connection; current = this;
            SnapshotResyncStatus.Forget();
        }

        public static void Reset() { recovery = null; current = null; SnapshotResyncStatus.Forget(); lastRecovery = double.NegativeInfinity; }
        static string Digest(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes));
        }
        static ReplayService Replay => SingletonManager.GetSingleton<ReplayService>();
        static JObject Message(string command, string id)
        {
            var message = TimberNetBase.Control(command); message["id"] = id; return message;
        }

        public static bool TryRecover(ReplayService replay)
        {
            if (current == null || ReplayService.HasReplayFailure) return false;
            if (Active) { current.Fail("The freshly loaded snapshot diverged before recovery completed."); return true; }
            WaterDiagnostics.WriteOnDesync();
            if (EventIO.Get() is ServerEventIO server)
            {
                current.BeginHost(server, replay);
                return true;
            }
            if (EventIO.Get() is ClientEventIO client && client.NetBase != null)
            {
                recovery = new Recovery { Owner = client, Phase = Phase.WaitingForHost, Deadline = Now + 300 };
                replay.FreezeForSnapshot();
                client.NetBase.SendControl(Message("ResyncRequest", ""));
                Plugin.LogWarning("Desync detected: requesting a shared host snapshot.");
                return true;
            }
            return false;
        }

        void BeginHost(ServerEventIO server, ReplayService replay)
        {
            if (Active || replay == null || ReplayService.HasReplayFailure) return;
            RollingDiagnosticsService.Trigger("host snapshot recovery requested");
            recovery = new Recovery { Id = GuidPatcher.RealNewGuid().ToString("N"), Owner = server,
                Phase = Phase.FinishingTick, Deadline = Now + 30,
                Expected = server.NetBase.ClientCount, Speed = replay.TargetSpeed };
            server.NetBase.StopAcceptingClients("The host is preparing a recovery snapshot.");
            server.NetBase.SendControl(Message("ResyncPrepare", recovery.Id));
            string disabledReason = server.HasSteamClients ? "Automatic snapshot recovery currently supports direct-IP connections. This session includes a Steam invite connection; rehost manually to recreate its lobby."
                : !Settings.SnapshotResyncEnabled ? "Automatic snapshot recovery is disabled by the host."
                : Now - lastRecovery < 120 ? "Another desync occurred within two minutes of recovery. Automatic reloads have stopped to avoid a reload loop." : null;
            if (disabledReason == null) lastRecovery = Now;
            WaterDiagnostics.WriteOnDesync();
            var expected = recovery;
            Plugin.LogWarning("Recovering multiplayer: completing host tick, then saving a shared snapshot.");
            replay.FinishTickForSnapshot(() =>
            {
                if (recovery != expected || ReplayService.HasReplayFailure) return;
                replay.FreezeForSnapshot();
                if (disabledReason != null) { Fail(disabledReason); return; }
                expected.Phase = Phase.Saving; expected.Deadline = Now + 600;
            });
        }

        public static void Receive(EventIO owner, ISocketStream peer, JObject message)
        {
            if (current == null || owner != EventIO.Get() || ReplayService.HasReplayFailure) return;
            string command = (string)message["command"], id = (string)message["id"];
            if (owner is ServerEventIO host)
            {
                if (command == "ResyncRequest") { if (!Active) current.BeginHost(host, Replay); }
                else if (command == "ResyncCancel" && Active && id == recovery.Id)
                    current.Fail("A player stopped snapshot recovery or could not load the snapshot.");
                else if (command == "ResyncReady" && recovery?.Phase == Phase.HostLoading &&
                         recovery.Owner == owner && id == recovery.Id) recovery.Ready.Add(peer);
                return;
            }
            if (!(owner is ClientEventIO client)) return;
            if (command == "ResyncPrepare" && (!Active || recovery.Phase == Phase.WaitingForHost))
            {
                recovery = new Recovery { Id = id, Owner = owner, Phase = Phase.WaitingForHost, Deadline = Now + 300 };
                Replay?.FreezeForSnapshot();
            }
            else if (command == "ResyncReload" && recovery?.Phase == Phase.WaitingForHost && id == recovery.Id)
            {
                recovery.Digest = (string)message["snapshotSha256"];
                recovery.Phase = Phase.Connecting; recovery.Deadline = Now + 600;
                client.Close();
                current.connection.BeginSnapshotReconnect();
            }
            else if (command == "ResyncComplete" && recovery?.Phase == Phase.ClientLoading &&
                     id == recovery.Id && recovery.ReadySent)
            {
                SnapshotResyncStatus.Hide();
                recovery = null;
                Plugin.LogWarning("Shared snapshot loaded; multiplayer recovery completed.");
            }
            else if (command == "ResyncFailed" && Active && id == recovery.Id)
                current.Fail("The host could not complete snapshot recovery. Rehost manually or reload a known-good save.", false);
        }

        public static void ConnectionFailed(string error)
        {
            if (Active && !IsReconnecting)
                current?.Fail("A recovery connection closed: " + error);
        }

        public static bool MapLoading(ClientEventIO client, byte[] bytes)
        {
            if (!IsReconnecting) return true;
            if (string.IsNullOrEmpty(recovery.Digest) || recovery.Digest != Digest(bytes))
            {
                current.Fail("The received snapshot does not match the host's snapshot. The old world has not been replaced.");
                return false;
            }
            SnapshotResyncStatus.Hide();
            recovery.Owner = client; recovery.Phase = Phase.ClientLoading;
            recovery.Initialized = false; recovery.ReadySent = false;
            return true;
        }

        // A loaded scene alone is insufficient: wait until its initial network events have replayed.
        public static void InitializedClient()
        {
            if (recovery?.Phase == Phase.ClientLoading) recovery.Initialized = true;
        }

        public void UpdateSingleton()
        {
            if (recovery == null) return;
            if (ReplayService.HasReplayFailure) { SnapshotResyncStatus.Hide(); recovery = null; return; }
            var state = recovery;
            // Frozen ReplayService no longer pumps IO. Recovery controls still must be delivered.
            EventIO.Get()?.Update();
            if (recovery != state || state.Phase == Phase.Failed) return;
            if (Now > state.Deadline) { Fail("Snapshot recovery timed out waiting for the host or another player."); return; }
            if (ReplayService.IsLoaded && state.Phase != Phase.FinishingTick)
                SnapshotResyncStatus.Show(dialogs, Status(state), () =>
                {
                    if (recovery == state) Fail("Snapshot recovery was stopped by this player.");
                });
            try
            {
                if (state.Phase == Phase.Saving && !state.SaveStarted)
                {
                    state.SaveStarted = true;
                    if (!rehosting.SaveRehostFile(save =>
                    {
                        if (recovery != state || ReplayService.HasReplayFailure) return;
                        try
                        {
                            state.Save = save;
                            state.Bytes = ServerHostingUtils.GetMapBtyes(repository, save);
                            var host = (ServerEventIO)state.Owner;
                            state.Digest = Digest(state.Bytes);
                            var reload = Message("ResyncReload", state.Id);
                            reload["snapshotSha256"] = state.Digest;
                            host.NetBase.SendControl(reload);
                            state.Drain = host.NetBase.FlushAsync();
                            state.Phase = Phase.Draining; state.Deadline = Now + 15;
                        }
                        catch (Exception error) { Fail("Could not read the recovery snapshot: " + error.Message); }
                    }, true)) Fail("Could not save the host recovery snapshot.");
                }
                else if (state.Phase == Phase.Draining && state.Drain.IsCompleted)
                {
                    if (state.Drain.IsCanceled || state.Drain.IsFaulted) { Fail("A player could not receive the recovery notice."); return; }
                    SnapshotResyncStatus.Hide();
                    var host = new ServerEventIO();
                    EventIO.Set(host); // closes old connections before listening on the same port
                    state.Owner = host; state.Phase = Phase.HostLoading;
                    state.Deadline = Now + 600;
                    host.Start(state.Bytes);
                    if (host.NetBase == null || host.NetBase.IsStopped) throw new InvalidOperationException("Could not restart hosting.");
                    SingletonManager.Reset();
                    DeterminismService.InitGameStartState(state.Bytes);
                    loader.StartSaveGame(state.Save);
                    state.Bytes = null;
                }
                else if (state.Phase == Phase.ClientLoading && ReplayService.IsLoaded && state.Initialized && !state.ReadySent)
                {
                    ((ClientEventIO)state.Owner).NetBase.SendControl(Message("ResyncReady", state.Id));
                    state.ReadySent = true;
                }
                else if (state.Phase == Phase.HostLoading && ReplayService.IsLoaded &&
                         state.Ready.Count >= state.Expected && AllReadyConnected(state))
                {
                    var host = (ServerEventIO)state.Owner;
                    host.NetBase.StopAcceptingClients("Snapshot recovery is complete. Ask the host to rehost before joining.");
                    host.NetBase.SendControl(Message("ResyncComplete", state.Id));
                    SnapshotResyncStatus.Hide();
                    recovery = null;
                    lastRecovery = Now;
                    Replay.RecordEvent(new SpeedSetEvent { speed = state.Speed });
                    Plugin.LogWarning("All players loaded the host snapshot. Resuming multiplayer.");
                }
            }
            catch (Exception error) { Fail("Snapshot recovery failed: " + error.Message); }
        }

        static string Status(Recovery state)
        {
            switch (state.Phase)
            {
                case Phase.Saving: return "Recovering multiplayer: saving the host snapshot...";
                case Phase.Draining: return "Recovering multiplayer: notifying players to reload...";
                case Phase.HostLoading: return $"Recovering multiplayer: waiting for players to finish loading ({state.Ready.Count}/{state.Expected})...";
                case Phase.ClientLoading: return "Recovering multiplayer: snapshot loaded; waiting for everyone to be ready...";
                case Phase.Connecting: return "Recovering multiplayer: reconnecting to download the host snapshot...";
                default: return "Recovering multiplayer: waiting for the host snapshot...";
            }
        }

        static bool AllReadyConnected(Recovery state)
        {
            foreach (var peer in state.Ready) if (!peer.Connected) return false;
            return ((ServerEventIO)state.Owner).NetBase.ClientCount == state.Expected;
        }

        void Fail(string reason, bool notify = true)
        {
            if (recovery?.Phase == Phase.Failed) return;
            SnapshotResyncStatus.Hide();
            if (notify && EventIO.Get() is ServerEventIO host)
                host.NetBase?.SendControl(Message("ResyncFailed", recovery?.Id));
            if (notify && EventIO.Get() is ClientEventIO client)
                client.NetBase?.SendControl(Message("ResyncCancel", recovery?.Id));
            if (recovery != null) recovery.Phase = Phase.Failed;
            if (EventIO.Get() is ServerEventIO && Replay != null && !ReplayService.HasReplayFailure)
                Replay.FinishTickForSnapshot(() => Replay?.FreezeForSnapshot());
            else Replay?.FreezeForSnapshot();
            Plugin.LogError(reason);
            dialogs.Create().SetMessage(reason + "\n\nThe game remains paused. You can rehost manually, or return to the main menu and load a known-good save.")
                .SetConfirmButton(() =>
                {
                    bool isHost = EventIO.Get() is ServerEventIO;
                    if (isHost && Replay != null)
                        Replay.FinishTickForSnapshot(() => { recovery = null; rehosting.RehostGame(); });
                    else { recovery = null; connection.ConnectOrShowFailureMessage(); }
                }, EventIO.Get() is ServerEventIO ? "Rehost manually" : "Reconnect")
                .SetDefaultCancelButton().Show();
        }
    }
}
