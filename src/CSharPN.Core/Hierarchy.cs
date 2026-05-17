namespace CSharPN.Core;

// ── PageGroup ─────────────────────────────────────────────────────────────────

/// <summary>
/// Describes which places and transitions belong to a single sub-page,
/// used by the visualizer to draw page-group boundaries.
/// </summary>
public sealed record PageGroup(
    string PageName,
    IReadOnlyList<string> PlaceIds,
    IReadOnlyList<string> TransitionIds,
    IReadOnlyList<PortInfo> Ports);

/// <summary>Port place with direction, for visualization.</summary>
public sealed record PortInfo(string PlaceName, PortType Direction);

// ── PortType ──────────────────────────────────────────────────────────────────

/// <summary>
/// Direction of a port place in a hierarchical CPN (Jensen Vol. 2 §2).
/// </summary>
public enum PortType
{
    /// <summary>Tokens flow into the sub-page (from the parent socket).</summary>
    In,
    /// <summary>Tokens flow out of the sub-page (to the parent socket).</summary>
    Out,
    /// <summary>Tokens flow in both directions.</summary>
    InOut
}

// ── CpnPage ───────────────────────────────────────────────────────────────────

/// <summary>
/// A named page in a hierarchical CPN.  Derive from this class to define a
/// sub-page with typed port places declared via <see cref="In{T}"/>,
/// <see cref="Out{T}"/> and <see cref="InOut{T}"/> (CPN Tools style).
/// </summary>
public abstract class CpnPage : CpnModel
{
    private readonly Dictionary<IPlace, PortType> _portPlaces = new(ReferenceEqualityComparer.Instance);

    private Func<CpnTime>? _parentClock;
    private readonly List<Func<CpnTime, CpnTime?>> _timedPlaceInspectors = [];

    protected CpnPage(string name) : base(name) { }

    /// <summary>Called by <see cref="HierarchicalCpnModel.AddSubPage"/> to inject the parent's clock.</summary>
    internal void SetClock(Func<CpnTime> getClock) => _parentClock = getClock;

    /// <summary>Timed-place inspectors registered by this page, consumed by the parent model.</summary>
    internal IReadOnlyList<Func<CpnTime, CpnTime?>> TimedPlaceInspectors => _timedPlaceInspectors;

    // ── Port declarations (CPN Tools style) ──────────────────────────────────

    /// <summary>Declares an <b>input</b> port — tokens flow into the sub-page.</summary>
    protected Place<T> In<T>(Place<T> socket) where T : notnull, IEquatable<T>
    { _portPlaces[socket] = PortType.In; return socket; }

    /// <summary>Declares an <b>output</b> port — tokens flow out of the sub-page.</summary>
    protected Place<T> Out<T>(Place<T> socket) where T : notnull, IEquatable<T>
    { _portPlaces[socket] = PortType.Out; return socket; }

    /// <summary>Declares a <b>bidirectional</b> port — tokens flow in both directions.</summary>
    protected Place<T> InOut<T>(Place<T> socket) where T : notnull, IEquatable<T>
    { _portPlaces[socket] = PortType.InOut; return socket; }

    /// <summary>Returns <see langword="true"/> if <paramref name="place"/> is a declared port.</summary>
    internal bool IsPort(IPlace place) => _portPlaces.ContainsKey(place);

    /// <summary>Returns all port places with their directions.</summary>
    internal IReadOnlyList<PortInfo> GetPortInfos() =>
        _portPlaces.Select(kv => new PortInfo(kv.Key.Name, kv.Value)).ToList();

    // ── Accessible building methods ───────────────────────────────────────────

    /// <summary>Creates and registers a local place (not a port).</summary>
    protected new Place<T> AddPlace<T>(string name, Multiset<T>? initial = null)
        where T : notnull, IEquatable<T>
        => base.AddPlace(name, initial);

    /// <summary>Creates and registers a local timed place.</summary>
    protected Place<Timed<T>> AddTimedPlace<T>(string name, Multiset<Timed<T>>? initial = null)
        where T : notnull
    {
        var place = base.AddPlace<Timed<T>>(name, initial);
        _timedPlaceInspectors.Add(afterClock =>
        {
            CpnTime? min = null;
            foreach (var token in place.Marking.DistinctItems())
                if (token.ReadyAt > afterClock && (min == null || token.ReadyAt < min.Value))
                    min = token.ReadyAt;
            return min;
        });
        return place;
    }

    /// <summary>Starts building a local transition (clock-aware if parent is timed).</summary>
    protected new TransitionBuilder AddTransition(string name)
        => new(name, this, () => (_parentClock ?? (() => CpnTime.Zero))());
}

// ── HierarchicalCpnModel ──────────────────────────────────────────────────────

/// <summary>
/// A CPN model composed of multiple pages connected by substitution transitions
/// and port places (Jensen Vol. 2 Chapter 2).  Inherits from <see cref="TimedCpnModel"/>
/// so that sub-pages can use timed arcs with the shared global clock.
/// </summary>
public class HierarchicalCpnModel : TimedCpnModel
{
    private readonly record struct SubEntry(CpnPage Page, string SubstitutionName);
    private readonly List<SubEntry> _subPages = [];

    public HierarchicalCpnModel(string name) : base(name) { }

