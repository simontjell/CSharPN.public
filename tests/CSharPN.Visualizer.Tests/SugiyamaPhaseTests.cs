using CSharPN.Visualizer.Layout.Sugiyama;
using FluentAssertions;
using Xunit;

namespace CSharPN.Visualizer.Tests;

// ── Network simplex (Gansner et al. 1993) ─────────────────────────────────────

public class NetworkSimplexTests
{
    [Fact]
    public void Chain_gets_consecutive_ranks()
    {
        var rank = NetworkSimplex.Rank(3, [(0, 1, 1), (1, 2, 1)]);
        rank.Should().Equal(0, 1, 2);
    }

    [Fact]
    public void Source_feeding_a_deep_node_is_pulled_next_to_it()
    {
        // a→b→c→d and x→d: longest-path layering puts x at rank 0 (arc length 3);
        // the optimum puts x at rank 2 (arc length 1).
        var rank = NetworkSimplex.Rank(5, [(0, 1, 1), (1, 2, 1), (2, 3, 1), (4, 3, 1)]);
        rank.Should().Equal(0, 1, 2, 3, 2);
    }

    [Fact]
    public void Diamond_with_shortcut_is_optimal()
    {
        // a→b, a→c, b→d, c→d, a→d: total length 6 is optimal.
        var rank = NetworkSimplex.Rank(4, [(0, 1, 1), (0, 2, 1), (1, 3, 1), (2, 3, 1), (0, 3, 1)]);
        rank.Should().Equal(0, 1, 1, 2);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void Ranking_is_optimal_on_random_small_dags(int seed)
    {
        var rng = new Random(seed);
        int n = 5 + rng.Next(2);
        var edges = RandomConnectedDag(n, rng);

        var rank = NetworkSimplex.Rank(n, edges);
        foreach (var (u, v, _) in edges) (rank[v] - rank[u]).Should().BeGreaterThanOrEqualTo(1);

        long cost = edges.Sum(e => (long)e.W * (rank[e.V] - rank[e.U]));
        cost.Should().Be(BruteForceOptimum(n, edges));
    }

    [Theory]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)]
    public void Bipartite_graphs_get_alternating_parity(int seed)
    {
        // Nodes 0..k-1 are "places", k..n-1 "transitions"; every edge joins the two kinds.
        var rng = new Random(seed);
        int k = 3 + rng.Next(3), n = k + 3 + rng.Next(3);
        var edges = new List<(int U, int V, int W)>();
        for (int i = 0; i < n; i++)
        {
            int j = i < k ? k + rng.Next(n - k) : rng.Next(k);
            edges.Add(rng.Next(2) == 0 ? (i, j, 1) : (j, i, 1));
        }
        edges = MakeAcyclic(edges);
        if (!IsConnected(n, edges)) return;

        var rank = NetworkSimplex.Rank(n, edges);
        var placeParity = rank.Take(k).Select(r => r % 2).Distinct();
        placeParity.Should().HaveCount(1);
        var transParity = rank.Skip(k).Select(r => r % 2).Distinct();
        transParity.Should().HaveCount(1);
        transParity.Single().Should().NotBe(placeParity.Single());
    }

    private static List<(int U, int V, int W)> RandomConnectedDag(int n, Random rng)
    {
        var edges = new List<(int U, int V, int W)>();
        for (int v = 1; v < n; v++) edges.Add((rng.Next(v), v, 1 + rng.Next(2)));  // spanning tree
        for (int extra = 0; extra < n; extra++)
        {
            int u = rng.Next(n), v = rng.Next(n);
            if (u < v && !edges.Any(e => e.U == u && e.V == v)) edges.Add((u, v, 1));
        }
        return edges;
    }

    private static List<(int U, int V, int W)> MakeAcyclic(List<(int U, int V, int W)> edges)
        => edges.Select(e => e.U < e.V ? e : (e.V, e.U, e.W)).Distinct().ToList();

    private static bool IsConnected(int n, List<(int U, int V, int W)> edges)
    {
        var seen = new HashSet<int> { 0 };
        var stack = new Stack<int>([0]);
        while (stack.Count > 0)
        {
            int x = stack.Pop();
            foreach (var (u, v, _) in edges)
            {
                int o = u == x ? v : v == x ? u : -1;
                if (o >= 0 && seen.Add(o)) stack.Push(o);
            }
        }
        return seen.Count == n;
    }

    private static long BruteForceOptimum(int n, List<(int U, int V, int W)> edges)
    {
        long best = long.MaxValue;
        var rank = new int[n];
        void Recurse(int i)
        {
            if (i == n)
            {
                foreach (var (u, v, _) in edges) if (rank[v] - rank[u] < 1) return;
                best = Math.Min(best, edges.Sum(e => (long)e.W * (rank[e.V] - rank[e.U])));
                return;
            }
            for (int r = 0; r < n; r++) { rank[i] = r; Recurse(i + 1); }
        }
        Recurse(0);
        return best;
    }
}

