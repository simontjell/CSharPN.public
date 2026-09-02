namespace CSharPN.Core;

// ── Token readiness (timed colour sets) ──────────────────────────────────────

/// <summary>
/// Implemented by <see cref="Timed{T}"/>. Lets the generic arcs recognise timed
/// tokens so that a token is only available once its time stamp is ≤ the global
/// clock (Jensen &amp; Kristensen 2009, Chapter 10: a token is <em>ready</em>).
/// </summary>
internal interface ITimedToken
{
    CpnTime ReadyAt { get; }
}

internal static class TokenReadiness
{
    /// <summary>Untimed tokens are always ready; timed tokens are ready when <c>ReadyAt ≤ clock</c>.</summary>
    public static bool IsReady<T>(T token, CpnTime clock) where T : notnull
        => token is not ITimedToken timed || timed.ReadyAt <= clock;
}

// ── Input arcs ────────────────────────────────────────────────────────────────

/// <summary>
/// Internal interface for input arcs, i.e. arcs <c>a = (p, t)</c> with arc
/// expression <c>E(a)</c> (Jensen &amp; Kristensen 2009, Definition 4.2 (8)).
/// </summary>
/// <remarks>
/// Two roles are separated:
/// <list type="bullet">
///   <item><description>
///   <b>Pattern arcs</b> (<see cref="BoundVariables"/> non-empty) introduce variables. During
///   binding enumeration they propose candidate values for their variables via
///   <see cref="EnumerateCandidates"/>. This is CPN Tools' rule that a variable is bound
///   from an input arc whose inscription is a pattern (here: a single variable).
///   </description></item>
///   <item><description>
///   <b>Every</b> input arc evaluates its demand <c>E(a)⟨b⟩</c> under a complete binding via
///   <see cref="TryConsume"/>. This is what the enabling rule (Definition 4.4) and the
///   occurrence rule (Definition 4.5) use; expression arcs only take part here, so the
///   order in which arcs are declared never matters for enabling.
///   </description></item>
/// </list>
/// </remarks>
internal interface IInputArc
{
    /// <summary>The place <c>p</c> this arc reads from.</summary>
    IPlaceInternal Place { get; }

    /// <summary>Short inscription shown on the arc in the visualizer (e.g. variable name).</summary>
    string Inscription { get; }

    /// <summary>The variables this arc binds (pattern variables). Empty for expression arcs.</summary>
    IReadOnlyList<IVar> BoundVariables { get; }

    /// <summary>
    /// Enumerates candidate bindings of this arc's variables given the tokens still
    /// <paramref name="available"/> on the place (a boxed <c>Multiset&lt;T&gt;</c>).
    /// Each candidate provides the remaining marking after this arc's demand, and
    /// actions that bind / unbind the variables. Only valid for pattern arcs.
    /// </summary>
    IEnumerable<(object remaining, Action bind, Action unbind)> EnumerateCandidates(object available);

    /// <summary>
    /// Evaluates the arc expression under the current (complete) binding, <c>E(a)⟨b⟩</c>,
    /// and removes it from <paramref name="available"/>. Returns the remaining marking,
    /// or <see langword="null"/> when <paramref name="available"/> does not contain the
    /// demanded (and, for timed places, ready) tokens.
    /// </summary>
    object? TryConsume(object available);
}

// ── Output arcs ───────────────────────────────────────────────────────────────

/// <summary>
/// Internal interface for output arcs, i.e. arcs <c>a = (t, p)</c>.
/// </summary>
internal interface IOutputArc
{
    /// <summary>The place <c>p</c> this arc writes to.</summary>
    IPlaceInternal Place { get; }

    /// <summary>Short inscription shown on the arc in the visualizer (e.g. expression label).</summary>
    string Inscription { get; }

    /// <summary>
    /// Evaluates the arc expression under the current binding, <c>E(a)⟨b⟩</c>, and returns
    /// the produced tokens as a boxed <c>Multiset&lt;T&gt;</c>. Does not touch the place.
    /// </summary>
    object Produce();
}

// ── Concrete input arcs ───────────────────────────────────────────────────────

/// <summary>
/// Pattern input arc with inscription <c>count`var</c>: binds <see cref="Var{T}"/> to one
/// distinct token colour of the place, demanding <c>count</c> copies of it.
/// </summary>
internal sealed class VarInputArc<T> : IInputArc where T : notnull, IEquatable<T>
{
    private readonly Place<T>      _place;
    private readonly Var<T>        _var;
    private readonly int           _count;
    private readonly Func<CpnTime> _getClock;

    public VarInputArc(Place<T> place, Var<T> var, Func<CpnTime> getClock, int count = 1)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        _place    = place;
        _var      = var;
        _count    = count;
        _getClock = getClock;
    }

    public IPlaceInternal Place => _place;
    public string Inscription   => _count == 1 ? _var.Name : $"{_count}`{_var.Name}";
    public IReadOnlyList<IVar> BoundVariables => [_var];

    public IEnumerable<(object, Action, Action)> EnumerateCandidates(object available)
    {
        if (_var.IsBound)
        {
            // Unification: the variable was bound by an earlier arc. The only candidate
            // is that value; bind/unbind are no-ops so the earlier arc keeps ownership.
            var remaining = TryConsume(available);
            if (remaining is not null) yield return (remaining, () => { }, () => { });
            yield break;
        }

        var marking = (Multiset<T>)available;
        var clock   = _getClock();
        foreach (var token in marking.DistinctItems())
        {
            if (!TokenReadiness.IsReady(token, clock) || marking.Count(token) < _count) continue;
            var t = token; // capture
            yield return (marking - Multiset.Repeat(t, _count), () => _var.Bind(t), () => _var.Unbind());
        }
    }

    public object? TryConsume(object available)
    {
        var marking = (Multiset<T>)available;
        var value   = _var.Val;
        if (!TokenReadiness.IsReady(value, _getClock()) || marking.Count(value) < _count) return null;
        return marking - Multiset.Repeat(value, _count);
    }
}

/// <summary>
/// Expression input arc: its inscription is an arbitrary multiset expression over
/// already-bound variables. It introduces no variables; it is only evaluated once
/// all variables of the transition are bound, regardless of declaration order.
/// </summary>
internal sealed class ExprInputArc<T> : IInputArc where T : notnull, IEquatable<T>
{
    private readonly Place<T>           _place;
    private readonly Func<Multiset<T>>  _expr;
    private readonly Func<CpnTime>      _getClock;

    public ExprInputArc(Place<T> place, Func<Multiset<T>> expr, Func<CpnTime> getClock)
    {
        _place    = place;
        _expr     = expr;
        _getClock = getClock;
    }

    public IPlaceInternal Place => _place;
    public string Inscription   => "";   // expression lambdas can't be introspected
    public IReadOnlyList<IVar> BoundVariables => [];

    public IEnumerable<(object, Action, Action)> EnumerateCandidates(object available)
        => throw new InvalidOperationException("Expression arcs do not bind variables.");

    public object? TryConsume(object available)
    {
        var marking  = (Multiset<T>)available;
        var required = _expr();
        var clock    = _getClock();
        foreach (var token in required.DistinctItems())
            if (!TokenReadiness.IsReady(token, clock)) return null;
        return required <= marking ? marking - required : null;
    }
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

    public IPlaceInternal Place => _place;
    public string Inscription { get; }
    public object Produce() => Multiset.Of(_expr());
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

    public IPlaceInternal Place => _place;
    public string Inscription { get; }
    public object Produce() => _expr();
}
