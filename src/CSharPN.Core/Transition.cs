using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace CSharPN.Core;

// ── BindingSnapshot ───────────────────────────────────────────────────────────

/// <summary>
/// An immutable snapshot of one valid variable assignment for a transition.
/// Returned by <see cref="Transition.GetEnabledBindings"/>;
/// passed to <see cref="Transition.Fire"/> to execute the transition.
/// </summary>
public sealed class BindingSnapshot : IEquatable<BindingSnapshot>
{
    /// <summary>The transition this binding belongs to.</summary>
    public Transition Transition { get; }

    /// <summary>Named variable values for display and inspection (name → value).</summary>
    public IReadOnlyDictionary<string, object> Values { get; }

    private readonly List<(IVar var, object value)> _bindings;

    internal BindingSnapshot(
        Transition transition,
        List<(IVar var, object value)> bindings,
        IReadOnlyDictionary<string, object> values)
    {
        Transition = transition;
        _bindings = bindings;
        Values = values;
    }

    /// <summary>Re-applies all variable bindings. Called internally before firing.</summary>
    internal void ApplyBindings()
    {
        foreach (var (v, val) in _bindings) v.BindObject(val);
    }

    /// <summary>Clears all variable bindings. Called internally after firing.</summary>
    internal void ClearBindings()
    {
        foreach (var (v, _) in _bindings) v.Unbind();
    }

    public bool Equals(BindingSnapshot? other)
    {
        if (other is null) return false;
        if (!ReferenceEquals(Transition, other.Transition)) return false;
        if (Values.Count != other.Values.Count) return false;
        foreach (var kvp in Values)
            if (!other.Values.TryGetValue(kvp.Key, out var v) || !Equals(v, kvp.Value))
                return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as BindingSnapshot);

    public override int GetHashCode()
    {
        int hash = RuntimeHelpers.GetHashCode(Transition);
        foreach (var kvp in Values)
            hash ^= HashCode.Combine(kvp.Key, kvp.Value);
        return hash;
    }

    /// <summary>Human-readable representation, e.g. <c>p=Phil(1), f1=1, f2=2</c>.</summary>
    public override string ToString() =>
        Values.Count == 0
            ? "(ε)"
            : string.Join(", ", Values.Select(kvp => $"{kvp.Key}={kvp.Value}"));
}

// ── Transition ────────────────────────────────────────────────────────────────

/// <summary>
/// A CPN transition. Created exclusively via <see cref="TransitionBuilder"/>.
/// </summary>
public sealed class Transition
{
    private readonly List<IInputArc>  _inputArcs;
    private readonly List<IOutputArc> _outputArcs;
    private readonly Func<bool>?      _guard;

    public string Name       { get; }
    /// <summary>
    /// Text representation of the guard for display, e.g. "[n &lt; 10]".
    /// Automatically set to "[G]" when a guard is present but no label supplied.
    /// Empty when there is no guard.
    /// </summary>
    public string GuardLabel { get; }

    internal Transition(
        string name,
        List<IInputArc>  inputArcs,
        List<IOutputArc> outputArcs,
        Func<bool>?      guard,
        string?          guardLabel = null)
    {
        Name        = name;
        _inputArcs  = inputArcs;
        _outputArcs = outputArcs;
        _guard      = guard;
        GuardLabel  = guard is null ? "" : (guardLabel ?? "[G]");
    }

    // ── Enabled bindings ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns all currently enabled bindings (one per valid variable assignment).
    /// The list is fully materialized before returning, so place markings and
    /// variable states are consistent throughout.
    /// </summary>
    public IReadOnlyList<BindingSnapshot> GetEnabledBindings()
    {
        // Snapshot the current available marking for each place referenced by an input arc.
        var remainders = new Dictionary<IPlace, object>(ReferenceEqualityComparer.Instance);
        foreach (var arc in _inputArcs)
            remainders.TryAdd(arc.Place, arc.Place.GetMarkingObject());

        var results = new List<BindingSnapshot>();
        Enumerate(0, remainders, results);
        return results;
    }

    private void Enumerate(int i, Dictionary<IPlace, object> remainders, List<BindingSnapshot> results)
    {
        if (i == _inputArcs.Count)
        {
            if (_guard == null || _guard())
                results.Add(CaptureSnapshot());
            return;
        }

        var arc = _inputArcs[i];
        var prev = remainders[arc.Place];

        foreach (var (updated, bind, unbind) in arc.EnumerateCandidates(prev))
        {
            bind();
            remainders[arc.Place] = updated;

            Enumerate(i + 1, remainders, results);

            unbind();
            remainders[arc.Place] = prev;
        }
    }

