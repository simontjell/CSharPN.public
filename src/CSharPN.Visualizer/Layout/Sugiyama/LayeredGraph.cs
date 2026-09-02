namespace CSharPN.Visualizer.Layout.Sugiyama;

/// <summary>
/// A node of the layered graph: a place, a transition, or a dummy node that
/// subdivides a long edge so that every segment connects adjacent layers.
/// </summary>
internal sealed class LNode
{
    /// <summary>Model id; <see langword="null"/> for dummy nodes.</summary>
    public string? Id { get; init; }
    public bool IsPlace { get; init; }

    /// <summary>Footprint reserved for the node (0×0 for dummies).</summary>
    public double W { get; init; }
    public double H { get; init; }

    /// <summary>Position in the model (declaration order). Used for all tie-breaking.</summary>
    public int Order { get; init; }

    /// <summary>Depth-first visiting order from cycle removal; the initial in-layer order.</summary>
    public double DfsOrder { get; set; }

    public bool IsDummy => Id is null;

    /// <summary>The long edge this dummy subdivides.</summary>
    public LEdge? DummyOf { get; init; }

    public int Layer { get; set; } = -1;
    /// <summary>Index within the layer after crossing minimisation.</summary>
    public int Pos { get; set; }

    /// <summary>Segments (proper edges) entering / leaving this node, in DAG orientation.</summary>
    public List<LSegment> In  { get; } = [];
    public List<LSegment> Out { get; } = [];

    /// <summary>Coordinate along the layer (assigned by Brandes–Köpf).</summary>
    public double Coord { get; set; }

    public override string ToString() => Id ?? $"dummy({DummyOf?.From.Id}→{DummyOf?.To.Id})";
}

/// <summary>One drawn arc of the model, attached to the layout edge that carries it.</summary>
internal sealed record ArcRef(string FromId, string ToId, bool OppositeToEdge);

/// <summary>
/// An edge of the layered graph in DAG orientation (after cycle removal). Parallel arcs
/// between the same pair of nodes — including a place/transition double arc — are merged
/// into one edge that carries several <see cref="Arcs"/>.
/// </summary>
internal sealed class LEdge
{
    public required LNode From { get; set; }
    public required LNode To   { get; set; }
    public int Weight { get; set; } = 1;
    public int Order  { get; init; }

    /// <summary>The model arcs drawn along this edge.</summary>
    public List<ArcRef> Arcs { get; } = [];

    /// <summary>True when arcs run in both directions between the two nodes.</summary>
    public bool Bidirectional => Arcs.Any(a => a.OppositeToEdge) && Arcs.Any(a => !a.OppositeToEdge);

    /// <summary>True when the edge was reversed by cycle removal (all its arcs point the other way).</summary>
    public bool IsReversed => Arcs.Count > 0 && Arcs.All(a => a.OppositeToEdge);

    /// <summary>Dummy nodes subdividing this edge, ordered from <see cref="From"/> to <see cref="To"/>.</summary>
    public List<LNode> Dummies { get; } = [];

    public int Span => To.Layer - From.Layer;

    public override string ToString() => $"{From}→{To}";
}

/// <summary>A proper edge between two adjacent layers (a piece of an <see cref="LEdge"/>).</summary>
internal sealed record LSegment(LNode From, LNode To, int Weight, LEdge Edge)
{
    /// <summary>An inner segment connects two dummy nodes (Brandes–Köpf terminology).</summary>
    public bool IsInner => From.IsDummy && To.IsDummy;
}
