namespace MathNotes.Core;

public static class StrokeGeometry
{
    /// <summary>True, wenn der Punkt (x,y) hoechstens r von der Strichlinie entfernt ist.</summary>
    public static bool Hits(IEnumerable<(double X, double Y)> points, double x, double y, double r)
    {
        (double X, double Y)? prev = null;
        foreach (var q in points)
        {
            if (prev is null)
            {
                if (Dist(q.X, q.Y, x, y) <= r) return true;
            }
            else if (SegmentDistance(x, y, prev.Value.X, prev.Value.Y, q.X, q.Y) <= r) return true;
            prev = q;
        }
        return false;
    }

    private static double Dist(double ax, double ay, double bx, double by) =>
        Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    private static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        double t = len2 == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        return Dist(px, py, ax + t * dx, ay + t * dy);
    }
}