    private BindingSnapshot CaptureSnapshot()
    {
        var bindings = new List<(IVar, object)>();
        var values = new Dictionary<string, object>();

        foreach (var arc in _inputArcs)
            foreach (var (v, val) in arc.GetCurrentVarBindings())
            {
                bindings.Add((v, val));
                if (!string.IsNullOrEmpty(v.Name))
                    values[v.Name] = val;
            }

        return new BindingSnapshot(this, bindings, values);
    }

    // ── Firing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires this transition with the given binding: re-applies variable values,
    /// atomically consumes tokens from input places and produces tokens to output places.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the place markings no longer support the binding (stale snapshot).
    /// </exception>
    public void Fire(BindingSnapshot binding)
    {
        if (!ReferenceEquals(binding.Transition, this))
            throw new ArgumentException("Binding belongs to a different transition.", nameof(binding));

        binding.ApplyBindings();
        try
        {
            foreach (var arc in _inputArcs) arc.ConsumeFromPlace();
            foreach (var arc in _outputArcs) arc.ProduceToPlace();
        }
        finally
        {
            binding.ClearBindings();
        }
    }

    /// <summary>The variables declared (bound) by this transition's input arcs.</summary>
    internal IEnumerable<IVar> Variables => _inputArcs.SelectMany(a => a.Variables);

    public override string ToString() => $"Transition(\"{Name}\")";

    // ── Arc views for visualization / tooling ─────────────────────────────────

    /// <summary>Returns all input and output arc descriptions (for visualization).</summary>
    public IReadOnlyList<ArcView> GetArcViews() =>
        _inputArcs .Select(a => new ArcView(a.Place, ArcDirection.Input,  a.Inscription))
        .Concat(
         _outputArcs.Select(a => new ArcView(a.Place, ArcDirection.Output, a.Inscription)))
        .ToList();
}

// ── ArcView ───────────────────────────────────────────────────────────────────

/// <summary>Direction of an arc relative to its transition.</summary>
public enum ArcDirection { Input, Output }

/// <summary>Lightweight description of one arc for visualization and tooling.</summary>
public sealed record ArcView(IPlace Place, ArcDirection Direction, string Inscription = "");

// ── TransitionBuilder ─────────────────────────────────────────────────────────

/// <summary>
/// Fluent builder for <see cref="Transition"/>. Obtain via <see cref="CpnModel.AddTransition"/>.
/// </summary>
public sealed class TransitionBuilder
{
    private readonly string _name;
    private readonly CpnModel _model;
    private readonly List<IInputArc>  _inputArcs  = [];
    private readonly List<IOutputArc> _outputArcs = [];
    private Func<bool>? _guard;
    private string?     _guardLabel;
    private readonly Func<CpnTime> _getClock;

    internal TransitionBuilder(string name, CpnModel model, Func<CpnTime>? getClock = null)
    {
        _name = name;
        _model = model;
        _getClock = getClock ?? (() => CpnTime.Zero);
    }

    // ── Input arcs ────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds an input arc that binds <paramref name="var"/> to one token
    /// (or <paramref name="count"/> identical tokens) from <paramref name="place"/>.
    /// </summary>
    public TransitionBuilder Input<T>(Place<T> place, Var<T> var, int count = 1) where T : notnull, IEquatable<T>
    {
        _inputArcs.Add(new VarInputArc<T>(place, var, count));
        return this;
    }

    /// <summary>
    /// Adds an input arc that consumes the multiset produced by <paramref name="expr"/>.
    /// The expression may reference previously bound <see cref="Var{T}"/> values.
    /// </summary>
    public TransitionBuilder Input<T>(Place<T> place, Func<Multiset<T>> expr) where T : notnull, IEquatable<T>
    {
        _inputArcs.Add(new ExprInputArc<T>(place, expr));
        return this;
    }

    public TransitionBuilder Input<T>(Place<T> place, Func<T> expr) where T : notnull, IEquatable<T>
    {
        Input(place, () => Multiset.Of<T>(expr()));
        return this;
    }

    // ── Timed input arcs ──────────────────────────────────────────────────────

