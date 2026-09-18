using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace TimberNet
{
    public class MultiSocketListener : ISocketListener
    {
        readonly List<ISocketListener> listeners;
        readonly BlockingCollection<ISocketStream> accepted = new BlockingCollection<ISocketStream>();
        volatile bool stopped;
        readonly object stopGate = new object();
        public IEnumerable<ISocketListener> Listeners => listeners;
        public MultiSocketListener(params ISocketListener[] listeners) => this.listeners = listeners.ToList();
        public ISocketStream AcceptClient()
        {
            try { return accepted.Take(); }
            catch (InvalidOperationException error) { throw new IOException("Listener stopped.", error); }
        }
        public void Start()
        {
            try
            {
                foreach (var listener in listeners) listener.Start();
                foreach (var listener in listeners)
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            while (!stopped)
                            {
                                var stream = listener.AcceptClient();
                                if (stopped) { stream.Close(); return; }
                                try { accepted.Add(stream); }
                                catch (InvalidOperationException) { stream.Close(); return; }
                            }
                        }
                        catch { if (!stopped) Stop(); }
                    });
            }
            catch { Stop(); throw; }
        }
        public void Stop()
        {
            lock (stopGate)
            {
                if (stopped) return;
                stopped = true;
                accepted.CompleteAdding(); // Wake the outer TimberServer accept worker too.
                foreach (var listener in listeners)
                    try { listener.Stop(); } catch { }
                while (accepted.TryTake(out var stream)) stream.Close();
            }
        }
        public T GetListener<T>() => (T)(object)listeners.FirstOrDefault(listener => listener is T);
    }
}
