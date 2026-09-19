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

namespace TimberNet
{
    // TODO: I should create a method here that attempts to operate on a steam,
    // and handles errors uniformly if it fails.
    // Pretty much any error means the session is over, but the game should show
    // the error rather than crashing.
    public abstract class TimberNetBase
    {
        public const int HEADER_SIZE = 4;
        public string? CompatibilityIdentity { get; set; }
        /// <summary>Optional information sent to the other player once builds match (see CompatibilityHandshake).</summary>
        public string? CompatibilityAdvisory { get; set; }
        public delegate void PeerAdvisoryReceived(string? peerName, string advisory);
        /// <summary>Raised on the update thread with the other player's advisory after a successful handshake.</summary>
        public event PeerAdvisoryReceived? OnPeerAdvisory;
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
        private readonly ConcurrentQueue<(string? Peer, string Advisory)> peerAdvisories = new ConcurrentQueue<(string?, string)>();
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

        public virtual bool ShouldTick => Started && !IsStopped;

        protected List<JObject> receivedEvents = new List<JObject>();

        public virtual void Close()
        {
            isStopped = true;
        }

        // ---- Player activity (presentation only) ----
        // Activity frames never touch receivedEventQueue, the replay script or Hash.

        private readonly ActivityMailbox activityMailbox = new ActivityMailbox();

        /// <summary>Takes the newest state of each remote player; call from the game thread.</summary>
        public PlayerActivity[] TakeActivity() => activityMailbox.Take(ActivityMailbox.Now);

        public void ClearActivity() => activityMailbox.Clear();

        /// <summary>Shares this player's activity. The sender's id is ignored: the host assigns identity.</summary>
        public virtual void SendActivity(PlayerActivity activity) { }

        // The map is always the first frame a client reads, so nothing else may be written
        // to the host before this point.
        protected virtual void OnMapFrameReceived(ISocketStream stream) { }

        protected virtual void HandleActivity(ISocketStream source, PlayerActivity activity)
        {
            activityMailbox.Put(activity, ActivityMailbox.Now);
        }

        // ---- Connection status feed (presentation only) ----
        // Ping probes and the player roster share the activity lane: never replayed, never hashed.

        protected virtual void HandleStatusFrame(ISocketStream source, string type, JObject message) { }

        /// <summary>Called on the game thread from <see cref="Update"/> while the session is running.</summary>
        protected virtual void OnUpdate() { }

        /// <summary>A snapshot of who is connected and how well, for display.</summary>
        public virtual NetworkStatus GetNetworkStatus() => NetworkStatus.None(IsStopped);

        protected ActivityChannel CreateActivityChannel(ISocketStream stream) =>
            new ActivityChannel(stream, WriteActivityFrame, HandleConnectionFailure);

        private void WriteActivityFrame(ISocketStream stream, JObject message)
        {
            SendDataWithLength(stream, MessageToBuffer(message));
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
            if (ShouldLogDetails) Log($"Sending: {GetType(message)} for tick {GetTick(message)}");
            byte[] buffer = MessageToBuffer(message);

            try
            {
                SendDataWithLength(client, buffer);
            } catch (Exception e)
            {
                HandleConnectionFailure(client, $"Error sending event: {e.Message}");
            }
        }

        protected virtual void HandleConnectionFailure(ISocketStream stream, string message)
        {
            // Read the reason before closing: closing may replace it with a generic one.
            message = DescribeFailure(stream, message);
            // After a partial write the framing cannot safely be reused.
            stream.Close();
            Log(message);
        }

        /// <summary>Adds the transport's own explanation (for example Steam's end reason), if it has one.</summary>
        protected static string DescribeFailure(ISocketStream stream, string message)
        {
            string? reason = (stream as IFailureDescriber)?.FailureReason;
            return string.IsNullOrEmpty(reason) || message.Contains(reason) ? message : message + "\n" + reason;
        }

        protected void QueueError(string message) => errorQueue.Enqueue(message);

