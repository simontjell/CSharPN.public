namespace CSharPN.Core;

// ── Input arcs ────────────────────────────────────────────────────────────────

/// <summary>
/// Internal interface for input arcs. The framework uses this to enumerate
/// candidate bindings and to consume tokens from places when a transition fires.
/// </summary>
internal interface IInputArc
{
    /// <summary>The place this arc reads from.</summary>
    IPlaceInternal Place { get; }
    /// <summary>Short inscription shown on the arc in the visualizer (e.g. variable name).</summary>
    string Inscription { get; }

    /// <summary>
    /// Given the currently <paramref name="available"/> marking for this arc's place
    /// (a <c>Multiset&lt;T&gt;</c> boxed as <c>object</c>), enumerates all candidate
    /// bindings this arc can produce.
    ///
    /// Each candidate provides:
    /// <list type="bullet">
    ///   <item><description><c>updatedAvailable</c> – the remaining marking after consuming this candidate</description></item>
    ///   <item><description><c>bind</c> – action that sets the arc's variable(s) to this candidate's values</description></item>
    ///   <item><description><c>unbind</c> – action that clears those bindings</description></item>
    /// </list>
    /// </summary>
    IEnumerable<(object updatedAvailable, Action bind, Action unbind)> EnumerateCandidates(object available);

    /// <summary>
    /// Consumes the appropriate tokens from the actual place marking using the
    /// currently bound variable values. Called during <see cref="Transition.Fire"/>.
    /// </summary>
    void ConsumeFromPlace();

    /// <summary>
    /// Returns (IVar, currentValue) pairs for all variables currently bound by this arc.
    /// Used to capture a <see cref="BindingSnapshot"/>.
    /// </summary>
    IEnumerable<(IVar var, object value)> GetCurrentVarBindings();
}

// ── Output arcs ───────────────────────────────────────────────────────────────

/// <summary>Internal interface for output arcs.</summary>
internal interface IOutputArc
{
    /// <summary>The place this arc writes to.</summary>
    IPlaceInternal Place { get; }
    /// <summary>Short inscription shown on the arc in the visualizer (e.g. expression label).</summary>
    string Inscription { get; }

    /// <summary>
    /// Produces tokens into the place using the currently bound variable values.
    /// Called during <see cref="Transition.Fire"/>.
    /// </summary>
    void ProduceToPlace();
}

// ── Concrete input arcs ───────────────────────────────────────────────────────

/// <summary>
/// Input arc that binds a <see cref="Var{T}"/> to a single distinct token
/// from a <see cref="Place{T}"/>, optionally consuming multiple copies.
/// </summary>
internal sealed class VarInputArc<T> : IInputArc where T : notnull, IEquatable<T>
{
    private readonly Place<T> _place;
    private readonly Var<T> _var;
    private readonly int _count;

    public VarInputArc(Place<T> place, Var<T> var, int count = 1)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        _place = place;
        _var = var;
        _count = count;
    }

    public IPlaceInternal Place => _place;
    public string Inscription   => _count == 1 ? _var.Name : $"{_count}`{_var.Name}";

    public IEnumerable<(object, Action, Action)> EnumerateCandidates(object available)
    {
        var marking = (Multiset<T>)available;

        if (_var.IsBound)
        {
            // Variable already bound by an earlier arc → unification: the only
            // candidate is the matching token, and bind/unbind are no-ops so the
            // earlier arc retains ownership of the binding.
            var bound = _var.Val;
            if (marking.Count(bound) >= _count)
                yield return (marking - Multiset.Repeat(bound, _count), () => { }, () => { });
            yield break;
        }

        foreach (var token in marking.DistinctItems())
        {
            if (marking.Count(token) >= _count)
            {
                var t = token; // capture
                var consumed = Multiset.Repeat(t, _count);
                yield return (
                    marking - consumed,
                    () => _var.Bind(t),
                    () => _var.Unbind()
                );
            }
        }
    }

    public void ConsumeFromPlace() =>
        _place.Marking = _place.Marking - Multiset.Repeat(_var.Val, _count);

    public IEnumerable<(IVar, object)> GetCurrentVarBindings()
    {
        if (_var.IsBound) yield return (_var, _var.Val!);
    }
}

/// <summary>
/// Input arc that consumes a multiset computed by a lambda expression.
/// The expression may reference bound <see cref="Var{T}"/> values from
/// preceding arcs in the same transition.
/// No new variable bindings are introduced.
/// </summary>
internal sealed class ExprInputArc<T> : IInputArc where T : notnull, IEquatable<T>
{
    private readonly Place<T> _place;
    private readonly Func<Multiset<T>> _expr;

    public ExprInputArc(Place<T> place, Func<Multiset<T>> expr)
    {
        _place = place;
        _expr = expr;
    }

    public IPlaceInternal Place  => _place;
    public string          Inscription => "";   // expression lambdas can't be introspected

    public IEnumerable<(object, Action, Action)> EnumerateCandidates(object available)
    {
        var marking = (Multiset<T>)available;
        var required = _expr();
        if (required <= marking)
            yield return (marking - required, () => { }, () => { });
    }

    public void ConsumeFromPlace() =>
        _place.Marking = _place.Marking - _expr();

    public IEnumerable<(IVar, object)> GetCurrentVarBindings() =>
        Enumerable.Empty<(IVar, object)>();
}

// ── Concrete output arcs ──────────────────────────────────────────────────────

/// <summary>Output arc that produces a single token computed by a lambda.</summary>
internal sealed class SingleOutputArc<T> : IOutputArc where T : notnull, IEquatable<T>
{
    private readonly Place<T> _place;
    private readonly Func<T>  _expr;

    public SingleOutputArc(Place<T> place, Func<T> expr, string inscription = "")
    {
        _place = place;
        _expr  = expr;
        Inscription = inscription;
    }

    public IPlaceInternal Place       => _place;
    public string          Inscription { get; }
    public void ProduceToPlace() => _place.Marking = _place.Marking.Add(_expr());
}

/// <summary>Output arc that produces a multiset computed by a lambda.</summary>
internal sealed class MultisetOutputArc<T> : IOutputArc where T : notnull, IEquatable<T>
{
    private readonly Place<T>           _place;
    private readonly Func<Multiset<T>>  _expr;

    public MultisetOutputArc(Place<T> place, Func<Multiset<T>> expr, string inscription = "")
    {
        _place = place;
        _expr  = expr;
        Inscription = inscription;
    }

    public IPlaceInternal Place       => _place;
    public string          Inscription { get; }
    public void ProduceToPlace() => _place.Marking = _place.Marking + _expr();
}
