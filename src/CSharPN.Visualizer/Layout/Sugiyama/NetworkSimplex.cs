namespace CSharPN.Visualizer.Layout.Sugiyama;

/// <summary>
/// Phase 2 of the Sugiyama framework: optimal layer assignment by the network simplex
/// method of Gansner, Koutsofios, North &amp; Vo, "A Technique for Drawing Directed
/// Graphs" (IEEE TSE 19(3), 1993), §2.
/// </summary>
/// <remarks>
/// <para>
/// Minimises <c>Σ w(e)·(rank(head) − rank(tail))</c> subject to
/// <c>rank(head) − rank(tail) ≥ 1</c> for every edge of a connected DAG. Fewer and
/// shorter long edges mean fewer dummy nodes, fewer crossings and shorter arcs.
/// </para>
/// <para>
/// Because every tree edge of a feasible spanning tree is tight (length exactly 1), the
/// rank difference between two nodes equals the length of the tree path between them.
/// In a bipartite graph such as a Petri net all paths between two nodes have the same
/// parity, so places and transitions automatically end up in alternating layers.
/// </para>
/// <para>
/// The implementation favours clarity over speed: cut values are recomputed from scratch
/// after every pivot. That is O(|E|·(|V|+|E|)) per pivot, which is negligible for the
/// sizes of Petri nets drawn on screen.
/// </para>
/// </remarks>
internal static class NetworkSimplex
{
    /// <summary>
    /// Returns a rank for each of the <paramref name="n"/> nodes of a <b>connected</b> DAG
    /// given by <paramref name="edges"/> (tail, head, weight). Ranks are normalised so
    /// the smallest is 0.
    /// </summary>
    public static int[] Rank(int n, IReadOnlyList<(int U, int V, int W)> edges, int maxPivots = 100_000)
    {
        var rank = InitRank(n, edges);
        if (edges.Count == 0) return rank;

        var inTree = new bool[edges.Count];
        FeasibleTree(n, edges, rank, inTree);

        // A pivot on an entering edge with slack 0 leaves the ranks unchanged (a degenerate
        // pivot). A bounded run of them is harmless; an unbounded run would be cycling.
        int degenerate = 0;

        for (int pivot = 0; pivot < maxPivots; pivot++)
        {
            // Leaving edge: the tree edge with the most negative cut value.
            int leave = -1; long bestCut = 0;
            bool[]? leaveTail = null;
            for (int i = 0; i < edges.Count; i++)
            {
                if (!inTree[i]) continue;
                var tail = TailComponent(n, edges, inTree, i);
                long cut = CutValue(edges, tail);
                if (cut < bestCut) { bestCut = cut; leave = i; leaveTail = tail; }
            }
            if (leave < 0) break;

            // Entering edge: the non-tree edge from the head component into the tail
            // component with minimum slack (ties broken by index for determinism).
            int enter = -1; int bestSlack = int.MaxValue;
            for (int i = 0; i < edges.Count; i++)
            {
                if (inTree[i]) continue;
                var (u, v, _) = edges[i];
                if (leaveTail![u] || !leaveTail[v]) continue;      // must run head-component → tail-component
                int s = Slack(rank, edges[i]);
                if (s < bestSlack) { bestSlack = s; enter = i; }
            }
            if (enter < 0) break; // cannot happen for a connected graph
            degenerate = bestSlack == 0 ? degenerate + 1 : 0;
            if (degenerate > 2 * edges.Count) break;

            inTree[leave] = false;
            inTree[enter] = true;
            RecomputeRanks(n, edges, inTree, rank);
        }

        Normalise(rank);
        return rank;
    }