// ── Bilayer cross counting (Barth, Jünger & Mutzel 2004) ──────────────────────

public class CrossCountingTests
{
    [Fact]
    public void Two_crossing_segments_count_one()
    {
        var (upper, lower) = Bilayer(2, 2, [(0, 1), (1, 0)]);
        CrossingMinimizer.CountBilayer(upper, lower).Should().Be(1);
    }

    [Fact]
    public void Parallel_segments_do_not_cross()
    {
        var (upper, lower) = Bilayer(2, 2, [(0, 0), (1, 1)]);
        CrossingMinimizer.CountBilayer(upper, lower).Should().Be(0);
    }

    [Fact]
    public void Weighted_segments_count_the_product_of_their_weights()
    {
        var (upper, lower) = Bilayer(2, 2, [(0, 1, 2), (1, 0, 3)]);
        CrossingMinimizer.CountBilayer(upper, lower).Should().Be(6);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Matches_the_naive_count_on_random_bilayers(int seed)
    {
        var rng = new Random(seed);
        int nu = 2 + rng.Next(6), nl = 2 + rng.Next(6);
        var segs = new List<(int, int, int)>();
        for (int i = 0; i < nu * nl / 2; i++) segs.Add((rng.Next(nu), rng.Next(nl), 1 + rng.Next(2)));
        var (upper, lower) = Bilayer(nu, nl, segs);

        long naive = 0;
        var all = upper.SelectMany(u => u.Out).ToList();
        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
            {
                var a = all[i]; var b = all[j];
                bool crosses = (a.From.Pos - b.From.Pos) * (a.To.Pos - b.To.Pos) < 0;
                if (crosses) naive += (long)a.Weight * b.Weight;
            }

        CrossingMinimizer.CountBilayer(upper, lower).Should().Be(naive);
    }

    internal static (List<LNode> Upper, List<LNode> Lower) Bilayer(int nu, int nl, IEnumerable<(int U, int L)> segs)
        => Bilayer(nu, nl, segs.Select(s => (s.U, s.L, 1)));

    internal static (List<LNode> Upper, List<LNode> Lower) Bilayer(int nu, int nl, IEnumerable<(int U, int L, int W)> segs)
    {
        var upper = Enumerable.Range(0, nu).Select(i => new LNode { Id = $"u{i}", Order = i, Layer = 0, Pos = i }).ToList();
        var lower = Enumerable.Range(0, nl).Select(i => new LNode { Id = $"l{i}", Order = i, Layer = 1, Pos = i }).ToList();
        foreach (var (u, l, w) in segs)
        {
            var e = new LEdge { From = upper[u], To = lower[l], Weight = w };
            var s = new LSegment(upper[u], lower[l], w, e);
            upper[u].Out.Add(s);
            lower[l].In.Add(s);
        }
        return (upper, lower);
    }
}

// ── Crossing minimisation ─────────────────────────────────────────────────────

public class CrossingMinimizerTests
{
    [Fact]
    public void Untangles_a_crossed_matching()
    {
        // u0→l1, u1→l0 crosses; swapping the lower layer removes the crossing.
        var (upper, lower) = CrossCountingTests.Bilayer(2, 2, [(0, 1), (1, 0)]);
        var layers = new List<List<LNode>> { upper, lower };
        CrossingMinimizer.Run(layers);
        CrossingMinimizer.CountCrossings(layers).Should().Be(0);
    }