        /// <summary>
        /// Runs the build check with this player's identity, and swaps advisories if one is set. The other
        /// player's advisory is delivered to OnPeerAdvisory on the next Update.
        /// </summary>
        protected void RunCompatibilityHandshake(ISocketStream stream, bool server)
        {
            string? remote = CompatibilityHandshake.Run(stream, CompatibilityIdentity!, server, advisory: CompatibilityAdvisory);
            if (remote != null) peerAdvisories.Enqueue((stream.Name, remote));
        }

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

                // First message is always the file
                if (messageCount == 0 && isClient)
                {
                    if (messageLength == 0)
                    {
                        ReadErrorMessage(client);
                        return;
                    }

                    ReceiveFile(client, messageLength);
                    OnMapFrameReceived(client);
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
                    // Optional display data: a malformed frame is dropped, never fatal to the session.
                    if (PlayerActivity.TryParse(control, out PlayerActivity? activity) && activity != null)
                        HandleActivity(client, activity);
                    continue;
                }
                string? controlType = (string?)control[TYPE_KEY];
                if (StatusFrames.IsStatusType(controlType))
                {
                    // Optional display data: a bad frame is ignored, never fatal to the session.
                    try { HandleStatusFrame(client, controlType!, control); }
                    catch (Exception e) { Log("Ignoring a bad status frame: " + e.Message); }
                    continue;
                }
                if ((string?)control[TYPE_KEY] == "SessionFault")
                {
                    sessionFaults.Enqueue("A peer could not replay a multiplayer action. Reload a known-good save before rehosting.");
                    return;
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
            byte[] received = mapBytes;
            mapBytes = null;
            if (!NotifyEach(OnMapReceived, handler => ((MapReceived)handler)(received), "the received save"))
            {
                // Without this the guest would sit in the menu with no explanation.
                QueueError("The save from the host arrived but could not be loaded. See Player.log for details.");
            }
        }

        /// <summary>
        /// Calls every subscriber by itself and never lets one throw into the caller. These handlers show
        /// dialogs and load scenes, and they are reached from the game's update loop: in 1.0.4 one of them
        /// threw while reporting a lost connection and the uncaught exception crashed the game. A failing
        /// handler is logged; the others still run. Returns false if any subscriber failed.
        /// </summary>
        private bool NotifyEach(Delegate? handlers, Action<Delegate> call, string what)
        {
            if (handlers == null) return true;
            bool allSucceeded = true;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try { call(handler); }
                catch (Exception e)
                {
                    allSucceeded = false;
                    Log($"Ignoring an error in a handler for {what}: {e}");
                }
            }
            return allSucceeded;
        }

        /**
         * Updates, processing queued logs, maps and events.
         */
        public void Update()
        {
            ProcessLogs();
            while (sessionFaults.TryDequeue(out string? fault))
            {
                string message = fault;
                NotifyEach(OnSessionFault, handler => ((MessageReceived)handler)(message), "a session fault");
            }
            // UI subscribers must only run on the caller's update thread.
            while (errorQueue.TryDequeue(out string? error))
            {
                string message = error;
                NotifyEach(OnError, handler => ((MessageReceived)handler)(message), "a connection error");
            }
            while (peerAdvisories.TryDequeue(out var advisory))
            {
                // Optional information: a handler that fails must never end the session.
                try { OnPeerAdvisory?.Invoke(advisory.Peer, advisory.Advisory); }
                catch (Exception e) { Log("Ignoring an error handling the other player's compatibility information: " + e.Message); }
            }
            if (!Started || IsStopped) return;
            ProcessReceivedMap();
            ProcessReceivedEventsQueue();
            OnUpdate();

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
            if (IsStopped) return new List<JObject>();
            List<JObject> toProcess = PopEventsToProcess(receivedEvents);
            toProcess.ForEach(e => ProcessReceivedEvent(e));
            return FilterEvents(toProcess);
        }

        public bool HasEventsForTick(int tickSinceLoad)
        {
            Update();
            return !IsStopped && receivedEvents.Any(e => GetTick(e) == tickSinceLoad);
        }
    }
}
