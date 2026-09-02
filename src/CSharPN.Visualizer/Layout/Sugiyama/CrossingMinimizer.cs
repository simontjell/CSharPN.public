namespace CSharPN.Visualizer.Layout.Sugiyama;

/// <summary>
/// Phase 3 of the Sugiyama framework: order the nodes within each layer so that as few
/// segments as possible cross.
/// </summary>
/// <remarks>
/// <para>
/// The one-sided crossing minimisation problem is NP-hard (Eades &amp; Wormald 1994), so
/// heuristics are combined, each covering a weakness of the previous one:
/// </para>
/// <list type="number">
///   <item><description>The classic layer-by-layer sweep (Sugiyama, Tagawa &amp; Toda 1981):
///   each layer is reordered by the weighted barycenter — and, when that stalls, the weighted
///   median (Gansner et al. 1993) — of its neighbours in the layer that was just fixed.</description></item>
///   <item><description>After every sweep the greedy <em>transpose</em> heuristic of Gansner
///   et al. swaps adjacent nodes, and <em>sifting</em> (Matuszewski, Schönfeld &amp; Molitor
///   1999) re-inserts every node at its best position, both evaluating the true crossing
///   count of the layer with its two neighbours.</description></item>
///   <item><description>Finally a dynamic programme over the layers chooses, among all orderings
///   of small layers and all single-node relocations of large ones, the combination with the
///   fewest crossings — a coordinated move across several layers that none of the local
///   heuristics can make (see <see cref="DynamicProgrammingRefinement"/>).</description></item>
/// </list>
/// <para>
/// Crossings are always counted exactly with the bilayer cross-counting algorithm of Barth,
/// Jünger &amp; Mutzel (2004), and the best ordering seen is kept. Nodes without neighbours in
/// the fixed layer keep their position and sorting is stable, so the initial order resolves
/// all ties and the result is deterministic.
/// </para>
/// </remarks>
internal static class CrossingMinimizer
{
    public static void Run(List<List<LNode>> layers, int maxSweeps = 40)
    {
        if (layers.Count == 0) return;
        AssignPositions(layers);

        var best = Snapshot(layers);
        long bestCount = CountCrossings(layers);
        int stale = 0;

        for (int sweep = 0; sweep < maxSweeps && bestCount > 0; sweep++)
        {
            bool useMedian = stale >= 2;   // diversify once the barycenter stalls
            bool downward  = sweep % 2 == 0;

            if (downward)
                for (int l = 1; l < layers.Count; l++) Reorder(layers[l], upper: true, useMedian);
            else
                for (int l = layers.Count - 2; l >= 0; l--) Reorder(layers[l], upper: false, useMedian);

            Transpose(layers);
            Sift(layers);

            long count = CountCrossings(layers);
            if (count < bestCount)
            {
                bestCount = count;
                best = Snapshot(layers);
                stale = 0;
            }
            else if (++stale >= 4) break;
        }

        Restore(layers, best);
        DynamicProgrammingRefinement(layers);
    }

    // ── Coordinated multi-layer refinement ────────────────────────────────────

    /// <summary>Layers up to this size are searched exhaustively by the refinement.</summary>
    private const int ExhaustiveLayerSize = 5;

    /// <summary>
    /// Refinement that moves nodes in several layers at once. The crossings of a layered
    /// graph are a sum over adjacent layer pairs, each term depending only on the orderings
    /// of those two layers, so for a set of candidate orderings per layer the best
    /// combination is found exactly by dynamic programming over the layers. Candidates are
    /// every ordering for small layers and the current ordering plus every single-node
    /// relocation for larger ones; the step is repeated while it improves. This finds the
    /// coordinated moves — e.g. shifting a whole chain of dummy nodes past a node in each
    /// of its layers — that the layer-by-layer heuristics cannot make, and it is exact when
    /// all layers are small.
    /// </summary>
    private static void DynamicProgrammingRefinement(List<List<LNode>> layers)
    {
        if (layers.Count < 2) return;
        long current = CountCrossings(layers);
        for (int round = 0; round < 10 && current > 0; round++)
        {
            var candidates = layers.Select(Candidates).ToList();

            // cost[l][p][q]: crossings between layer l-1 in ordering p and layer l in ordering q.
            var cost = new long[layers.Count][][];
            for (int l = 1; l < layers.Count; l++)
            {
                cost[l] = new long[candidates[l - 1].Count][];
                for (int p = 0; p < candidates[l - 1].Count; p++)
                {
                    Apply(layers[l - 1], candidates[l - 1][p]);
                    cost[l][p] = new long[candidates[l].Count];
                    for (int q = 0; q < candidates[l].Count; q++)
                    {
                        Apply(layers[l], candidates[l][q]);
                        cost[l][p][q] = CountBilayer(layers[l - 1], layers[l]);
                    }
                }
            }

            var best = new long[candidates[0].Count];
            var back = new int[layers.Count][];
            for (int l = 1; l < layers.Count; l++)
            {
                var next = new long[candidates[l].Count];
                back[l] = new int[candidates[l].Count];
                for (int q = 0; q < candidates[l].Count; q++)
                {
                    long min = long.MaxValue; int arg = 0;
                    for (int p = 0; p < candidates[l - 1].Count; p++)
                    {
                        long c = best[p] + cost[l][p][q];
                        if (c < min) { min = c; arg = p; }
                    }
                    next[q] = min;
                    back[l][q] = arg;
                }
                best = next;
            }

            int choice = Array.IndexOf(best, best.Min());
            long optimum = best[choice];
            for (int l = layers.Count - 1; l >= 0; l--)
            {
                Apply(layers[l], candidates[l][choice]);
                if (l > 0) choice = back[l][choice];
            }

            if (optimum >= current) break;
            current = optimum;
        }
    }

