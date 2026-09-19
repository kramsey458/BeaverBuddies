using Steamworks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TimberNet;

namespace BeaverBuddies.Steam
{
    public class SteamListener : ISocketListener
    {
        public const string Protocol = "bb-relay-14";
        public CSteamID LobbyID { get; private set; }
        public string Status { get; private set; } = "Preparing Steam invites...";
        readonly object gate = new object();
        readonly BlockingCollection<ISocketStream> joining = new BlockingCollection<ISocketStream>();
        readonly Dictionary<HSteamNetConnection, SteamRelaySocket> sockets = new Dictionary<HSteamNetConnection, SteamRelaySocket>();
        readonly HashSet<HSteamNetConnection> admitted = new HashSet<HSteamNetConnection>();
        readonly HashSet<ulong> knownPeers = new HashSet<ulong>();
        readonly HashSet<ulong> recoveryPeers;
        Callback<SteamNetConnectionStatusChangedCallback_t> connections;
        CallResult<LobbyCreated_t> created;
        HSteamListenSocket listener;
        bool stopped, detached, inviteWhenReady, started;

        public SteamListener(ulong existingLobby = 0, ulong[] recoveryPeers = null)
        {
            LobbyID = new CSteamID(existingLobby);
            this.recoveryPeers = recoveryPeers == null ? null : new HashSet<ulong>(recoveryPeers);
            if (recoveryPeers != null) knownPeers.UnionWith(recoveryPeers);
        }
        public ulong[] ExportPeers() { lock (gate) return knownPeers.ToArray(); }

        public void Start()
        {
            if (started || stopped) return;
            started = true;
            // A Steam outage must not disable the independent TCP listener.
            try
            {
                SteamNetworkingUtils.InitRelayNetworkAccess();
                connections = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnection);
                listener = SteamNetworkingSockets.CreateListenSocketP2P(SteamRelaySocket.VirtualPort, 0, null);
                if (listener == HSteamListenSocket.Invalid) throw new IOException("Steam could not open its relay listener.");
                if (LobbyID.m_SteamID != 0) ConfigureLobby();
                else
                {
                    created = CallResult<LobbyCreated_t>.Create(OnCreated);
                    created.Set(SteamMatchmaking.CreateLobby(Settings.LobbyJoinable ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypePrivate, 8));
                }
            }
            catch (Exception error) { Status = "Steam invites unavailable: " + error.Message + " Direct IP is still available."; Plugin.LogWarning(Status); }
        }

        void OnCreated(LobbyCreated_t result, bool ioFailure)
        {
            lock (gate)
            {
                if (stopped)
                {
                    if (!ioFailure && result.m_eResult == EResult.k_EResultOK) SteamMatchmaking.LeaveLobby(new CSteamID(result.m_ulSteamIDLobby));
                    return;
                }
                if (ioFailure || result.m_eResult != EResult.k_EResultOK)
                { Status = "Could not create the Steam lobby. Check Steam is online; direct IP is still available."; return; }
                LobbyID = new CSteamID(result.m_ulSteamIDLobby);
                ConfigureLobby();
                if (inviteWhenReady) ShowInviteFriendsPanel();
            }
        }

        void ConfigureLobby()
        {
            SteamMatchmaking.SetLobbyData(LobbyID, "beaverbuddies_protocol", Protocol);
            SteamMatchmaking.SetLobbyJoinable(LobbyID, true);
            Status = "Steam invites ready. Invite your friends, then start once everyone is connected.";
        }

        bool IsMember(CSteamID peer)
        {
            if (peer == SteamUser.GetSteamID() || LobbyID.m_SteamID == 0) return false;
            int count = SteamMatchmaking.GetNumLobbyMembers(LobbyID);
            for (int i = 0; i < count; i++) if (SteamMatchmaking.GetLobbyMemberByIndex(LobbyID, i) == peer) return true;
            return false;
        }

        void OnConnection(SteamNetConnectionStatusChangedCallback_t change)
        {
            lock (gate)
            {
                if (stopped || change.m_info.m_hListenSocket != listener) return;
                var state = change.m_info.m_eState;
                if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)
                {
                    foreach (var old in sockets.Where(pair => !pair.Value.Connected).Select(pair => pair.Key).ToArray())
                    { sockets.Remove(old); admitted.Remove(old); }
                    if (sockets.ContainsKey(change.m_hConn)) return;
                    var peer = change.m_info.m_identityRemote.GetSteamID();
                    bool allowed = recoveryPeers != null ? recoveryPeers.Contains(peer.m_SteamID) : IsMember(peer);
                    if (!allowed || sockets.Count >= 8)
                    { SteamNetworkingSockets.CloseConnection(change.m_hConn, 1001, "Join the host's Steam lobby using an invite first.", false); return; }
                    if (SteamNetworkingSockets.AcceptConnection(change.m_hConn) != EResult.k_EResultOK)
                    { SteamNetworkingSockets.CloseConnection(change.m_hConn, 1002, "Steam could not accept the connection.", false); return; }
                    sockets.Add(change.m_hConn, new SteamRelaySocket(peer, change.m_hConn));
                    knownPeers.Add(peer.m_SteamID);
                }
                else if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
                {
                    if (sockets.TryGetValue(change.m_hConn, out var socket) && admitted.Add(change.m_hConn))
                        try { joining.Add(socket); } catch (InvalidOperationException) { socket.Close(); }
                }
                else if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ||
                         state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
                {
                    if (sockets.TryGetValue(change.m_hConn, out var socket)) socket.Close();
                    else SteamNetworkingSockets.CloseConnection(change.m_hConn, 0, "Connection ended", false);
                    sockets.Remove(change.m_hConn); admitted.Remove(change.m_hConn);
                }
            }
        }

        public ISocketStream AcceptClient()
        {
            try { while (true) { var socket = joining.Take(); if (socket.Connected) return socket; } }
            catch (InvalidOperationException error) { throw new IOException("Steam listener stopped.", error); }
        }

        // Transfer the lobby to the replacement listener, keeping invited players together.
        public ulong DetachLobby()
        {
            lock (gate) { detached = true; return LobbyID.m_SteamID; }
        }

        public void Stop()
        {
            lock (gate)
            {
                if (stopped) return;
                stopped = true; joining.CompleteAdding();
                foreach (var socket in sockets.Values) socket.Close();
                sockets.Clear(); admitted.Clear();
                if (listener != HSteamListenSocket.Invalid) SteamNetworkingSockets.CloseListenSocket(listener);
                if (!detached && LobbyID.m_SteamID != 0) SteamMatchmaking.LeaveLobby(LobbyID);
                connections?.Dispose();
                // An outstanding create result must still leave any late-created lobby.
                if (created != null && !created.IsActive()) created.Dispose();
            }
        }

        public void ShowInviteFriendsPanel()
        {
            lock (gate)
            {
                if (stopped) return;
                if (LobbyID.m_SteamID == 0) { inviteWhenReady = true; return; }
                inviteWhenReady = false;
                SteamFriends.ActivateGameOverlayInviteDialog(LobbyID);
            }
        }
    }
}
