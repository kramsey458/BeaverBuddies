using BeaverBuddies.IO;
using BeaverBuddies.Steam;
using BeaverBuddies.Util;
using Steamworks;
using System;
using System.IO;
using System.Net.Sockets;
using System.Net;
using Timberborn.CoreUI;
using Timberborn.GameSaveRepositorySystem;
using Timberborn.GameSceneLoading;
using Timberborn.Localization;
using Timberborn.SingletonSystem;
using Timberborn.WebNavigation;
using TimberNet;
using System.Linq;
using Timberborn.SettlementNameSystem;
using UnityEngine.UIElements;

namespace BeaverBuddies.Connect
{
    public class ClientConnectionService : IUpdatableSingleton
    {
        private GameSceneLoader _gameSceneLoader;
        private GameSaveRepository _gameSaveRepository;
        private DialogBoxShower _dialogBoxShower;
        private UrlOpener _urlOpener;
        private ClientEventIO client;
        private Settings _settings;
        static Func<ISocketStream> reconnectSocket;
        static string reconnectToken;
        double nextReconnect;
        bool snapshotAttempt;
        DialogBox progress;
        double nextProgress;
        bool steamConnection;
        void HideProgress() { var old = progress; progress = null; old?.Close(); }
        public void ReconnectOriginal()
        {
            if (reconnectSocket != null) { reconnectToken = null; TryToConnect(reconnectSocket()); ShowConnectionMessage(true); }
            else ConnectOrShowFailureMessage();
        }
        static double Now => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
        public void BeginSnapshotReconnect()
        {
            client?.Close(); client = null;
            snapshotAttempt = false; nextReconnect = Now + 1;
        }


        public ClientConnectionService(
            GameSceneLoader gameSceneLoader,
            GameSaveRepository gameSaveRepository,
            DialogBoxShower dialogBoxShower,
            UrlOpener urlOpener,
            Settings settings
        )
        {
            _gameSceneLoader = gameSceneLoader;
            _gameSaveRepository = gameSaveRepository;
            _dialogBoxShower = dialogBoxShower;
            _urlOpener = urlOpener;
            _settings = settings;
        }

        public bool TryToConnect(CSteamID friendID)
        {
            reconnectToken = null;
            reconnectSocket = () => new SteamRelaySocket(friendID);
            return TryToConnect(reconnectSocket());
        }

        public bool TryToConnect(string address)
        {
            SteamGuestLobby.Leave();
            int port = _settings.DefaultPort.Value;
            Plugin.Log("Try to resolve address: " + address);
            // Parse address and port
            if (TryParseHostAndPort(address, out string parsedAddress, out int? parsedPort))
            {
                address = parsedAddress;
                Plugin.Log($"Parsed address: {address}, port: {port}");
            }
            else
            {
                ShowError("BeaverBuddies.JoinCoopGame.Error.InvalidFormat");
                return false;
            }

            // Set port if provided
            if (parsedPort.HasValue)
            {
                port = parsedPort.Value;
            }


            // If it's not an IP address, resolve the hostname
            if (!IPAddress.TryParse(address, out _))
            {

                // Resolve the address if it's a hostname
                if (ResolveHostnameIfNecessary(parsedAddress, out string resolvedAddress))
                {
                    address = resolvedAddress;
                }
                else
                {
                    ShowError("BeaverBuddies.JoinCoopGame.Error.InvalidAddress");
                    return false;
                }
            }

            reconnectToken = null;
            reconnectSocket = () => new TCPClientWrapper(address, port);
            return TryToConnect(reconnectSocket());
        }

        private bool TryToConnect(ISocketStream socket)
        {
            HideProgress();
            steamConnection = socket is SteamRelaySocket;
            Plugin.Log("Connecting client");
            ClientEventIO attempt = null;
            attempt = ClientEventIO.Create(socket, LoadMap, (error) =>
            {
                if (attempt != null && attempt != EventIO.Get()) return;
                if (SnapshotResyncService.Active)
                {
                    snapshotAttempt = false; nextReconnect = Now + 2;
                    SnapshotResyncService.ConnectionFailed(error);
                }
                else
                {
                    HideProgress();
                    if (steamConnection) _dialogBoxShower.Create().SetMessage("Could not join through Steam.\n\n" + error + "\n\nCheck both games use the same mod build and ask the host for another invite.").SetDefaultCancelButton().Show();
                    else ShowError("BeaverBuddies.JoinCoopGame.Error.CouldNotConnect", error);
                    if (attempt != null && attempt == EventIO.Get()) EventIO.Reset();
                    SteamGuestLobby.Leave();
                }
            }, reconnectToken);
            client = attempt;

            if (client == null)
            {
                Plugin.Log("Client creation failed.");
                return false;
            }

            EventIO.Set(client);
            return true;
        }

        public void ConnectOrShowFailureMessage()
        {
            ConnectOrShowFailureMessage(_settings.ClientConnectionAddress.Value);
        }

        public void ConnectOrShowFailureMessage(string address)
        {
            TryToConnect(address);
        }

        public void ShowConnectionMessage(bool success)
        {
            if (success)
            {
                if (client == null || client != EventIO.Get() || client.NetBase == null || client.NetBase.IsStopped) return;
                HideProgress();
                progress = _dialogBoxShower.Create()
                    .SetMessage(client.NetBase.ConnectionStatus)
                    .SetCancelButton(() => { progress = null; client?.Close(); EventIO.Reset(); SteamGuestLobby.Leave(); }, "Cancel joining")
                    .Show();
            }
            else
            {
                ShowError("BeaverBuddies.JoinCoopGame.ConnectionFailedMessage");
            }
        }

