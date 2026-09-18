using Steamworks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using TimberNet;

namespace BeaverBuddies.Steam
{
    public class SteamListener : ISocketListener, ISteamPacketReceiver
    {
        public CSteamID LobbyID { get; private set; }

        private List<IDisposable> callbacks = new List<IDisposable>();
        private readonly BlockingCollection<SteamSocket> joiningUsers = new BlockingCollection<SteamSocket>();
        private bool stopped;
        private SteamPacketListener steamPacketListener;

        public SteamListener()
        {
            if (!SteamOverlayConnectionService.IsSteamEnabled)
            {
                throw new Exception("SteamListener created when Steam is not enabled!");
            }
        }

        public void RegisterSteamPacketListener(SteamPacketListener steamPacketListener)
        {
            this.steamPacketListener = steamPacketListener;
        }

        public void Start()
        {
            if (steamPacketListener == null)
            {
                throw new InvalidOperationException("SteamPacketListener must be registered before starting the SteamListener.");
            }
            Plugin.Log("SteamListener started...");
            callbacks.Add(Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate));
            callbacks.Add(Callback<LobbyCreated_t>.Create(OnLobbyCreated));
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 8);
        }

        private void OnLobbyCreated(LobbyCreated_t callback)
        {
            // Handle the callback
            if (callback.m_eResult == EResult.k_EResultOK)
            {
                // Lobby created successfully
                LobbyID = new CSteamID(callback.m_ulSteamIDLobby);
                // Friend only is the default; invisible means invite-only.
                var type = Settings.LobbyJoinable ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypeInvisible;
                SteamMatchmaking.SetLobbyType(LobbyID, type);
                Plugin.Log($"Lobby created with ID: {LobbyID} is joinable={Settings.LobbyJoinable}");
            }
            else
            {
                // Handle error
                Plugin.LogError("Failed to create lobby: " + callback.m_eResult);
            }
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t callback)
        {
            if (stopped || callback.m_ulSteamIDLobby != LobbyID.m_SteamID) return;
            Plugin.Log("Lobby chat update: " + callback.m_ulSteamIDLobby);
            if ((callback.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
            {
                CSteamID userJoined = new CSteamID(callback.m_ulSteamIDUserChanged);
                
                // Don't include in release
                //string name = SteamFriends.GetFriendPersonaName(userJoined);
                //Plugin.Log("User " + name + " has joined the lobby.");

                var socket = new SteamSocket(userJoined, true);
                socket.RegisterSteamPacketListener(steamPacketListener);
                try { joiningUsers.Add(socket); }
                catch (InvalidOperationException) { socket.Close(); }
            }
        }

        public ISocketStream AcceptClient()
        {
            Plugin.Log("Waiting to accept a client...");
            SteamSocket socket;
            try { socket = joiningUsers.Take(); }
            catch (InvalidOperationException error) { throw new IOException("Steam listener stopped.", error); }
            Plugin.Log("New client accepted!");
            return socket;
        }

        public void Stop()
        {
            stopped = true;
            joiningUsers.CompleteAdding();
            while (joiningUsers.TryTake(out var pending)) pending.Close();
            Plugin.Log("Stopping SteamListener...");
            SteamMatchmaking.LeaveLobby(LobbyID);
            foreach (IDisposable callback in callbacks)
            {
                callback.Dispose();
            }
        }

        public void ShowInviteFriendsPanel()
        {
            SteamFriends.ActivateGameOverlayInviteDialog(LobbyID);
        }
    }
}