    private static List<LNode[]> Candidates(List<LNode> layer)
    {
        var result = new List<LNode[]> { layer.ToArray() };
        if (layer.Count <= 1) return result;
        if (layer.Count <= ExhaustiveLayerSize)
        {
            result.Clear();
            Permute(layer.ToArray(), 0, result);
            return result;
        }
        for (int i = 0; i < layer.Count; i++)
            for (int j = 0; j < layer.Count; j++)
            {
                if (i == j) continue;
                var moved = layer.ToList();
                var v = moved[i];
                moved.RemoveAt(i);
                moved.Insert(j, v);
                result.Add(moved.ToArray());
            }
        return result;
    }

    private static void Permute(LNode[] items, int k, List<LNode[]> into)
    {
        if (k == items.Length) { into.Add((LNode[])items.Clone()); return; }
        for (int i = k; i < items.Length; i++)
        {
            (items[k], items[i]) = (items[i], items[k]);
            Permute(items, k + 1, into);
            (items[k], items[i]) = (items[i], items[k]);
        }
    }

    private static void Apply(List<LNode> layer, LNode[] ordering)
    {
        layer.Clear();
        layer.AddRange(ordering);
        for (int i = 0; i < layer.Count; i++) layer[i].Pos = i;
    }

    // ── Ordering heuristics ───────────────────────────────────────────────────

    private static void Reorder(List<LNode> layer, bool upper, bool useMedian)
    {
        var key = new Dictionary<LNode, double>(ReferenceEqualityComparer.Instance);
        foreach (var v in layer)
        {
            var nbrs = upper
                ? v.In.Select(s => (Pos: (double)s.From.Pos, s.Weight)).ToList()
                : v.Out.Select(s => (Pos: (double)s.To.Pos, s.Weight)).ToList();
            key[v] = nbrs.Count == 0 ? v.Pos : useMedian ? WeightedMedian(nbrs) : Barycenter(nbrs);
        }

        // Stable sort: ties keep the current order.
        var sorted = layer.OrderBy(v => key[v]).ToList();
        layer.Clear();
        layer.AddRange(sorted);
        for (int i = 0; i < layer.Count; i++) layer[i].Pos = i;
    }

    private static double Barycenter(List<(double Pos, int Weight)> nbrs)
        => nbrs.Sum(n => n.Pos * n.Weight) / nbrs.Sum(n => n.Weight);

    /// <summary>Weighted median of Gansner et al. (1993), §3: biased towards the denser side.</summary>
    private static double WeightedMedian(List<(double Pos, int Weight)> nbrs)
    {
        var p = nbrs.OrderBy(n => n.Pos).Select(n => n.Pos).ToList();
        int m = p.Count / 2;
        if (p.Count % 2 == 1) return p[m];
        if (p.Count == 2) return (p[0] + p[1]) / 2;
        double left = p[m - 1] - p[0], right = p[^1] - p[m];
        return left + right == 0 ? (p[m - 1] + p[m]) / 2 : (p[m - 1] * right + p[m] * left) / (left + right);
    }

