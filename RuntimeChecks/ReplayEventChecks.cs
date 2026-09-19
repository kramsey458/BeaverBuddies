using System.Reflection;

internal static class ReplayEventChecks
{
    public static void Run(Assembly mod, Action<string, Action> test)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var registryType = Assembly.Load("Timberborn.EntitySystem").GetType("Timberborn.EntitySystem.EntityRegistry", true);
        var eventType = mod.GetType("BeaverBuddies.Events.ClearResourcesMarkedEvent", true);
        var contextType = mod.GetType("BeaverBuddies.Events.IReplayContext", true);

        // Plugin.Log* would otherwise reach Unity's native logger.
        var pluginLogger = mod.GetType("BeaverBuddies.Plugin", true).GetField("logger", all);
        var loggerType = mod.GetType("BeaverBuddies.Util.Logging.ILogger", true);
        object previousLogger = pluginLogger.GetValue(null);

        ReplayContextProxy Context(object registry)
        {
            var context = (ReplayContextProxy)DispatchProxy.Create(contextType, typeof(ReplayContextProxy));
            context.Registry = registry; context.RegistryType = registryType;
            return context;
        }
        object Event(bool markForDemolition, params Guid[] ids)
        {
            var replayEvent = Activator.CreateInstance(eventType, true);
            eventType.GetField("blocks").SetValue(replayEvent, ids.ToList());
            eventType.GetField("markForDemolition").SetValue(replayEvent, markForDemolition);
            return replayEvent;
        }
        void Replay(object replayEvent, ReplayContextProxy context) =>
            eventType.GetMethod("Replay").Invoke(replayEvent, new object[] { context });
        void WithQuietLogger(Action run)
        {
            pluginLogger.SetValue(null, DispatchProxy.Create(loggerType, typeof(QuietLoggerProxy)));
            try { run(); } finally { pluginLogger.SetValue(null, previousLogger); }
        }

        // Regression: a stale selection (builders already demolished the objects
        // before the event arrived) threw a NullReferenceException that aborted
        // the whole multiplayer session.
        foreach (bool mark in new[] { true, false })
            test($"Replay of a stale demolition selection is skipped (mark={mark})", () => WithQuietLogger(() =>
            {
                var context = Context(Activator.CreateInstance(registryType));
                Replay(Event(mark, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), context);
                // Nothing left to act on, so neither tool may be touched.
                if (context.Requested.Any(type => type != registryType))
                    throw new Exception("Stale selection reached the demolition tool");
            }));
    }
}

public class ReplayContextProxy : DispatchProxy
{
    public object Registry;
    public Type RegistryType;
    public List<Type> Requested = new();
    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        var type = targetMethod.GetGenericArguments()[0];
        Requested.Add(type);
        return type == RegistryType ? Registry : null;
    }
}

public class QuietLoggerProxy : DispatchProxy
{
    protected override object Invoke(MethodInfo targetMethod, object[] args) => null;
}
