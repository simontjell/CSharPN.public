namespace CSharPN.Visualizer.Layout.Sugiyama;

/// <summary>
/// Phase 4 of the Sugiyama framework: assign a coordinate along the layer to every node
/// with the algorithm of Brandes &amp; Köpf, "Fast and Simple Horizontal Coordinate
/// Assignment" (GD 2001), including the two corrections of Brandes, Walter &amp; Zink,
/// "Erratum: Fast and Simple Horizontal Coordinate Assignment" (arXiv:2008.01252, 2020).
/// </summary>
/// <remarks>
/// <para>
/// The algorithm keeps the node order from crossing minimisation, guarantees a minimum
/// separation between neighbours, draws long edges (chains of dummy nodes) as straight
/// lines wherever possible, and centres nodes over their median neighbours. It is run for
/// the four combinations of vertical direction (aligning to upper or lower neighbours) and
/// horizontal direction (compacting to the left or right); the final coordinate is the
/// average of the two median candidates, after aligning the four layouts to the narrowest
/// one (Brandes &amp; Köpf, §4 "Balancing").
/// </para>
/// <para>
/// Node sizes enter through the separation function: two neighbours <c>u, v</c> in a layer
/// must be at least <c>sep(u, v)</c> apart (Rüegg et al. 2015 show that the algorithm
/// carries over unchanged to this setting).
/// </para>
/// <para>
/// Terminology follows the papers, which draw layers top to bottom and assign the
/// <em>horizontal</em> coordinate x. The caller maps that coordinate to whichever screen
/// axis runs along the layers.
/// </para>
/// </remarks>
internal static class BrandesKoepf
{
    private const double Inf = double.PositiveInfinity;

    /// <summary>
    /// Assigns <see cref="LNode.Coord"/> for every node in <paramref name="layers"/>
    /// (ordered lists, with <see cref="LNode.Pos"/> set).
    /// </summary>
    public static void Assign(List<List<LNode>> layers, Func<LNode, LNode, double> separation)
    {
        var all = layers.SelectMany(l => l).ToList();
        if (all.Count == 0) return;

        var candidates = new List<Dictionary<LNode, double>>();
        double[] widths = new double[4];
        int k = 0;
        foreach (bool downward in new[] { true, false })
        foreach (bool leftToRight in new[] { true, false })
        {
            var x = RunOne(layers, downward, leftToRight, separation);
            candidates.Add(x);
            widths[k++] = x.Values.Max() - x.Values.Min();
        }

        // Balancing (Alg. 4): align every layout to the narrowest one — left-aligned
        // layouts by their minimum, right-aligned layouts by their maximum.
        int narrowest = Array.IndexOf(widths, widths.Min());
        double minRef = candidates[narrowest].Values.Min();
        double maxRef = candidates[narrowest].Values.Max();
        for (int i = 0; i < 4; i++)
        {
            bool leftToRight = i % 2 == 0;
            double delta = leftToRight
                ? minRef - candidates[i].Values.Min()
                : maxRef - candidates[i].Values.Max();
            foreach (var v in all) candidates[i][v] += delta;
        }

        foreach (var v in all)
        {
            var xs = candidates.Select(c => c[v]).OrderBy(d => d).ToArray();
            v.Coord = (xs[1] + xs[2]) / 2;
        }
    }

    // ── One of the four directional layouts ───────────────────────────────────

    private static Dictionary<LNode, double> RunOne(
        List<List<LNode>> inputLayers, bool downward, bool leftToRight,
        Func<LNode, LNode, double> separation)
    {
        // Orient the problem so that we always align to "upper" neighbours and compact "leftwards".
        var layers = inputLayers.Select(l => leftToRight ? l.ToList() : Enumerable.Reverse(l).ToList()).ToList();
        if (!downward) layers.Reverse();

        var ctx = new Context(layers, downward, separation);
        MarkType1Conflicts(ctx);
        VerticalAlignment(ctx);
        HorizontalCompaction(ctx);

        var result = new Dictionary<LNode, double>(ReferenceEqualityComparer.Instance);
        foreach (var v in ctx.Nodes) result[v] = leftToRight ? ctx.X[v] : -ctx.X[v];
        return result;
    }

    private sealed class Context
    {
        public readonly List<List<LNode>> Layers;
        public readonly List<LNode> Nodes;
        public readonly Dictionary<LNode, int> Pos   = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<LNode, int> LayerOf = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<LNode, LNode> Root  = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<LNode, LNode> Align = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<LNode, LNode> Sink  = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<LNode, double> Shift = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<LNode, double> X     = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<(LNode Upper, LNode Lower)> Marked = [];
        private readonly bool _downward;
        private readonly Func<LNode, LNode, double> _sep;

        public Context(List<List<LNode>> layers, bool downward, Func<LNode, LNode, double> sep)
        {
            Layers = layers;
            _downward = downward;
            _sep = sep;
            Nodes = layers.SelectMany(l => l).ToList();
            for (int i = 0; i < layers.Count; i++)
                for (int k = 0; k < layers[i].Count; k++)
                {
                    Pos[layers[i][k]] = k;
                    LayerOf[layers[i][k]] = i;
                }
        }

