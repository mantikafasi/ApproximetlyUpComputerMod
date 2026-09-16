using System.Globalization;

namespace ApproximatelyUp.ComputerMod;

// CLR-only plot contract. No Lua table, native pointer or entity crosses this boundary.
public sealed class GraphSeries
{
    public string Name { get; set; } = "";
    public int Color { get; set; }
    public double[][] Points { get; set; } = Array.Empty<double[]>();
}
public sealed class GraphMarker
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Label { get; set; } = "";
}
public sealed class GraphFrame
{
    public string Title { get; set; } = "GRAPH";
    public string XLabel { get; set; } = "X";
    public string YLabel { get; set; } = "Y";
    public double[]? XRange { get; set; }
    public double[]? YRange { get; set; }
    public GraphSeries?[] Series { get; set; } = new GraphSeries?[4];
    public GraphMarker?[] Markers { get; set; } = new GraphMarker?[8];

    public GraphFrame Copy() => new() { Title = Title, XLabel = XLabel, YLabel = YLabel,
        XRange = XRange?.ToArray(), YRange = YRange?.ToArray(), Series = Series.ToArray(), Markers = Markers.ToArray() };
    public static bool Number(double n) => double.IsFinite(n) && Math.Abs(n) <= (double)1e20f;
    public static void Text(string? text)
    {
        if (text is null || text.Length > 48 || text.Any(c => c < 32 || c > 126))
            throw new ArgumentException("Graph labels must contain at most 48 printable ASCII characters.");
    }
    public static void Range(double[]? r)
    {
        if (r is not null && (r.Length != 2 || !Number(r[0]) || !Number(r[1]) || r[0] >= r[1]))
            throw new ArgumentException("Graph range requires finite min < max within the numeric port range.");
    }
    public void Validate()
    {
        Text(Title); Text(XLabel); Text(YLabel); Range(XRange); Range(YRange);
        if (Series is null || Series.Length != 4 || Markers is null || Markers.Length != 8)
            throw new ArgumentException("Graphs have four series slots and eight marker slots.");
        foreach (var s in Series)
        {
            if (s is null) continue;
            Text(s.Name);
            if (s.Color is < 0 or > 3 || s.Points is null || s.Points.Length > 256)
                throw new ArgumentException("Graph series limit is 256 points; color is 1..4 in Lua.");
            foreach (var p in s.Points)
                if (p is null || (p.Length != 0 && (p.Length != 2 || !Number(p[0]) || !Number(p[1]))))
                    throw new ArgumentException("Graph points require finite {x, y} pairs.");
        }
        foreach (var m in Markers)
            if (m is not null) { Text(m.Label); if (!Number(m.X) || !Number(m.Y)) throw new ArgumentException("Invalid graph marker."); }
    }
    public (double Min, double Max) Bounds(bool x)
    {
        var range = x ? XRange : YRange;
        if (range is not null) return (range[0], range[1]);
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var s in Series)
            if (s is not null) foreach (var p in s.Points) { if (p.Length == 0) continue; min = Math.Min(min, p[x ? 0 : 1]); max = Math.Max(max, p[x ? 0 : 1]); }
        foreach (var m in Markers)
            if (m is not null) { var v = x ? m.X : m.Y; min = Math.Min(min, v); max = Math.Max(max, v); }
        if (!double.IsFinite(min)) return (0, 1);
        double pad = min == max ? Math.Max(1, Math.Abs(min) * .05) : (max - min) * .05;
        return (min - pad, max + pad);
    }
    public static string Format(double n) => n.ToString("G4", CultureInfo.InvariantCulture);
}

public sealed class GraphSnapshot
{
    public GraphFrame Frame { get; set; } = new();
    public GraphSettings Settings { get; set; } = new();
    public string Status { get; set; } = "NO DATA";
    public string[] Sources { get; set; } = new[] { "", "", "", "" };
    public void Validate()
    {
        if (Frame is null || Settings is null || Sources is null || Sources.Length!=4) throw new ArgumentException("Missing graph snapshot.");
        Frame.Validate(); Settings.Validate(); GraphFrame.Text(Status); foreach(var s in Sources) GraphFrame.Text(s);
    }
}

public sealed class GraphSettings
{
    public bool Programmed { get; set; }
    public int Seconds { get; set; } = 60;
    public bool Frozen { get; set; }
    public double[]? YRange { get; set; }
    public void Validate()
    {
        if (Seconds is < 5 or > 120) throw new ArgumentException("History window is 5..120 simulation seconds.");
        GraphFrame.Range(YRange);
    }
}

public sealed class GraphHistory
{
    // ponytail: two minutes at the game's nominal 60 Hz, bounded even at unusual tick rates.
    private const int Capacity = 7201;
    private readonly double[] times = new double[Capacity];
    private readonly double[,] values = new double[Capacity, 4];
    private int next, count, lastTick = int.MinValue;
    private double time;
    public double Time => time;
    public int Count => count;
    public void Clear() { next = count = 0; lastTick = int.MinValue; time = 0; }
    public void Sample(int tick, double dt, double?[] inputs)
    {
        if (inputs.Length != 4 || !double.IsFinite(dt) || dt <= 0) throw new ArgumentException("Invalid history sample.");
        if (tick == lastTick) return;
        if (tick < lastTick) Clear();
        time += dt; lastTick = tick; times[next] = time;
        for (int i = 0; i < 4; i++) values[next, i] = inputs[i] is { } v && GraphFrame.Number(v) ? v : double.NaN;
        next = (next + 1) % Capacity; count = Math.Min(Capacity, count + 1);
    }
    public GraphFrame Frame(GraphSettings settings)
    {
        settings.Validate();
        var f = new GraphFrame { Title = "SIGNAL HISTORY", XLabel = "TIME (S)", YLabel = "VALUE",
            XRange = new[] { Math.Max(0, time - settings.Seconds), Math.Max(settings.Seconds, time) }, YRange = settings.YRange?.ToArray() };
        for (int ch = 0; ch < 4; ch++)
        {
            // Each time bin contributes its min and max in chronological order. Empty points
            // explicitly break the line at disconnected/invalid samples, never inventing zeros.
            var samples = new List<(double T, double V)>();
            for (int i = 0; i < count; i++)
            {
                int index = (next - count + i + Capacity) % Capacity;
                if (times[index] < f.XRange[0]) continue;
                samples.Add((times[index], values[index, ch]));
            }
            var points = new List<double[]>();
            int stride = Math.Max(1, (samples.Count + 127) / 128);
            for (int i = 0; i < samples.Count; i += stride)
            {
                int low = i, high = i;
                bool gap = !double.IsFinite(samples[i].V);
                for (int j = i + 1; j < Math.Min(samples.Count, i + stride); j++)
                { gap |= !double.IsFinite(samples[j].V); if (samples[j].V < samples[low].V) low = j; if (samples[j].V > samples[high].V) high = j; }
                if (gap) { points.Add(Array.Empty<double>()); continue; }
                foreach (int j in low == high ? new[] { low } : new[] { Math.Min(low, high), Math.Max(low, high) })
                    points.Add(new[] { samples[j].T, samples[j].V });
            }
            f.Series[ch] = new GraphSeries { Name = "INPUT " + (ch + 1), Color = ch, Points = points.ToArray() };
        }
        return f;
    }
}