    [Fact]
    public void Keeps_the_order_of_nodes_without_neighbours()
    {
        var (upper, lower) = CrossCountingTests.Bilayer(3, 1, [(1, 0)]);
        var layers = new List<List<LNode>> { upper, lower };
        CrossingMinimizer.Run(layers);
        layers[0].Select(n => n.Id).Should().Equal("u0", "u1", "u2");
    }
}

// ── Brandes–Köpf coordinate assignment ────────────────────────────────────────

public class BrandesKoepfTests
{
    private static double Sep(LNode u, LNode v) => u.H / 2 + v.H / 2 + 10;

    [Fact]
    public void Neighbours_in_a_layer_keep_their_order_and_minimum_separation()
    {
        var rng = new Random(42);
        var layers = RandomLayered(rng, layerCount: 5, perLayer: 4);
        BrandesKoepf.Assign(layers, Sep);
        foreach (var layer in layers)
            for (int i = 0; i + 1 < layer.Count; i++)
                (layer[i + 1].Coord - layer[i].Coord).Should().BeGreaterThanOrEqualTo(Sep(layer[i], layer[i + 1]) - 1e-9);
    }

    [Fact]
    public void A_parent_is_centred_over_two_children()
    {
        var p  = new LNode { Id = "p", Layer = 0, H = 20 };
        var c1 = new LNode { Id = "c1", Layer = 1, H = 20 };
        var c2 = new LNode { Id = "c2", Layer = 1, H = 20 };
        Connect(p, c1); Connect(p, c2);
        var layers = Layers([p], [c1, c2]);
        BrandesKoepf.Assign(layers, Sep);
        p.Coord.Should().BeApproximately((c1.Coord + c2.Coord) / 2, 1e-9);
    }

    [Fact]
    public void Long_edges_are_drawn_straight()
    {
        // a → d1 → d2 → b with side nodes competing for alignment.
        var a  = new LNode { Id = "a",  Layer = 0, H = 20 };
        var s0 = new LNode { Id = "s0", Layer = 0, H = 20 };
        var d1 = new LNode { Id = null, Layer = 1 };
        var s1 = new LNode { Id = "s1", Layer = 1, H = 20 };
        var d2 = new LNode { Id = null, Layer = 2 };
        var s2 = new LNode { Id = "s2", Layer = 2, H = 20 };
        var b  = new LNode { Id = "b",  Layer = 3, H = 20 };
        var s3 = new LNode { Id = "s3", Layer = 3, H = 20 };
        Connect(a, d1); Connect(d1, d2); Connect(d2, b);
        Connect(s0, s1); Connect(s1, s2); Connect(s2, s3); Connect(s0, d1); Connect(s2, b);
        var layers = Layers([s0, a], [s1, d1], [s2, d2], [s3, b]);
        BrandesKoepf.Assign(layers, Sep);
        d1.Coord.Should().Be(d2.Coord);
    }

    private static void Connect(LNode u, LNode v)
    {
        var e = new LEdge { From = u, To = v };
        var s = new LSegment(u, v, 1, e);
        u.Out.Add(s); v.In.Add(s);
    }

    private static List<List<LNode>> Layers(params LNode[][] layers)
    {
        var result = layers.Select(l => l.ToList()).ToList();
        foreach (var layer in result) for (int i = 0; i < layer.Count; i++) layer[i].Pos = i;
        return result;
    }

    private static List<List<LNode>> RandomLayered(Random rng, int layerCount, int perLayer)
    {
        var layers = new List<List<LNode>>();
        for (int l = 0; l < layerCount; l++)
            layers.Add(Enumerable.Range(0, perLayer).Select(i => new LNode { Id = $"n{l}_{i}", Layer = l, Pos = i, H = 10 + rng.Next(30) }).ToList());
        for (int l = 0; l + 1 < layerCount; l++)
            foreach (var u in layers[l])
                for (int k = 0; k < 2; k++)
                    Connect(u, layers[l + 1][rng.Next(perLayer)]);
        return layers;
    }
}
