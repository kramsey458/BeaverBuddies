using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using TimberNet;

namespace BeaverBuddies.Steam
{
    /// <summary>
    /// Owns every Steam connection and drives them from <see cref="Pump"/>. All methods except those on
    /// <see cref="SteamLinkListener"/> must be called on the game thread.
    /// </summary>
    public sealed class SteamLinkManager
    {
        /// <summary>
        /// A guest connects right after joining the host's lobby, and the host may see the connection
        /// before its own view of the lobby has caught up, so a not-yet-listed guest gets a short grace period.
        /// </summary>
        public const double MembershipGraceSeconds = 5;

        sealed class PendingIncoming
        {
            public ulong Connection, Remote;
            public double Deadline;
        }

        readonly ISteamLinkBackend backend;
        readonly Func<double> clock;
        readonly Func<ulong, string> nameOf;
        readonly List<SteamLinkSocket> sockets = new List<SteamLinkSocket>();
        readonly List<PendingIncoming> pending = new List<PendingIncoming>();
        // Steam may report the same arrival more than once; each connection must be accepted only once.
        readonly HashSet<ulong> seenIncoming = new HashSet<ulong>();
        SteamLinkListener listener;
        Func<ulong, bool> isAllowed;
        ulong listen;

        public SteamLinkManager(ISteamLinkBackend backend, Func<double> clock, Func<ulong, string> nameOf)
        {
            this.backend = backend; this.clock = clock; this.nameOf = nameOf;
        }

        public int ConnectionCount => sockets.Count;
        public bool IsListening => listen != 0;

        /// <summary>Starts connecting to another player. Returns null if Steam refuses to start.</summary>
        public SteamLinkSocket Connect(ulong remoteSteamId)
        {
            ulong connection = backend.Connect(remoteSteamId);
            if (connection == 0)
            {
                Plugin.LogWarning("Steam could not start a connection to the host.");
                return null;
            }
            backend.Configure(connection);
            var socket = new SteamLinkSocket(backend, connection, remoteSteamId, nameOf(remoteSteamId), clock, false);
            sockets.Add(socket);
            Plugin.Log($"Steam link to {socket.Name}: connecting...");
            return socket;
        }

        internal void BeginListening(SteamLinkListener owner, Func<ulong, bool> allow)
        {
            if (listen != 0)
            {
                // A rehost normally stops the old listener first, but that stop can be queued behind this start.
                // The newest host wins rather than silently disabling Steam invites.
                Plugin.LogWarning("Replacing a Steam listener that was never stopped.");
                EndListening(listener);
            }
            listen = backend.CreateListenSocket();
            if (listen == 0) throw new IOException("Steam could not open a listening connection. Make sure Steam is running and online.");
            listener = owner; isAllowed = allow;
            seenIncoming.Clear();
            Plugin.Log("Steam is listening for players.");
        }

        internal void EndListening(SteamLinkListener owner)
        {
            if (!ReferenceEquals(listener, owner)) return;
            foreach (var p in pending) backend.Close(p.Connection, SteamEndReasons.NotAccepting, "The host stopped hosting.", false);
            pending.Clear();
            if (listen != 0) backend.CloseListenSocket(listen);
            listen = 0; listener = null; isAllowed = null;
            seenIncoming.Clear();
            Plugin.Log("Steam stopped listening for players.");
        }

        /// <summary>A player is connecting to our listen socket. Called from Steam's status callback.</summary>
        public void OnIncomingConnection(ulong listenHandle, ulong connection, ulong remote)
        {
            if (listen == 0 || listenHandle != listen)
            {
                backend.Close(connection, SteamEndReasons.NotAccepting, "The host is not accepting players.", false);
                return;
            }
            if (!seenIncoming.Add(connection)) return;
            if (Allowed(remote)) AcceptIncoming(connection, remote);
            else
            {
                pending.Add(new PendingIncoming { Connection = connection, Remote = remote, Deadline = clock() + MembershipGraceSeconds });
                Plugin.Log($"Steam connection from {nameOf(remote)} is waiting for them to appear in the lobby.");
            }
        }

