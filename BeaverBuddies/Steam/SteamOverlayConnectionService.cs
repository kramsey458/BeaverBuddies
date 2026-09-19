#if IS_STEAM
using Timberborn.SteamStoreSystem;
#endif

using BeaverBuddies.Connect;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Text;
using Timberborn.SingletonSystem;
using UnityEngine;
using Timberborn.SteamOverlaySystem;
using Timberborn.CoreUI;

namespace BeaverBuddies.Steam
{
    class SteamOverlayConnectionService : IUpdatableSingleton
    {
        public static bool IsSteamEnabled { get; private set; } = false;

#if IS_STEAM
        private SteamManager _steamManager;
        private ClientConnectionService _clientConnectionService;
        private PanelStack _panelStack;
        private SteamOverlayInputBlocker _inputBlocker;
        private EventBus _eventBus;
        private Settings _settings;

        private bool? _lastSuccess = null;

        private static List<IDisposable> callbacks = new List<IDisposable>();

        public SteamOverlayConnectionService(
            SteamManager steamManager,
            ClientConnectionService clientConnectionService,
            SteamOverlayInputBlocker steamOverlayInputBlocker,
            PanelStack panelStack,
            EventBus eventBus,
            Settings settings
            )
        {
            _steamManager = steamManager;
            _clientConnectionService = clientConnectionService;
            _panelStack = panelStack;
            _inputBlocker = steamOverlayInputBlocker;
            _eventBus = eventBus;
            _settings = settings;
        }

        bool done = false;
        public void UpdateSingleton()
        {
            //Read();
            if (!done)
            {
                if (_steamManager.Initialized)
                {
                    IsSteamEnabled = true;

                    foreach (var callback in callbacks)
                    {
                        callback.Dispose();
                    }

                    done = true;

                    //Callback<LobbyCreated_t>.Create(OnLobbyCreated);
                    //Callback<LobbyInvite_t>.Create(OnLobbyInvite);
                    //Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
                    callbacks.Add(Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested));
                    callbacks.Add(Callback<LobbyEnter_t>.Create(OnLobbyEntered));

                    // Start Steam networking (relay warm-up and the per-frame pump).
                    try { SteamNet.Initialize(); }
                    catch (Exception e) { Plugin.LogError("Steam networking could not start; Steam invites are unavailable: " + e.Message); }

                    TrySetPingName();
                    TryJoinLaunchLobby();
                }
                else
                {
                    Plugin.Log("Waiting on Steamworks to initialize...");
                }
            }
            //ReceiveMessages();
        }

        private void TrySetPingName()
        {
            if (Settings.PingDisplayName != Settings.DefaultPingPlayerName) return;
            try
            {
                string steamName = SteamFriends.GetFriendPersonaName(SteamUser.GetSteamID());
                if (!string.IsNullOrEmpty(steamName))
                {
                    _settings.PingPlayerName.SetValue(steamName);
                }
            }
            catch (Exception)
            {
            }
        }

        private void OnLobbyJoinRequested(GameLobbyJoinRequested_t callback)
        {
            string name = SteamFriends.GetFriendPersonaName(callback.m_steamIDFriend);
            Debug.Log("User " + name + " has requested to join the lobby; joining...");
            SteamMatchmaking.JoinLobby(callback.m_steamIDLobby);
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t callback)
        {
            Debug.Log("Lobby chat update: " + callback.m_ulSteamIDLobby);
            if ((callback.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
            {
                CSteamID userJoined = new CSteamID(callback.m_ulSteamIDUserChanged);
                string name = SteamFriends.GetFriendPersonaName(userJoined);
                Debug.Log("User " + name + " has joined the lobby.");


                //SteamNetworking.CreateP2PConnectionSocket(memberId, 0, )
                string message = "Hello, beaver buddy!";
                byte[] data = Encoding.UTF8.GetBytes(message);
                SteamNetworking.SendP2PPacket(userJoined, data, (uint)data.Length, EP2PSend.k_EP2PSendReliable);
            }
        }

        private void OnLobbyInvite(LobbyInvite_t param)
        {
            string invitingUser = SteamFriends.GetFriendPersonaName(new CSteamID(param.m_ulSteamIDUser));
            Plugin.Log($"Invited to lobby {param.m_ulSteamIDLobby} by {invitingUser}");
        }

        private static bool launchLobbyHandled;

        // If the invited friend did not have the game running, Steam launches it with "+connect_lobby <id>".
        private void TryJoinLaunchLobby()
        {
            if (launchLobbyHandled) return;
            launchLobbyHandled = true;
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong lobbyId) && lobbyId != 0)
                    {
                        Plugin.Log($"Launched from a Steam invite; joining lobby {lobbyId}...");
                        SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.LogWarning("Could not read the Steam launch invite: " + e.Message);
            }
        }

        // This should only be called if the SteamOverlayInputBlocker is on top,
        // so we can assume it was the panel that was just removed.
        // If the game starts, it clears the stack silently, so this isn't shown
        // (also this Singleton is disposed when the current scene ends).
        [OnEvent]
        public void OnPanelHidden(PanelHiddenEvent panelHiddenEvent)
        {
            if (!_lastSuccess.HasValue) return;
            _clientConnectionService.ShowConnectionMessage(_lastSuccess.Value);
            ClearWaitForSteamOverlay();
        }

        private void WaitForSteamOverlayToClose(bool success)
        {
            _lastSuccess = success;
            _eventBus.Register(this);
        }

        private void ClearWaitForSteamOverlay()
        {
            _lastSuccess = null;
            _eventBus.Unregister(this);

        }


        private void OnLobbyEntered(LobbyEnter_t callback)
        {
            ClearWaitForSteamOverlay();
            var lobby = new CSteamID(callback.m_ulSteamIDLobby);
            var owner = SteamMatchmaking.GetLobbyOwner(lobby);
            if (owner != SteamUser.GetSteamID())
            {
                if (callback.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
                {
                    Plugin.LogWarning($"Could not enter the host's Steam lobby (response {callback.m_EChatRoomEnterResponse}).");
                    _clientConnectionService.ShowConnectionMessage(false);
                    return;
                }
                if (SteamMatchmaking.GetLobbyData(lobby, SteamListener.OpenKey) == "0")
                {
                    // An old invite: the host already started, so nobody can join until they rehost.
                    Plugin.Log("The host has already started the game; not connecting.");
                    SteamMatchmaking.LeaveLobby(lobby);
                    _clientConnectionService.ShowJoinError("BeaverBuddies.JoinCoopGame.Error.HostStarted");
                    return;
                }
                Plugin.Log("Joining another's lobby...");
                bool success = _clientConnectionService.TryToConnect(owner);
                if (_panelStack.IsPanelOnTop(_inputBlocker))
                {
                    WaitForSteamOverlayToClose(success);
                }
                else
                {
                    _clientConnectionService.ShowConnectionMessage(success);
                }
            }
        }
#else
        // Need to implement the interface
        public void UpdateSingleton() { }
#endif
    }
}
