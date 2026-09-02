namespace CSharPN.Core;

// ── CpnState ──────────────────────────────────────────────────────────────────

/// <summary>
/// An immutable snapshot of all place markings in a model.
/// Used for state-space exploration and for resetting the model to a prior state.
/// </summary>
public sealed class CpnState : IEquatable<CpnState>
{
    internal readonly IReadOnlyDictionary<IPlace, object> Markings;

    internal CpnState(Dictionary<IPlace, object> markings)
    {
        Markings = markings;
    }

    public bool Equals(CpnState? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Markings.Count != other.Markings.Count) return false;

        // Build a name → marking view of the other state for cross-instance comparison
        var otherByName = other.Markings.ToDictionary(k => k.Key.Name, k => k.Value);

        foreach (var kvp in Markings)
            if (!otherByName.TryGetValue(kvp.Key.Name, out var v) || !kvp.Value.Equals(v))
                return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as CpnState);

    public override int GetHashCode()
    {
        int hash = 0;
        foreach (var kvp in Markings)
            hash ^= HashCode.Combine(kvp.Key.Name, kvp.Value.GetHashCode());
        return hash;
    }

    /// <summary>Compact textual representation of all place markings.</summary>
    public override string ToString()
    {
        var parts = Markings
            .OrderBy(k => k.Key.Name, StringComparer.Ordinal)
            .Select(k => $"{k.Key.Name}: {k.Value}");
        return "{" + string.Join(", ", parts) + "}";
    }
}

// ── CpnModel ──────────────────────────────────────────────────────────────────

/// <summary>
/// Base class for all CPN models. Derive from this class and define places and
/// transitions in the constructor.
/// </summary>
/// <example>
/// <code>
/// public class MyNet : CpnModel
/// {
///     public readonly Place&lt;int&gt; Tokens;
///
///     public MyNet()
///     {
///         Tokens = AddPlace("Tokens", Multiset.Of(1, 2, 3));
///         var x = new Var&lt;int&gt;("x");
///         AddTransition("Consume")
///             .Input(Tokens, x)
///             .Guard(() => x.Val > 1)
///             .Build();
///     }
/// }
/// </code>
/// </example>
public abstract class CpnModel
{
    private readonly List<IPlace> _places = [];
    private readonly List<Transition> _transitions = [];

    /// <summary>The model's display name (defaults to the class name).</summary>
    public string Name { get; }

    /// <summary>
    /// Lock that serialises binding enumeration and firing on this model. Variable bindings
    /// and place markings are shared mutable state, so two drivers of one model (say an HTTP
    /// host and an interactive visualizer) must hold this lock around each
    /// enumerate-then-fire unit. <see cref="CpnSimulator"/> does so for its own operations.
    /// </summary>
    public object SyncRoot { get; } = new();

    protected CpnModel(string? name = null)
    {
        Name = name ?? GetType().Name;
    }

    // ── Registration (called from constructors of derived classes) ─────────────

    /// <summary>Creates and registers a new place with an optional initial marking.</summary>
    protected Place<T> AddPlace<T>(string name, Multiset<T>? initial = null) where T : notnull, IEquatable<T>
    {
        var place = new Place<T>(name ?? Pluralize<T>(), initial);
        _places.Add(place);
        return place;
    }

    protected Place<T> AddPlace<T>(T initial) where T : notnull, IEquatable<T>
        => AddPlace<T>(Multiset.Of(initial));

    protected Place<T> AddPlace<T>(string name, T initial) where T : notnull, IEquatable<T>
        => AddPlace<T>(name, Multiset.Of(initial));
    protected Place<T> AddPlace<T>(string name) where T : notnull, IEquatable<T>
        => AddPlace<T>(name, null);

    protected Place<T> AddPlace<T>(Multiset<T>? initial = null) where T : notnull, IEquatable<T>
        => AddPlace<T>(Pluralize<T>(), initial);

    private static string Pluralize<T>()
        => typeof(T).Name + 's'; // TODO: Make more sophisticated...

