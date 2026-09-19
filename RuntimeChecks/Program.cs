using System.Reflection;
using System.Runtime.Loader;

// Tests the real compiled mod's pure managed RNG wrappers. Requires a local
// game installation; does not start Unity or a multiplayer simulation.
if (args.Length < 2) throw new ArgumentException("Usage: RuntimeChecks <BeaverBuddies.dll> <game Managed directory>");
string modPath = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (string dir in new[] {Path.GetDirectoryName(modPath), Path.GetFullPath(args[1])}.Concat(args.Skip(2).Select(Path.GetFullPath)))
    {
        string path = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(modPath);
var service = assembly.GetType("BeaverBuddies.DeterminismService", true);
var flag = service.GetField("IsNonGameplay");
var call = service.GetMethod("GetNonGameRandom").MakeGenericMethod(typeof(int));
int failures = 0, total = 0;
bool Flag() => (bool)flag.GetValue(null);
void Check(bool condition) { if (!condition) throw new Exception("RNG classification was not restored"); }
void Test(string name, Action run)
{
    total++;
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.GetBaseException().Message); }
    finally { flag.SetValue(null, false); }
}
foreach (bool initial in new[] {false, true})
{
    Test($"GetNonGameRandom preserves initial={initial}", () =>
    {
        flag.SetValue(null, initial);
        call.Invoke(null, new object[] {(Func<int>)(() => { Check(Flag()); return 42; })});
        Check(Flag() == initial);
    });
    Test($"GetNonGameRandom restores initial={initial} on exception", () =>
    {
        flag.SetValue(null, initial);
        try { call.Invoke(null, new object[] {(Func<int>)(() => throw new InvalidOperationException("injected"))}); }
        catch (TargetInvocationException) { }
        Check(Flag() == initial);
    });
}
Test("Nested GetNonGameRandom leaves the outer scope active", () =>
{
    call.Invoke(null, new object[] {(Func<int>)(() =>
    {
        call.Invoke(null, new object[] {(Func<int>)(() => 1)});
        Check(Flag()); return 2;
    })});
    Check(!Flag());
});
var rngInterface = Assembly.Load("Timberborn.Common").GetType("Timberborn.Common.IRandomNumberGenerator", true);
var proxy = (RngProxy)DispatchProxy.Create(rngInterface, typeof(RngProxy));
var wrapper = Activator.CreateInstance(assembly.GetType("BeaverBuddies.NonTickRandomNumberGenerator", true), proxy);
foreach (string method in new[] {"TryGetListElement", "TryGetEnumerableElement"})
foreach (bool initial in new[] {false, true})
foreach (bool throws in new[] {false, true})
{
    Test($"{method} restores initial={initial}, exception={throws}", () =>
    {
        flag.SetValue(null, initial); proxy.Throw = throws; proxy.CheckActive = () => Check(Flag());
        try { wrapper.GetType().GetMethod(method).MakeGenericMethod(typeof(int)).Invoke(wrapper, new object[] {new int[] {7}, 0}); }
        catch (TargetInvocationException) when (throws) { }
        Check(Flag() == initial);
    });
}
var saving = assembly.GetType("BeaverBuddies.GameSaverSavePatcher", true).GetProperty("IsSaving");
var exitPatcher = assembly.GetType("BeaverBuddies.AutosaverCreateExitSavePatcher", true);
var exitPrefix = exitPatcher.GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);
var exitFinalizer = exitPatcher.GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static);
foreach (bool initial in new[] {false, true})
{
    Test($"Exit-save scope restores initial={initial}", () =>
    {
        saving.SetValue(null, initial);
        object[] state = exitPrefix.GetParameters().Length == 0 ? Array.Empty<object>() : new object[] {false};
        exitPrefix.Invoke(null, state);
        Check((bool)saving.GetValue(null));
        if (exitFinalizer == null) throw new Exception("Exit save has no cleanup finalizer");
        exitFinalizer.Invoke(null, state);
        Check((bool)saving.GetValue(null) == initial);
    });
}
Test("Scene reset clears a stale saving flag", () =>
{
    saving.SetValue(null, true);
    object instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(service);
    service.GetMethod("Reset").Invoke(instance, null);
    if ((bool)saving.GetValue(null)) throw new Exception("Saving flag survived scene reset");
});
saving.SetValue(null, false);
ScopeChecks.Run(assembly, Test);
WaterChecks.Run(assembly, Test);
TimingChecks.Run(assembly, Test);
DemolitionChecks.Run(assembly, Test);
InputRecoveryChecks.Run(assembly, Test);
ReplayEventChecks.Run(assembly, Test);
Console.WriteLine($"{total-failures}/{total} passed");
return failures == 0 ? 0 : 1;

public class RngProxy : DispatchProxy
{
    public bool Throw;
    public Action CheckActive;
    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        CheckActive();
        if (Throw) throw new InvalidOperationException("injected RNG failure");
        args[1] = 7; return true;
    }
}
