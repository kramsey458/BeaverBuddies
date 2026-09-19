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
using System.Threading;

namespace TimberNet
{ 

    public class TimberServer : TimberNetBase
    {

        private readonly List<ISocketStream> clients = new List<ISocketStream>();
        private readonly ConcurrentDictionary<ISocketStream, ConcurrentQueue<JObject>> queuedMessages =
            new ConcurrentDictionary<ISocketStream, ConcurrentQueue<JObject>>();

        // Player activity is presentation-only and deliberately kept out of queuedMessages' lock,
        // so a slow gameplay send can never stall a guest's receive thread.
        private readonly ConcurrentDictionary<ISocketStream, int> playerIds = new ConcurrentDictionary<ISocketStream, int>();
        private readonly ConcurrentDictionary<ISocketStream, ActivityChannel> activityChannels =
            new ConcurrentDictionary<ISocketStream, ActivityChannel>();
        private int lastPlayerId;

        // How often the host pings each guest and publishes the roster. Adjustable so tests need not wait.
        public static int StatusIntervalMs = 1000;
        private readonly ConcurrentDictionary<ISocketStream, RttTracker> trackers = new ConcurrentDictionary<ISocketStream, RttTracker>();
        private double nextStatusAtMs;

        private readonly ISocketListener listener;

        private Func<Task<byte[]>> mapProvider;
        private Func<JObject>? initEventProvider;

        public int ClientCount { get { lock (queuedMessages) return clients.Count; } }

        private string? errorMessage = null;
        public bool IsAcceptingClients => errorMessage == null;

        public List<string?> GetConnectedClients()
        {
            lock (queuedMessages) return clients.Select(c => c.Name).ToList();
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
                        Log("Error accepting client.");
                        Log(e.StackTrace);
                        continue;
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

                            if (CompatibilityIdentity != null) RunCompatibilityHandshake(client, true);
                            if (IsStopped || !IsAcceptingClients) { client.Close(); return; }
                            await SendMap(client);
                            SendState(client);
                            if (initEventProvider != null)
                            {
                                JObject initEvent = initEventProvider();
                                // Send the event before finishing queueing
                                // so it is guaranteed to arrive first.
                                // (This also sends it to other clients.)
                                DoUserInitiatedEvent(initEvent, true);
                            }
                            FinishQueuing(client);

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
                if (IsStopped) { client.Close(); throw new IOException("Session closed while joining."); }
                queuedMessages.TryAdd(client, new ConcurrentQueue<JObject>());
                clients.Add(client);
                // The host is player 0; the host, not the guest, chooses each guest's id.
                playerIds[client] = Interlocked.Increment(ref lastPlayerId);
                trackers[client] = new RttTracker(RttTracker.NowMs);
            }
        }

        private void RemoveActivity(ISocketStream client)
        {
            playerIds.TryRemove(client, out _);
            trackers.TryRemove(client, out _);
            if (activityChannels.TryRemove(client, out ActivityChannel? channel)) channel.Close();
        }

        protected override void HandleStatusFrame(ISocketStream source, string type, JObject message)
        {
            // Only a guest's reply to our probe means anything to the host.
            if (type != StatusFrames.ReplyType || !StatusFrames.TryParseSequence(message, out int sequence)) return;
            if (trackers.TryGetValue(source, out RttTracker? tracker)) tracker.OnReply(sequence, RttTracker.NowMs);
        }

        protected override void OnUpdate()
        {
            double now = RttTracker.NowMs;
            if (now < nextStatusAtMs) return;
            nextStatusAtMs = now + StatusIntervalMs;
            // Only guests that have finished joining have a channel, so nothing is sent mid-transfer.
            foreach (var pair in activityChannels)
            {
                ISocketStream stream = pair.Key;
                if (!stream.Connected || !trackers.TryGetValue(stream, out RttTracker? tracker)) continue;
                if (!playerIds.TryGetValue(stream, out int id)) continue;
                // Built at write time, so the probe's timestamp is when it really leaves.
                pair.Value.PostFrame("probe", () => StatusFrames.Probe(tracker.BeginProbe(RttTracker.NowMs)));
                pair.Value.PostFrame("roster", () => StatusFrames.Roster(id, BuildPeerStatuses()));
            }
        }

