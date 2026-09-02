using CSharPN.Core;
using CSharPN.Visualizer.Layout.Sugiyama;

namespace CSharPN.Visualizer.Layout;

// ── Data types ────────────────────────────────────────────────────────────────

public sealed record LayoutNode(string Id, string Label, bool IsPlace, double X, double Y);

/// <summary>
/// Arc between two nodes. When <see cref="Waypoints"/> is non-empty the arc should be
/// drawn as a polyline through those waypoints (bends of long arcs routed around
/// intermediate layers, and the two offset lines of a place/transition double arc).
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

/// <summary>A node to be laid out, with the footprint the drawing reserves for it.</summary>
public sealed record LayoutNodeSpec(string Id, string Label, bool IsPlace, double W, double H);

/// <summary>Spacing parameters of the layout (all in SVG user units, i.e. pixels at zoom 1).</summary>
public sealed record LayoutOptions
{
    /// <summary>Border-to-border distance between two neighbouring layer columns.</summary>
    public double LayerGap { get; init; } = 120;
    /// <summary>Border-to-border distance between two neighbouring nodes in a column.</summary>
    public double NodeGap { get; init; } = 40;
    /// <summary>Distance between a bend of a long arc and its neighbours in the column.</summary>
    public double EdgeGap { get; init; } = 28;
    /// <summary>Vertical distance between two connected components of the net.</summary>
    public double ComponentGap { get; init; } = 90;
    /// <summary>Canvas padding around the drawing.</summary>
    public double Padding { get; init; } = 70;
    /// <summary>Perpendicular offset of each line of a place/transition double arc.</summary>
    public double DoubleArcOffset { get; init; } = 12;

    public static LayoutOptions Default { get; } = new();
}

// ── LayoutEngine ──────────────────────────────────────────────────────────────

/// <summary>
/// Automatic layout of a Petri net with the Sugiyama framework (Sugiyama, Tagawa &amp; Toda
/// 1981), left to right: layers are drawn as columns and the flow runs from the initially
/// marked places towards the right.
/// </summary>
/// <remarks>
/// The five phases and the algorithms chosen for each (see <c>LAYOUT.md</c> for the
/// rationale and references):
/// <list type="number">
///   <item><description><b>Cycle removal</b> — depth-first search from the initially marked
///   places; back edges are reversed (<see cref="CycleRemoval"/>).</description></item>
///   <item><description><b>Layer assignment</b> — network simplex of Gansner et al. (1993),
///   which minimises total arc length and therefore the number of long arcs
///   (<see cref="NetworkSimplex"/>). Places and transitions land in alternating columns.</description></item>
///   <item><description><b>Crossing minimisation</b> — layer sweep with barycenter / median
///   ordering and greedy transposition, keeping the ordering with the fewest crossings as
///   counted exactly by Barth, Jünger &amp; Mutzel (<see cref="CrossingMinimizer"/>).</description></item>
///   <item><description><b>Coordinate assignment</b> — Brandes &amp; Köpf (2001) with the
///   corrections of Brandes, Walter &amp; Zink (2020) (<see cref="BrandesKoepf"/>): long arcs
///   are straight, nodes are centred over their neighbours, node sizes are respected.</description></item>
///   <item><description><b>Arc routing</b> — long arcs bend at the dummy nodes of their
///   intermediate layers; the two lines of a place/transition double arc are offset to
///   opposite sides.</description></item>
/// </list>
/// Connected components are laid out independently and stacked vertically. The result is
/// deterministic: all ties are resolved by declaration order in the model.
/// </remarks>
public static class LayoutEngine
{
    /// <summary>Measurements of the last layout computed on this thread (for tests and tuning).</summary>
    internal sealed class LayoutDiagnostics
    {
        /// <summary>Weighted segment crossings of the layered graph, summed over components.</summary>
        public long LayeredCrossings { get; set; }
        /// <summary>Number of dummy nodes inserted for long arcs.</summary>
        public int DummyCount { get; set; }
        /// <summary>The final ordered layers of every component (real and dummy nodes).</summary>
        public List<List<List<LNode>>> ComponentLayers { get; } = [];
    }