    /// <summary>
    /// Adds a time-aware input arc that binds <paramref name="valueVar"/> to the
    /// <em>value</em> of a <see cref="Timed{T}"/> token whose timestamp ≤ global clock.
    /// Only available when the transition was created via <see cref="TimedCpnModel.AddTransition"/>.
    /// </summary>
    public TransitionBuilder TimedInput<T>(Place<Timed<T>> place, Var<T> valueVar, int count = 1)
        where T : notnull
    {
        _inputArcs.Add(new TimedVarInputArc<T>(place, valueVar, _getClock, count));
        return this;
    }

    // ── Timed output arcs ─────────────────────────────────────────────────────

    /// <summary>
    /// Adds a time-aware output arc that produces a <see cref="Timed{T}"/> token
    /// with timestamp = current clock + result of <paramref name="delayExpr"/>.
    /// </summary>
    public TransitionBuilder TimedOutput<T>(Place<Timed<T>> place, Func<T> valueExpr, Func<int> delayExpr)
        where T : notnull
    {
        _outputArcs.Add(new TimedOutputArc<T>(place, valueExpr, delayExpr, _getClock));
        return this;
    }

    /// <summary>
    /// Adds a time-aware output arc with a constant <paramref name="delay"/>.
    /// </summary>
    public TransitionBuilder TimedOutput<T>(Place<Timed<T>> place, Func<T> valueExpr, int delay)
        where T : notnull
    {
        _outputArcs.Add(new TimedOutputArc<T>(place, valueExpr, () => delay, _getClock));
        return this;
    }

    // ── Guard ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the guard from an expression tree. The display label is derived
    /// automatically from the expression body — <c>() =&gt; p.Val == a.Val.Owner</c>
    /// shows <c>p == a.Owner</c> (the <c>.Val</c> suffix is stripped for readability).
    /// Use the <see cref="Guard(Func{bool}, string)"/> overload for a custom label
    /// or a statement-bodied guard.
    /// </summary>
    public TransitionBuilder Guard(Expression<Func<bool>> guard)
    {
        _guard      = guard.Compile();
        _guardLabel = $"[{FormatExpr(guard.Body)}]";
        return this;
    }

    /// <summary>
    /// Sets the guard with an explicit display label. Use this overload when the
    /// auto-derived label would be unclear, or for statement-bodied guards
    /// (<c>() =&gt; { … }</c>) that cannot be represented as an expression tree.
    /// </summary>
    /// <param name="guard">The boolean guard expression.</param>
    /// <param name="label">Display label shown on the transition, e.g. <c>"[n &lt; 10]"</c>.</param>
    public TransitionBuilder Guard(Func<bool> guard, string label)
    {
        _guard      = guard;
        _guardLabel = label;
        return this;
    }

    // ── Output arcs ───────────────────────────────────────────────────────────

    /// <summary>
    /// Adds an output arc that produces a single token with the current value of
    /// <paramref name="var"/>. The arc inscription defaults to the variable name.
    /// </summary>
    public TransitionBuilder Output<T>(Place<T> place, Var<T> var, string? label = null) where T : notnull, IEquatable<T>
    {
        _outputArcs.Add(new SingleOutputArc<T>(place, () => var.Val, label ?? var.Name));
        return this;
    }

    /// <summary>
    /// Adds an output arc whose inscription is automatically derived from the
    /// lambda expression body.  Write <c>() => x.Val * 2</c> — the arc shows
    /// <c>x * 2</c> (the <c>.Val</c> suffix is stripped for readability).
    /// </summary>
    public TransitionBuilder Output<T>(Place<T> place, Expression<Func<T>> expr) where T : notnull, IEquatable<T>
    {
        _outputArcs.Add(new SingleOutputArc<T>(place, expr.Compile(), FormatExpr(expr.Body)));
        return this;
    }

    /// <summary>
    /// Adds an output arc that produces the multiset returned by <paramref name="expr"/>,
    /// with the inscription automatically derived from the lambda expression body.
    /// </summary>
    public TransitionBuilder Output<T>(Place<T> place, Expression<Func<Multiset<T>>> expr) where T : notnull, IEquatable<T>
    {
        _outputArcs.Add(new MultisetOutputArc<T>(place, expr.Compile(), FormatExpr(expr.Body)));
        return this;
    }