        private List<PeerStatus> BuildPeerStatuses()
        {
            double now = RttTracker.NowMs;
            var peers = new List<PeerStatus>();
            foreach (var pair in activityChannels)
            {
                ISocketStream stream = pair.Key;
                if (!stream.Connected || !playerIds.TryGetValue(stream, out int id) || !trackers.TryGetValue(stream, out RttTracker? tracker)) continue;
                peers.Add(tracker.Snapshot(id, (stream as ITransportInfo)?.TransportName ?? "", now));
            }
            peers.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
            return peers;
        }

        public override NetworkStatus GetNetworkStatus() =>
            new NetworkStatus(true, IsStopped, 0, null, BuildPeerStatuses());

        protected override void HandleActivity(ISocketStream source, PlayerActivity activity)
        {
            // Frames from a connection that has not been admitted are ignored.
            if (!playerIds.TryGetValue(source, out int id)) return;
            PlayerActivity assigned = activity.WithPlayerId(id);
            base.HandleActivity(source, assigned);
            RelayActivity(assigned, source);
        }

        public override void SendActivity(PlayerActivity activity)
        {
            if (IsStopped) return;
            RelayActivity(activity.WithPlayerId(0), null);
        }

        private void RelayActivity(PlayerActivity activity, ISocketStream? except)
        {
            foreach (var pair in activityChannels)
            {
                if (ReferenceEquals(pair.Key, except)) continue;
                if (!pair.Key.Connected) { RemoveActivity(pair.Key); continue; }
                pair.Value.Post(activity);
            }
        }

        private void FinishQueuing(ISocketStream client)
        {
            // Log("finishing queuing");
            lock(queuedMessages)
            {
                if (queuedMessages.TryGetValue(client, out ConcurrentQueue<JObject> queue))
                {
                    // Log($"Found {queue.Count} messages");
                    while (queue.TryDequeue(out JObject message))
                    {
                        // Log(message.ToString());
                        SendEvent(client, message);
                    }
                    queuedMessages.TryRemove(client, out _);
                    // The map, state and init event are already written, so this client can
                    // now receive activity frames.
                    if (!IsStopped) activityChannels[client] = CreateActivityChannel(client);
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
            Task<byte[]> task = mapProvider();
            Log("Waiting for map...");
            byte[] mapBytes = await task;

            // TODO: This may happen a bit early - it seems possible for
            // events from a prior frame to get queued. Maybe just need to filter
            // them on the client side.
            // Start recording messages as soon as the map is saved,
            // while the map is sending
            StartQueuing(client);

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
            lock (queuedMessages)
            {
                for (int i = clients.Count - 1; i >= 0; i--)
                {
                    if (!clients[i].Connected)
                    {
                        queuedMessages.TryRemove(clients[i], out _);
                        RemoveActivity(clients[i]);
                        clients.RemoveAt(i);
                    }
                }
                // Share the join/close lock across enumeration and mutation.
                clients.ForEach(client =>
                {
                    if (sendNow)
                    {
                        SendEvent(client, message);
                    }
                    else
                    {
                        QueueOrSentToClient(client, message);
                    }
                });
            }
        }

        private void QueueOrSentToClient(ISocketStream client, JObject message)
        {
            if (!client.Connected) return;

            if (queuedMessages.TryGetValue(client, out ConcurrentQueue<JObject> queue))
            {
                queue.Enqueue(message);
            }
            else
            {
                SendEvent(client, message);
            }
        }

        public override void AbortSession(string reason)
        {
            try
            {
                lock (queuedMessages)
                    foreach (var client in clients.ToArray()) SendSessionFault(client, reason);
            }
            finally { Close(); }
        }

        public override void Close()
        {
            base.Close();
            foreach (var pair in activityChannels) pair.Value.Close();
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
