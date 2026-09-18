using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TimberNet
{
    // TODO: I should create a method here that attempts to operate on a steam,
    // and handles errors uniformly if it fails.
    // Pretty much any error means the session is over, but the game should show
    // the error rather than crashing.
    public abstract class TimberNetBase
    {
        public const int HEADER_SIZE = 4;
        readonly ConcurrentDictionary<ISocketStream, OrderedSender> senders = new ConcurrentDictionary<ISocketStream, OrderedSender>();
        readonly ConcurrentQueue<(ISocketStream Stream, JObject Message)> controls = new ConcurrentQueue<(ISocketStream, JObject)>();
        public event Action<ISocketStream, JObject>? OnControl;
        volatile bool draining;
        protected bool CloseDeferred => draining;
        public Task FlushAsync() => Task.WhenAll(senders.Values.Select(s => s.Drain()));
        public int PendingReliableMessages => senders.Values.Sum(s => s.PendingMessages);
        public void CloseAfterFlush()
        {
            if (draining || IsStopped) return;
            draining = true; isStopped = true;
            _ = Task.Run(async () =>
            {
                try { await Task.WhenAny(FlushAsync(), Task.Delay(3000)); }
                finally { draining = false; Close(); }
            });
        }
        public static JObject Control(string command) => new JObject { [TYPE_KEY] = "SessionControl", ["command"] = command };
        public virtual void SendControl(JObject message) { }
        readonly ActivityMailbox activityInbox = new ActivityMailbox();
        public event Action<PlayerActivity>? OnActivity;
        public virtual void SendActivity(PlayerActivity activity) { }
        protected virtual void QueueActivity(ISocketStream stream, PlayerActivity activity) => activityInbox.Put(activity, ActivityMailbox.Now);
        protected virtual void ProcessActivity(PlayerActivity activity) => OnActivity?.Invoke(activity);
        protected void SendActivityTo(ISocketStream stream, PlayerActivity activity)
        {
            if (IsStopped || !stream.Connected) return;
            SenderFor(stream).EnqueueLatest(activity.PlayerId, activity.ToJson().ToString(Newtonsoft.Json.Formatting.None));
        }
        OrderedSender SenderFor(ISocketStream stream) => senders.GetOrAdd(stream, socket => new OrderedSender(
            text => SendDataWithLength(socket, MessageToBuffer(text)),
            error => HandleConnectionFailure(socket, "Error sending event: " + error.Message)));

        public string? CompatibilityIdentity { get; set; }
        CompatibilityAdmission? admission;
        DateTime? loadedAt;
        public string? LoadedCompatibilityIdentity { get; private set; }
        public string? SnapshotDigest { get; protected set; }
        public bool RequiresLoadedCompatibility => CompatibilityIdentity?.StartsWith("{", StringComparison.Ordinal) == true;
        protected virtual IEnumerable<object> AdmissionPeers => Array.Empty<object>();
        public bool CompatibilityVerified => !RequiresLoadedCompatibility || (admission?.Ready(AdmissionPeers) == true);
        CompatibilityAdmission Admission => admission ??= new CompatibilityAdmission(this is TimberServer,
            (peer, message) => { if (peer is ISocketStream stream) SendEvent(stream, message); else SendControl(message); },
            reason => { OnSessionFault?.Invoke(reason); if (!IsStopped) AbortSession(reason); });
        public void SubmitLoadedCompatibility(string identity)
        {
            if (!RequiresLoadedCompatibility) return;
            loadedAt = DateTime.UtcNow;
            LoadedCompatibilityIdentity = identity;
            Admission.Loaded(identity);
        }
        public Func<bool>? DetailedLoggingEnabled { get; set; }
        protected bool ShouldLogDetails => DetailedLoggingEnabled?.Invoke() == true;
        public event MessageReceived? OnSessionFault;
        private readonly ConcurrentQueue<string> sessionFaults = new ConcurrentQueue<string>();
        public virtual void AbortSession(string reason) { Close(); }
        protected void SendSessionFault(ISocketStream stream, string reason)
        {
            SendEvent(stream, new JObject { [TYPE_KEY] = "SessionFault", [TICKS_KEY] = TickCount, ["reason"] = reason });
        }
        public const string TICKS_KEY = "ticksSinceLoad";
        public const string TYPE_KEY = "type";
        public const string SET_STATE_EVENT = "SetState";
        public const string HEARTBEAT_EVENT = "Heartbeat";
        public const int MAX_BUFFER_SIZE = 8192 * 4; // 32K

        public delegate void MessageReceived(string message);
        public delegate void MapReceived(byte[] mapBytes);

        public event MessageReceived? OnLog;
        public event MessageReceived? OnError;
        public event MapReceived? OnMapReceived;

        private readonly ConcurrentQueue<JObject> receivedEventQueue = new ConcurrentQueue<JObject>();
        private readonly ConcurrentQueue<string> logQueue = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> errorQueue = new ConcurrentQueue<string>();
        private byte[]? mapBytes = null;

        private volatile bool isStopped;
        public bool IsStopped => isStopped;

        public int Hash { get; private set; } = 17;

        public int TickCount { get; private set; }

        public int TicksBehind
        {
            get
            {
                if (receivedEvents.Count == 0)
                    return 0;
                return Math.Max(0, GetTick(receivedEvents.Last()) - TickCount);
            }
        }

        public bool Started { get; private set; }

        public virtual bool ShouldTick => Started && !IsStopped && CompatibilityVerified;

        protected List<JObject> receivedEvents = new List<JObject>();

        public virtual void Close()
        {
            if (draining) return;
            isStopped = true;
            activityInbox.Clear();
            foreach (var sender in senders.Values) sender.Stop();
        }

        public TimberNetBase()
        {
            Log("Started");
        }

        protected void Log(string message)
        {
            Log(message, TickCount, Hash);
        }

        protected void Log(string message, int ticks, int hash)
        {
            // Should be threadsafe
            OnLog?.Invoke($"T{ticks.ToString("D4")} [{hash.ToString("X8")}] : {message}");
            //logQueue.Enqueue($"T{ticks.ToString("D4")} [{hash.ToString("X8")}] : {message}");
        }

        public virtual void Start()
        {
            Started = true;
        }

        public static int GetTick(JObject message)
        {
            if (message[TICKS_KEY] == null)
                throw new Exception($"Message does not contain {TICKS_KEY} key");
            return message[TICKS_KEY]!.ToObject<int>();
        }

        public static string GetType(JObject message)
        {
            var type = message["type"];
            if (type == null)
                throw new Exception($"Message does not contain type key");
            return type.ToObject<string>()!;
        }

        protected void InsertInScript(JObject message, List<JObject> script)
        {
            int tick = GetTick(message);
            // The common path is already in tick order. Equal ticks append,
            // preserving the host's action order.
            if (script.Count == 0 || GetTick(script[script.Count - 1]) <= tick)
            {
                script.Add(message);
                return;
            }
            int low = 0, high = script.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (GetTick(script[middle]) <= tick) low = middle + 1;
                else high = middle;
            }
            script.Insert(low, message);
        }

        public static List<T> PopEventsForTick<T>(int tick, List<T> events, Func<T, int> getTick)
        {
            int count = 0;
            while (count < events.Count && getTick(events[count]) <= tick) count++;
            if (count == 0) return new List<T>();
            var ready = events.GetRange(0, count);
            // Shift the remaining backlog once, rather than once per event.
            events.RemoveRange(0, count);
            return ready;
        }

        private List<JObject> PopEventsToProcess(List<JObject> events)
        {
            if (events.Count == 0) return new List<JObject>();
            JObject firstEvent = events[0];
            int firstEventTick = GetTick(firstEvent);
            if (firstEventTick < TickCount)
                Log($"Warning: late event {GetType(firstEvent)}: {firstEventTick} < {TickCount}");

            return PopEventsForTick(TickCount, events, GetTick);
        }

        /**
         * Process an event that the user initiated.
         */
        public virtual void DoUserInitiatedEvent(JObject message)
        {
            AddEventToHash(message);
        }

        /**
        * Process a validated event from a peer that is ready to happen on
        * the Update() thread.
        */
        protected virtual void ProcessReceivedEvent(JObject message)
        {
        }

        protected void AddEventToHash(JObject message)
        {
            if (GetType(message) == SET_STATE_EVENT)
            {
                Hash = message["hash"]!.ToObject<int>();
            }
            else
            {
                AddToHash(message.ToString());
            }
            if (ShouldLogDetails) Log($"Event: {GetType(message)}");
        }

        protected void SendLength(ISocketStream stream, int length)
        {
            byte[] buffer = BitConverter.GetBytes(length);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(buffer);
            stream.Write(buffer, 0, buffer.Length);
        }

        protected void SendDataWithLength(ISocketStream stream, byte[] data)
        {
            // A frame includes both its header and every payload chunk. Join
            // workers and the game thread can otherwise interleave their writes.
            lock (stream)
            {
                SendLength(stream, data.Length);
                int chunkSize = stream.MaxChunkSize;
                // How long to sleep between chunks (may be 0)
                int sleepMS = stream.MaxChunkSize * 1000 / stream.MaxBytesPerSecond;
                for (int i = 0; i < data.Length; i += chunkSize)
                {
                    if (i != 0)
                    {
                        Thread.Sleep(sleepMS);
                    }
                    int length = Math.Min(chunkSize, data.Length - i);
                    stream.Write(data, i, length);
                }
            }
        }

        protected void SendEvent(ISocketStream client, JObject message)
        {
            if (ShouldLogDetails) Log($"Sending: {GetType(message)}");
            SendSerializedEvent(client, message.ToString(Newtonsoft.Json.Formatting.None));
        }

        protected void SendSerializedEvent(ISocketStream client, string json)
        {
            if (IsStopped) return;
            try
            {
                // Snapshot JSON before returning to callers; compression and writes run on the worker.
                SenderFor(client).Enqueue(json);
            }
            catch (Exception error) { HandleConnectionFailure(client, error.Message); }
        }

        protected virtual void HandleConnectionFailure(ISocketStream stream, string message)
        {
            // After a partial write the framing cannot safely be reused.
            if (senders.TryRemove(stream, out var sender)) sender.Stop();
            stream.Close();
            Log(message);
        }

        protected void QueueError(string message) => errorQueue.Enqueue(message);

        protected bool TryReadLength(ISocketStream stream, out int length)
        {
            byte[] headerBuffer;
            try
            {
                headerBuffer = stream.ReadUntilComplete(HEADER_SIZE);
            }
            catch
            {
                length = 0;
                return false;
            }
            if (BitConverter.IsLittleEndian)
                Array.Reverse(headerBuffer);

            length = BitConverter.ToInt32(headerBuffer, 0);
            return true;
        }

        protected void StartListening(ISocketStream client, bool isClient)
        {
            try
            {
                ReceiveMessages(client, isClient);
            }
            catch (Exception e)
            {
                if (!IsStopped) HandleConnectionFailure(client, $"Error receiving data: {e.Message}");
            }
            finally
            {
                if (!IsStopped) HandleConnectionFailure(client, "The multiplayer connection was closed.");
                client.Close();
            }
        }

        private void ReceiveMessages(ISocketStream client, bool isClient)
        {
            //Log("Client connected");
            int messageCount = 0;
            while (client.Connected && !IsStopped)
            {
                if (!TryReadLength(client, out int messageLength)) break;
                if (messageLength < 0 || messageLength > 256 * 1024 * 1024) throw new IOException("Invalid multiplayer frame length.");

                // First message is always the file
                if (messageCount == 0 && isClient)
                {
                    if (messageLength == 0)
                    {
                        ReadErrorMessage(client);
                        return;
                    }

                    ReceiveFile(client, messageLength);
                    messageCount++;
                    continue;
                }

                if (messageLength == 0)
                {
                    Log("Received message of length 0; aborting listen");
                    break;
                }

                //Log($"Starting to read {messageLength} bytes");
                // TODO: How should this fail and not hang if map stops sending?
                byte[] buffer = client.ReadUntilComplete(messageLength);

                string message = BufferToStringMessage(buffer);
                var control = JObject.Parse(message);
                if ((string?)control[TYPE_KEY] == PlayerActivity.MessageType)
                {
                    if (message.Length <= 4096 && PlayerActivity.TryParse(control, out var activity)) QueueActivity(client, activity!);
                    messageCount++;
                    continue;
                }
                if ((string?)control[TYPE_KEY] == "SessionFault")
                {
                    string reason = (string?)control["reason"] ?? "A peer could not replay a multiplayer action.";
                    if (reason.Length > 2048) reason = reason.Substring(0, 2048);
                    sessionFaults.Enqueue("Peer stopped the session: " + reason);
                    return;
                }
                if ((string?)control[TYPE_KEY] == "SessionControl")
                {
                    controls.Enqueue((client, control));
                    messageCount++;
                    continue;
                }
                //Log($"Queuing message of length {messageLength} bytes");
                receivedEventQueue.Enqueue(control);
                messageCount++;
            }
        }

        protected byte[] MessageToBuffer(JObject message)
        {
            string json = message.ToString(Newtonsoft.Json.Formatting.None);
            return MessageToBuffer(json);
        }

        protected byte[] MessageToBuffer(string message)
        {
            return CompressionUtils.Compress(message);
        }

        protected string BufferToStringMessage(byte[] buffer)
        {
            return CompressionUtils.Decompress(buffer);
        }

        private void ReadErrorMessage(ISocketStream stream)
        {
            if (TryReadLength(stream, out int length))
            {
                byte[] bytes = stream.ReadUntilComplete(length);
                string message = BufferToStringMessage(bytes);
                HandleConnectionFailure(stream, message);
            }
        }

        public static int CombineHash(int h1, int h2)
        {
            return h1 * 31 + h2;
        }

        private void AddToHash(string str)
        {
            AddToHash(Encoding.UTF8.GetBytes(str));
        }

        private void AddToHash(byte[] bytes)
        {
            Hash = CombineHash(Hash, GetHashCode(bytes));
        }

        public static int GetHashCode(byte[] bytes)
        {
            int code = 0;
            foreach (byte b in bytes)
            {
                code = CombineHash(code, b);
            }
            return code;
        }

        private void AddFileToHash(byte[] bytes)
        {
            AddToHash(bytes);
        }

        private void ReceiveFile(ISocketStream stream, int messageLength)
        {
            byte[] mapBytes = stream.ReadUntilComplete(messageLength);
            SnapshotDigest = DigestSnapshot(mapBytes);
            AddFileToHash(mapBytes);
            Log($"Received map with length {mapBytes.Length} and Hash: {GetHashCode(mapBytes).ToString("X8")}");
            this.mapBytes = mapBytes;
        }

        private void ProcessReceivedEventsQueue()
        {
            while (receivedEventQueue.TryDequeue(out JObject? message))
            {
                try
                {
                    ReceiveEvent(message);
                } catch (Exception e)
                {
                    Log($"Error receiving event: {e.Message}");
                }
            }
        }

        /**
         * Called when an event is received from a connected Net
         * and ready to be added to the queue for processing.
         */
        protected virtual void ReceiveEvent(JObject message)
        {
            InsertInScript(message, receivedEvents);
        }

        private void ProcessLogs()
        {
            while (logQueue.TryDequeue(out string? log))
            {
                OnLog?.Invoke(log);
            }
        }

        private void ProcessReceivedMap()
        {
            if (mapBytes == null) return;
            OnMapReceived?.Invoke(mapBytes);
            mapBytes = null;
        }

        /**
         * Updates, processing queued logs, maps and events.
         */
        public virtual void Update()
        {
            ProcessLogs();
            while (controls.TryDequeue(out var control))
                if (!RequiresLoadedCompatibility || !Admission.Receive(control.Stream, control.Message)) OnControl?.Invoke(control.Stream, control.Message);
            if (!IsStopped && loadedAt.HasValue && !CompatibilityVerified && DateTime.UtcNow - loadedAt.Value > TimeSpan.FromMinutes(10))
            {
                loadedAt = null;
                const string reason = "Timed out waiting for all players to finish loading and verify mod settings. Rehost and try again.";
                OnSessionFault?.Invoke(reason);
                if (!IsStopped) AbortSession(reason);
            }
            while (sessionFaults.TryDequeue(out string? fault)) OnSessionFault?.Invoke(fault);
            // UI subscribers must only run on the caller's update thread.
            while (errorQueue.TryDequeue(out string? error)) OnError?.Invoke(error);
            if (!Started || IsStopped) return;
            foreach (var activity in activityInbox.Take(ActivityMailbox.Now)) ProcessActivity(activity);
            ProcessReceivedMap();
            ProcessReceivedEventsQueue();

        }

        private List<JObject> FilterEvents(List<JObject> events)
        {
            return events.Where(ShouldReadEvent).ToList();
        }

        private bool ShouldReadEvent(JObject message)
        { 
            string type = GetType(message);
            return !(type == SET_STATE_EVENT || type == HEARTBEAT_EVENT);
        }

        /**
         * Reads received events that should be processed by the game
         * and deletes and returns.
         * Will call update before processing events.
         */
        public virtual List<JObject> ReadEvents(int ticksSinceLoad)
        {
            //if (ticksSinceLoad != TickCount) Log($"Setting ticks from {TickCount} to {ticksSinceLoad}");
            TickCount = ticksSinceLoad;
            Update();
            if (IsStopped || !CompatibilityVerified) return new List<JObject>();
            List<JObject> toProcess = PopEventsToProcess(receivedEvents);
            toProcess.ForEach(e => ProcessReceivedEvent(e));
            return FilterEvents(toProcess);
        }

        public bool HasEventsForTick(int tickSinceLoad)
        {
            Update();
            return !IsStopped && receivedEvents.Any(e => GetTick(e) == tickSinceLoad);
        }
        protected static string DigestSnapshot(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
