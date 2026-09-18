using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace TimberNet
{
    // Main-thread, per-connection admission. Profiles never enter the replay script.
    public sealed class CompatibilityAdmission
    {
        readonly bool host;
        readonly Action<object?, JObject> send;
        readonly Action<string> fail;
        readonly Dictionary<object, string> remote = new Dictionary<object, string>();
        readonly HashSet<object> accepted = new HashSet<object>();
        string? local;
        bool clientAccepted, failed;
        public CompatibilityAdmission(bool host, Action<object?, JObject> send, Action<string> fail)
        { this.host = host; this.send = send; this.fail = fail; }
        public bool Ready(IEnumerable<object> peers)
        {
            if (failed || local == null) return false;
            if (!host) return clientAccepted;
            foreach (var peer in peers) if (!accepted.Contains(peer)) return false;
            return true;
        }
        public void Loaded(string identity)
        {
            if (local != null) throw new InvalidOperationException("Loaded compatibility was already submitted.");
            CompatibilityProfile.Parse(identity);
            local = identity;
            if (host) { foreach (var peer in new List<object>(remote.Keys)) Check(peer); }
            else send(null, new JObject { [TimberNetBase.TYPE_KEY] = "SessionControl", ["command"] = "CompatibilityLoaded", ["profile"] = identity });
        }
        public bool Receive(object peer, JObject message)
        {
            string? command = (string?)message["command"];
            if (command != "CompatibilityLoaded" && command != "CompatibilityAccepted" && command != "CompatibilityRejected") return false;
            if (failed) return true;
            try
            {
                if (host && command == "CompatibilityLoaded")
                {
                    string profile = (string?)message["profile"] ?? throw new IOException("Missing loaded mod profile.");
                    CompatibilityProfile.Parse(profile);
                    if (remote.ContainsKey(peer)) throw new IOException("Duplicate loaded mod profile.");
                    remote.Add(peer, profile); Check(peer);
                }
                else if (!host && command == "CompatibilityAccepted" && local != null) clientAccepted = true;
                else if (!host && command == "CompatibilityRejected") Reject((string?)message["reason"] ?? "Host rejected mod compatibility.");
                else throw new IOException("Unexpected mod compatibility response.");
            }
            catch (Exception e) { Reject("Mod compatibility check failed: " + e.Message); }
            return true;
        }
        void Check(object peer)
        {
            if (local == null || failed) return;
            string? difference = CompatibilityProfile.Difference(local, remote[peer], true);
            if (difference == null)
            {
                accepted.Add(peer);
                send(peer, TimberNetBase.Control("CompatibilityAccepted"));
            }
            else
            {
                var rejection = TimberNetBase.Control("CompatibilityRejected"); rejection["reason"] = difference;
                send(peer, rejection); Reject(difference);
            }
        }
        void Reject(string reason) { if (failed) return; failed = true; fail(reason); }
    }
}
