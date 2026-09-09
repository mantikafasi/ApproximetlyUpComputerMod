using System;
using MoonSharp.Interpreter;

namespace ApproximatelyUp.ComputerMod;

/// <summary>
/// Single-threaded Lua computer. Execution failures require a new instance; state is not rolled back.
/// Initialization and each tick allow 10000 VM instructions and 4 MiB measured allocation each.
/// Parsing has a separate 4 MiB post-check. Tick is captured after initialization; ports are tick-only.
/// Source parsing and individual VM/host operations cannot be preempted. Allocation checks are
/// per-entry measurements, NOT retained-heap quotas or hostile-code isolation.
/// </summary>
public sealed class LuaComputer
{
    private const int InstructionBudget = 10000;
    private const long AllocationBudget = 4 * 1024 * 1024;
    private readonly Script script;
    private readonly DynValue tick;
    private readonly double[] inputs;
    private readonly int outputCount;
    private double[]? pending;
    private bool faulted;

    public LuaComputer(string source, int inputCount, int outputCount)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 16384) throw new ArgumentException("Lua source exceeds 16384 characters.", nameof(source));
        if (inputCount < 0 || inputCount > 8) throw new ArgumentOutOfRangeException(nameof(inputCount));
        if (outputCount < 0 || outputCount > 8) throw new ArgumentOutOfRangeException(nameof(outputCount));
        if (source.StartsWith("MoonSharp_dump_b64::", StringComparison.Ordinal) || source.StartsWith("\x1b", StringComparison.Ordinal))
            throw new ArgumentException("Only Lua source text is accepted.", nameof(source));
        inputs = new double[inputCount];
        this.outputCount = outputCount;
        script = new Script(CoreModules.None);
        script.Globals.Set("state", DynValue.NewTable(script));

        // Fixed-arity numeric callbacks avoid stock library coercions, varargs, and random state.
        var math = new Table(script);
        void Unary(string name, Func<double, double> operation) => math.Set(name,
            DynValue.NewCallback((_, a) => a.Count == 1 ? DynValue.NewNumber(operation(Number(a[0])))
                : throw new ScriptRuntimeException("This math function requires exactly one argument.")));
        void Binary(string name, Func<double, double, double> operation) => math.Set(name,
            DynValue.NewCallback((_, a) => a.Count == 2 ? DynValue.NewNumber(operation(Number(a[0]), Number(a[1])))
                : throw new ScriptRuntimeException("This math function requires exactly two arguments.")));
        Unary("abs", Math.Abs); Unary("acos", Math.Acos); Unary("asin", Math.Asin); Unary("atan", Math.Atan);
        Unary("ceil", Math.Ceiling); Unary("cos", Math.Cos); Unary("exp", Math.Exp); Unary("floor", Math.Floor);
        Unary("log", Math.Log); Unary("sin", Math.Sin); Unary("sqrt", Math.Sqrt); Unary("tan", Math.Tan);
        Unary("deg", x => x * (180 / Math.PI)); Unary("rad", x => x * (Math.PI / 180));
        Binary("atan2", Math.Atan2); Binary("min", Math.Min); Binary("max", Math.Max);
        Binary("pow", Math.Pow); Binary("fmod", (x, y) => x % y);
        math.Set("pi", DynValue.NewNumber(Math.PI));
        script.Globals.Set("math", DynValue.NewTable(math));
        script.Globals.Set("type", DynValue.NewCallback((_, a) => DynValue.NewString(a[0].Type.ToLuaTypeString())));
        script.Globals.Set("assert", DynValue.NewCallback((_, a) =>
            a[0].CastToBool() ? a[0] : throw new ScriptRuntimeException("assertion failed")));
        script.Globals.Set("error", DynValue.NewCallback((_, a) => throw new ScriptRuntimeException(
            a[0].Type == DataType.String ? Clip(a[0].String) : "script error")));
        script.Globals.Set("input", DynValue.NewCallback((_, a) =>
            DynValue.NewNumber(inputs[Index(a[0], inputs.Length)])));
        script.Globals.Set("input_bool", DynValue.NewCallback((_, a) =>
            DynValue.NewBoolean(inputs[Index(a[0], inputs.Length)] >= 0.5)));
        script.Globals.Set("input_vec3", DynValue.NewCallback((_, a) =>
        {
            int i = Index(a[0], inputs.Length, 3);
            var vector = new Table(script);
            vector.Set("x", DynValue.NewNumber(inputs[i]));
            vector.Set("y", DynValue.NewNumber(inputs[i + 1]));
            vector.Set("z", DynValue.NewNumber(inputs[i + 2]));
            return DynValue.NewTable(vector);
        }));
        script.Globals.Set("output", DynValue.NewCallback((_, a) =>
        {
            int i = Index(a[0], outputCount);
            pending![i] = Scalar(a[1]);
            return DynValue.Nil;
        }));
        script.Globals.Set("output_vec3", DynValue.NewCallback((_, a) =>
        {
            int i = Index(a[0], outputCount, 3);
            if (a[1].Type != DataType.Table) throw new ScriptRuntimeException("Expected an x/y/z table.");
            double x = Scalar(a[1].Table.Get("x")), y = Scalar(a[1].Table.Get("y")), z = Scalar(a[1].Table.Get("z"));
            pending![i] = x; pending[i + 1] = y; pending[i + 2] = z;
            return DynValue.Nil;
        }));
        try
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var initialize = script.LoadString(source, codeFriendlyName: "computer");
            CheckAllocation(allocated);
            Run(initialize);
            tick = script.Globals.Get("tick");
            if (tick.Type != DataType.Function) throw new InvalidOperationException("Source must define function tick().");
        }
        catch (InterpreterException ex) { throw Failure(ex); }
    }

    /// <summary>Returns a fresh zero-default output array only on success. Inputs and dt must be finite.</summary>
    public double[] Tick(double[] inputs, double deltaTime, int tickId)
    {
        if (faulted) throw new InvalidOperationException("Computer is faulted; create a new instance.");
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Length != this.inputs.Length) throw new ArgumentException("Input count does not match.", nameof(inputs));
        if (!double.IsFinite(deltaTime)) throw new ArgumentOutOfRangeException(nameof(deltaTime), "dt must be finite.");
        for (int i = 0; i < inputs.Length; i++)
        {
            double value = inputs[i];
            if (!double.IsFinite(value)) throw new ArgumentException("Inputs must be finite.", nameof(inputs));
            this.inputs[i] = value;
        }
        try
        {
            pending = new double[outputCount];
            script.Globals.Set("dt", DynValue.NewNumber(deltaTime));
            script.Globals.Set("tick_id", DynValue.NewNumber(tickId));
            Run(tick);
            return pending;
        }
        catch (InterpreterException ex) { faulted = true; throw Failure(ex); }
        catch { faulted = true; throw; }
        finally { pending = null; }
    }

    private void Run(DynValue function)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var coroutine = script.CreateCoroutine(function).Coroutine;
        // ponytail: check each VM slice, not a heap quota; use process isolation for hostile scripts.
        coroutine.AutoYieldCounter = 1;
        for (int i = 0; i < InstructionBudget; i++)
        {
            coroutine.Resume();
            CheckAllocation(allocated);
            if (coroutine.State == CoroutineState.Dead) return;
            if (coroutine.State != CoroutineState.ForceSuspended)
                throw new InvalidOperationException("Script yielded unexpectedly.");
        }
        throw new InvalidOperationException("Lua instruction budget exceeded.");
    }

    private int Index(DynValue value, int count, int width = 1)
    {
        if (pending is null) throw new ScriptRuntimeException("Port access is only valid inside tick().");
        double n = Number(value);
        if (n != Math.Truncate(n) || n < 1 || n > count - width + 1)
            throw new ScriptRuntimeException("Port index must be a one-based integer within the configured ports.");
        return (int)n - 1;
    }

    private static double Number(DynValue value)
    {
        if (value.Type != DataType.Number || !double.IsFinite(value.Number))
            throw new ScriptRuntimeException("Expected a finite number (no string coercion).");
        return value.Number;
    }

    private static double Scalar(DynValue value)
    {
        double number = value.Type == DataType.Boolean ? (value.Boolean ? 1 : 0) : Number(value);
        if (number < -(double)1e20f || number > (double)1e20f) throw new ScriptRuntimeException("Output must be within the native [-1e20f, 1e20f] range.");
        return number;
    }

    private static void CheckAllocation(long before)
    {
        if (GC.GetAllocatedBytesForCurrentThread() - before > AllocationBudget)
            throw new InvalidOperationException("Lua allocation budget exceeded (4 MiB per entry).");
    }

    private static string Clip(string message) => message.Length <= 256 ? message : message.Substring(0, 256);
    private static InvalidOperationException Failure(InterpreterException ex) =>
        new InvalidOperationException("Lua: " + Clip(ex.DecoratedMessage ?? ex.Message));

    public static void SelfTest()
    {
        var computer = new LuaComputer("function tick() output(1, input(1) * 2) end", 1, 1);
        if (computer.Tick(new[] { 3.0 }, 0, 0)[0] != 6) throw new InvalidOperationException("Lua smoke check failed.");
    }
}