        private void ShowError(string reasonKey, string details = null)
        {
            string messageKey;
            if (reasonKey != null)
            {
                messageKey = "BeaverBuddies.JoinCoopGame.ConnectionFailedMessageWithError";
            }
            else
            {
                messageKey = "BeaverBuddies.JoinCoopGame.ConnectionFailedMessage";
            }

            ILoc _loc = _dialogBoxShower._loc;
            string reasonMessage = null;
            if (reasonKey != null)
            {
                reasonMessage = _loc.T(reasonKey);
            }

            if (details != null)
            {
                if (reasonMessage != null)
                {
                    reasonMessage += "\n";
                }
                else
                {
                    reasonMessage = "";
                }
                reasonMessage += "\"" + details + "\"";
            }

            var action = () =>
            {
                _urlOpener.OpenUrl(LinkHelper.TroubleshootingUrl);
            };

            string message = _loc.T(messageKey, reasonMessage);
            _dialogBoxShower.Create()
                .SetMessage(message)
                .SetConfirmButton(action)
                .SetDefaultCancelButton()
                .Show();
        }

        private void LoadMap(byte[] mapBytes)
        {
            HideProgress();
            try
            {
                // Clean up our current co-op state before loading,
                // so we don't, for example, end up ticking the client before
                // it's actually loaded.
                reconnectToken = client.NetBase?.ReconnectToken;
                if (!SnapshotResyncService.MapLoading(client, mapBytes)) return;
                SingletonManager.Reset();

                Plugin.Log("Loading map");
                //string saveName = Guid.NewGuid().ToString();
                string saveName = TimberNetBase.GetHashCode(mapBytes).ToString("X8");
                SaveReference saveRef = new SaveReference("Online Games", new SettlementReference(saveName, _gameSaveRepository.DefaultSaveDirectory));
                using (Stream stream = _gameSaveRepository.CreateSaveSkippingNameValidation(saveRef))
                    stream.Write(mapBytes);

                // Set the RNG seed before loading the map
                // The server does the same
                DeterminismService.InitGameStartState(mapBytes);
                _gameSceneLoader.StartSaveGame(saveRef);
            }
            catch (Exception error)
            {
                Plugin.LogError("Could not load the host snapshot: " + error);
                if (SnapshotResyncService.Active) SnapshotResyncService.ConnectionFailed(error.Message, false);
                else ShowError("BeaverBuddies.JoinCoopGame.Error.CouldNotConnect", error.Message);
            }
        }

        public void UpdateSingleton()
        {
            if (progress != null && Now >= nextProgress)
            {
                nextProgress = Now + .25;
                if (client?.NetBase == null || client != EventIO.Get()) HideProgress();
                else progress.GetPanel().Q<Label>("Message").text = client.NetBase.ConnectionStatus;
            }
            if (SnapshotResyncService.IsReconnecting && !snapshotAttempt && Now >= nextReconnect)
            {
                snapshotAttempt = true;
                try
                {
                    if (reconnectSocket == null) throw new InvalidOperationException("Original host address is unavailable. Join the host manually.");
                    if (steamConnection) SteamGuestLobby.Rejoin();
                    if (!TryToConnect(reconnectSocket())) { snapshotAttempt = false; nextReconnect = Now + 2; }
                }
                catch (Exception error)
                {
                    snapshotAttempt = false; nextReconnect = Now + 2;
                    Plugin.LogWarning("Snapshot reconnect attempt failed: " + error.Message);
                }
            }
            if (client == null || client != EventIO.Get()) return;
            //Plugin.Log("Updating client!");
            client.Update();
        }

        /// <summary>
        /// Tries to parse a host:port or [IPv6]:port string.
        /// Supports IPv4, IPv6, and hostnames.
        /// Returns true if parsing succeeded.
        /// </summary>
        public static bool TryParseHostAndPort(
            string input,
            out string host,
            out int? port)
        {
            host = null;
            port = null;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            // Uri requires a scheme, so we prepend a dummy one
            var uriString = input.Contains("://") ? input : "tcp://" + input;

            if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
            {
                if (!input.StartsWith("[") && input.Count(c => c == ':') >= 2)
                {
                    Plugin.Log("Attempting to wrap likely IPv6 address");
                    return TryParseHostAndPort($"[{input}]", out host, out port);
                }
                return false;
            }

            // Hostname or IP string (IPv6 brackets stripped)
            host = uri.Host;

            // Port: Uri.Port returns -1 if missing
            if (uri.Port != -1)
                port = uri.Port;

            return true;
        }

        private bool ResolveHostnameIfNecessary(string address, out string resolvedAddress)
        {
            resolvedAddress = null;

            try
            {
                // Otherwise, try to resolve it
                IPHostEntry hostEntry = Dns.GetHostEntry(address);
                if (hostEntry.AddressList.Length > 0)
                {
                    resolvedAddress = hostEntry.AddressList[0].ToString();
                    Plugin.Log(address + " resolved to " + resolvedAddress);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogError("Could not resolve hostname: " + ex.ToString());
            }

            return false;
        }
    }
}
