namespace CSharPN.Core;

// ── CpnTime ───────────────────────────────────────────────────────────────────

/// <summary>
/// Integer-based model-time point for timed CPN (Jensen Vol. 2 §1).
/// Arithmetic is in whole time units; the global clock never goes backwards.
/// </summary>
public readonly struct CpnTime : IComparable<CpnTime>, IEquatable<CpnTime>
{
    /// <summary>Time zero – the start of a fresh simulation.</summary>
    public static readonly CpnTime Zero = new(0);

    public int Value { get; }

    public CpnTime(int value) => Value = value;

    /// <summary>Returns a new time point <paramref name="delay"/> units in the future.</summary>
    public CpnTime Advance(int delay) => new(Value + delay);

    public static CpnTime operator +(CpnTime t, int delay) => new(t.Value + delay);
    public static int      operator -(CpnTime a, CpnTime b) => a.Value - b.Value;
    public static bool     operator ==(CpnTime a, CpnTime b) => a.Value == b.Value;
    public static bool     operator !=(CpnTime a, CpnTime b) => a.Value != b.Value;
    public static bool     operator  <(CpnTime a, CpnTime b) => a.Value  < b.Value;
    public static bool     operator  >(CpnTime a, CpnTime b) => a.Value  > b.Value;
    public static bool     operator <=(CpnTime a, CpnTime b) => a.Value <= b.Value;
    public static bool     operator >=(CpnTime a, CpnTime b) => a.Value >= b.Value;

    public int  CompareTo(CpnTime other) => Value.CompareTo(other.Value);
    public bool Equals(CpnTime other)    => Value == other.Value;
    public override bool Equals(object? obj) => obj is CpnTime t && Equals(t);
    public override int  GetHashCode()       => Value.GetHashCode();
    public override string ToString()        => $"@{Value}";
}

// ── Timed<T> ──────────────────────────────────────────────────────────────────

/// <summary>
/// A CPN token of colour <typeparamref name="T"/> annotated with a timestamp
/// <see cref="ReadyAt"/>. The token can only be consumed once the global clock
/// has reached or passed that timestamp (Jensen's <c>c@t</c> notation).
/// </summary>
public readonly record struct Timed<T>(T Value, CpnTime ReadyAt) where T : notnull
{
    /// <summary>Creates a token that becomes available at absolute time <paramref name="time"/>.</summary>
    public static Timed<T> At(T value, int time) => new(value, new CpnTime(time));

    /// <summary>Creates a token available immediately at the current clock.</summary>
    public static Timed<T> Now(T value, CpnTime clock) => new(value, clock);

    /// <summary>Creates a token available <paramref name="delay"/> units after the current clock.</summary>
    public static Timed<T> After(T value, CpnTime clock, int delay) => new(value, clock + delay);

    public override string ToString() => $"{Value}{ReadyAt}";
}

// ── TimedVarInputArc<T> ───────────────────────────────────────────────────────

/// <summary>
/// Input arc that binds a <see cref="Var{T}"/> to the <em>value</em> of a
/// <see cref="Timed{T}"/> token whose <c>ReadyAt</c> ≤ current clock.
/// An internal companion variable tracks the full token for correct token consumption.
/// </summary>
internal sealed class TimedVarInputArc<T> : IInputArc where T : notnull
{
    private readonly Place<Timed<T>> _place;
    private readonly Var<T>          _valueVar;   // user-facing: bound to token value
    private readonly Var<Timed<T>>   _timedVar;   // internal: bound to full Timed<T> (empty name)
    private readonly int             _count;
    private readonly Func<CpnTime>   _getClock;

    public TimedVarInputArc(Place<Timed<T>> place, Var<T> valueVar, Func<CpnTime> getClock, int count = 1)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        _place    = place;
        _valueVar = valueVar;
        _timedVar = new Var<Timed<T>>(""); // empty name → hidden from binding display
        _getClock = getClock;
        _count    = count;
    }

    public IPlaceInternal Place  => _place;
    public string          Inscription => _count == 1 ? _valueVar.Name : $"{_count}`{_valueVar.Name}";

    public IEnumerable<(object, Action, Action)> EnumerateCandidates(object available)
    {
        var marking = (Multiset<Timed<T>>)available;
        var clock   = _getClock();

        // If the value variable is already bound by an earlier arc, unify: only
        // tokens carrying that value are candidates, and the value var's binding
        // is owned by the earlier arc (we only bind/unbind the internal timed var).
        var valueAlreadyBound = _valueVar.IsBound;

        foreach (var token in marking.DistinctItems())
        {
            if (token.ReadyAt > clock || marking.Count(token) < _count)
                continue;
            if (valueAlreadyBound && !EqualityComparer<T>.Default.Equals(token.Value, _valueVar.Val))
                continue;

            var t        = token; // capture for closure
            var consumed = Multiset.Repeat(t, _count);
            yield return (
                marking - consumed,
                valueAlreadyBound
                    ? () => _timedVar.Bind(t)
                    : () => { _timedVar.Bind(t); _valueVar.Bind(t.Value); },
                valueAlreadyBound
                    ? () => _timedVar.Unbind()
                    : () => { _timedVar.Unbind(); _valueVar.Unbind(); }
            );
        }
    }

    public void ConsumeFromPlace()
    {
        // Use the internal timed var (reliably set via ApplyBindings) to identify
        // the exact Timed<T> token to remove.
        _place.Marking = _place.Marking - Multiset.Repeat(_timedVar.Val, _count);
    }

    public IEnumerable<(IVar, object)> GetCurrentVarBindings()
    {
        // Yield internal var first so ApplyBindings can restore it before ConsumeFromPlace.
        if (_timedVar.IsBound) yield return (_timedVar, _timedVar.Val);
        if (_valueVar.IsBound) yield return (_valueVar, _valueVar.Val!);
    }

    // Only the user-facing value variable is named; the internal timed var carries
    // an empty name and is excluded from name-uniqueness checks.
    public IEnumerable<IVar> Variables => [_valueVar];
}

