using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Security.Cryptography;

namespace TimberNet
{ 

    public class TimberServer : TimberNetBase
    {

        private readonly List<ISocketStream> clients = new List<ISocketStream>();
        readonly ConcurrentDictionary<ISocketStream, int> activityIds = new ConcurrentDictionary<ISocketStream, int>();
        int nextActivityId;

        protected override void QueueActivity(ISocketStream stream, PlayerActivity activity)
        {
            // Identity belongs to the connection, never to the peer-supplied name or player field.
            if (activityIds.TryGetValue(stream, out int id)) base.QueueActivity(stream, activity.WithPlayerId(id));
        }
        protected override void ProcessActivity(PlayerActivity activity)
        {
            if (!activityIds.Any(pair => pair.Value == activity.PlayerId && pair.Key.Connected)) return;
            base.ProcessActivity(activity);
            BroadcastActivity(activity);
        }
        public override void SendActivity(PlayerActivity activity) => BroadcastActivity(activity.WithPlayerId(0));
        void BroadcastActivity(PlayerActivity activity)
        {
            lock (queuedMessages)
            {
                foreach (var peer in clients)
                    if (peer.Connected && !queuedMessages.ContainsKey(peer) &&
                        activityIds.TryGetValue(peer, out int id) && id != activity.PlayerId)
                        SendActivityTo(peer, activity);
            }
        }
        protected override void HandleConnectionFailure(ISocketStream stream, string message)
        {
            activityIds.TryRemove(stream, out _);
            base.HandleConnectionFailure(stream, message);
        }
        private readonly ConcurrentDictionary<ISocketStream, ConcurrentQueue<string>> queuedMessages =
            new ConcurrentDictionary<ISocketStream, ConcurrentQueue<string>>();

        private readonly ISocketListener listener;
        readonly ConcurrentQueue<Action> completedJoins = new ConcurrentQueue<Action>();
        public override void Update()
        {
            while (completedJoins.TryDequeue(out var complete)) complete();
            base.Update();
        }

        private Func<Task<byte[]>> mapProvider;
        private Func<JObject>? initEventProvider;

        public ISocketStream[] GetConnections() { lock (queuedMessages) return clients.Where(c => c.Connected).ToArray(); }
        protected override IEnumerable<object> AdmissionPeers => GetConnections();

        public int ClientCount { get { lock (queuedMessages) return clients.Count(client => client.Connected); } }

        private string? errorMessage = null;
        public bool IsAcceptingClients => errorMessage == null;

        public List<string?> GetConnectedClients()
        {
            lock (queuedMessages) return clients.Where(c => c.Connected).Select(c => c.Name).ToList();
        }

        public TimberServer(ISocketListener listener, Func<Task<byte[]>> mapProvider, Func<JObject>? initEventProvider)
        {
            this.listener = listener;
            this.mapProvider = mapProvider;
            this.initEventProvider = initEventProvider;
        }

        public void UpdateProviders(Func<Task<byte[]>> mapProvider, Func<JObject>? initEventProvider)
        {
            this.mapProvider = mapProvider;
            this.initEventProvider = initEventProvider;
        }

        protected override void ReceiveEvent(JObject message)
        {
            message[TICKS_KEY] = TickCount;
            base.ReceiveEvent(message);
        }

        public override void Start()
        {
            base.Start();

            listener.Start();
            Log("Server started listening");
            
            Task.Run(() =>
            {
                // TODO: I have a suspicion that this while plus the catch/continue below
                // is responsible for the server hanging sometimes on a connection that's dropped.
                // Logging now to see if I can catch it.
                while (!IsStopped)
                {
                    ISocketStream client;
                    try
                    {
                        Log("Accepting client...");
                        client = listener.AcceptClient();
                    } catch (Exception e)
                    {
                        if (!IsStopped)
                        {
                            QueueError("The host listener stopped: " + e.Message);
                            Close();
                        }
                        break;
                    }
                    Task.Run(async () =>
                    {
                        try
                        {
                            if (!IsAcceptingClients)
                            {
                                SendErrorMessage(client);
                                client.Close();
                                return;
                            }

                            if (CompatibilityIdentity != null) CompatibilityHandshake.Run(client, CompatibilityIdentity, true);
                            if (IsStopped || !IsAcceptingClients) { client.Close(); return; }
                            await SendMap(client);
                            var initialized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            completedJoins.Enqueue(() =>
                            {
                                try
                                {
                                    if (IsStopped || !client.Connected) { initialized.TrySetResult(false); return; }
                                    lock (queuedMessages)
                                    {
                                        FinishQueuing(client);
                                        SendState(client);
                                        if (initEventProvider != null) DoUserInitiatedEvent(initEventProvider());
                                    }
                                    initialized.TrySetResult(true);
                                }
                                catch (Exception error) { initialized.TrySetException(error); }
                            });
                            while (!initialized.Task.IsCompleted && !IsStopped && client.Connected) await Task.Delay(20);
                            if (IsStopped || !client.Connected || !await initialized.Task) { client.Close(); return; }

                            // This must come last - it is an infinite loop
                            // until the client disconnects
                            StartListening(client, false);
                        }
                        catch (Exception error) { HandleConnectionFailure(client, "Connection rejected: " + error.Message); }
                    });
                }
            });
        }

