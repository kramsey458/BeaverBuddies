using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TimberNet
{
    // Session-scoped bearer tickets. Never persisted, logged, or included in replay/diagnostics.
    // All admissions are locked so closing the grace window cannot race a new claimant.
    public sealed class ReconnectTickets
    {
        readonly object gate = new object();
        readonly HashSet<string> members = new HashSet<string>();
        readonly Dictionary<ISocketStream, string> bindings = new Dictionary<ISocketStream, string>();
        readonly HashSet<ISocketStream> activated = new HashSet<ISocketStream>();
        readonly string? recoveryId, digest;
        bool sealedRoster;
        double admissionDeadline = double.PositiveInfinity;
        public void SetDeadline(double deadline) { lock (gate) admissionDeadline = deadline; }
        public ReconnectTickets() { }
        public ReconnectTickets(IEnumerable<string> tickets, string recoveryId, string digest)
        { foreach (var ticket in tickets) members.Add(ticket); this.recoveryId = recoveryId; this.digest = digest; }
        public string[] Export(bool includeDisconnected = false) { lock (gate) return includeDisconnected ? members.ToArray() : bindings.Where(p => p.Key.Connected && activated.Contains(p.Key)).Select(p => p.Value).ToArray(); }
        public int ConnectedCount { get { lock (gate) return bindings.Keys.Count(p => p.Connected); } }
        public int Seal() { lock (gate) { sealedRoster = true; members.IntersectWith(bindings.Where(p => p.Key.Connected).Select(p => p.Value)); return bindings.Keys.Count(p => p.Connected); } }
        public void Forget(ISocketStream peer)
        {
            lock (gate)
            {
                if (bindings.TryGetValue(peer, out var ticket)) members.Remove(ticket);
                bindings.Remove(peer); activated.Remove(peer);
            }
        }
        public bool IsRecovery => recoveryId != null;
        public bool HasClaim(ISocketStream peer) { lock (gate) return bindings.ContainsKey(peer); }
        public void Activate(ISocketStream peer) { lock (gate) if (bindings.ContainsKey(peer)) activated.Add(peer); }
        public bool Release(ISocketStream peer)
        {
            lock (gate)
            {
                bool wasMember = activated.Remove(peer);
                if (bindings.TryGetValue(peer, out var ticket))
                {
                    bindings.Remove(peer);
                    if (recoveryId != null) wasMember = true;
                    if (!wasMember && recoveryId == null) members.Remove(ticket);
                }
                return wasMember;
            }
        }
        public JObject Claim(ISocketStream peer, string ticket, bool allowNew)
        {
            lock (gate)
            {
                if (sealedRoster || ConnectionTelemetry.Now >= admissionDeadline) throw new IOException("The reconnect window has closed. Ask the host to rehost.");
                if (recoveryId == null)
                {
                    if (!allowNew || !string.IsNullOrEmpty(ticket)) throw new IOException("The host is preparing a snapshot or this session has ended.");
                    if (members.Count >= PlayerActivity.MaxPlayers) throw new IOException("The multiplayer session is full.");
                    byte[] bytes = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                    ticket = BitConverter.ToString(bytes).Replace("-", ""); members.Add(ticket);
                }
                else if (!members.Contains(ticket) || bindings.Any(p => p.Value == ticket && p.Key.Connected))
                    throw new IOException("This connection does not have an available place in the recovery session.");
                bindings[peer] = ticket;
                return new JObject { ["ticket"] = ticket, ["recoveryId"] = recoveryId, ["digest"] = digest };
            }
        }
    }
    public static class ReconnectHandshake
    {
        // Runs only after build/mod compatibility. Small plain JSON frames precede the snapshot.
        static JObject Exchange(ISocketStream peer, Func<JObject> exchange)
        {
            int state = 0;
            using var timer = new Timer(_ => { if (Interlocked.CompareExchange(ref state, 1, 0) == 0) peer.Close(); }, null, 5000, Timeout.Infinite);
            try
            {
                var result = exchange();
                if (Interlocked.CompareExchange(ref state, 2, 0) == 1) throw new IOException("Reconnect admission timed out.");
                return result;
            }
            catch { peer.Close(); throw; }
        }
        public static void Host(ISocketStream peer, ReconnectTickets tickets, bool allowNew) => Exchange(peer, () => {
            var request = Read(peer);
            if (request["ticket"]?.Type != JTokenType.String || ((string)request["ticket"]!).Length > 64) throw new IOException("Invalid reconnect ticket.");
            var reply = tickets.Claim(peer, (string)request["ticket"]!, allowNew); Write(peer, reply); return reply;
        });
        public static JObject Guest(ISocketStream peer, string? ticket) => Exchange(peer, () => {
            Write(peer, new JObject { ["ticket"] = ticket ?? "" }); var reply = Read(peer);
            if (reply["ticket"]?.Type != JTokenType.String || ((string)reply["ticket"]!).Length != 64) throw new IOException("Invalid reconnect admission.");
            return reply;
        });
        static JObject Read(ISocketStream peer)
        {
            var header = peer.ReadUntilComplete(4); if (BitConverter.IsLittleEndian) Array.Reverse(header);
            int length = BitConverter.ToInt32(header, 0); if (length < 2 || length > 2048) throw new IOException("Invalid reconnect frame.");
            return JObject.Parse(Encoding.UTF8.GetString(peer.ReadUntilComplete(length)));
        }
        static void Write(ISocketStream peer, JObject value)
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToString(Newtonsoft.Json.Formatting.None));
            var header = BitConverter.GetBytes(bytes.Length); if (BitConverter.IsLittleEndian) Array.Reverse(header);
            peer.Write(header, 0, header.Length); peer.Write(bytes, 0, bytes.Length);
        }
    }
}
