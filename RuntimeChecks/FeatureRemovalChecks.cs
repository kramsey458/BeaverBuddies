using System.Reflection;

internal static class FeatureRemovalChecks
{
    public static void Run(Assembly mod, Action<string, Action> test)
    {
        test("Removed recovery and compatibility services are absent from the shipped mod", () =>
        {
            foreach (string type in new[] { "Connect.SnapshotResyncService", "Connect.SnapshotResyncMenu", "IO.BuildCompatibility", "IO.CompatibilityStatus" })
                if (mod.GetType("BeaverBuddies." + type) != null) throw new Exception(type + " still ships");
        });
        test("Removed recovery settings are absent while retained features remain", () =>
        {
            var settings = mod.GetType("BeaverBuddies.Settings", true);
            foreach (string name in new[] { "AutomaticSnapshotResync", "ReconnectGracePeriod" })
                if (settings.GetProperty(name) != null) throw new Exception(name + " still exposed");
            foreach (string name in new[] { "PlayerActivity", "RollingDiagnostics", "ConnectionStatusPanel" })
                if (settings.GetProperty(name) == null) throw new Exception(name + " missing");
        });
    }
}
