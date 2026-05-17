using CSharPN.Core;

namespace CSharPN.Visualizer.Layout;

// ── Data types ────────────────────────────────────────────────────────────────

public sealed record LayoutNode(string Id, string Label, bool IsPlace, double X, double Y);

/// <summary>
/// Arc between two nodes.  When <see cref="Waypoints"/> is non-empty the arc
/// should be drawn as a polyline through those waypoints
/// (used for back-arcs and forward arcs with orthogonal routing).
/// </summary>
public sealed record LayoutArc(
    string FromId,
    string ToId,
    IReadOnlyList<(double X, double Y)> Waypoints);

public sealed record LayoutResult(
    IReadOnlyList<LayoutNode> Nodes,
    IReadOnlyList<LayoutArc>  Arcs,
    double Width,
    double Height);

// ── LayoutEngine ──────────────────────────────────────────────────────────────

/// <summary>
/// Computes 2-D positions using the Sugiyama layered-graph framework:
///   1. Cycle removal  (DFS back-arc detection)
///   2. Layer assignment  (longest-path from sources)
///   3. Crossing minimisation  (barycenter method, 4 sweeps)
///   4. Coordinate assignment  (even spacing within layers)
///   5. Arc routing  (orthogonal Z-shape for forward arcs; Π-shape above
///      the net for back-arcs)
/// </summary>
public static class LayoutEngine
{
    private const double PlaceRX  = 38.0;
    private const double PlaceRY  = 25.0;
    private const double TransW   = 76.0;
    private const double TransH   = 32.0;
    private const double LayerGap = 220.0;   // horizontal distance between layer columns
    private const double NodeGap  = 130.0;   // vertical distance within a layer
    private const double Pad      = 90.0;    // canvas padding (also reserves space for back-arc curves)
    private const double RailStep = 26.0;    // horizontal spacing between parallel arc rails

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Compute layout from a CpnModel (flat view of all places and transitions).</summary>
    public static LayoutResult Compute(CpnModel model, double minW = 900, double minH = 500)
    {
        var nodes = model.Places.Select(p => (Name: p.Name, IsPlace: true))
            .Concat(model.Transitions.Select(t => (Name: t.Name, IsPlace: false)))
            .ToList();

        var edges = new List<(string From, string To)>();
        foreach (var t in model.Transitions)
            foreach (var av in t.GetArcViews())
            {
                var e = av.Direction == ArcDirection.Input
                    ? (av.Place.Name, t.Name)
                    : (t.Name, av.Place.Name);
                if (!edges.Contains(e)) edges.Add(e);
            }

        return Compute(nodes, edges, minW, minH);
    }