    /// <summary>Greedy switch (Gansner et al., <c>transpose</c>): swap neighbours while it helps.</summary>
    private static void Transpose(List<List<LNode>> layers)
    {
        bool improved = true;
        int guard = 0;
        while (improved && guard++ < 100)
        {
            improved = false;
            foreach (var layer in layers)
            {
                for (int i = 0; i + 1 < layer.Count; i++)
                {
                    var v = layer[i]; var w = layer[i + 1];
                    if (PairCrossings(w, v) < PairCrossings(v, w))
                    {
                        layer[i] = w; layer[i + 1] = v;
                        v.Pos = i + 1; w.Pos = i;
                        improved = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Sifting (Matuszewski, Schönfeld &amp; Molitor 1999): every node is taken out of its
    /// layer and re-inserted at the position that minimises the crossings with both
    /// neighbouring layers. Unlike the barycenter it evaluates the true objective, so it
    /// escapes many of the plateaus where the sweep stalls. Repeated while it improves.
    /// </summary>
    private static void Sift(List<List<LNode>> layers)
    {
        bool improved = true;
        int guard = 0;
        while (improved && guard++ < 20)
        {
            improved = false;
            for (int l = 0; l < layers.Count; l++)
            {
                var layer = layers[l];
                if (layer.Count < 2) continue;
                foreach (var v in layer.ToList())
                {
                    long Local() => (l > 0 ? CountBilayer(layers[l - 1], layer) : 0)
                                  + (l + 1 < layers.Count ? CountBilayer(layer, layers[l + 1]) : 0);

                    int original = layer.IndexOf(v);
                    long bestCount = Local();
                    int bestPos = original;
                    layer.RemoveAt(original);
                    for (int p = 0; p <= layer.Count; p++)
                    {
                        layer.Insert(p, v);
                        for (int i = 0; i < layer.Count; i++) layer[i].Pos = i;
                        long c = Local();
                        if (c < bestCount) { bestCount = c; bestPos = p; }
                        layer.RemoveAt(p);
                    }
                    layer.Insert(bestPos, v);
                    for (int i = 0; i < layer.Count; i++) layer[i].Pos = i;
                    if (bestPos != original) improved = true;
                }
            }
        }
    }

    /// <summary>Crossings among the segments of <paramref name="v"/> and <paramref name="w"/> if v is left of w.</summary>
    private static long PairCrossings(LNode v, LNode w)
    {
        long c = 0;
        foreach (var a in v.In) foreach (var b in w.In) if (a.From.Pos > b.From.Pos) c += (long)a.Weight * b.Weight;
        foreach (var a in v.Out) foreach (var b in w.Out) if (a.To.Pos > b.To.Pos) c += (long)a.Weight * b.Weight;
        return c;
    }

    // ── Crossing counting ─────────────────────────────────────────────────────

    /// <summary>Total weighted crossings between all pairs of adjacent layers.</summary>
    public static long CountCrossings(List<List<LNode>> layers)
    {
        long total = 0;
        for (int l = 0; l + 1 < layers.Count; l++) total += CountBilayer(layers[l], layers[l + 1]);
        return total;
    }

    /// <summary>
    /// Bilayer cross counting of Barth, Jünger &amp; Mutzel, "Simple and Efficient Bilayer
    /// Cross Counting" (JGAA 8(2), 2004): segments are visited in lexicographic order of
    /// (upper position, lower position) and inserted into an accumulator tree; every
    /// previously inserted segment ending further right crosses the new one.
    /// </summary>
    public static long CountBilayer(List<LNode> upper, List<LNode> lower)
    {
        var segments = new List<(int South, int Weight)>();
        foreach (var u in upper)
            foreach (var s in u.Out.OrderBy(s => s.To.Pos))
                segments.Add((s.To.Pos, s.Weight));

        int firstIndex = 1;
        while (firstIndex < lower.Count) firstIndex <<= 1;
        var tree = new long[2 * firstIndex - 1];
        firstIndex -= 1;

        long crossings = 0;
        foreach (var (south, weight) in segments)
        {
            int index = south + firstIndex;
            tree[index] += weight;
            while (index > 0)
            {
                if (index % 2 == 1) crossings += weight * tree[index + 1];
                index = (index - 1) / 2;
                tree[index] += weight;
            }
        }
        return crossings;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AssignPositions(List<List<LNode>> layers)
    {
        foreach (var layer in layers)
            for (int i = 0; i < layer.Count; i++) layer[i].Pos = i;
    }

    private static List<LNode[]> Snapshot(List<List<LNode>> layers) => layers.Select(l => l.ToArray()).ToList();

    private static void Restore(List<List<LNode>> layers, List<LNode[]> snapshot)
    {
        for (int l = 0; l < layers.Count; l++)
        {
            layers[l].Clear();
            layers[l].AddRange(snapshot[l]);
        }
        AssignPositions(layers);
    }
}
