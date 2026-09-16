using MoonSharp.Interpreter;

namespace ApproximatelyUp.ComputerMod;

internal sealed class LuaGraph
{
    internal IReadOnlyDictionary<int, GraphFrame> Frames => frames;
    private readonly Dictionary<int, GraphFrame> frames = new();
    private Dictionary<int, GraphFrame>? pending;
    private readonly int outputs;
    internal LuaGraph(Script script, int outputs)
    {
        this.outputs = outputs;
        var api = new Table(script);
        api.Set("clear", DynValue.NewCallback((_, a) => { pending![Output(a[0])] = new GraphFrame(); return DynValue.Nil; }));
        api.Set("axes", DynValue.NewCallback((_, a) =>
        {
            var f = Edit(a[0]); f.Title = Label(a[1]); f.XLabel = Label(a[2]); f.YLabel = Label(a[3]);
            f.XRange = Range(a[4], a[5]); f.YRange = Range(a[6], a[7]); return DynValue.Nil;
        }));
        api.Set("series", DynValue.NewCallback((_, a) =>
        {
            var f = Edit(a[0]); int slot = Slot(a[1], 4);
            if (a[2].Type != DataType.Table || a[2].Table.Length > 256) throw new ScriptRuntimeException("Graph series needs at most 256 {x, y} pairs.");
            var points = new double[a[2].Table.Length][];
            for (int i = 0; i < points.Length; i++)
            {
                var p = a[2].Table.Get(i + 1);
                if (p.Type == DataType.Boolean && !p.Boolean) { points[i] = Array.Empty<double>(); continue; }
                if (p.Type != DataType.Table) throw new ScriptRuntimeException("Expected {x, y} point or false for a gap.");
                points[i] = new[] { Number(p.Table.Get(1)), Number(p.Table.Get(2)) };
            }
            f.Series[slot] = new GraphSeries { Points = points, Name = a[3].IsNil() ? "SERIES " + (slot + 1) : Label(a[3]),
                Color = a[4].IsNil() ? slot : Slot(a[4], 4) };
            return DynValue.Nil;
        }));
        api.Set("marker", DynValue.NewCallback((_, a) =>
        {
            Edit(a[0]).Markers[Slot(a[1], 8)] = new GraphMarker { X = Number(a[2]), Y = Number(a[3]), Label = Label(a[4]) };
            return DynValue.Nil;
        }));
        script.Globals.Set("graph", DynValue.NewTable(api));
    }
    internal void Begin() => pending = new();
    internal void Finish(bool success)
    {
        if (success && pending is not null)
        {
            foreach (var f in pending.Values) f.Validate();
            foreach (var p in pending) frames[p.Key] = p.Value;
        }
        pending = null;
    }
    private int Output(DynValue n)
    {
        if (pending is null) throw new ScriptRuntimeException("Graph access is only valid inside tick().");
        return Slot(n, outputs) + 1;
    }
    private GraphFrame Edit(DynValue n)
    {
        int i = Output(n);
        if (!pending!.TryGetValue(i, out var f)) pending[i] = f = frames.TryGetValue(i, out var old) ? old.Copy() : new();
        return f;
    }
    private static int Slot(DynValue n, int count)
    {
        double v = Number(n);
        if (v != Math.Truncate(v) || v < 1 || v > count) throw new ScriptRuntimeException("Graph slot/output index is out of range.");
        return (int)v - 1;
    }
    private static double Number(DynValue v) => v.Type == DataType.Number && GraphFrame.Number(v.Number) ? v.Number : throw new ScriptRuntimeException("Graph values must be finite numbers within the port range.");
    private static string Label(DynValue v)
    {
        if (v.Type != DataType.String) throw new ScriptRuntimeException("Graph label must be text.");
        GraphFrame.Text(v.String); return v.String;
    }
    private static double[]? Range(DynValue a, DynValue b)
    {
        if (a.IsNil() && b.IsNil()) return null;
        var r = new[] { Number(a), Number(b) }; GraphFrame.Range(r); return r;
    }
}