    /// <summary>
    /// Starts building a new transition. Call <see cref="TransitionBuilder.Build"/> to
    /// finalise it and register it with the model.
    /// </summary>
    protected TransitionBuilder AddTransition(string name) => new(name, this);

    internal void RegisterTransition(Transition t)
    {
        _transitions.Add(t);
        ValidateUniqueVariableNames();
    }

    /// <summary>
    /// Verifies that no two <em>distinct</em> <see cref="Var{T}"/> instances in the model
    /// share the same <see cref="Var{T}.Name"/>. Reusing the same instance across arcs or
    /// transitions is allowed; two separately-created variables with an identical name are not,
    /// because bindings are identified by name (e.g. in <see cref="BindingSnapshot"/>).
    /// Called after every transition is registered.
    /// </summary>
    /// <exception cref="InvalidOperationException">A duplicate variable name is found.</exception>
    private void ValidateUniqueVariableNames()
    {
        var byName = new Dictionary<string, IVar>(StringComparer.Ordinal);
        var seen   = new HashSet<IVar>(ReferenceEqualityComparer.Instance);

        foreach (var transition in _transitions)
            foreach (var v in transition.Variables)
            {
                if (string.IsNullOrEmpty(v.Name)) continue; // unnamed/internal vars are exempt
                if (!seen.Add(v)) continue;                 // same instance already accounted for

                if (byName.TryGetValue(v.Name, out var existing) && !ReferenceEquals(existing, v))
                    throw new InvalidOperationException(
                        $"Duplicate variable name '{v.Name}' in model '{Name}': two distinct " +
                        "Var instances share the same Name. Give each variable a unique name, " +
                        "or reuse the same Var instance across arcs/transitions.");

                byName[v.Name] = v;
            }
    }

    /// <summary>
    /// Injects a place created outside this model (e.g. in a sub-page) into
    /// this model's place list so it participates in Reset / GetState / SetState.
    /// Used by <see cref="HierarchicalCpnModel"/>.
    /// </summary>
    internal void InjectPlace(IPlace place) => _places.Add(place);

    // ── Public structure ──────────────────────────────────────────────────────

    public IReadOnlyList<IPlace> Places => _places.AsReadOnly();
    public IReadOnlyList<Transition> Transitions => _transitions.AsReadOnly();

    // ── Steps (Jensen & Kristensen 2009, Definitions 4.3 (6), 4.4 and 4.5) ────

    /// <summary>
    /// Is the step <paramref name="step"/> — a non-empty multiset of binding elements, given as a
    /// list in which a binding element may appear several times — enabled in the current marking?
    /// Definition 4.4: <c>∀(t,b) ∈ Y: G(t)⟨b⟩</c> and <c>∀p ∈ P: Σ_{(t,b)∈Y} E(p,t)⟨b⟩ ≤ M(p)</c>.
    /// Two binding elements are <em>concurrently enabled</em> when the step containing both is
    /// enabled; a binding element is concurrently enabled with itself when the step containing
    /// it twice is enabled.
    /// </summary>
    public bool IsEnabled(IReadOnlyList<BindingSnapshot> step)
        => TryComputeStep(step, produce: false) is not null;

    /// <inheritdoc cref="IsEnabled(IReadOnlyList{BindingSnapshot})"/>
    public bool IsEnabled(params BindingSnapshot[] step) => IsEnabled((IReadOnlyList<BindingSnapshot>)step);

    /// <summary>
    /// Lets the step occur. Definition 4.5: for every place
    /// <c>M₂(p) = (M₁(p) − Σ_{(t,b)∈Y} E(p,t)⟨b⟩) + Σ_{(t,b)∈Y} E(t,p)⟨b⟩</c>.
    /// The effect equals that of the binding elements occurring one after another in any order,
    /// but the step is checked and applied as a whole (all-or-nothing).
    /// </summary>
    /// <exception cref="InvalidOperationException">The step is not enabled in the current marking.</exception>
    public void Occur(IReadOnlyList<BindingSnapshot> step)
    {
        var (afterRemoval, produced) = TryComputeStep(step, produce: true)
            ?? throw new InvalidOperationException("The step is not enabled in the current marking.");

        foreach (var (place, m) in afterRemoval)
            ((IPlaceInternal)place).SetMarkingObject(m);
        foreach (var (place, tokens) in produced)
            place.SetMarkingObject(place.AddMarkingObject(place.GetMarkingObject(), tokens));
    }