    /// <summary>Longest-path layering from the sources: a feasible initial ranking.</summary>
    private static int[] InitRank(int n, IReadOnlyList<(int U, int V, int W)> edges)
    {
        var rank = new int[n];
        var indeg = new int[n];
        var outAdj = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        foreach (var (u, v, _) in edges) { indeg[v]++; outAdj[u].Add(v); }

        var queue = new Queue<int>(Enumerable.Range(0, n).Where(i => indeg[i] == 0));
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int v in outAdj[u])
            {
                rank[v] = Math.Max(rank[v], rank[u] + 1);
                if (--indeg[v] == 0) queue.Enqueue(v);
            }
        }
        return rank;
    }

    private static int Slack(int[] rank, (int U, int V, int W) e) => rank[e.V] - rank[e.U] - 1;

    /// <summary>
    /// Grows a spanning tree of tight edges (Gansner et al., <c>feasible_tree</c>): whenever the
    /// tight tree does not span all nodes, the non-tree edge with minimum slack incident to
    /// the tree is made tight by shifting the ranks of the whole tree.
    /// </summary>
    private static void FeasibleTree(int n, IReadOnlyList<(int U, int V, int W)> edges, int[] rank, bool[] inTree)
    {
        while (true)
        {
            var treeNodes = TightTree(n, edges, rank, inTree);
            if (treeNodes.Count(t => t) == n) return;

            int best = -1; int bestSlack = int.MaxValue;
            for (int i = 0; i < edges.Count; i++)
            {
                var (u, v, _) = edges[i];
                if (treeNodes[u] == treeNodes[v]) continue;
                int s = Slack(rank, edges[i]);
                if (s < bestSlack) { bestSlack = s; best = i; }
            }
            if (best < 0) throw new InvalidOperationException("NetworkSimplex requires a connected graph.");

            int delta = treeNodes[edges[best].V] ? -bestSlack : bestSlack;
            for (int i = 0; i < n; i++)
                if (treeNodes[i]) rank[i] += delta;
        }
    }

    /// <summary>Breadth-first growth of a tree over tight edges from node 0; marks tree edges.</summary>
    private static bool[] TightTree(int n, IReadOnlyList<(int U, int V, int W)> edges, int[] rank, bool[] inTree)
    {
        Array.Clear(inTree);
        var inNode = new bool[n];
        inNode[0] = true;
        bool grew = true;
        while (grew)
        {
            grew = false;
            for (int i = 0; i < edges.Count; i++)
            {
                var (u, v, _) = edges[i];
                if (inNode[u] == inNode[v] || Slack(rank, edges[i]) != 0) continue;
                inNode[u] = inNode[v] = true;
                inTree[i] = true;
                grew = true;
            }
        }
        return inNode;
    }

    /// <summary>Nodes on the tail side of tree edge <paramref name="edgeIdx"/> once it is removed.</summary>
    private static bool[] TailComponent(int n, IReadOnlyList<(int U, int V, int W)> edges, bool[] inTree, int edgeIdx)
    {
        var comp = new bool[n];
        var stack = new Stack<int>();
        stack.Push(edges[edgeIdx].U);
        comp[edges[edgeIdx].U] = true;
        while (stack.Count > 0)
        {
            int x = stack.Pop();
            for (int i = 0; i < edges.Count; i++)
            {
                if (!inTree[i] || i == edgeIdx) continue;
                var (u, v, _) = edges[i];
                int other = u == x ? v : v == x ? u : -1;
                if (other < 0 || comp[other]) continue;
                comp[other] = true;
                stack.Push(other);
            }
        }
        return comp;
    }

    /// <summary>
    /// Cut value of a tree edge: total weight of edges from the tail component to the head
    /// component minus the total weight of edges the other way (Gansner et al., §2.3).
    /// </summary>
    private static long CutValue(IReadOnlyList<(int U, int V, int W)> edges, bool[] tail)
    {
        long cut = 0;
        foreach (var (u, v, w) in edges)
        {
            if (tail[u] && !tail[v]) cut += w;
            else if (!tail[u] && tail[v]) cut -= w;
        }
        return cut;
    }

    /// <summary>All tree edges are tight, so ranks follow from a traversal of the tree.</summary>
    private static void RecomputeRanks(int n, IReadOnlyList<(int U, int V, int W)> edges, bool[] inTree, int[] rank)
    {
        var done = new bool[n];
        rank[0] = 0; done[0] = true;
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            int x = stack.Pop();
            for (int i = 0; i < edges.Count; i++)
            {
                if (!inTree[i]) continue;
                var (u, v, _) = edges[i];
                if (u == x && !done[v]) { rank[v] = rank[u] + 1; done[v] = true; stack.Push(v); }
                else if (v == x && !done[u]) { rank[u] = rank[v] - 1; done[u] = true; stack.Push(u); }
            }
        }
    }

    private static void Normalise(int[] rank)
    {
        int min = rank.Min();
        for (int i = 0; i < rank.Length; i++) rank[i] -= min;
    }
}