    // ── Public building interface (top-level shared places / transitions) ─────

    /// <summary>Creates a shared (top-level) place that sub-pages may use as port places.</summary>
    public new Place<T> AddPlace<T>(string name, Multiset<T>? initial = null) where T : notnull, IEquatable<T>
        => base.AddPlace(name, initial);

    /// <summary>Starts building a top-level transition.</summary>
    public new TransitionBuilder AddTransition(string name) => base.AddTransition(name);

    // ── Sub-page registration ─────────────────────────────────────────────────

    /// <summary>
    /// Merges <paramref name="page"/> into this model, linking it to a named
    /// substitution transition.
    /// </summary>
    public void AddSubPage(CpnPage page, string substitutionTransitionName)
    {
        _subPages.Add(new(page, substitutionTransitionName));

        page.SetClock(() => GlobalClock);

        foreach (var place in page.Places)
            if (!page.IsPort(place))
                InjectPlace(place);

        foreach (var t in page.Transitions)
            RegisterTransition(t);

        foreach (var inspector in page.TimedPlaceInspectors)
            RegisterTimedPlaceInspector(inspector);
    }

    /// <summary>
    /// Describes the substitution links for display / documentation.
    /// </summary>
    public IReadOnlyList<(string SubstitutionName, string PageName)> SubPageLinks =>
        _subPages.Select(e => (e.SubstitutionName, e.Page.Name)).ToList();

    /// <summary>
    /// Returns one <see cref="PageGroup"/> per sub-page listing the local
    /// (non-port) place names, all transition names, and port info.
    /// </summary>
    public IReadOnlyList<PageGroup> GetPageGroups() =>
        _subPages.Select(e => new PageGroup(
            e.Page.Name,
            e.Page.Places.Where(p => !e.Page.IsPort(p)).Select(p => p.Name).ToList(),
            e.Page.Transitions.Select(t => t.Name).ToList(),
            e.Page.GetPortInfos()
        )).ToList();

    // ── Page-level views (for hierarchical navigation) ────────────────────────

    /// <summary>Returns the page tree: top page name + sub-page names.</summary>
    public IReadOnlyList<string> GetPageNames()
    {
        var names = new List<string> { Name };
        names.AddRange(_subPages.Select(s => s.Page.Name));
        return names;
    }

    /// <summary>
    /// Returns nodes and edges for the top-level page view.
    /// Shared places are shown as places; each sub-page as a substitution transition.
    /// Edges connect port places to their substitution transition based on direction.
    /// </summary>
    public (IReadOnlyList<(string Name, bool IsPlace)> Nodes,
            IReadOnlyList<(string From, string To)> Edges) GetTopPageView()
    {
        // Places that are NOT local to any sub-page = shared/top-level
        var localPlaces = _subPages
            .SelectMany(s => s.Page.Places.Where(p => !s.Page.IsPort(p)))
            .ToHashSet(ReferenceEqualityComparer.Instance);

        var nodes = new List<(string Name, bool IsPlace)>();
        foreach (var p in Places)
            if (!localPlaces.Contains(p))
                nodes.Add((p.Name, true));

        // One substitution-transition node per sub-page
        foreach (var sub in _subPages)
            nodes.Add((sub.SubstitutionName, false));

        // Edges: port places ↔ substitution transitions based on direction
        var edges = new List<(string From, string To)>();
        foreach (var sub in _subPages)
        {
            foreach (var port in sub.Page.GetPortInfos())
            {
                switch (port.Direction)
                {
                    case PortType.In:
                        edges.Add((port.PlaceName, sub.SubstitutionName));
                        break;
                    case PortType.Out:
                        edges.Add((sub.SubstitutionName, port.PlaceName));
                        break;
                    case PortType.InOut:
                        edges.Add((port.PlaceName, sub.SubstitutionName));
                        edges.Add((sub.SubstitutionName, port.PlaceName));
                        break;
                }
            }
        }

        return (nodes, edges);
    }

    /// <summary>
    /// Returns nodes and edges for a sub-page view.
    /// Port places + local places + transitions, with arcs from the page's transitions.
    /// </summary>
    public (IReadOnlyList<(string Name, bool IsPlace)> Nodes,
            IReadOnlyList<(string From, string To)> Edges) GetSubPageView(string pageName)
    {
        var sub = _subPages.FirstOrDefault(s => s.Page.Name == pageName);
        if (sub.Page == null)
            return ([], []);

        var page = sub.Page;
        var nodes = new List<(string Name, bool IsPlace)>();

        // Port places
        foreach (var port in page.GetPortInfos())
            nodes.Add((port.PlaceName, true));

        // Local places
        foreach (var p in page.Places)
            if (!page.IsPort(p))
                nodes.Add((p.Name, true));

        // Transitions
        foreach (var t in page.Transitions)
            nodes.Add((t.Name, false));

        // Edges from arc views
        var edges = new List<(string From, string To)>();
        foreach (var t in page.Transitions)
            foreach (var av in t.GetArcViews())
            {
                var e = av.Direction == ArcDirection.Input
                    ? (av.Place.Name, t.Name)
                    : (t.Name, av.Place.Name);
                if (!edges.Contains(e)) edges.Add(e);
            }

        return (nodes, edges);
    }
}
