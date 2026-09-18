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

        public override void SendControl(JObject message) => SendEvent(client, message);

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

        protected override void HandleConnectionFailure(ISocketStream stream, string message)
        {
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
                    if (await Task.WhenAny(connect, Task.Delay(3000)) != connect) throw new ConnectionFailureException();
                    await connect;
                    if (CompatibilityIdentity != null) CompatibilityHandshake.Run(client, CompatibilityIdentity, false);
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
            if (CloseDeferred) return;
            base.Close();
            client.Close();
        }
    }
}
