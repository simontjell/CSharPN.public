using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace CSharPN.Core;

// ── BindingSnapshot ───────────────────────────────────────────────────────────

/// <summary>
/// A <b>binding</b> <c>b ∈ B(t)</c> of a transition: a function that maps every variable
/// <c>v ∈ Var(t)</c> to a value <c>b(v) ∈ Type[v]</c> (Jensen &amp; Kristensen 2009,
/// Definition 4.3 (4)). Together with <see cref="Transition"/> it forms a
/// <b>binding element</b> <c>(t, b)</c> (Definition 4.3 (5)).
/// Returned by <see cref="Transition.GetEnabledBindings()"/>; passed to
/// <see cref="Transition.Fire"/> to let the binding element occur.
/// </summary>
public sealed class BindingSnapshot : IEquatable<BindingSnapshot>
{
    /// <summary>The transition <c>t</c> this binding belongs to.</summary>
    public Transition Transition { get; }

    /// <summary>Named variable values for display and inspection (name → value).</summary>
    public IReadOnlyDictionary<string, object> Values { get; }

    private readonly List<(IVar var, object value)> _bindings;

    internal BindingSnapshot(Transition transition, List<(IVar var, object value)> bindings)
    {
        Transition = transition;
        _bindings  = bindings;

        var values = new Dictionary<string, object>();
        foreach (var (v, val) in bindings)
            if (!string.IsNullOrEmpty(v.Name)) values[v.Name] = val;
        Values = values;
    }

    /// <summary>Re-applies all variable bindings (makes <c>b</c> the current binding).</summary>
    internal void ApplyBindings()
    {
        foreach (var (v, val) in _bindings) v.BindObject(val);
    }

    /// <summary>Clears all variable bindings.</summary>
    internal void ClearBindings()
    {
        foreach (var (v, _) in _bindings) v.Unbind();
    }