        /// <summary>Neighbours in the layer above (in the oriented problem), sorted by position.</summary>
        public List<LNode> UpperNeighbours(LNode v) =>
            (_downward ? v.In.Select(s => s.From) : v.Out.Select(s => s.To))
                .OrderBy(u => Pos[u]).ToList();

        /// <summary>Segments to the layer above that are inner segments (dummy–dummy).</summary>
        public LNode? InnerUpperNeighbour(LNode v)
        {
            if (!v.IsDummy) return null;
            var segs = _downward ? v.In.Where(s => s.IsInner).Select(s => s.From)
                                 : v.Out.Where(s => s.IsInner).Select(s => s.To);
            return segs.FirstOrDefault();
        }

        public LNode? Pred(LNode v) => Pos[v] > 0 ? Layers[LayerOf[v]][Pos[v] - 1] : null;
        public double Sep(LNode u, LNode v) => _sep(u, v);
    }

    /// <summary>
    /// Alg. 1 (Brandes &amp; Köpf): mark type-1 conflicts — segments that cross an inner
    /// segment — so that inner segments (long edges) always win the alignment.
    /// </summary>
    private static void MarkType1Conflicts(Context c)
    {
        for (int i = 0; i + 1 < c.Layers.Count; i++)
        {
            var lower = c.Layers[i + 1];
            int k0 = 0, l = 0;
            for (int l1 = 0; l1 < lower.Count; l1++)
            {
                var inner = c.InnerUpperNeighbour(lower[l1]);
                if (l1 == lower.Count - 1 || inner is not null)
                {
                    int k1 = c.Layers[i].Count - 1;
                    if (inner is not null) k1 = c.Pos[inner];
                    while (l <= l1)
                    {
                        foreach (var u in c.UpperNeighbours(lower[l]))
                        {
                            int k = c.Pos[u];
                            if (k < k0 || k > k1) c.Marked.Add((u, lower[l]));
                        }
                        l++;
                    }
                    k0 = k1;
                }
            }
        }
    }

    /// <summary>Alg. 2: align every node with one of its median upper neighbours, forming blocks.</summary>
    private static void VerticalAlignment(Context c)
    {
        foreach (var v in c.Nodes) { c.Root[v] = v; c.Align[v] = v; }

        foreach (var layer in c.Layers)
        {
            int r = -1;
            foreach (var v in layer)
            {
                var nbrs = c.UpperNeighbours(v);
                int d = nbrs.Count;
                if (d == 0) continue;
                foreach (int m in new[] { (d - 1) / 2, d / 2 }.Distinct())
                {
                    if (c.Align[v] != v) continue;
                    var u = nbrs[m];
                    if (!c.Marked.Contains((u, v)) && r < c.Pos[u])
                    {
                        c.Align[u] = v;
                        c.Root[v] = c.Root[u];
                        c.Align[v] = c.Root[v];
                        r = c.Pos[u];
                    }
                }
            }
        }
    }

    /// <summary>Alg. 3b of the erratum: place blocks relative to their class sink, then shift classes.</summary>
    private static void HorizontalCompaction(Context c)
    {
        foreach (var v in c.Nodes) { c.Sink[v] = v; c.Shift[v] = Inf; }
        c.X.Clear();

        // Coordinates relative to the class sink.
        foreach (var v in c.Nodes)
            if (c.Root[v] == v) PlaceBlock(c, v);

        // Class offsets, tracing the lower contour of each class from its sink (Lemma 1 of the erratum).
        for (int i = 0; i < c.Layers.Count; i++)
        {
            var first = c.Layers[i][0];
            if (c.Sink[first] != first) continue;
            if (double.IsPositiveInfinity(c.Shift[first])) c.Shift[first] = 0;

            int j = i, k = 0;
            LNode v;
            do
            {
                v = c.Layers[j][k];
                while (c.Align[v] != c.Root[v])
                {
                    v = c.Align[v];
                    j++;
                    var u = c.Pred(v);
                    if (u is not null)
                        c.Shift[c.Sink[u]] = Math.Min(c.Shift[c.Sink[u]],
                            c.Shift[c.Sink[v]] + c.X[v] - (c.X[u] + c.Sep(u, v)));
                }
                k = c.Pos[v] + 1;
            }
            while (k < c.Layers[j].Count && c.Sink[v] == c.Sink[c.Layers[j][k]]);
        }

        // Absolute coordinates.
        foreach (var v in c.Nodes) c.X[v] += c.Shift[c.Sink[v]];
    }

    /// <summary>Alg. 3a of the erratum: place a block (all of it, not just the root).</summary>
    private static void PlaceBlock(Context c, LNode v)
    {
        if (c.X.ContainsKey(v)) return;
        c.X[v] = 0;
        var w = v;
        do
        {
            var pred = c.Pred(w);
            if (pred is not null)
            {
                var u = c.Root[pred];
                PlaceBlock(c, u);
                if (c.Sink[v] == v) c.Sink[v] = c.Sink[u];
                if (c.Sink[v] == c.Sink[u])
                    c.X[v] = Math.Max(c.X[v], c.X[u] + c.Sep(pred, w));
            }
            w = c.Align[w];
        }
        while (w != v);

        // Align the whole block with its root.
        while (c.Align[w] != v)
        {
            w = c.Align[w];
            c.X[w] = c.X[v];
            c.Sink[w] = c.Sink[v];
        }
    }
}
