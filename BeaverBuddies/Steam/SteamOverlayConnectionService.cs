#if IS_STEAM
using Timberborn.SteamStoreSystem;
#endif
using BeaverBuddies.Connect;
using BeaverBuddies.IO;
using Steamworks;
using System;
using System.Collections.Generic;
using Timberborn.SingletonSystem;
using Timberborn.SteamOverlaySystem;
using Timberborn.CoreUI;

namespace BeaverBuddies.Steam
{
    // Lobby membership survives a world reload, independently of native transport handles.
    public static class SteamGuestLobby
    {
        public static CSteamID Current { get; private set; }
        public static void Set(CSteamID lobby)
        {
            if (Current == lobby) return;
            Leave(); Current = lobby;
        }
        public static void Leave()
        {
            if (Current.m_SteamID != 0) SteamMatchmaking.LeaveLobby(Current);
            Current = default;
        }
        public static void Rejoin()
        {
            // Steam itself may have dropped lobby membership during a network outage.
            // Rejoining the retained lobby is idempotent and needs no new invitation.
            if (Current.m_SteamID != 0) SteamMatchmaking.JoinLobby(Current);
        }
    }

    class SteamOverlayConnectionService : IUpdatableSingleton
    {
        public static bool IsSteamEnabled { get; private set; }
#if IS_STEAM
        readonly SteamManager steam;
        readonly ClientConnectionService connection;
        readonly PanelStack panels;
        readonly SteamOverlayInputBlocker overlay;
        readonly DialogBoxShower dialogs;
        readonly Settings settings;
        static readonly List<IDisposable> callbacks = new List<IDisposable>();
        static CSteamID pendingLobby;
        static bool checkedLaunchInvite;
        bool initialized, showProgress;
        string pendingError;
        double joinDeadline;
        static double Now => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

        public SteamOverlayConnectionService(SteamManager steamManager, ClientConnectionService clientConnectionService,
            SteamOverlayInputBlocker steamOverlayInputBlocker, PanelStack panelStack, EventBus eventBus,
            Settings settings, DialogBoxShower dialogBoxShower)
        {
            steam = steamManager; connection = clientConnectionService; overlay = steamOverlayInputBlocker;
            panels = panelStack; this.settings = settings; dialogs = dialogBoxShower;
        }

        public void UpdateSingleton()
        {
            if (!initialized && steam.Initialized)
            {
                IsSteamEnabled = true; initialized = true;
                foreach (var callback in callbacks) callback.Dispose();
                callbacks.Clear();
                callbacks.Add(Callback<GameLobbyJoinRequested_t>.Create(OnInvite));
                callbacks.Add(Callback<LobbyEnter_t>.Create(OnEntered));
                SteamNetworkingUtils.InitRelayNetworkAccess();
                if (Settings.PingDisplayName == Settings.DefaultPingPlayerName)
                {
                    string name = SteamFriends.GetFriendPersonaName(SteamUser.GetSteamID());
                    if (!string.IsNullOrEmpty(name)) settings.PingPlayerName.SetValue(name);
                }
                if (!checkedLaunchInvite)
                {
                    checkedLaunchInvite = true;
                    var args = Environment.GetCommandLineArgs();
                    for (int i = 0; i + 1 < args.Length; i++)
                        if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong id) && id != 0)
                        { Join(new CSteamID(id)); break; }
                }
            }
            if (pendingLobby.m_SteamID != 0 && joinDeadline != 0 && Now > joinDeadline)
            { pendingLobby = default; joinDeadline = 0; pendingError = "Steam did not respond to the invite. Check Steam is online and ask your friend to invite you again."; }
            if (panels.IsPanelOnTop(overlay)) return;
            if (pendingError != null)
            {
                string error = pendingError; pendingError = null;
                dialogs.Create().SetMessage(error).SetDefaultCancelButton().Show();
            }
            if (showProgress) { showProgress = false; connection.ShowConnectionMessage(true); }
        }

        void OnInvite(GameLobbyJoinRequested_t invite) => Join(invite.m_steamIDLobby);

        void Join(CSteamID lobby)
        {
            if (!EventIO.IsNull)
            {
                pendingError = "You are already in a multiplayer session. Return to the main menu before accepting a different invite.";
                return;
            }
            if (!Settings.EnableSteam)
            { pendingError = "Enable Steam Networking in BeaverBuddies settings, then accept the invite again."; return; }
            if (pendingLobby == lobby) return;
            pendingLobby = lobby; joinDeadline = Now + 30;
            SteamMatchmaking.JoinLobby(lobby);
        }

        void OnEntered(LobbyEnter_t entered)
        {
            var lobby = new CSteamID(entered.m_ulSteamIDLobby);
            // Ignore our own host lobby, duplicate responses, and superseded invite requests.
            if (pendingLobby != lobby)
            {
                if (SteamMatchmaking.GetLobbyOwner(lobby) != SteamUser.GetSteamID() && SteamGuestLobby.Current != lobby)
                    SteamMatchmaking.LeaveLobby(lobby);
                return;
            }
            pendingLobby = default; joinDeadline = 0;
            if (entered.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            { pendingError = "The Steam lobby could not be joined. It may have closed or become full. Ask the host for a fresh invite."; return; }
            if (!EventIO.IsNull) { SteamMatchmaking.LeaveLobby(lobby); return; }
            var owner = SteamMatchmaking.GetLobbyOwner(lobby);
            if (owner.m_SteamID == 0 || SteamMatchmaking.GetLobbyData(lobby, "beaverbuddies_protocol") != SteamListener.Protocol)
            {
                SteamMatchmaking.LeaveLobby(lobby);
                pendingError = "This Steam invite uses a different BeaverBuddies networking version. Install the same Preview 16 or newer build on both computers and restart Timberborn.";
                return;
            }
            SteamGuestLobby.Set(lobby);
            showProgress = connection.TryToConnect(owner);
            if (!showProgress) SteamGuestLobby.Leave();
        }
#else
        public void UpdateSingleton() { }
#endif
    }
}