    /// <summary>Two snapshots are equal when they are bindings of the same transition assigning the same values.</summary>
    public bool Equals(BindingSnapshot? other)
    {
        if (other is null) return false;
        if (!ReferenceEquals(Transition, other.Transition)) return false;
        if (_bindings.Count != other._bindings.Count) return false;
        for (int i = 0; i < _bindings.Count; i++)
        {
            if (!ReferenceEquals(_bindings[i].var, other._bindings[i].var)) return false;
            if (!Equals(_bindings[i].value, other._bindings[i].value)) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as BindingSnapshot);

    public override int GetHashCode()
    {
        int hash = RuntimeHelpers.GetHashCode(Transition);
        foreach (var (v, val) in _bindings)
            hash = HashCode.Combine(hash, RuntimeHelpers.GetHashCode(v), val);
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
/// A CPN transition <c>t ∈ T</c> with its guard <c>G(t)</c>, its input arcs
/// (<c>E(p,t)</c>), its output arcs (<c>E(t,p)</c>) and its variables <c>Var(t)</c>.
/// Created exclusively via <see cref="TransitionBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// The semantics implemented here follows Jensen &amp; Kristensen, <i>Coloured Petri Nets:
/// Modelling and Validation of Concurrent Systems</i> (2009), Chapter 4, and — for timed
/// places — Chapter 10. Each method names the definition it implements. See
/// <c>SEMANTICS.md</c> for the complete mapping.
/// </para>
/// <para>
/// Multiple input arcs from the same place are allowed; as in CPN Tools their arc expressions
/// are summed: <c>E(p,t) = Σ_{a ∈ A(p,t)} E(a)</c>.
/// </para>
/// </remarks>
public sealed class Transition
{
    private static readonly ReferenceEqualityComparer Ref = ReferenceEqualityComparer.Instance;

    private readonly List<IInputArc>     _inputArcs;
    private readonly List<IInputArc>     _patternArcs;    // input arcs that bind variables
    private readonly List<IOutputArc>    _outputArcs;
    private readonly Func<bool>?         _guard;
    private readonly IReadOnlyList<IVar> _variables;      // Var(t)
    private readonly IReadOnlyList<IVar> _freeVariables;  // Var(t) minus the variables bound by input arcs

    public string Name { get; }

    /// <summary>
    /// Text representation of the guard for display, e.g. "[n &lt; 10]".
    /// Automatically set to "[G]" when a guard is present but no label supplied.
    /// Empty when there is no guard.
    /// </summary>
    public string GuardLabel { get; }

    internal Transition(
        string               name,
        List<IInputArc>      inputArcs,
        List<IOutputArc>     outputArcs,
        Func<bool>?          guard,
        string?              guardLabel,
        IReadOnlyList<IVar>  variables,
        IReadOnlyList<IVar>  freeVariables)
    {
        Name           = name;
        _inputArcs     = inputArcs;
        _patternArcs   = inputArcs.Where(a => a.BoundVariables.Count > 0).ToList();
        _outputArcs    = outputArcs;
        _guard         = guard;
        GuardLabel     = guard is null ? "" : (guardLabel ?? "[G]");
        _variables     = variables;
        _freeVariables = freeVariables;
    }

    // ── Var(t) — Definition 4.3 (3) ───────────────────────────────────────────

    /// <summary>The variables <c>Var(t)</c> of this transition.</summary>
    internal IEnumerable<IVar> Variables => _variables;

    /// <summary>Names of the variables <c>Var(t)</c> of this transition (unnamed variables omitted).</summary>
    public IReadOnlyList<string> VariableNames =>
        _variables.Select(v => v.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();

    /// <summary>
    /// Names of the <em>free</em> variables of this transition: variables in <c>Var(t)</c> that no
    /// input arc binds and which are therefore bound by enumerating their colour set.
    /// </summary>
    public IReadOnlyList<string> FreeVariableNames =>
        _freeVariables.Select(v => v.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();

    // ── Enabled binding elements ──────────────────────────────────────────────

    /// <summary>
    /// Returns every binding <c>b ∈ B(t)</c> such that the step <c>1`(t,b)</c> is enabled in the
    /// current marking (Definition 4.4). The list is fully materialised before returning, so
    /// place markings and variable states are consistent throughout.
    /// </summary>
    /// <remarks>
    /// Candidate bindings are generated the way CPN Tools binds variables: pattern input arcs
    /// propose values from the tokens on their place (with unification when a variable occurs
    /// on several arcs, and with the tokens demanded by earlier arcs already subtracted), and
    /// free variables range over their colour set. Every candidate is then checked against the
    /// enabling rule itself, so expression arcs and the guard are evaluated under a complete
    /// binding regardless of the order in which arcs were declared.
    /// </remarks>
    public IReadOnlyList<BindingSnapshot> GetEnabledBindings() => GetEnabledBindings(int.MaxValue);

    /// <summary>
    /// Returns at most <paramref name="max"/> enabled bindings, abandoning the search as
    /// soon as that many have been found. Use <c>max: 1</c> to ask "is this transition
    /// enabled, and with which binding?" without enumerating every binding.
    /// </summary>
    public IReadOnlyList<BindingSnapshot> GetEnabledBindings(int max)
    {
        if (max < 1) throw new ArgumentOutOfRangeException(nameof(max), "Must be at least 1.");
        var marking = SnapshotMarking();
        var results = new List<BindingSnapshot>();
        ForEachCandidateBinding(marking, () =>
        {
            if (IsEnabledUnderCurrentBinding(marking))
                results.Add(CaptureSnapshot());
            return results.Count >= max;
        });
        return results;
    }

    /// <summary>
    /// Is the binding element <c>(t, b)</c> enabled in the <em>current</em> marking?
    /// (Definition 4.4 with <c>Y = 1`(t,b)</c>.) Use this to re-validate a snapshot
    /// obtained earlier.
    /// </summary>
    public bool IsEnabled(BindingSnapshot binding)
    {
        CheckOwnership(binding);
        binding.ApplyBindings();
        try { return IsEnabledUnderCurrentBinding(SnapshotMarking()); }
        finally { binding.ClearBindings(); }
    }

    // ── Definition 4.4: enabling of the step 1`(t,b) ──────────────────────────

    /// <summary>
    /// <c>G(t)⟨b⟩ ∧ ∀p ∈ P: E(p,t)⟨b⟩ ≤ M(p)</c> for the currently applied binding.
    /// </summary>
    private bool IsEnabledUnderCurrentBinding(Dictionary<IPlace, object> marking)
        => EvaluateGuard() && TryConsumeAll(marking) is not null;

    /// <summary><c>G(t)⟨b⟩</c> — the guard evaluated in the current binding (true when there is no guard).</summary>
    internal bool EvaluateGuard()
    {
        if (_guard is null) return true;
        return Evaluate(() =>
        {
            bool result = GuardScope.Evaluate(_guard, out var readPlace);
            if (readPlace is not null)
                throw new InvalidOperationException(
                    $"Transition '{Name}': the guard read the marking of place \"{readPlace.Name}\" " +
                    $"(through a method call, which the build-time check cannot see). {GuardRule.Requirement}");
            return result;
        }, "the guard");
    }

    /// <summary>
    /// Checks <c>∀p: E(p,t)⟨b⟩ ≤ M(p)</c> by folding every input arc's demand over
    /// <paramref name="marking"/>: each arc removes <c>E(a)⟨b⟩</c> from what is left of its
    /// place. Summing the arc expressions of all arcs from <c>p</c> to <c>t</c> and comparing
    /// with <c>M(p)</c> is exactly this fold. Returns the marking after removal, i.e.
    /// <c>M(p) − E(p,t)⟨b⟩</c> per place, or <see langword="null"/> when some place lacks tokens.
    /// Places not present in <paramref name="marking"/> are read from the model, so a caller
    /// may fold several binding elements (a step) through the same dictionary.
    /// </summary>
    internal Dictionary<IPlace, object>? TryConsumeAll(Dictionary<IPlace, object> marking)
    {
        var remaining = new Dictionary<IPlace, object>(marking, Ref);
        foreach (var arc in _inputArcs)
        {
            if (!remaining.TryGetValue(arc.Place, out var available))
                available = arc.Place.GetMarkingObject();

            var after = Evaluate(() => arc.TryConsume(available), $"the input arc from '{arc.Place.Name}'");
            if (after is null) return null;
            remaining[arc.Place] = after;
        }
        return remaining;
    }

    /// <summary>
    /// <c>E(t,p)⟨b⟩</c> for every output arc, evaluated in the current binding.
    /// Nothing is written to the places.
    /// </summary>
    internal List<(IPlaceInternal place, object tokens)> Produce()
    {
        var produced = new List<(IPlaceInternal, object)>(_outputArcs.Count);
        foreach (var arc in _outputArcs)
            produced.Add((arc.Place, Evaluate(arc.Produce, $"the output arc to '{arc.Place.Name}'")));
        return produced;
    }

    // ── Candidate generation (binding inference) ──────────────────────────────

    /// <summary>
    /// Calls <paramref name="onCandidate"/> once per candidate binding (with the variables
    /// bound); the callback returns true to stop the enumeration early.
    /// </summary>
    private void ForEachCandidateBinding(Dictionary<IPlace, object> marking, Func<bool> onCandidate)
    {
        var remaining = new Dictionary<IPlace, object>(marking, Ref);
        EnumeratePatternArcs(0, remaining, onCandidate);
    }

    /// <summary>
    /// Depth-first enumeration over the pattern arcs: arc <c>i</c> proposes values for its
    /// variable from the tokens still available on its place after arcs <c>0..i-1</c>.
    /// Returns true when the enumeration was stopped by the callback.
    /// </summary>
    private bool EnumeratePatternArcs(int i, Dictionary<IPlace, object> remaining, Func<bool> onCandidate)
    {
        if (i == _patternArcs.Count)
            return EnumerateFreeVariables(0, onCandidate);

        var arc  = _patternArcs[i];
        var prev = remaining[arc.Place];

        foreach (var (updated, bind, unbind) in arc.EnumerateCandidates(prev))
        {
            bind();
            remaining[arc.Place] = updated;
            bool stop;
            try
            {
                stop = EnumeratePatternArcs(i + 1, remaining, onCandidate);
            }
            finally
            {
                unbind();
                remaining[arc.Place] = prev;
            }
            if (stop) return true;
        }
        return false;
    }

    /// <summary>
    /// A free variable is bound to every value of its colour set in turn (CPN Tools: a variable
    /// that occurs only on output arcs / in the guard is bound to an arbitrary value of its
    /// small colour set; Definition 4.3 (4) allows any <c>b(v) ∈ Type[v]</c>).
    /// </summary>
    private bool EnumerateFreeVariables(int j, Func<bool> onCandidate)
    {
        if (j == _freeVariables.Count)
            return onCandidate();

        var v = _freeVariables[j];
        foreach (var value in v.DomainObjects!)
        {
            v.BindObject(value);
            bool stop;
            try { stop = EnumerateFreeVariables(j + 1, onCandidate); }
            finally { v.Unbind(); }
            if (stop) return true;
        }
        return false;
    }

    private BindingSnapshot CaptureSnapshot()
    {
        var bindings = new List<(IVar, object)>(_variables.Count);
        foreach (var v in _variables)
            bindings.Add((v, v.GetValue()));
        return new BindingSnapshot(this, bindings);
    }

    // ── Definition 4.5: occurrence of the step 1`(t,b) ────────────────────────

    /// <summary>
    /// Lets the binding element <c>(t, b)</c> occur: for every place
    /// <c>M₂(p) = (M₁(p) − E(p,t)⟨b⟩) + E(t,p)⟨b⟩</c>.
    /// All arc expressions are evaluated before any place is modified, so the
    /// occurrence is atomic.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The binding element is no longer enabled in the current marking (stale snapshot).
    /// </exception>
    public void Fire(BindingSnapshot binding)
    {
        CheckOwnership(binding);
        binding.ApplyBindings();
        try
        {
            var marking = SnapshotMarking();

            // M₁(p) − E(p,t)⟨b⟩
            var afterRemoval = TryConsumeAll(marking)
                ?? throw new InvalidOperationException(
                    $"Binding element ({Name}, <{binding}>) is not enabled in the current marking.");

            // E(t,p)⟨b⟩
            var produced = Produce();

            foreach (var (place, m) in afterRemoval)
                ((IPlaceInternal)place).SetMarkingObject(m);
            foreach (var (place, tokens) in produced)
                place.SetMarkingObject(place.AddMarkingObject(place.GetMarkingObject(), tokens));
        }
        finally
        {
            binding.ClearBindings();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Dictionary<IPlace, object> SnapshotMarking()
    {
        var marking = new Dictionary<IPlace, object>(Ref);
        foreach (var arc in _inputArcs)
            marking.TryAdd(arc.Place, arc.Place.GetMarkingObject());
        return marking;
    }

    private void CheckOwnership(BindingSnapshot binding)
    {
        if (!ReferenceEquals(binding.Transition, this))
            throw new ArgumentException("Binding belongs to a different transition.", nameof(binding));
    }

    /// <summary>
    /// Evaluates an inscription and turns an <see cref="UnboundVariableException"/> into the
    /// error a CPN Tools user expects ("variable cannot be bound"), naming the transition.
    /// </summary>
    private TResult Evaluate<TResult>(Func<TResult> inscription, string where)
    {
        try
        {
            return inscription();
        }
        catch (UnboundVariableException ex)
        {
            throw new InvalidOperationException(
                $"Transition '{Name}': the variable '{ex.VariableName}' is used in {where} but is not bound " +
                "by any input arc of the transition (CPN Tools: \"variable cannot be bound\"). " +
                "Bind it on an input arc, or declare it as a free variable with .Free(var) and give the " +
                "Var a Domain so its colour set can be enumerated.", ex);
        }
    }

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
/// <remarks>
/// <para>
/// The order of <c>Input</c>/<c>Output</c>/<c>Guard</c> calls is irrelevant for the semantics,
/// exactly as arc placement is irrelevant in CPN Tools. Any variable may be bound by any input
/// arc that carries it; expression arcs and the guard are evaluated once all variables are bound.
/// </para>
/// <para>
/// A variable that occurs only in output-arc expressions or the guard (a <em>free variable</em>)
/// is bound by enumerating its colour set. Variables referenced from expression-tree overloads
/// (<see cref="Guard(Expression{Func{bool}}, string)"/>, <see cref="Output{T}(Place{T}, Expression{Func{T}})"/>,
/// <see cref="Output{T}(Place{T}, Var{T}, string)"/>, …) are discovered automatically; variables used
/// only inside plain <c>Func</c> lambdas must be declared with <see cref="Free{T}"/>.
/// </para>
/// </remarks>
public sealed class TransitionBuilder
{
    private readonly string   _name;
    private readonly CpnModel _model;
    private readonly List<IInputArc>  _inputArcs  = [];
    private readonly List<IOutputArc> _outputArcs = [];
    private readonly List<IVar>       _referencedVars = [];   // Var(t) contributions from non-input inscriptions
    private Func<bool>? _guard;
    private string?     _guardLabel;
    private string?     _guardProblem;
    private Func<int>?  _transitionDelay;
    private readonly Func<CpnTime> _getClock;

    internal TransitionBuilder(string name, CpnModel model, Func<CpnTime>? getClock = null)
    {
        _name = name;
        _model = model;
        _getClock = getClock ?? (() => CpnTime.Zero);
    }

    // ── Input arcs ────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds an input arc with inscription <c>count`var</c>: binds <paramref name="var"/> to one
    /// token colour of <paramref name="place"/> and demands <paramref name="count"/> copies of it.
    /// If the same variable occurs on several input arcs it is bound to the same value on all
    /// of them (unification).
    /// </summary>
    public TransitionBuilder Input<T>(Place<T> place, Var<T> var, int count = 1) where T : notnull, IEquatable<T>
    {
        _inputArcs.Add(new VarInputArc<T>(place, var, _getClock, count));
        return this;
    }

    /// <summary>
    /// Adds an input arc whose inscription is the multiset expression <paramref name="expr"/>.
    /// The expression may reference any variable of the transition, no matter which arc binds it;
    /// it is evaluated only under complete bindings. It cannot bind variables itself.
    /// </summary>
    public TransitionBuilder Input<T>(Place<T> place, Func<Multiset<T>> expr) where T : notnull, IEquatable<T>
    {
        _inputArcs.Add(new ExprInputArc<T>(place, expr, _getClock));
        return this;
    }

    /// <summary>Adds an input arc whose inscription is the single-token expression <paramref name="expr"/>.</summary>
    public TransitionBuilder Input<T>(Place<T> place, Func<T> expr) where T : notnull, IEquatable<T>
    {
        Input(place, () => Multiset.Of<T>(expr()));
        return this;
    }

    // ── Timed input arcs ──────────────────────────────────────────────────────

    /// <summary>
    /// Adds an input arc on a timed place that binds <paramref name="valueVar"/> to the
    /// <em>colour</em> of a ready <see cref="Timed{T}"/> token (time stamp ≤ global clock).
    /// When the transition occurs, the ready tokens with the smallest time stamps are removed.
    /// Only meaningful when the transition was created via <see cref="TimedCpnModel.AddTransition"/>.
    /// </summary>
    public TransitionBuilder TimedInput<T>(Place<Timed<T>> place, Var<T> valueVar, int count = 1)
        where T : notnull
    {
        _inputArcs.Add(new TimedVarInputArc<T>(place, valueVar, _getClock, count));
        return this;
    }

    // ── Timed output arcs ─────────────────────────────────────────────────────

    /// <summary>
    /// Adds an output arc <c>valueExpr @+ delayExpr</c> producing a <see cref="Timed{T}"/> token
    /// with time stamp <c>clock + transition delay + delayExpr()</c>.
    /// </summary>
    public TransitionBuilder TimedOutput<T>(Place<Timed<T>> place, Func<T> valueExpr, Func<int> delayExpr)
        where T : notnull
    {
        _outputArcs.Add(new TimedOutputArc<T>(place, valueExpr, delayExpr, () => _transitionDelay?.Invoke() ?? 0, _getClock));
        return this;
    }

    /// <summary>
    /// Adds an output arc <c>valueExpr @+ delay</c> with a constant arc delay.
    /// </summary>
    public TransitionBuilder TimedOutput<T>(Place<Timed<T>> place, Func<T> valueExpr, int delay)
        where T : notnull
        => TimedOutput(place, valueExpr, () => delay);

    /// <summary>
    /// Sets the time delay inscription of the transition (<c>@+delay</c> on the transition).
    /// It is added to the time stamp of every token produced by a timed output arc.
    /// </summary>
    public TransitionBuilder Delay(int delay) => Delay(() => delay);

    /// <summary>Sets the time delay inscription of the transition as an expression evaluated per occurrence.</summary>
    public TransitionBuilder Delay(Func<int> delayExpr)
    {
        _transitionDelay = delayExpr;
        return this;
    }

    // ── Free variables ────────────────────────────────────────────────────────

    /// <summary>
    /// Declares that <paramref name="var"/> belongs to <c>Var(t)</c> although no input arc binds it
    /// (it is used in an output-arc expression or the guard given as a plain <c>Func</c>, which
    /// cannot be inspected). The variable is bound by enumerating its <see cref="Var{T}.Domain"/>,
    /// giving one binding element per value, just like CPN Tools binds an output-arc variable of
    /// a small colour set. Not needed for expression-tree overloads, which discover the variable
    /// automatically; harmless for a variable that is bound by an input arc.
    /// </summary>
    public TransitionBuilder Free<T>(Var<T> var) where T : notnull
    {
        _referencedVars.Add(var);
        return this;
    }

    // ── Guard ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the guard. The guard is an expression over the values of the transition's
    /// variables and over constants of the net; it may not read a place, the model or any
    /// other reference that could carry state (<see cref="GuardRule"/>) — checked by
    /// <see cref="Build"/>. Variables it reads are added to <c>Var(t)</c>. The display label
    /// is derived from the expression body — <c>() =&gt; p.Val == a.Val.Owner</c> shows
    /// <c>[p == a.Owner]</c> — unless <paramref name="label"/> is given.
    /// </summary>
    /// <remarks>
    /// The guard is taken as an expression tree rather than a delegate so that it can be
    /// inspected; a condition that needs statements belongs in a static method called from
    /// the expression. <see cref="GuardScope.Strict"/> catches marking reads made inside
    /// such a method at runtime.
    /// </remarks>
    public TransitionBuilder Guard(Expression<Func<bool>> guard, string? label = null)
    {
        var (problem, variables) = GuardRule.Inspect(guard);
        _guardProblem = problem;
        _guard        = guard.Compile();
        _guardLabel   = label ?? $"[{FormatExpr(guard.Body)}]";
        _referencedVars.AddRange(variables);
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
        _referencedVars.Add(var);
        return this;
    }

    /// <summary>
    /// Adds an output arc whose inscription is automatically derived from the
    /// lambda expression body.  Write <c>() => x.Val * 2</c> — the arc shows
    /// <c>x * 2</c> (the <c>.Val</c> suffix is stripped for readability).
    /// Variables referenced by the expression are added to <c>Var(t)</c>.
    /// </summary>
    public TransitionBuilder Output<T>(Place<T> place, Expression<Func<T>> expr) where T : notnull, IEquatable<T>
    {
        _outputArcs.Add(new SingleOutputArc<T>(place, expr.Compile(), FormatExpr(expr.Body)));
        _referencedVars.AddRange(VarCollector.Collect(expr));
        return this;
    }

    /// <summary>
    /// Adds an output arc that produces the multiset returned by <paramref name="expr"/>,
    /// with the inscription automatically derived from the lambda expression body.
    /// Variables referenced by the expression are added to <c>Var(t)</c>.
    /// </summary>
    public TransitionBuilder Output<T>(Place<T> place, Expression<Func<Multiset<T>>> expr) where T : notnull, IEquatable<T>
    {
        _outputArcs.Add(new MultisetOutputArc<T>(place, expr.Compile(), FormatExpr(expr.Body)));
        _referencedVars.AddRange(VarCollector.Collect(expr));
        return this;
    }

    /// <summary>
    /// Adds an output arc with an explicit inscription label.
    /// Use when the auto-derived label would be unclear. Variables that occur only in
    /// such an expression must be declared with <see cref="Free{T}"/>.
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

    // ── Var(t) discovery in expression trees ──────────────────────────────────

    /// <summary>
    /// Finds every <see cref="Var{T}"/> instance referenced by an expression tree, without
    /// evaluating the expression itself. Captured locals appear as field accesses on a closure
    /// constant, model fields as member accesses on <c>this</c>; both are resolved here.
    /// </summary>
    private sealed class VarCollector : ExpressionVisitor
    {
        private readonly List<IVar> _vars = [];

        public static IReadOnlyList<IVar> Collect(Expression expression)
        {
            var c = new VarCollector();
            c.Visit(expression);
            return c._vars;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (typeof(IVar).IsAssignableFrom(node.Type))
            {
                if (TryEvaluate(node) is IVar v) Add(v);
                return node;
            }
            return base.VisitMember(node);
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            if (node.Value is IVar v) Add(v);
            return base.VisitConstant(node);
        }

        private void Add(IVar v)
        {
            if (!_vars.Any(x => ReferenceEquals(x, v))) _vars.Add(v);
        }

        private static object? TryEvaluate(Expression e)
        {
            try { return Expression.Lambda(e).Compile().DynamicInvoke(); }
            catch { return null; }
        }
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finalises the transition, computes <c>Var(t)</c>, validates that every variable can be
    /// bound (CPN Tools' syntax check), registers the transition with the model, and returns it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The guard reads state other than the transition's variables (<see cref="GuardRule"/>), or
    /// a variable occurs only in output arcs / the guard and its colour set cannot be enumerated
    /// (CPN Tools: "variable cannot be bound").
    /// </exception>
    public Transition Build()
    {
        if (_guardProblem is not null)
            throw new InvalidOperationException(
                $"Transition '{_name}': the guard is not an expression over the transition's variables; " +
                $"{_guardProblem}. {GuardRule.Requirement}");

        // Var(t): variables bound by input arcs first (in arc order), then the remaining
        // variables referenced by the guard, output arcs, or declared with Free().
        var variables = new List<IVar>();
        var boundByInputArcs = new HashSet<IVar>(ReferenceEqualityComparer.Instance);
        foreach (var arc in _inputArcs)
            foreach (var v in arc.BoundVariables)
                if (boundByInputArcs.Add(v)) variables.Add(v);

        var free = new List<IVar>();
        foreach (var v in _referencedVars)
        {
            if (boundByInputArcs.Contains(v) || free.Any(f => ReferenceEquals(f, v))) continue;
            if (v.DomainObjects is null)
                throw new InvalidOperationException(
                    $"Transition '{_name}': the variable '{v.Name}' occurs only in output arcs or the guard, " +
                    "and its colour set is not enumerable, so it cannot be bound (CPN Tools: \"variable cannot " +
                    "be bound\" — free variables must have a small colour set). Bind it on an input arc, or give " +
                    "the Var a Domain (new Var<T>(name, domain)).");
            free.Add(v);
            variables.Add(v);
        }

        var t = new Transition(_name, _inputArcs, _outputArcs, _guard, _guardLabel, variables, free);
        _model.RegisterTransition(t);
        return t;
    }
}
