using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace TimberNet
{
    // One writer per connection. Limits include the frame currently being written.
    public sealed class OrderedSender
    {
        readonly object gate = new object();
        readonly Queue<(string Text, TaskCompletionSource<bool> Done)> queue = new Queue<(string, TaskCompletionSource<bool>)>();
        readonly Action<string> write;
        readonly Action<Exception> failed;
        readonly int maxCharacters, maxMessages;
        int characters, messages;
        bool running, stopped;
        Task tail = Task.CompletedTask;
        readonly Dictionary<int, string> latest = new Dictionary<int, string>();
        readonly Queue<int> latestKeys = new Queue<int>();

        // Disposable presentation traffic has its own small budget, and yields to all reliable commands.
        // Replacing a pending cursor never drops or reorders a gameplay command.
        public void EnqueueLatest(int key, string text)
        {
            lock (gate)
            {
                if (stopped || text.Length > 4096) return;
                if (!latest.ContainsKey(key))
                {
                    if (latest.Count >= PlayerActivity.MaxPlayers) return;
                    latestKeys.Enqueue(key);
                }
                latest[key] = text;
                if (!running) { running = true; _ = Task.Run(Pump); }
            }
        }

        public OrderedSender(Action<string> write, Action<Exception> failed,
            int maxCharacters = 8 * 1024 * 1024, int maxMessages = 2048)
        { this.write = write; this.failed = failed; this.maxCharacters = maxCharacters; this.maxMessages = maxMessages; }

        public Task Enqueue(string text)
        {
            lock (gate)
            {
                if (stopped) throw new IOException("Connection sender has stopped.");
                if (messages >= maxMessages || text.Length > maxCharacters - characters)
                    throw new IOException("Peer cannot keep up: multiplayer send queue limit exceeded.");
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                queue.Enqueue((text, done)); characters += text.Length; messages++;
                tail = done.Task;
                if (!running) { running = true; _ = Task.Run(Pump); }
                return tail;
            }
        }

        public Task Drain() { lock (gate) return tail; }
        public int PendingMessages { get { lock (gate) return messages; } }
        public void Stop()
        {
            lock (gate)
            {
                stopped = true;
                latest.Clear(); latestKeys.Clear();
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue(); characters -= item.Text.Length; messages--;
                    item.Done.TrySetCanceled();
                }
            }
        }

        void Pump()
        {
            while (true)
            {
                (string Text, TaskCompletionSource<bool> Done) item;
                bool disposable = false;
                lock (gate)
                {
                    if (stopped || (queue.Count == 0 && latestKeys.Count == 0)) { running = false; return; }
                    if (queue.Count != 0) item = queue.Dequeue();
                    else
                    {
                        int key = latestKeys.Dequeue();
                        item = (latest[key], null!); latest.Remove(key); disposable = true;
                    }
                }
                try { write(item.Text); }
                catch (Exception error)
                {
                    item.Done?.TrySetCanceled(); Stop(); failed(error); return;
                }
                finally { if (!disposable) lock (gate) { characters -= item.Text.Length; messages--; } }
                // Publish completion only after releasing its queue budget.
                item.Done?.TrySetResult(true);
            }
        }
    }
}