// ── TimedOutputArc<T> ────────────────────────────────────────────────────────

/// <summary>
/// Output arc that produces a <see cref="Timed{T}"/> token whose timestamp is
/// <c>current clock + delayExpr()</c>.  Corresponds to Jensen's <c>e @+ d</c> notation.
/// </summary>
internal sealed class TimedOutputArc<T> : IOutputArc where T : notnull
{
    private readonly Place<Timed<T>> _place;
    private readonly Func<T>         _valueExpr;
    private readonly Func<int>        _delayExpr;
    private readonly Func<CpnTime>   _getClock;

    public TimedOutputArc(Place<Timed<T>> place, Func<T> valueExpr, Func<int> delayExpr, Func<CpnTime> getClock)
    {
        _place     = place;
        _valueExpr = valueExpr;
        _delayExpr = delayExpr;
        _getClock  = getClock;
    }

    public IPlaceInternal Place  => _place;
    public string          Inscription => "";   // delay expression can't be introspected

    public void ProduceToPlace()
    {
        var token = Timed<T>.After(_valueExpr(), _getClock(), _delayExpr());
        _place.Marking = _place.Marking.Add(token, 1);
    }
}

// ── TimedCpnModel ─────────────────────────────────────────────────────────────

/// <summary>
/// Base class for timed CPN models. Provides a global clock, helper methods for
/// creating timed places, and a clock-aware <see cref="AddTransition"/> override
/// so all timed arcs share the same clock reference automatically.
/// </summary>
/// <remarks>
/// Simulate with <see cref="TimedCpnSimulator"/> which advances the clock when
/// no transitions are enabled but future-timestamped tokens exist.
/// </remarks>
public abstract class TimedCpnModel : CpnModel
{
    private CpnTime _globalClock = CpnTime.Zero;
    private readonly List<Func<CpnTime, CpnTime?>> _timedPlaceInspectors = [];

    protected TimedCpnModel(string? name = null) : base(name) { }

    /// <summary>The current global simulation clock.</summary>
    public CpnTime GlobalClock => _globalClock;

    /// <summary>Advances the global clock. Called exclusively by <see cref="TimedCpnSimulator"/>.</summary>
    internal void SetClock(CpnTime time) => _globalClock = time;

    /// <summary>Registers a timed-place inspector from a sub-page.</summary>
    internal void RegisterTimedPlaceInspector(Func<CpnTime, CpnTime?> inspector)
        => _timedPlaceInspectors.Add(inspector);

    /// <summary>
    /// Creates a place that holds <see cref="Timed{T}"/> tokens and registers it
    /// for clock-advancement queries.
    /// </summary>
    protected Place<Timed<T>> AddTimedPlace<T>(string name, Multiset<Timed<T>>? initial = null)
        where T : notnull
    {
        var place = base.AddPlace<Timed<T>>(name, initial);

        // Register an inspector closure so the simulator can find the next ready time.
        _timedPlaceInspectors.Add(afterClock =>
        {
            CpnTime? min = null;
            foreach (var token in place.Marking.DistinctItems())
            {
                if (token.ReadyAt > afterClock &&
                    (min == null || token.ReadyAt < min.Value))
                    min = token.ReadyAt;
            }
            return min;
        });

        return place;
    }

    /// <summary>
    /// Returns the earliest future timestamp across all timed places —
    /// i.e. the next moment at which at least one token will become ready.
    /// Returns <see langword="null"/> if no future tokens exist (true deadlock).
    /// </summary>
    public CpnTime? GetNextReadyTime(CpnTime afterClock)
    {
        CpnTime? min = null;
        foreach (var inspect in _timedPlaceInspectors)
        {
            var next = inspect(afterClock);
            if (next.HasValue && (min == null || next.Value < min.Value))
                min = next.Value;
        }
        return min;
    }

    /// <summary>
    /// Starts building a transition. The builder automatically inherits this
    /// model's global clock, enabling <c>TimedInput</c> / <c>TimedOutput</c> arcs.
    /// </summary>
    protected new TransitionBuilder AddTransition(string name)
        => new(name, this, () => _globalClock);
}
