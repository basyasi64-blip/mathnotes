namespace MathNotes.Core;

public sealed record StrokeBox(double MinX, double MinY, double MaxX, double MaxY)
{
    public double CenterY => (MinY + MaxY) / 2;
}

public sealed record StrokeGroup(IReadOnlyList<long> Ids, StrokeBox Box);

/// <summary>
/// Gruppiert Striche zu Formeln (neben- und untereinander). Identisch zur Python-Referenz
/// src/mathnotes/segmentation.py: zwei Striche gehoeren zusammen, wenn ihre Umrandungen horizontal
/// hoechstens KX*s und vertikal hoechstens KY*s auseinander liegen (s = Median der Strichgroessen).
/// </summary>
public static class StrokeGrouping
{
    public const double KX = 1.5;
    public const double KY = 0.5;
    public const double RowTol = 0.9;
    public const double MinS = 12.0;

    public static StrokeBox BoxOf(StrokeData s) => new(
        s.Points.Min(p => p.X), s.Points.Min(p => p.Y), s.Points.Max(p => p.X), s.Points.Max(p => p.Y));

    private static double Gap(double aMin, double aMax, double bMin, double bMax) =>
        Math.Max(0.0, Math.Max(aMin, bMin) - Math.Min(aMax, bMax));

    public static List<StrokeGroup> Group(IReadOnlyList<(long Id, StrokeData Stroke)> items)
    {
        var list = items.Where(i => i.Stroke.Points.Count > 0).ToList();
        if (list.Count == 0) return new List<StrokeGroup>();
        var boxes = list.Select(i => BoxOf(i.Stroke)).ToList();

        var sizes = boxes.Select(b => Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY)).OrderBy(v => v).ToList();
        var median = sizes.Count % 2 == 1
            ? sizes[sizes.Count / 2]
            : (sizes[sizes.Count / 2 - 1] + sizes[sizes.Count / 2]) / 2;
        var s = Math.Max(MinS, median);

        var n = list.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                var a = boxes[i];
                var b = boxes[j];
                var gx = Gap(a.MinX, a.MaxX, b.MinX, b.MaxX);
                var gy = Gap(a.MinY, a.MaxY, b.MinY, b.MaxY);
                if (gx <= KX * s && gy <= KY * s) parent[Find(i)] = Find(j);
            }

        var comps = new Dictionary<int, List<int>>();
        for (var i = 0; i < n; i++)
        {
            var r = Find(i);
            if (!comps.TryGetValue(r, out var l)) comps[r] = l = new List<int>();
            l.Add(i);
        }

        var groups = new List<StrokeGroup>();
        foreach (var idxs in comps.Values)
        {
            var bb = idxs.Select(i => boxes[i]).ToList();
            groups.Add(new StrokeGroup(
                idxs.Select(i => list[i].Id).OrderBy(x => x).ToList(),
                new StrokeBox(bb.Min(b => b.MinX), bb.Min(b => b.MinY), bb.Max(b => b.MaxX), bb.Max(b => b.MaxY))));
        }
        return ReadingOrder(groups, s);
    }

    /// <summary>Zeilenweise von oben nach unten, innerhalb einer Zeile von links nach rechts.</summary>
    public static List<StrokeGroup> ReadingOrder(List<StrokeGroup> groups, double s)
    {
        var rows = new List<List<StrokeGroup>>();
        foreach (var g in groups.OrderBy(g => g.Box.CenterY))
        {
            if (rows.Count > 0 && Math.Abs(g.Box.CenterY - rows[^1].Average(x => x.Box.CenterY)) <= RowTol * s)
                rows[^1].Add(g);
            else
                rows.Add(new List<StrokeGroup> { g });
        }
        return rows.SelectMany(row => row.OrderBy(g => g.Box.MinX)).ToList();
    }
}
