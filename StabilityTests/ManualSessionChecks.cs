using Newtonsoft.Json.Linq;
using TimberNet;

static class ManualSessionChecks
{
    static void Check(bool value) { if (!value) throw new Exception("assertion failed"); }
    static void Until(Func<bool> done) => Check(SpinWait.SpinUntil(done, 3000));
    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Dropped guest does not pause or replace the host session", () =>
        {
            var (hostStream, guestStream) = PipeStream.Pair();
            int maps = 0, faults = 0;
            var host = new TimberServer(new PipeListener(hostStream), () => { maps++; return Task.FromResult(new byte[] { 1, 2, 3 }); }, null) { KeepAliveEnabled = true };
            var guest = new TimberClient(guestStream) { KeepAliveEnabled = true };
            bool loaded = false; guest.OnMapReceived += _ => loaded = true;
            host.OnSessionFault += _ => faults++;
            try
            {
                host.Start(); guest.Start();
                Until(() => { host.Update(); guest.Update(); return loaded; });
                host.StopAcceptingClients("Rehost before joining.");
                guest.Close(); Until(() => !hostStream.Connected);
                host.Update();
                Check(host.ShouldTick && !host.IsStopped && faults == 0 && maps == 1);
                int hash = host.Hash;
                host.DoUserInitiatedEvent(new JObject { ["type"] = "Action", ["ticksSinceLoad"] = 1 });
                // Host-originated actions are already applied locally; only their hash/send path runs here.
                Check(host.Hash != hash && host.ShouldTick);
            }
            finally { guest.Close(); host.Close(); }
        });
        yield return ("Host loss reports once and never silently reconnects the guest", () =>
        {
            var (hostStream, guestStream) = PipeStream.Pair();
            var host = new TimberServer(new PipeListener(hostStream), () => Task.FromResult(new byte[] { 4, 5 }), null) { KeepAliveEnabled = true };
            var guest = new TimberClient(guestStream) { KeepAliveEnabled = true };
            int maps = 0, errors = 0; guest.OnMapReceived += _ => maps++; guest.OnError += _ => errors++;
            try
            {
                host.Start(); guest.Start(); Until(() => { host.Update(); guest.Update(); return maps == 1; });
                host.Close(); Until(() => { guest.Update(); return errors == 1; });
                for (int i = 0; i < 100; i++) guest.Update();
                Check(errors == 1 && maps == 1 && guest.IsStopped && !guest.ShouldTick);
            }
            finally { guest.Close(); host.Close(); }
        });
    }
}
