using System.Collections;
using System.Reflection;

internal static class InputRecoveryChecks
{
    public static void Run(Assembly mod, Action<string, Action> test)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var input = Assembly.Load("Timberborn.InputSystem");
        var keys = Assembly.Load("Timberborn.KeyBindingSystem");
        var type = mod.GetType("BeaverBuddies.Fixes.MultiplayerInputRecovery", true);
        var bindingType = keys.GetType("Timberborn.KeyBindingSystem.KeyBinding", true);
        var registryType = keys.GetType("Timberborn.KeyBindingSystem.KeyBindingRegistry", true);
        var reset = (InputResetProxy)DispatchProxy.Create(input.GetType("Timberborn.InputSystem.IInputStateResetter", true), typeof(InputResetProxy));
        var registry = Activator.CreateInstance(registryType, new object[] { null, null, null });
        var binding = Activator.CreateInstance(bindingType, new object[] { "test", null, false });
        ((IList)registryType.GetField("_keyBindings", all).GetValue(registry)).Add(binding);
        var recovery = Activator.CreateInstance(type, reset, registry);
        void Call(string method) => type.GetMethod(method).Invoke(recovery, null);
        void Require(bool value) { if (!value) throw new Exception("Input recovery invariant failed"); }

        test("Input recovery leaves normal frames untouched", () =>
        { Call("UpdateSingleton"); Require(reset.Calls == 0); });
        test("Input recovery defers and coalesces disconnect requests", () =>
        {
            Call("RequestReset"); Call("RequestReset"); Require(reset.Calls == 0);
            Call("UpdateSingleton"); Require(reset.Calls == 1);
            Call("UpdateSingleton"); Require(reset.Calls == 1);
        });
        test("Input recovery clears held/down/release flags without a phantom click", () =>
        {
            string[] properties = { "IsDown", "IsHeld", "IsLongHeld", "IsUp", "IsUpAfterShortHeld" };
            foreach (var name in properties)
                bindingType.GetField("<" + name + ">k__BackingField", all).SetValue(binding, true);
            Call("RequestReset"); Call("UpdateSingleton");
            foreach (var name in properties) Require(!(bool)bindingType.GetProperty(name).GetValue(binding));
        });
        test("Input recovery requests one fresh reset after multiplayer scene load", () =>
        {
            int before = reset.Calls;
            Call("PostLoad"); Require(reset.Calls == before);
            Call("UpdateSingleton"); Call("UpdateSingleton"); Require(reset.Calls == before + 1);
        });
        test("Input recovery permits subsequent independent disconnect resets", () =>
        {
            int before = reset.Calls;
            Call("RequestReset"); Call("UpdateSingleton");
            Call("RequestReset"); Call("UpdateSingleton"); Require(reset.Calls == before + 2);
        });
        test("Recovered binding unlocks on the next normal unpressed update", () =>
        {
            Require((bool)bindingType.GetField("_isLocked", all).GetValue(binding));
            bindingType.GetMethod("UpdateUnpressedState", all).Invoke(binding, null);
            Require(!(bool)bindingType.GetField("_isLocked", all).GetValue(binding));
        });
    }
}

public class InputResetProxy : DispatchProxy
{
    public int Calls;
    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.Name != "ResetInputState") throw new NotSupportedException(method.Name);
        Calls++;
        return null;
    }
}