    /// <summary>
    /// Adds an output arc with an explicit inscription label.
    /// Use when the auto-derived label would be unclear.
    /// </summary>
    public TransitionBuilder Output<T>(Place<T> place, Func<T> expr, string label) where T : notnull, IEquatable<T>
    {
        _outputArcs.Add(new SingleOutputArc<T>(place, expr, label));
        return this;
    }

    /// <summary>Adds a multiset output arc with an explicit inscription label.</summary>
    public TransitionBuilder Output<T>(Place<T> place, Func<Multiset<T>> expr, string label) where T : notnull, IEquatable<T>
    {
        _outputArcs.Add(new MultisetOutputArc<T>(place, expr, label));
        return this;
    }

    // ── Expression label formatter ────────────────────────────────────────────

    private static string FormatExpr(Expression e) => e switch
    {
        // Strip implicit/explicit casts — produced by Var<T> → T implicit conversion
        UnaryExpression { NodeType: ExpressionType.Convert
                       or ExpressionType.ConvertChecked } u
            => FormatExpr(u.Operand),

        // Strip .Val accessor
        MemberExpression { Member.Name: "Val" } m
            => FormatExpr(m.Expression!),

        // Closure field → variable name (e.g. the captured Var<T> instance)
        MemberExpression { Expression: ConstantExpression } m
            => m.Member.Name,

        // Static member (e.g. AllData) — Expression is null
        MemberExpression { Expression: null } m
            => m.Member.Name,

        // Other member access (e.g. p.Data)
        MemberExpression m
            => $"{FormatExpr(m.Expression!)}.{m.Member.Name}",

        // Array indexer (e.g. AllData[k])
        BinaryExpression { NodeType: ExpressionType.ArrayIndex } b
            => $"{FormatExpr(b.Left)}[{FormatExpr(b.Right)}]",

        // Binary operators
        BinaryExpression b
            => $"{FormatExpr(b.Left)} {BinOpStr(b.NodeType)} {FormatExpr(b.Right)}",

        // Unary minus / not
        UnaryExpression { NodeType: ExpressionType.Negate } u
            => $"-{FormatExpr(u.Operand)}",
        UnaryExpression { NodeType: ExpressionType.Not } u
            => $"!{FormatExpr(u.Operand)}",

        // Constants
        ConstantExpression { Value: null }  => "null",
        ConstantExpression c                => c.Value!.ToString()!,

        // Method calls: Multiset.Of(x), string.Format(…) etc.
        MethodCallExpression mc => FormatCall(mc),

        // New object: new Packet(…) etc.
        NewExpression nw => FormatNew(nw),

        // Fallback — ToString() and clean up closure noise
        _ => CleanFallback(e)
    };

    private static string BinOpStr(ExpressionType t) => t switch
    {
        ExpressionType.Add              => "+",
        ExpressionType.Subtract         => "-",
        ExpressionType.Multiply         => "*",
        ExpressionType.Divide           => "/",
        ExpressionType.Modulo           => "%",
        ExpressionType.Equal            => "==",
        ExpressionType.NotEqual         => "!=",
        ExpressionType.LessThan         => "<",
        ExpressionType.LessThanOrEqual  => "<=",
        ExpressionType.GreaterThan      => ">",
        ExpressionType.GreaterThanOrEqual => ">=",
        ExpressionType.AndAlso          => "&&",
        ExpressionType.OrElse           => "||",
        _                               => t.ToString()
    };

    private static string FormatCall(MethodCallExpression mc)
    {
        var args = string.Join(", ", mc.Arguments.Select(FormatExpr));
        if (mc.Object != null)
            return $"{FormatExpr(mc.Object)}.{mc.Method.Name}({args})";
        return $"{mc.Method.DeclaringType?.Name}.{mc.Method.Name}({args})";
    }

    private static string FormatNew(NewExpression nw)
    {
        var args = string.Join(", ", nw.Arguments.Select(FormatExpr));
        return $"new {nw.Type.Name}({args})";
    }

    private static string CleanFallback(Expression e)
    {
        var s = e.ToString();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"value\([^)]*\)\.", "");
        s = s.Replace(".Val", "");
        if (s.Length > 2 && s[0] == '(' && s[^1] == ')') s = s[1..^1];
        return s;
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finalises the transition, registers it with the model, and returns it.
    /// </summary>
    public Transition Build()
    {
        var t = new Transition(_name, _inputArcs, _outputArcs, _guard, _guardLabel);
        _model.RegisterTransition(t);
        return t;
    }
}