    [ThreadStatic] private static LayoutDiagnostics? _diagnostics;
    internal static LayoutDiagnostics Diagnostics
    {
        get => _diagnostics ??= new LayoutDiagnostics();
        private set => _diagnostics = value;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Compute layout from a CpnModel (flat view of all places and transitions).</summary>
    public static LayoutResult Compute(CpnModel model, double minW = 900, double minH = 500, LayoutOptions? options = null)
    {
        var specs = new List<LayoutNodeSpec>();
        foreach (var p in model.Places)
        {
            var (w, h) = NodeMetrics.LayoutFootprint(p.Name, isPlace: true);
            specs.Add(new LayoutNodeSpec(p.Name, p.Name, true, w, h));
        }
        foreach (var t in model.Transitions)
        {
            var (w, h) = NodeMetrics.LayoutFootprint(t.Name, isPlace: false, hasGuard: t.GuardLabel != "");
            specs.Add(new LayoutNodeSpec(t.Name, t.Name, false, w, h));
        }

        var edges = new List<(string From, string To)>();
        foreach (var t in model.Transitions)
            foreach (var av in t.GetArcViews())
            {
                var e = av.Direction == ArcDirection.Input
                    ? (av.Place.Name, t.Name)
                    : (t.Name, av.Place.Name);
                if (!edges.Contains(e)) edges.Add(e);
            }

        // The behaviour starts where the tokens are: initially marked places lead the flow.
        var sources = model.Places.Where(p => p.InitialTokenCount > 0).Select(p => p.Name).ToHashSet();

        return Compute(specs, edges, sources, minW, minH, options ?? LayoutOptions.Default);
    }

    /// <summary>Compute layout from raw node/edge lists (for hierarchical page views).</summary>
    public static LayoutResult Compute(
        IReadOnlyList<(string Name, bool IsPlace)> allNames,
        IReadOnlyList<(string From, string To)> rawEdges,
        double minW = 900, double minH = 500, LayoutOptions? options = null)
    {
        var specs = allNames.Select(n =>
        {
            var (w, h) = NodeMetrics.LayoutFootprint(n.Name, n.IsPlace);
            return new LayoutNodeSpec(n.Name, n.Name, n.IsPlace, w, h);
        }).ToList();
        return Compute(specs, rawEdges, null, minW, minH, options ?? LayoutOptions.Default);
    }

    /// <summary>
    /// Compute layout from node specifications with explicit footprints.
    /// </summary>
    /// <param name="preferredSources">
    /// Ids of nodes the flow should start from (initially marked places); may be null.
    /// </param>
    public static LayoutResult Compute(
        IReadOnlyList<LayoutNodeSpec> specs,
        IReadOnlyList<(string From, string To)> rawEdges,
        IReadOnlySet<string>? preferredSources,
        double minW, double minH, LayoutOptions options)
    {
        Diagnostics = new LayoutDiagnostics();
        if (specs.Count == 0) return new LayoutResult([], [], minW, minH);

        // ── Build the graph ──────────────────────────────────────────────────
        var nodes = specs.Select((s, i) => new LNode
        {
            Id = s.Id, IsPlace = s.IsPlace, W = s.W, H = s.H, Order = i
        }).ToList();
        var byId = new Dictionary<string, LNode>();
        foreach (var n in nodes) byId.TryAdd(n.Id!, n);

        var edges = new List<LEdge>();
        var seen  = new HashSet<(string, string)>();
        foreach (var (from, to) in rawEdges)
        {
            if (!byId.TryGetValue(from, out var f) || !byId.TryGetValue(to, out var t)) continue;
            if (from == to || !seen.Add((from, to))) continue;
            var e = new LEdge { From = f, To = t, Order = edges.Count };
            e.Arcs.Add(new ArcRef(from, to, OppositeToEdge: false));
            edges.Add(e);
        }

        // ── Lay out every connected component on its own ────────────────────
        var components = ConnectedComponents(nodes, edges);
        var laidOut = new List<(List<LNode> Nodes, List<LEdge> Edges)>();
        foreach (var comp in components)
        {
            var compEdges = edges.Where(e => comp.Contains(e.From)).ToList();
            laidOut.Add(LayoutComponent(comp, compEdges, preferredSources, options));
        }

        // ── Columns: shared across components so the layers line up ─────────
        int layerCount = laidOut.SelectMany(c => c.Nodes).Max(n => n.Layer) + 1;
        var maxW = new double[layerCount];
        foreach (var n in laidOut.SelectMany(c => c.Nodes))
            maxW[n.Layer] = Math.Max(maxW[n.Layer], n.W);

        var colX = new double[layerCount];
        colX[0] = options.Padding + maxW[0] / 2;
        for (int l = 1; l < layerCount; l++)
            colX[l] = colX[l - 1] + maxW[l - 1] / 2 + options.LayerGap + maxW[l] / 2;

        // ── Stack components vertically ─────────────────────────────────────
        var y = new Dictionary<LNode, double>(ReferenceEqualityComparer.Instance);
        double curY = options.Padding;
        foreach (var (compNodes, _) in laidOut)
        {
            double minY = compNodes.Min(n => n.Coord - n.H / 2);
            double maxY = compNodes.Max(n => n.Coord + n.H / 2);
            foreach (var n in compNodes) y[n] = n.Coord - minY + curY;
            curY += maxY - minY + options.ComponentGap;
        }
        double contentBottom = curY - options.ComponentGap + options.Padding;
        double contentRight  = colX[^1] + maxW[^1] / 2 + options.Padding;

        // Centre a small drawing on the minimum canvas.
        double dy = contentBottom < minH ? (minH - contentBottom) / 2 : 0;
        double dx = contentRight  < minW ? (minW - contentRight) / 2 : 0;

        // ── Output ──────────────────────────────────────────────────────────
        var positions = new Dictionary<LNode, (double X, double Y)>(ReferenceEqualityComparer.Instance);
        foreach (var n in laidOut.SelectMany(c => c.Nodes))
            positions[n] = (Math.Round(colX[n.Layer] + dx, 1), Math.Round(y[n] + dy, 1));

        var layoutNodes = specs
            .Select(s => byId[s.Id])
            .Select(n => new LayoutNode(n.Id!, n.Id!, n.IsPlace, positions[n].X, positions[n].Y))
            .ToList();

        var layoutArcs = new List<LayoutArc>();
        foreach (var e in laidOut.SelectMany(c => c.Edges).OrderBy(e => e.Order))
        {
            // A long arc runs horizontally through the band of every intermediate column at
            // the height of its dummy node and only slants in the gaps between columns (the
            // box-corridor routing of dot / the polyline router of ELK), so it can never clip
            // a node that sits above or below the dummy in the same column.
            var polyline = new List<(double X, double Y)> { positions[e.From] };
            foreach (var d in e.Dummies)
            {
                double half = maxW[d.Layer] / 2;
                var (x, yy) = positions[d];
                polyline.Add((Math.Round(x - half, 1), yy));
                polyline.Add((Math.Round(x + half, 1), yy));
            }
            polyline.Add(positions[e.To]);

            foreach (var arc in e.Arcs)
            {
                List<(double X, double Y)> pts;
                if (e.Bidirectional)
                    pts = OffsetPolyline(polyline, arc.OppositeToEdge ? -options.DoubleArcOffset : options.DoubleArcOffset);
                else
                    pts = SimplifyInterior(polyline);
                if (arc.OppositeToEdge) pts.Reverse();
                layoutArcs.Add(new LayoutArc(arc.FromId, arc.ToId, pts));
            }
        }

        return new LayoutResult(layoutNodes, layoutArcs, Math.Max(minW, contentRight), Math.Max(minH, contentBottom));
    }

    // ── Sugiyama phases for one connected component ───────────────────────────

    private static (List<LNode> Nodes, List<LEdge> Edges) LayoutComponent(
        List<LNode> nodes, List<LEdge> edges, IReadOnlySet<string>? preferredSources, LayoutOptions options)
    {
        // 1. Cycle removal
        IEnumerable<LNode> sources = preferredSources is null
            ? []
            : nodes.Where(n => preferredSources.Contains(n.Id!)).OrderBy(n => n.Order);
        CycleRemoval.Run(nodes, edges, sources);

        // Merge parallel edges (in particular place/transition double arcs) into one edge.
        var merged = new List<LEdge>();
        var byPair = new Dictionary<(LNode, LNode), LEdge>();
        foreach (var e in edges)
        {
            if (byPair.TryGetValue((e.From, e.To), out var existing))
            {
                existing.Weight += e.Weight;
                existing.Arcs.AddRange(e.Arcs);
            }
            else
            {
                byPair[(e.From, e.To)] = e;
                merged.Add(e);
            }
        }

        // 2. Layer assignment
        var index = nodes.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        var rank = NetworkSimplex.Rank(nodes.Count, merged.Select(e => (index[e.From], index[e.To], e.Weight)).ToList());
        foreach (var n in nodes) n.Layer = rank[index[n]];

        // Places in even columns, transitions in odd columns (parity is uniform within a
        // component, so it suffices to look at one node).
        var probe = nodes[0];
        if ((probe.Layer % 2 == 1) == probe.IsPlace)
            foreach (var n in nodes) n.Layer++;

        // 3. Proper layering: subdivide long edges with dummy nodes
        var all = new List<LNode>(nodes);
        foreach (var e in merged)
        {
            var prev = e.From;
            for (int l = e.From.Layer + 1; l < e.To.Layer; l++)
            {
                var d = new LNode
                {
                    Id = null, IsPlace = false, W = 0, H = 0, Order = e.Order, DummyOf = e,
                    Layer = l, DfsOrder = e.From.DfsOrder + 0.5
                };
                e.Dummies.Add(d);
                all.Add(d);
                AddSegment(prev, d, e);
                prev = d;
            }
            AddSegment(prev, e.To, e);
        }

        // 4. Crossing minimisation. The layer sweep is a local heuristic that cannot move a
        //    whole dummy chain at once, so it is started from several deterministic initial
        //    orders and the best result is kept: DFS order with the chains of feedback arcs
        //    (reversed edges) below the main flow, the same with them above, and model order.
        int layerCount = all.Max(n => n.Layer) + 1;
        var initialOrders = new List<Func<LNode, double>>
        {
            n => n.IsDummy && n.DummyOf!.IsReversed ? double.MaxValue : n.DfsOrder,
            n => n.IsDummy && n.DummyOf!.IsReversed ? double.MinValue : n.DfsOrder,
            n => n.IsDummy ? n.DummyOf!.From.Order + 0.5 : n.Order,
        };
        List<List<LNode>>? layers = null;
        long bestCrossings = long.MaxValue;
        foreach (var key in initialOrders)
        {
            var candidate = Enumerable.Range(0, layerCount)
                .Select(l => all.Where(n => n.Layer == l).OrderBy(key).ThenBy(n => n.Order).ToList())
                .ToList();
            CrossingMinimizer.Run(candidate);
            long crossings = CrossingMinimizer.CountCrossings(candidate);
            if (crossings < bestCrossings)
            {
                bestCrossings = crossings;
                layers = candidate;
            }
            if (crossings == 0) break;
        }
        foreach (var layer in layers!)
            for (int i = 0; i < layer.Count; i++) layer[i].Pos = i;
        Diagnostics.LayeredCrossings += bestCrossings;
        Diagnostics.DummyCount += all.Count - nodes.Count;
        Diagnostics.ComponentLayers.Add(layers);

        // 5. Coordinate assignment along the columns
        BrandesKoepf.Assign(layers, (u, v) =>
            u.H / 2 + v.H / 2 + (u.IsDummy || v.IsDummy ? options.EdgeGap : options.NodeGap));

        return (all, merged);
    }

    private static void AddSegment(LNode from, LNode to, LEdge edge)
    {
        var s = new LSegment(from, to, edge.Weight, edge);
        from.Out.Add(s);
        to.In.Add(s);
    }

    private static List<List<LNode>> ConnectedComponents(List<LNode> nodes, List<LEdge> edges)
    {
        var parent = Enumerable.Range(0, nodes.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        var index = nodes.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        foreach (var e in edges)
        {
            int a = Find(index[e.From]), b = Find(index[e.To]);
            if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
        }
        return nodes
            .GroupBy(n => Find(index[n]))
            .OrderBy(g => g.Key)                 // components ordered by their first node in the model
            .Select(g => g.OrderBy(n => n.Order).ToList())
            .ToList();
    }

    // ── Arc routing helpers ───────────────────────────────────────────────────

    /// <summary>Interior points of a polyline with collinear bends removed (endpoints are node centres).</summary>
    private static List<(double X, double Y)> SimplifyInterior(List<(double X, double Y)> polyline)
    {
        var pts = new List<(double X, double Y)>(polyline);
        bool changed = true;
        while (changed && pts.Count > 2)
        {
            changed = false;
            for (int i = 1; i + 1 < pts.Count; i++)
            {
                if (DistanceToLine(pts[i], pts[i - 1], pts[i + 1]) < 0.5)
                {
                    pts.RemoveAt(i);
                    changed = true;
                    break;
                }
            }
        }
        return pts.GetRange(1, pts.Count - 2);
    }

    /// <summary>
    /// Interior points of a polyline shifted sideways by <paramref name="offset"/>, so that
    /// the two arcs of a double arc are drawn as distinct parallel lines. A straight arc gets
    /// two bends at one third and two thirds of its length.
    /// </summary>
    private static List<(double X, double Y)> OffsetPolyline(List<(double X, double Y)> polyline, double offset)
    {
        var pts = SimplifyInterior(polyline);
        var full = new List<(double X, double Y)> { polyline[0] };
        if (pts.Count == 0)
        {
            var (ax, ay) = polyline[0];
            var (bx, by) = polyline[^1];
            full.Add((ax + (bx - ax) / 3, ay + (by - ay) / 3));
            full.Add((ax + 2 * (bx - ax) / 3, ay + 2 * (by - ay) / 3));
        }
        else full.AddRange(pts);
        full.Add(polyline[^1]);

        var result = new List<(double X, double Y)>();
        for (int i = 1; i + 1 < full.Count; i++)
        {
            double dx = full[i + 1].X - full[i - 1].X, dy = full[i + 1].Y - full[i - 1].Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) { result.Add(full[i]); continue; }
            result.Add((Math.Round(full[i].X - dy / len * offset, 1), Math.Round(full[i].Y + dx / len * offset, 1)));
        }
        return result;
    }

    private static double DistanceToLine((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        return Math.Abs(dx * (p.Y - a.Y) - dy * (p.X - a.X)) / len;
    }

    // ── Geometry helpers (used by the renderer) ───────────────────────────────

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
}
