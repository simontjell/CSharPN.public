using CSharPN.Visualizer.Layout;

namespace CSharPN.Visualizer.Tests;

/// <summary>
/// Geometric quality measures of a <see cref="LayoutResult"/>, computed from the
/// drawing itself (node footprints and arc polylines) rather than from the layered
/// graph, so they measure what the user sees.
/// </summary>
internal static class LayoutGeometry
{
    public record Box(double Left, double Top, double Right, double Bottom)
    {
        public bool Overlaps(Box o) => Left < o.Right && o.Left < Right && Top < o.Bottom && o.Top < Bottom;
        public bool Contains((double X, double Y) p) => p.X > Left && p.X < Right && p.Y > Top && p.Y < Bottom;
    }

    public static Box Footprint(LayoutNode n)
    {
        var (w, h) = NodeMetrics.LayoutFootprint(n.Label, n.IsPlace);
        return new Box(n.X - w / 2, n.Y - h / 2, n.X + w / 2, n.Y + h / 2);
    }

    /// <summary>The shape only (ellipse / rectangle bounding box), without labels.</summary>
    public static Box Shape(LayoutNode n)
    {
        if (n.IsPlace)
        {
            var (_, rx, ry, _) = NodeMetrics.PlaceBox(n.Label);
            return new Box(n.X - rx, n.Y - ry, n.X + rx, n.Y + ry);
        }
        var (_, w, h, _) = NodeMetrics.TransBox(n.Label);
        return new Box(n.X - w / 2, n.Y - h / 2, n.X + w / 2, n.Y + h / 2);
    }

    public static List<(double X, double Y)> Polyline(LayoutResult r, LayoutArc a)
    {
        var byId = r.Nodes.ToDictionary(n => n.Id);
        var pts = new List<(double X, double Y)> { (byId[a.FromId].X, byId[a.FromId].Y) };
        pts.AddRange(a.Waypoints);
        pts.Add((byId[a.ToId].X, byId[a.ToId].Y));
        return pts;
    }

    public static IEnumerable<(LayoutNode A, LayoutNode B)> OverlappingNodes(LayoutResult r)
    {
        for (int i = 0; i < r.Nodes.Count; i++)
            for (int j = i + 1; j < r.Nodes.Count; j++)
                if (Footprint(r.Nodes[i]).Overlaps(Footprint(r.Nodes[j])))
                    yield return (r.Nodes[i], r.Nodes[j]);
    }

    /// <summary>Arcs whose polyline passes through the shape of a node that is not one of its endpoints.</summary>
    public static IEnumerable<(LayoutArc Arc, LayoutNode Node)> ArcsThroughNodes(LayoutResult r)
    {
        foreach (var a in r.Arcs)
        {
            var pts = Polyline(r, a);
            foreach (var n in r.Nodes)
            {
                if (n.Id == a.FromId || n.Id == a.ToId) continue;
                var box = Shape(n);
                for (int i = 0; i + 1 < pts.Count; i++)
                    if (SegmentIntersectsBox(pts[i], pts[i + 1], box)) { yield return (a, n); break; }
            }
        }
    }

    /// <summary>Number of pairs of arc segments that cross properly (touching at a shared node does not count).</summary>
    public static int CountCrossings(LayoutResult r)
    {
        var polylines = r.Arcs.Select(a => Polyline(r, a)).ToList();
        int crossings = 0;
        for (int i = 0; i < polylines.Count; i++)
            for (int j = i + 1; j < polylines.Count; j++)
                for (int s = 0; s + 1 < polylines[i].Count; s++)
                    for (int t = 0; t + 1 < polylines[j].Count; t++)
                        if (ProperIntersection(polylines[i][s], polylines[i][s + 1], polylines[j][t], polylines[j][t + 1]))
                            crossings++;
        return crossings;
    }

    private static bool ProperIntersection((double X, double Y) p1, (double X, double Y) p2,
                                           (double X, double Y) q1, (double X, double Y) q2)
    {
        if (Same(p1, q1) || Same(p1, q2) || Same(p2, q1) || Same(p2, q2)) return false;
        double d1 = Cross(q1, q2, p1), d2 = Cross(q1, q2, p2), d3 = Cross(p1, p2, q1), d4 = Cross(p1, p2, q2);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    private static bool Same((double X, double Y) a, (double X, double Y) b)
        => Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;

    private static double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) p)
        => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    private static bool SegmentIntersectsBox((double X, double Y) a, (double X, double Y) b, Box box)
    {
        // Liang–Barsky clipping of the segment against the box.
        double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        foreach (var (p, q) in new[] { (-dx, a.X - box.Left), (dx, box.Right - a.X), (-dy, a.Y - box.Top), (dy, box.Bottom - a.Y) })
        {
            if (p == 0) { if (q < 0) return false; continue; }
            double t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else       { if (t < t0) return false; if (t < t1) t1 = t; }
        }
        return t1 - t0 > 1e-9;
    }
}