    /// <summary>Compute layout from raw node/edge lists (for page views).</summary>
    public static LayoutResult Compute(
        IReadOnlyList<(string Name, bool IsPlace)> allNames,
        IReadOnlyList<(string From, string To)> rawEdges,
        double minW = 900, double minH = 500)
    {
        int n = allNames.Count;
        if (n == 0) return new LayoutResult([], [], minW, minH);

        var nameIdx = allNames.Select((x, i) => (x.Name, i))
                              .ToDictionary(x => x.Name, x => x.i);

        // Build deduplicated indexed edge list
        var edges = new List<(int F, int T)>();
        foreach (var (from, to) in rawEdges)
        {
            if (!nameIdx.TryGetValue(from, out var fi) ||
                !nameIdx.TryGetValue(to, out var ti)) continue;
            var e = (fi, ti);
            if (!edges.Contains(e)) edges.Add(e);
        }

        // ── 1. Cycle removal: DFS back-arc detection ──────────────────────────
        var reversed = FindBackArcs(n, edges);

        // DAG: reversed arcs are flipped
        var dagEdges = edges.Select((e, i) =>
            reversed.Contains(i) ? (F: e.T, T: e.F) : (F: e.F, T: e.T)).ToList();

        // ── 2. Layer assignment (longest-path) ────────────────────────────────
        var layer = new int[n];
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (f, t) in dagEdges)
                if (layer[t] <= layer[f]) { layer[t] = layer[f] + 1; changed = true; }
        }
        int numLayers = layer.Max() + 1;

        // ── 3. Crossing minimisation (barycenter, 4 sweeps) ───────────────────
        var layerOrder = Enumerable.Range(0, numLayers)
            .Select(l => Enumerable.Range(0, n).Where(i => layer[i] == l).ToList())
            .ToList();

        for (int sweep = 0; sweep < 4; sweep++)
        {
            bool fwd = sweep % 2 == 0;
            for (int l = fwd ? 1 : numLayers - 2;
                 fwd ? l < numLayers : l >= 0;
                 l += fwd ? 1 : -1)
            {
                int adjL = fwd ? l - 1 : l + 1;

                var adjPos = new Dictionary<int, double>();
                for (int k = 0; k < layerOrder[adjL].Count; k++)
                    adjPos[layerOrder[adjL][k]] = k;

                var bary = new Dictionary<int, double>();
                for (int k = 0; k < layerOrder[l].Count; k++)
                {
                    int ni = layerOrder[l][k];
                    var nbrs = dagEdges
                        .Where(e => (e.F == ni && layer[e.T] == adjL) ||
                                    (e.T == ni && layer[e.F] == adjL))
                        .Select(e => e.F == ni ? e.T : e.F)
                        .ToList();
                    bary[ni] = nbrs.Count == 0
                        ? (double)k
                        : nbrs.Average(nb => adjPos.GetValueOrDefault(nb, (double)k));
                }

                layerOrder[l].Sort((a, b) => bary[a].CompareTo(bary[b]));
            }
        }

        // ── 4. Assign 2-D coordinates ─────────────────────────────────────────
        var pos = new (double X, double Y)[n];
        for (int l = 0; l < numLayers; l++)
        {
            var grp = layerOrder[l];
            double totalH = (grp.Count - 1) * NodeGap;
            double startY = Pad + (Math.Max(minH - 2 * Pad, totalH) - totalH) / 2.0;
            for (int k = 0; k < grp.Count; k++)
                pos[grp[k]] = (Pad + l * LayerGap, startY + k * NodeGap);
        }

        double w = Math.Max(minW, pos.Max(p => p.X) + Pad);
        double h = Math.Max(minH, pos.Max(p => p.Y) + Pad);

        // ── 5. Build output ───────────────────────────────────────────────────
        var layoutNodes = allNames.Select((nd, i) =>
            new LayoutNode(nd.Name, nd.Name, nd.IsPlace,
                           Math.Round(pos[i].X, 1), Math.Round(pos[i].Y, 1))).ToList();

        var backArcList = reversed.ToList();
        double maxY = pos.Max(p => p.Y);
        double maxX = pos.Max(p => p.X);

        // Compute staggered rail X values for adjacent-layer forward arcs
        var forwardRailX = AssignRailXForLayerPairs(edges, reversed, layer, pos);

        var layoutArcs = edges.Select((e, ei) =>
        {
            bool isBack = reversed.Contains(ei);
            List<(double X, double Y)> wps;
            if (isBack)
            {
                // Π-shape BELOW the net; each back arc gets its own row spaced below maxY
                int idx = backArcList.IndexOf(ei);
                double railY = maxY + 55 + idx * 36;
                wps = [(pos[e.F].X, railY), (pos[e.T].X, railY)];
            }
            else
            {
                int layerSpan = Math.Abs(layer[e.T] - layer[e.F]);
                double fromY = pos[e.F].Y;
                double toY   = pos[e.T].Y;

                if (layerSpan > 1)
                {
                    // Skip arc (crosses intermediate layers) — route on the RIGHT side
                    // to avoid passing through nodes in intermediate layers
                    int skipIdx = AssignSkipArcRailIndex(e, ei, edges, reversed, layer);
                    double railX = maxX + 55 + skipIdx * RailStep;
                    wps = [(railX, fromY), (railX, toY)];
                }
                else if (Math.Abs(fromY - toY) < 2)
                {
                    // Same-Y direct line — no waypoints needed
                    wps = [];
                }
                else
                {
                    // Z-shape: 2 waypoints at the staggered rail X between adjacent layers
                    double railX = forwardRailX[ei];
                    wps = [(railX, fromY), (railX, toY)];
                }
            }
            return new LayoutArc(allNames[e.F].Name, allNames[e.T].Name, wps);
        }).ToList();

        // Expand canvas to accommodate back-arc rails below maxY
        double bottomRail = backArcList.Count > 0
            ? maxY + 55 + (backArcList.Count - 1) * 36 + Pad
            : 0;
        h = Math.Max(h, bottomRail);

        // Expand canvas to accommodate skip-arc rails to the right of maxX
        int skipCount = edges.Select((e, ei) => !reversed.Contains(ei) && Math.Abs(layer[e.T] - layer[e.F]) > 1 ? 1 : 0).Sum();
        if (skipCount > 0)
            w = Math.Max(w, maxX + 55 + skipCount * RailStep + Pad);

        return new LayoutResult(layoutNodes, layoutArcs, w, h);
    }

    // ── Geometry helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Point on the border of an ellipse centred at (cx, cy) with semi-axes
    /// (rx, ry), in the direction of (tx, ty).
    /// </summary>
    public static (double x, double y) EllipseBorderPoint(
        double cx, double cy, double rx, double ry, double tx, double ty)
    {
        double dx = tx - cx, dy = ty - cy;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001) return (cx, cy);
        double denom = Math.Sqrt(dx * dx / (rx * rx) + dy * dy / (ry * ry));
        double s = 1.0 / denom;
        return (cx + s * dx, cy + s * dy);
    }

    public static (double x, double y) RectBorderPoint(
        double rx, double ry, double w, double h, double tx, double ty)
    {
        double dx = tx - rx, dy = ty - ry;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001) return (rx, ry);
        double hw = w / 2, hh = h / 2;
        double sx = Math.Abs(dx) < 0.001 ? double.MaxValue : hw / Math.Abs(dx);
        double sy = Math.Abs(dy) < 0.001 ? double.MaxValue : hh / Math.Abs(dy);
        double s  = Math.Min(sx, sy);
        return (rx + s * dx, ry + s * dy);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static HashSet<int> FindBackArcs(int n, List<(int F, int T)> edges)
    {
        var reversed = new HashSet<int>();
        var adj      = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < edges.Count; i++) adj[edges[i].F].Add(i);

        var color = new int[n];  // 0=white 1=gray 2=black

        void Dfs(int u)
        {
            color[u] = 1;
            foreach (int ei in adj[u])
            {
                int v = edges[ei].T;
                if      (color[v] == 1) reversed.Add(ei);  // back-arc
                else if (color[v] == 0) Dfs(v);
            }
            color[u] = 2;
        }

        for (int i = 0; i < n; i++) if (color[i] == 0) Dfs(i);
        return reversed;
    }

    /// <summary>
    /// Returns the 0-based index (slot) for a skip arc on the right-side rail,
    /// ordered by average endpoint Y so nearby arcs get adjacent slots.
    /// </summary>
    private static int AssignSkipArcRailIndex(
        (int F, int T) arc, int arcIdx,
        List<(int F, int T)> edges, HashSet<int> reversed, int[] layer)
    {
        // Collect all skip arcs sorted by avg Y layer index
        var skipArcs = edges
            .Select((e, ei) => (e, ei))
            .Where(x => !reversed.Contains(x.ei) && Math.Abs(layer[x.e.T] - layer[x.e.F]) > 1)
            .OrderBy(x => (layer[x.e.F] + layer[x.e.T]) / 2.0)
            .ThenBy(x => x.ei)
            .Select(x => x.ei)
            .ToList();
        return skipArcs.IndexOf(arcIdx);
    }

    /// <summary>
    /// Assigns a staggered rail X value to each forward arc.
    /// Arcs that share the same (fromLayer, toLayer) pair are staggered by 16 px
    /// increments centred on the midpoint X between the two layer columns.
    /// </summary>
    private static double[] AssignRailXForLayerPairs(
        List<(int F, int T)> edges,
        HashSet<int> reversed,
        int[] layer,
        (double X, double Y)[] pos)
    {
        var result = new double[edges.Count];

        // Group forward arc indices by their (fromLayer, toLayer) pair
        var groups = new Dictionary<(int, int), List<int>>();
        for (int ei = 0; ei < edges.Count; ei++)
        {
            if (reversed.Contains(ei)) continue;
            var (f, t) = edges[ei];
            var key = (layer[f], layer[t]);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }
            list.Add(ei);
        }

        // For each group, sort arcs by average endpoint Y then assign staggered rails.
        // Sorting by avgY means arcs with similar vertical trajectories get adjacent rails,
        // which minimises visual crossings between sibling arcs in the same corridor.
        foreach (var (_, arcIndices) in groups)
        {
            arcIndices.Sort((ai, bi) =>
            {
                double avgA = (pos[edges[ai].F].Y + pos[edges[ai].T].Y) / 2.0;
                double avgB = (pos[edges[bi].F].Y + pos[edges[bi].T].Y) / 2.0;
                return avgA.CompareTo(avgB);
            });

            int count = arcIndices.Count;
            // midpoint X of the corridor between the two layer columns
            double midX = (pos[edges[arcIndices[0]].F].X + pos[edges[arcIndices[0]].T].X) / 2.0;
            double startOffset = -(count - 1) / 2.0 * RailStep;
            for (int i = 0; i < count; i++)
                result[arcIndices[i]] = midX + startOffset + i * RailStep;
        }

        return result;
    }
}