    /// <inheritdoc cref="Occur(IReadOnlyList{BindingSnapshot})"/>
    public void Occur(params BindingSnapshot[] step) => Occur((IReadOnlyList<BindingSnapshot>)step);

    /// <summary>
    /// Folds every binding element of the step through the marking: guards are checked and
    /// the input demands are subtracted cumulatively, so the sum over the step is compared
    /// with <c>M(p)</c>. Returns <see langword="null"/> when the step is not enabled.
    /// </summary>
    private (Dictionary<IPlace, object> afterRemoval, List<(IPlaceInternal place, object tokens)> produced)?
        TryComputeStep(IReadOnlyList<BindingSnapshot> step, bool produce)
    {
        if (step.Count == 0)
            throw new ArgumentException("A step is a non-empty multiset of binding elements.", nameof(step));

        var remaining = new Dictionary<IPlace, object>(ReferenceEqualityComparer.Instance);
        var produced  = new List<(IPlaceInternal, object)>();

        foreach (var be in step)
        {
            if (!_transitions.Contains(be.Transition))
                throw new ArgumentException(
                    $"Binding element of transition '{be.Transition.Name}' does not belong to model '{Name}'.", nameof(step));

            be.ApplyBindings();
            try
            {
                if (!be.Transition.EvaluateGuard()) return null;
                var after = be.Transition.TryConsumeAll(remaining);
                if (after is null) return null;
                remaining = after;
                if (produce) produced.AddRange(be.Transition.Produce());
            }
            finally
            {
                be.ClearBindings();
            }
        }
        return (remaining, produced);
    }

    // ── State management ──────────────────────────────────────────────────────

    /// <summary>Resets all places to their initial markings.</summary>
    public void Reset()
    {
        foreach (var p in _places) p.Reset();
    }

    /// <summary>Returns an immutable snapshot of the current state.</summary>
    public CpnState GetState()
    {
        var markings = new Dictionary<IPlace, object>(ReferenceEqualityComparer.Instance);
        foreach (var p in _places)
            markings[p] = ((IPlaceInternal)p).GetMarkingObject();
        return new CpnState(markings);
    }

    /// <summary>Restores a previously captured state.</summary>
    public void SetState(CpnState state)
    {
        foreach (var p in _places)
            if (state.Markings.TryGetValue(p, out var m))
                ((IPlaceInternal)p).SetMarkingObject(m);
    }

    /// <summary>
    /// Attempts to migrate the marking from <paramref name="source"/> into this model.
    /// For each place in this model a matching place in <paramref name="source"/> is looked
    /// up by name.  If found and the token types are identical the marking is copied.
    /// </summary>
    /// <returns>
    /// A tuple of (restored, skipped) counts, where <c>restored</c> is the number of
    /// places whose marking was successfully transferred and <c>skipped</c> is the number
    /// that were left at their initial marking (new place, type changed, or copy failed).
    /// </returns>
    public (int Restored, int Skipped) MigrateMarkingFrom(CpnModel source)
    {
        var snap = source.Places.ToDictionary(
            p => p.Name,
            p => (p.TypeName, Marking: ((IPlaceInternal)p).GetMarkingObject()));

        int restored = 0, skipped = 0;
        foreach (var place in _places)
        {
            if (!snap.TryGetValue(place.Name, out var s))     { skipped++; continue; }
            if (s.TypeName != place.TypeName)                  { skipped++; continue; }
            try { ((IPlaceInternal)place).SetMarkingObject(s.Marking); restored++; }
            catch { skipped++; }
        }
        return (restored, skipped);
    }
}
