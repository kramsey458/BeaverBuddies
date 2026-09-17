using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

internal static class TimingChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public static float FrameDuration;
    public static float FrameDelta() => FrameDuration;
    public static void KeepEnabledState(object instance) { }

    public static void Run(Assembly mod, Action<string, Action> test)
    {
        var harmony = Assembly.Load("0Harmony");
        var sourceType = Assembly.Load("Timberborn.WaterSourceSystem").GetType("Timberborn.WaterSourceSystem.WaterDepthStrengthModifier", true);
        var original = sourceType.GetMethod("GetStrengthModifier", All);
        var fix = mod.GetType("BeaverBuddies.Fixes.WaterSourceTimingFix", true);
        var transpiler = fix.GetMethod("Transpiler", All);
        var getter = fix.GetMethod("GetDeltaTime", All);

        // Clone the installed game's method body. Only mock its depth query and
        // Unity frame clock. The fade/clamp arithmetic is the actual game IL.
        Delegate Compile(bool patched)
        {
            var method = new DynamicMethod("WaterSourceFadeTest", typeof(float), new[]{sourceType}, typeof(TimingChecks).Module, true);
            var il = method.GetILGenerator();
            foreach (var local in original.GetMethodBody().LocalVariables) il.DeclareLocal(local.LocalType, local.IsPinned);
            object instructions = ReadInstructions(original, il, harmony.GetType("HarmonyLib.CodeInstruction"));
            if (patched) instructions = transpiler.Invoke(null, new[]{instructions});
            foreach (object instruction in (IEnumerable)instructions)
            {
                var type = instruction.GetType();
                foreach (Label label in (IEnumerable)type.GetField("labels").GetValue(instruction)) il.MarkLabel(label);
                var opcode = (OpCode)type.GetField("opcode").GetValue(instruction);
                var operand = type.GetField("operand").GetValue(instruction);
                if (operand is MethodInfo called && called.Name == "UpdateEnabledState")
                    operand = typeof(TimingChecks).GetMethod(nameof(KeepEnabledState));
                if (operand is MethodInfo clock && clock.Name == "get_deltaTime")
                    operand = typeof(TimingChecks).GetMethod(nameof(FrameDelta));
                switch (operand)
                {
                    case null: il.Emit(opcode); break;
                    case MethodInfo m: il.Emit(opcode, m); break;
                    case FieldInfo f: il.Emit(opcode, f); break;
                    case Label label: il.Emit(opcode, label); break;
                    case LocalBuilder local: il.Emit(opcode, local); break;
                    case float value: il.Emit(opcode, value); break;
                    case int value: il.Emit(opcode, value); break;
                    case byte value: il.Emit(opcode, value); break;
                    case sbyte value: il.Emit(opcode, value); break;
                    default: throw new Exception("Unsupported fixture operand: " + operand.GetType());
                }
            }
            return method.CreateDelegate(typeof(Func<,>).MakeGenericType(sourceType, typeof(float)));
        }
        object Source(float current, bool enabled = true)
        {
            object s = RuntimeHelpers.GetUninitializedObject(sourceType);
            sourceType.GetField("_currentModifier", All).SetValue(s, current);
            sourceType.GetField("_isEnabled", All).SetValue(s, enabled);
            return s;
        }
        test("Real depth-source fade differs at 30 versus 144 FPS before the timing fix", () =>
        {
            var run = Compile(false);
            FrameDuration = 1f/30;
            float a = (float)run.DynamicInvoke(Source(0.05349405f));
            FrameDuration = 1f/144;
            float b = (float)run.DynamicInvoke(Source(0.05349405f));
            if (a == b) throw new Exception("Frame-rate dependence was not reproduced");
            Console.WriteLine($"  Same saved modifier: 30 FPS -> {a:R}; 144 FPS -> {b:R}");
        });
        var eventType = mod.GetType("BeaverBuddies.IO.EventIO", true);
        var eventField = eventType.GetField("instance", All);
        var bufferType = mod.GetType("BeaverBuddies.Fixes.LateTickableBuffer", true);
        var tickType = Assembly.Load("Timberborn.TickSystem").GetType("Timberborn.TickSystem.ITickService", true);
        var tick = (TickIntervalProxy)DispatchProxy.Create(tickType, typeof(TickIntervalProxy));
        tick.Interval = 0.6f;
        var buffer = Activator.CreateInstance(bufferType, tick);
        object prior = eventField.GetValue(null);
        eventField.SetValue(null, DispatchProxy.Create(eventType, typeof(EmptyEventProxy)));
        try
        {
            test("Multiplayer source clock reads the injected simulation tick interval", () =>
            {
                if ((float)getter.Invoke(null, null) != 0.6f) throw new Exception("Wrong simulation clock");
                tick.Interval = 0.25f;
                if ((float)getter.Invoke(null, null) != 0.25f) throw new Exception("Tick interval was hardcoded");
                tick.Interval = 0.6f;
            });
            test("Patched real depth-source fade matches across different frame timings on every tick", () =>
            {
                var run = Compile(true);
                var a = Source(0.05349405f); var b = Source(0.05349405f);
                for (int i=0; i<10; i++)
                {
                    FrameDuration = i%2 == 0 ? 1f/30 : 0.1f;
                    float x = (float)run.DynamicInvoke(a);
                    FrameDuration = 1f/144;
                    float y = (float)run.DynamicInvoke(b);
                    if (BitConverter.SingleToInt32Bits(x) != BitConverter.SingleToInt32Bits(y))
                        throw new Exception("Source fade diverged at tick " + i);
                    if (i==0 && x <= 0.05349405f) throw new Exception("Fade was frozen");
                }
            });
            test("Timing patch preserves disabled-source reset and maximum-strength clamp", () =>
            {
                var run = Compile(true);
                if ((float)run.DynamicInvoke(Source(0.4f, false)) != 0f ||
                    (float)run.DynamicInvoke(Source(0.9f)) != 1f) throw new Exception("Game clamp/reset changed");
            });
            test("Timing transpiler rejects a missing clock call instead of silently not patching", () =>
            {
                var codeType = harmony.GetType("HarmonyLib.CodeInstruction");
                var empty = Array.CreateInstance(codeType, 0);
                try { transpiler.Invoke(null, new object[]{empty}); }
                catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { return; }
                throw new Exception("An incompatible method body was accepted");
            });
        }
        finally { eventField.SetValue(null, prior); }
    }

    // Decode this small method with .NET reflection rather than Harmony's
    // Unity-targeted MethodCopier, which cannot execute under this .NET 8 host.
    private static object ReadInstructions(MethodInfo method, ILGenerator il, Type codeType)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => (ushort)o.Value);
        var body = method.GetMethodBody().GetILAsByteArray();
        var decoded = new List<(int Offset, OpCode Opcode, object Operand)>();
        var targets = new Dictionary<int, Label>();
        int position = 0;
        int Int32() { int n=BitConverter.ToInt32(body, position); position+=4; return n; }
        while (position < body.Length)
        {
            int offset = position;
            ushort code = body[position++];
            if (code==0xfe) code=(ushort)(0xfe00 | body[position++]);
            var op = opcodes[code];
            object operand = null;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.InlineMethod: operand=method.Module.ResolveMethod(Int32()); break;
                case OperandType.InlineField: operand=method.Module.ResolveField(Int32()); break;
                case OperandType.InlineI: operand=Int32(); break;
                case OperandType.ShortInlineI: operand=(sbyte)body[position++]; break;
                case OperandType.ShortInlineVar: operand=body[position++]; break;
                case OperandType.ShortInlineR: operand=BitConverter.ToSingle(body, position); position+=4; break;
                case OperandType.InlineBrTarget:
                case OperandType.ShortInlineBrTarget:
                    int delta=op.OperandType==OperandType.InlineBrTarget ? Int32() : (sbyte)body[position++];
                    int target=position+delta;
                    if (!targets.ContainsKey(target)) targets[target]=il.DefineLabel();
                    operand=targets[target];
                    break;
                default: throw new Exception("Unsupported fixture opcode " + op);
            }
            decoded.Add((offset, op, operand));
        }
        var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(codeType));
        foreach (var instruction in decoded)
        {
            object code=Activator.CreateInstance(codeType, instruction.Opcode, instruction.Operand);
            if (targets.TryGetValue(instruction.Offset, out Label label))
                ((IList)codeType.GetField("labels").GetValue(code)).Add(label);
            list.Add(code);
        }
        return list;
    }
}

public class TickIntervalProxy : DispatchProxy
{
    public float Interval;
    protected override object Invoke(MethodInfo method, object[] args) =>
        method.Name == "get_TickIntervalInSeconds" ? Interval : throw new NotSupportedException(method.Name);
}
public class EmptyEventProxy : DispatchProxy
{
    protected override object Invoke(MethodInfo method, object[] args) => throw new NotSupportedException(method.Name);
}
