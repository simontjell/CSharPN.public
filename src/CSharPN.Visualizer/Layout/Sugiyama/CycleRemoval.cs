namespace CSharPN.Visualizer.Layout.Sugiyama;

/// <summary>
/// Phase 1 of the Sugiyama framework: make the graph acyclic by reversing the back
/// edges of a depth-first search (Gansner, Koutsofios, North &amp; Vo 1993, §2.1).
/// </summary>
/// <remarks>
/// The search starts from the <em>preferred sources</em> — for a Petri net the places
/// that carry tokens in the initial marking, i.e. where the behaviour starts — and
/// visits neighbours in model order. The flow of the drawing therefore follows the
/// token flow from the initial marking, and the declaration order in the model acts as
/// secondary notation that the modeller can use to steer the drawing (cf. Domrös et al.,
/// "Diagram Control and Model Order for Sugiyama Layouts", 2024).
/// </remarks>
internal static class CycleRemoval
{
    /// <summary>
    /// Reverses back edges in place (swapping <see cref="LEdge.From"/>/<see cref="LEdge.To"/>
    /// and flipping the arcs' orientation) and records the DFS visiting order in
    /// <see cref="LNode.DfsOrder"/>.
    /// </summary>
    public static void Run(IReadOnlyList<LNode> nodes, IReadOnlyList<LEdge> edges, IEnumerable<LNode> preferredSources)
    {
        var outEdges = nodes.ToDictionary(n => n, _ => new List<LEdge>());
        foreach (var e in edges) outEdges[e.From].Add(e);
        foreach (var list in outEdges.Values) list.Sort((a, b) => a.To.Order.CompareTo(b.To.Order));

        var colour = new Dictionary<LNode, int>(ReferenceEqualityComparer.Instance); // 0 white, 1 grey, 2 black
        foreach (var n in nodes) colour[n] = 0;
        int counter = 0;

        void Dfs(LNode u)
        {
            colour[u] = 1;
            u.DfsOrder = counter++;
            foreach (var e in outEdges[u])
            {
                var v = e.To;
                if (colour[v] == 1)
                {
                    Reverse(e);
                }
                else if (colour[v] == 0)
                {
                    Dfs(v);
                }
            }
            colour[u] = 2;
        }

        // Preferred sources first, then the remaining places, then transitions — each group in model order.
        var startOrder = preferredSources
            .Concat(nodes.Where(n => n.IsPlace).OrderBy(n => n.Order))
            .Concat(nodes.Where(n => !n.IsPlace).OrderBy(n => n.Order));

        foreach (var s in startOrder)
            if (colour[s] == 0) Dfs(s);
    }

    private static void Reverse(LEdge e)
    {
        (e.From, e.To) = (e.To, e.From);
        for (int i = 0; i < e.Arcs.Count; i++)
            e.Arcs[i] = e.Arcs[i] with { OppositeToEdge = !e.Arcs[i].OppositeToEdge };
    }
}
