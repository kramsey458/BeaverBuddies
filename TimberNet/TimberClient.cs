using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace TimberNet
{
    public class ConnectionFailureException : Exception
    {
        public ConnectionFailureException() : base("Client connection timed out") { }
    }

    public class TimberClient : TimberNetBase
    {

        private readonly ISocketStream client;
        private int connectionFailed;
        protected override bool ConnectionKeepAliveEnabled => UseReconnectHandshake;
        public bool UseReconnectHandshake { get; set; }
        public string? ReconnectToken { get; set; }
        public string? RecoveryId { get; private set; }
        public string? RecoveryDigest { get; private set; }
        public override void Update()
        {
            base.Update();
            if (UseReconnectHandshake && SnapshotDigest != null && !IsStopped)
                if (CompatibilityVerified) CheckConnectionSilence(client); else RefreshConnectionSilence(client);
        }

        volatile bool leaving;
        public void LeaveSession()
        {
            if (IsStopped || leaving) return;
            leaving = true;
            SendControl(Control("LeaveSession"));
            // Let the host consume the leave notice and close first. Closing immediately
            // after writing can race its outbound worker and be mistaken for a lost peer.
            _ = Task.Run(async () =>
            {
                try { await Task.WhenAny(FlushAsync(), Task.Delay(1000)); await Task.Delay(3000); }
                finally { leaving = false; Close(); }
            });
        }
        public override void SendControl(JObject message) => SendEvent(client, message);
        public override void SendActivity(PlayerActivity activity) => SendActivityTo(client, activity.WithPlayerId(0));

        public override bool ShouldTick => base.ShouldTick && receivedEvents.Count > 0;
        protected override IEnumerable<(ISocketStream Peer, string Label)> TelemetryPeers =>
            SnapshotDigest != null && client.Connected ? new[] { (client, "Host") } : Array.Empty<(ISocketStream, string)>();
        public string TransportName => client is TCPClientWrapper ? "Direct IP" : "Steam";

        public TimberClient(ISocketStream client) : base()
        {
            this.client = client;
        }

        public override void DoUserInitiatedEvent(JObject message)
        {
            // Don't actually do the event (i.e. add it to the hash)
            // Wait for the server to confirm w/ adjusted Tick
            SendEvent(client, message);
        }

        protected override void HandleConnectionFailure(ISocketStream stream, string message)
        {
            if (leaving) { leaving = false; Close(); return; }
            if (IsStopped || Interlocked.Exchange(ref connectionFailed, 1) != 0) return;
            Close();
            QueueError(message);
        }

        protected override void ProcessReceivedEvent(JObject message)
        {
            base.ProcessReceivedEvent(message);
            if (ShouldLogDetails) Log($"Received event: {message[TYPE_KEY]?.ToString() ?? "<null>"}");
            AddEventToHash(message);
        }

        public override void Start()
        {
            base.Start();
            Task.Run(async () =>
            {
                try
                {
                    var connect = client.ConnectAsync();
                    int timeout = (client as IConnectionOptions)?.ConnectTimeoutMilliseconds ?? 3000;
                    if (await Task.WhenAny(connect, Task.Delay(timeout)) != connect) throw new ConnectionFailureException();
                    await connect;
                    ConnectionStatus = "Connected. Checking installed mods...";
                    if (CompatibilityIdentity != null) CompatibilityHandshake.Run(client, CompatibilityIdentity, false);
                    if (!IsStopped && UseReconnectHandshake)
                    {
                        var admission = ReconnectHandshake.Guest(client, ReconnectToken);
                        ReconnectToken = (string?)admission["ticket"];
                        RecoveryId = (string?)admission["recoveryId"]; RecoveryDigest = (string?)admission["digest"];
                    }
                    ConnectionStatus = "Waiting for the host save...";
                    if (!IsStopped) StartListening(client, true);
                }
                catch (Exception error) { HandleConnectionFailure(client, error.Message); }
            });
        }


        public override void AbortSession(string reason)
        {
            try { SendSessionFault(client, reason); }
            finally { CloseAfterFlush(); }
        }

        public override void Close()
        {
            if (CloseDeferred || leaving) return;
            base.Close();
            client.Close();
        }
    }
}