        public void StopAcceptingClients(string errorMessage)
        {
            this.errorMessage = errorMessage;
        }

        private void StartQueuing (ISocketStream client)
        {
            lock (queuedMessages)
            {
                if (IsStopped || !IsAcceptingClients) { client.Close(); throw new IOException("Session closed to new joins."); }
                queuedMessages.TryAdd(client, new ConcurrentQueue<string>());
                clients.Add(client);
                activityIds.TryAdd(client, System.Threading.Interlocked.Increment(ref nextActivityId));
            }
        }

        private void FinishQueuing(ISocketStream client)
        {
            // Log("finishing queuing");
            lock(queuedMessages)
            {
                if (queuedMessages.TryGetValue(client, out ConcurrentQueue<string> queue))
                {
                    // Log($"Found {queue.Count} messages");
                    while (queue.TryDequeue(out string message))
                    {
                        // Log(message.ToString());
                        SendSerializedEvent(client, message);
                    }
                    queuedMessages.TryRemove(client, out _);
                }
                else
                {
                    Log("Warning! Missing client!");
                }
            }
        }

        private void SendErrorMessage(ISocketStream client)
        {
            SendLength(client, 0);
            byte[] bytes = MessageToBuffer(errorMessage!);
            // TODO: Not sure this makes sense for Steam
            SendDataWithLength(client, bytes);
        }

        private async Task SendMap(ISocketStream client)
        { 
            StartQueuing(client);
            Task<byte[]> task = mapProvider();
            Log("Waiting for map...");
            byte[] mapBytes = await task;

            // TODO: This may happen a bit early - it seems possible for
            // events from a prior frame to get queued. Maybe just need to filter
            // them on the client side.
            // Start recording messages as soon as the map is saved,
            // while the map is sending
            SnapshotDigest = DigestSnapshot(mapBytes);

            Log($"Sending map with length {mapBytes.Length}");
            SendDataWithLength(client, mapBytes);

            Log($"Sent map with length {mapBytes.Length} and Hash: {GetHashCode(mapBytes).ToString("X8")}");
        }

        private void SendState(ISocketStream client)
        {
            JObject message = new JObject();
            message[TICKS_KEY] = 0;
            message[TYPE_KEY] = SET_STATE_EVENT;
            message["hash"] = Hash;
            // Send directly - don't queue
            SendEvent(client, message);
        }

        void DoUserInitiatedEvent(JObject message, bool sendNow)
        {
            base.DoUserInitiatedEvent(message);
            SendEventToClients(message, sendNow);
        }

        public override void DoUserInitiatedEvent(JObject message)
        {
            DoUserInitiatedEvent(message, false);
        }

        private void SendEventToClients(JObject message, bool sendNow)
        {
            string json = message.ToString(Newtonsoft.Json.Formatting.None);
            lock (queuedMessages)
            {
                for (int i = clients.Count - 1; i >= 0; i--)
                {
                    if (!clients[i].Connected)
                    {
                        queuedMessages.TryRemove(clients[i], out _);
                        activityIds.TryRemove(clients[i], out _);
                        clients.RemoveAt(i);
                    }
                }
                // Share the join/close lock across enumeration and mutation.
                clients.ForEach(client =>
                {
                    if (sendNow)
                    {
                        SendSerializedEvent(client, json);
                    }
                    else
                    {
                        QueueOrSentToClient(client, json);
                    }
                });
            }
        }

        private void QueueOrSentToClient(ISocketStream client, string json)
        {
            if (!client.Connected) return;

            if (queuedMessages.TryGetValue(client, out ConcurrentQueue<string> queue))
            {
                if (queue.Count >= 2048 || queue.Sum(item => (long)item.Length) + json.Length > 8 * 1024 * 1024) { HandleConnectionFailure(client, "Join backlog exceeded."); return; }
                queue.Enqueue(json);
            }
            else
            {
                SendSerializedEvent(client, json);
            }
        }

        public override void SendControl(JObject message) => SendEventToClients(message, false);

        public override void AbortSession(string reason)
        {
            try
            {
                lock (queuedMessages)
                    foreach (var client in clients.ToArray()) SendSessionFault(client, reason);
            }
            finally { CloseAfterFlush(); }
        }

        public override void Close()
        {
            if (CloseDeferred) return;
            base.Close();
            try
            {
                lock (queuedMessages) clients.ForEach(client => client.Close());
            }
            catch (Exception e)
            {
                Log(e.ToString());
            }
            try
            {
                listener.Stop();
            }
            catch (Exception e)
            {
                Log(e.ToString());
            }  
        }

        public void SendHeartbeat()
        {
            JObject message = new JObject();
            message[TICKS_KEY] = TickCount;
            message[TYPE_KEY] = HEARTBEAT_EVENT;
            // Simulate the user doing this
            DoUserInitiatedEvent(message);
        }
    }
}
