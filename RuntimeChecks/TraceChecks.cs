using System.Collections;
using System.Reflection;

// The per-tick trace payload: stack traces stay on the machine that recorded them.
internal static class TraceChecks
{
    public static void Run(Assembly mod, Action<string, Action> test)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var serviceType = mod.GetType("BeaverBuddies.DesyncDetecter.DesyncDetecterService", true);
        var traceType = mod.GetType("BeaverBuddies.DesyncDetecter.Trace", true);
        var eventType = mod.GetType("BeaverBuddies.DesyncDetecter.TraceLoggedForTickEvent", true);
        var settingsType = mod.GetType("BeaverBuddies.Settings", true);
        var jsonType = mod.GetType("BeaverBuddies.IO.JsonSettings", true);
        var listType = typeof(List<>).MakeGenericType(traceType);

        // Plugin.Log* would otherwise reach Unity's native logger.
        var pluginLogger = mod.GetType("BeaverBuddies.Plugin", true).GetField("logger", all);
        var loggerType = mod.GetType("BeaverBuddies.Util.Logging.ILogger", true);
        object previousLogger = pluginLogger.GetValue(null);
        var debug = settingsType.GetProperty("TemporarilyDebug");

        // Traces are only recorded in debug mode. A fresh service resets the static trace history.
        void WithTracing(Action run)
        {
            pluginLogger.SetValue(null, DispatchProxy.Create(loggerType, typeof(QuietLoggerProxy)));
            debug.SetValue(null, true);
            try
            {
                Activator.CreateInstance(serviceType, true);
                serviceType.GetMethod("StartTick").Invoke(null, new object[] { 0 });
                run();
            }
            finally
            {
                debug.SetValue(null, false);
                pluginLogger.SetValue(null, previousLogger);
            }
        }
        void Trace(string message) => serviceType.GetMethod("Trace").Invoke(null, new object[] { message, true, false });
        object Other(params string[] messages)
        {
            var list = (IList)Activator.CreateInstance(listType);
            foreach (string message in messages)
            {
                var trace = Activator.CreateInstance(traceType);
                traceType.GetField("message").SetValue(trace, message);
                list.Add(trace);
            }
            return list;
        }

        test("A recorded trace keeps its stack, but only as an object", () => WithTracing(() =>
        {
            Trace("first");
            var events = ((IEnumerable)serviceType.GetMethod("CreateReplayEventsAndClear").Invoke(null, null)).Cast<object>().ToList();
            var traces = ((IEnumerable)eventType.GetField("traces").GetValue(events.Last())).Cast<object>().ToList();
            object first = traces.Single(t => (string)traceType.GetField("message").GetValue(t) == "first");
            var stack = traceType.GetField("stack").GetValue(first);
            if (stack is not System.Diagnostics.StackTrace) throw new Exception("The stack was not captured");
            string text = (string)traceType.GetProperty("StackText").GetValue(first);
            if (!text.Contains("DesyncDetecterService")) throw new Exception("The stack text does not name the recording method");
        }));

        test("The per-tick trace payload carries messages but no stack traces", () => WithTracing(() =>
        {
            Trace("first");
            Trace("second");
            var events = ((IEnumerable)serviceType.GetMethod("CreateReplayEventsAndClear").Invoke(null, null)).Cast<object>().ToList();
            var serialize = jsonType.GetMethod("Serialize").MakeGenericMethod(eventType);
            string json = (string)serialize.Invoke(null, new[] { events.Last() });
            if (!json.Contains("first") || !json.Contains("second")) throw new Exception("The messages were not sent");
            if (json.Contains("stack", StringComparison.OrdinalIgnoreCase) || json.Contains(" at ", StringComparison.Ordinal))
                throw new Exception("A stack trace was serialized:\n" + json);
            // The receiving player rebuilds the traces from those messages alone.
            var back = jsonType.GetMethod("Deserialize").MakeGenericMethod(eventType).Invoke(null, new object[] { json });
            var traces = ((IEnumerable)eventType.GetField("traces").GetValue(back)).Cast<object>().ToList();
            if (traces.Count != ((IEnumerable)eventType.GetField("traces").GetValue(events.Last())).Cast<object>().Count())
                throw new Exception("A trace was lost in transit");
            if (traces.Any(t => traceType.GetField("stack").GetValue(t) != null)) throw new Exception("A stack appeared out of nowhere");
        }));

        test("A desync report is built when the other player's traces have no stacks", () => WithTracing(() =>
        {
            Trace("first");
            Trace("second");
            var verify = serviceType.GetMethod("VerifyTraces");
            // Same first messages, then a difference: the mismatch must be reported, not thrown.
            object other = Other("Tick 0 started", "first", "DIFFERENT");
            bool ok = (bool)verify.Invoke(null, new object[] { 0, other });
            if (ok) throw new Exception("A mismatch was not detected");
            string report = (string)serviceType.GetMethod("GetLastDesyncTrace").Invoke(null, null);
            if (report == null || !report.Contains("Desync detected for tick 0!")) throw new Exception("No desync report");
            if (!report.Contains("DIFFERENT")) throw new Exception("The other player's message is missing from the report");
            // My own stack is formatted lazily, at report time.
            if (!report.Contains("at BeaverBuddies.DesyncDetecter.DesyncDetecterService")) throw new Exception("My stack is missing from the report");
        }));

        test("Matching traces verify without a report", () => WithTracing(() =>
        {
            Trace("first");
            object other = Other("Tick 0 started", "first");
            if (!(bool)serviceType.GetMethod("VerifyTraces").Invoke(null, new object[] { 0, other }))
                throw new Exception("Identical traces were reported as a desync");
            if (serviceType.GetMethod("GetLastDesyncTrace").Invoke(null, null) != null) throw new Exception("A report was built anyway");
        }));
    }
}
