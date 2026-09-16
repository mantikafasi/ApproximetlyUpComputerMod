namespace ApproximatelyUp.ComputerMod;

// Shared pixel-space drawing: physical meshes and enlarged view use the same lines.
internal static class GraphDrawing
{
    internal const int Width = 640, Height = 480;
    internal readonly record struct Line(float X, float Y, float X2, float Y2, int Color, float Thickness = 1);
    // 5x7 authored bitmap legends, expanded into horizontal strokes. No OS font or native texture shader required.
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-+./:()=?_ ";
    private static readonly string[] Glyphs = {
        "0E11111F111111","1E11111E11111E","0E11101010110E","1E11111111111E","1F10101E10101F","1F10101E101010",
        "0E11101711110F","1111111F111111","0E04040404040E","0702020212120C","11121418141211","1010101010101F",
        "111B1515111111","11191513111111","0E11111111110E","1E11111E101010","0E11111115120D","1E11111E141211",
        "0E11100E01110E","1F040404040404","1111111111110E","11111111110A04","11111115151B11","11110A040A1111",
        "11110A04040404","1F01020408101F","0E11131519110E","040C040404040E","0E11010204081F","1E01010601011E",
        "02060A121F0202","1F10101E01011E","0E10101E11110E","1F010204080808","0E11110E11110E","0E11110F01010E",
        "0000001F000000","0004041F040400","00000000000C0C","01010204081010","000C0C000C0C00","02040808080402",
        "08040202020408","00001F001F0000","0E110102040004","0000000000001F","00000000000000" };
    internal static List<Line> Build(GraphFrame frame, string status)
    {
        frame.Validate();
        var lines = new List<Line>(3000);
        var (xmin, xmax) = frame.Bounds(true); var (ymin, ymax) = frame.Bounds(false);
        for (int i = 0; i <= 4; i++)
        {
            float x = 70 + i * 135, y = 60 + i * 80;
            lines.Add(new(x, 60, x, 380, 0)); lines.Add(new(70, y, 610, y, 0));
            Center(lines, x, 391, GraphFrame.Format(xmin + (xmax - xmin) * i / 4), 1);
            Text(lines, 2, y - 3, GraphFrame.Format(ymax - (ymax - ymin) * i / 4), 1);
        }
        Text(lines, 14, 12, frame.Title, 2);
        Text(lines, 14, 36, status.Length > 80 ? status[..80] : status, 1);
        Center(lines, 340, 414, frame.XLabel, 1); Text(lines, 14, 414, frame.YLabel, 1);
        int slot = 0;
        foreach (var s in frame.Series)
        {
            if (s is null) { slot++; continue; }
            Text(lines, 14 + slot++ * 155, 450, s.Name[..Math.Min(23, s.Name.Length)], 1, s.Color + 2);
            for (int i = 1; i < s.Points.Length; i++)
            {
                if (s.Points[i - 1].Length == 0 || s.Points[i].Length == 0) continue;
                var a = Point(s.Points[i - 1][0], s.Points[i - 1][1]); var b = Point(s.Points[i][0], s.Points[i][1]);
                if (Clip(ref a, ref b)) lines.Add(new((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, s.Color + 2, 2));
            }
            if (s.Points.Length == 1 && s.Points[0].Length == 2) Cross(Point(s.Points[0][0], s.Points[0][1]), s.Color + 2);
        }
        foreach (var m in frame.Markers)
            if (m is not null)
            {
                var p = Point(m.X, m.Y);
                if (p.X is < 70 or > 610 || p.Y is < 60 or > 380) continue;
                Cross(p, 1); Text(lines, (float)Math.Min(p.X + 7, 610 - m.Label.Length * 6), (float)Math.Max(60, p.Y - 10), m.Label, 1);
            }
        return lines;

        (double X, double Y) Point(double x, double y) =>
            (Math.Clamp(70 + (x - xmin) / (xmax - xmin) * 540, -1e6, 1e6), Math.Clamp(380 - (y - ymin) / (ymax - ymin) * 320, -1e6, 1e6));
        void Cross((double X, double Y) p, int color)
        {
            if (p.X is < 73 or > 607 || p.Y is < 63 or > 377) return;
            lines.Add(new((float)p.X - 3,(float)p.Y,(float)p.X + 3,(float)p.Y,color,2));
            lines.Add(new((float)p.X,(float)p.Y - 3,(float)p.X,(float)p.Y + 3,color,2));
        }
    }
    private static bool Clip(ref (double X, double Y) a, ref (double X, double Y) b)
    {
        if (!double.IsFinite(a.X + a.Y + b.X + b.Y)) return false;
        double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        bool Edge(double p, double q)
        {
            if (p == 0) return q >= 0;
            double r = q / p;
            if (p < 0) { if (r > t1) return false; t0 = Math.Max(t0, r); }
            else { if (r < t0) return false; t1 = Math.Min(t1, r); }
            return true;
        }
        if (!Edge(-dx, a.X - 70) || !Edge(dx, 610 - a.X) || !Edge(-dy, a.Y - 60) || !Edge(dy, 380 - a.Y)) return false;
        b = (a.X + t1 * dx, a.Y + t1 * dy); a = (a.X + t0 * dx, a.Y + t0 * dy); return true;
    }
    private static void Center(List<Line> lines, float cx, float y, string text, int scale, int color = 1) =>
        Text(lines, cx - text.Length * 3f * scale, y, text, scale, color);
    private static void Text(List<Line> lines, float x, float y, string text, int scale, int color = 1)
    {
        foreach (char ch in text.ToUpperInvariant())
        {
            int gi = Alphabet.IndexOf(ch); if (gi < 0) gi = Alphabet.IndexOf('?');
            for (int row = 0; row < 7; row++)
            {
                int bits = Convert.ToInt32(Glyphs[gi].Substring(row * 2, 2), 16);
                for (int col = 0; col < 5; col++)
                    if ((bits & (1 << (4 - col))) != 0)
                    {
                        float px = x + col * scale, py = y + row * scale;
                        if (px >= 0 && px + scale < Width && py >= 0 && py < Height)
                            lines.Add(new(px, py, px + scale, py, color, scale));
                    }
            }
            x += 6 * scale; if (x >= Width - 5 * scale) break;
        }
    }
}