        bool Allowed(ulong remote)
        {
            try { return isAllowed != null && isAllowed(remote); }
            catch (Exception e) { Plugin.LogWarning("Steam lobby check failed: " + e.Message); return false; }
        }

        void AcceptIncoming(ulong connection, ulong remote)
        {
            if (!backend.Accept(connection))
            {
                Plugin.LogWarning($"Steam could not accept the connection from {nameOf(remote)}.");
                backend.Close(connection, SteamEndReasons.Rejected, "Accepting the connection failed.", false);
                return;
            }
            backend.Configure(connection);
            var socket = new SteamLinkSocket(backend, connection, remote, nameOf(remote), clock, true);
            sockets.Add(socket);
            Plugin.Log($"Steam link to {socket.Name}: accepted, waiting for it to finish connecting...");
        }

        /// <summary>Called every frame on the game thread.</summary>
        public void Pump()
        {
            double now = clock();
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                if (Allowed(p.Remote))
                {
                    pending.RemoveAt(i);
                    AcceptIncoming(p.Connection, p.Remote);
                }
                else if (now > p.Deadline)
                {
                    pending.RemoveAt(i);
                    Plugin.LogWarning($"Steam rejected {nameOf(p.Remote)}: they never joined the host's lobby.");
                    backend.Close(p.Connection, SteamEndReasons.Rejected, "You are not in the host's lobby.", false);
                }
            }

            for (int i = sockets.Count - 1; i >= 0; i--)
            {
                var socket = sockets[i];
                bool done = socket.Pump(now);
                if (socket.TakeAnnouncement()) listener?.Deliver(socket);
                if (done) sockets.RemoveAt(i);
            }
        }

        /// <summary>Closes everything, for when the game is exiting.</summary>
        public void Shutdown()
        {
            foreach (var p in pending) backend.Close(p.Connection, SteamEndReasons.SessionEnded, "game closing", false);
            pending.Clear();
            foreach (var socket in sockets) socket.Shutdown();
            sockets.Clear();
            if (listen != 0) { backend.CloseListenSocket(listen); listen = 0; }
        }
    }

    /// <summary>
    /// TimberNet's view of "players connecting over Steam". Accepted connections appear from
    /// <see cref="AcceptClient"/> once they are fully connected.
    /// </summary>
    public sealed class SteamLinkListener : ISocketListener
    {
        readonly SteamLinkManager manager;
        readonly Action<Action> runOnGameThread;
        readonly Func<ulong, bool> allow;
        readonly BlockingCollection<SteamLinkSocket> ready = new BlockingCollection<SteamLinkSocket>();

        /// <param name="runOnGameThread">Runs an action on the game thread (immediately if already there).</param>
        /// <param name="allow">Whether a player may connect, normally "is in the host's lobby".</param>
        public SteamLinkListener(SteamLinkManager manager, Action<Action> runOnGameThread, Func<ulong, bool> allow)
        {
            this.manager = manager; this.runOnGameThread = runOnGameThread; this.allow = allow;
        }

        public void Start()
        {
            Exception error = null;
            runOnGameThread(() =>
            {
                try { manager.BeginListening(this, allow); }
                catch (Exception e) { error = e; }
            });
            if (error != null) throw error;
        }

        public ISocketStream AcceptClient()
        {
            // Throws InvalidOperationException once stopped, the same way a closed TCP listener throws.
            return ready.Take();
        }

        public void Stop()
        {
            try { ready.CompleteAdding(); } catch (ObjectDisposedException) { }
            runOnGameThread(() => manager.EndListening(this));
        }

        internal void Deliver(SteamLinkSocket socket)
        {
            try { ready.Add(socket); }
            catch (InvalidOperationException) { socket.Close(); }
        }
    }
}
