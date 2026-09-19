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

        public override bool ShouldTick => base.ShouldTick && receivedEvents.Count > 0;

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

        // Long enough for a relayed connection to be established, but bounded.
        private const int BackgroundConnectTimeoutMilliseconds = 45000;

        protected override void HandleConnectionFailure(ISocketStream stream, string message)
        {
            if (IsStopped || Interlocked.Exchange(ref connectionFailed, 1) != 0) return;
            // Capture the reason first: Close() tears the stream down.
            message = DescribeFailure(stream, message);
            Close();
            QueueError(message);
        }

        private ActivityChannel? activityChannel;

        protected override void OnMapFrameReceived(ISocketStream stream)
        {
            activityChannel = CreateActivityChannel(stream);
        }

        private sealed class RosterSnapshot
        {
            public int You;
            public List<PeerStatus> Peers = new List<PeerStatus>();
            public double ReceivedAtMs;
        }

        private volatile RosterSnapshot? roster;
        private readonly double startedAtMs = RttTracker.NowMs;

        protected override void HandleStatusFrame(ISocketStream source, string type, JObject message)
        {
            if (type == StatusFrames.ProbeType && StatusFrames.TryParseSequence(message, out int sequence))
            {
                // Answered on the network thread, not the game thread, so the host measures the network
                // and not how busy this player's game happens to be.
                try { SendDataWithLength(client, MessageToBuffer(StatusFrames.Reply(sequence, TickCount))); }
                catch (Exception) { /* a dead connection is reported by the reader */ }
            }
            else if (type == StatusFrames.RosterType && StatusFrames.TryParseRoster(message, out int you, out List<PeerStatus> peers))
            {
                roster = new RosterSnapshot { You = you, Peers = peers, ReceivedAtMs = RttTracker.NowMs };
            }
        }

        public override NetworkStatus GetNetworkStatus()
        {
            RosterSnapshot? latest = roster;
            double now = RttTracker.NowMs;
            double silence = Math.Max(0, now - (latest?.ReceivedAtMs ?? startedAtMs)) / 1000.0;
            return new NetworkStatus(false, IsStopped, latest?.You ?? -1, silence,
                (IReadOnlyList<PeerStatus>?)latest?.Peers ?? Array.Empty<PeerStatus>());
        }

        public override void SendActivity(PlayerActivity activity)
        {
            if (IsStopped) return;
            // The host replaces the id with the one it assigned to this connection.
            activityChannel?.Post(activity);
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
            // TODO: Handle async properly and cleanup
            // TODO: Make wait configurable?
            if (!client.ConnectAsync().Wait(3000))
            {
                throw new ConnectionFailureException();
            }
            // Connect a TCP socket at the address
            Task.Run(() =>
            {
                try
                {
                    // Transports that connect in the background finish before the handshake clock starts.
                    (client as IConnectionAwaitable)?.WaitForConnection(BackgroundConnectTimeoutMilliseconds);
                    if (CompatibilityIdentity != null) RunCompatibilityHandshake(client, false);
                    if (!IsStopped) StartListening(client, true);
                }
                catch (Exception error) { HandleConnectionFailure(client, error.Message); }
            });
        }


        public override void AbortSession(string reason)
        {
            try { SendSessionFault(client, reason); }
            finally { Close(); }
        }

        public override void Close()
        {
            base.Close();
            activityChannel?.Close();
            client.Close();
        }
    }
}
