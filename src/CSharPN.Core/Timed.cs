namespace CSharPN.Core;

// ── CpnTime ───────────────────────────────────────────────────────────────────

/// <summary>
/// Integer-based model-time point for timed CPN (Jensen &amp; Kristensen 2009, Chapter 10).
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
/// A CPN token of colour <typeparamref name="T"/> annotated with a time stamp
/// <see cref="ReadyAt"/> (Jensen's <c>c@t</c> notation). The token is <em>ready</em>,
/// i.e. can be consumed, once the global clock has reached or passed that time stamp.
/// </summary>
public readonly record struct Timed<T>(T Value, CpnTime ReadyAt) : ITimedToken where T : notnull
{
    /// <summary>Creates a token that becomes available at absolute time <paramref name="time"/>.</summary>
    public static Timed<T> At(T value, int time) => new(value, new CpnTime(time));

    /// <summary>Creates a token available immediately at the current clock.</summary>
    public static Timed<T> Now(T value, CpnTime clock) => new(value, clock);

    /// <summary>Creates a token available <paramref name="delay"/> units after the current clock.</summary>
    public static Timed<T> After(T value, CpnTime clock, int delay) => new(value, clock + delay);

    public override string ToString() => $"{Value}{ReadyAt}";
}

// ── Timed multiset helpers ────────────────────────────────────────────────────

/// <summary>
/// Operations on timed multisets (Jensen &amp; Kristensen 2009, Section 10.3).
/// A timed multiset is represented as a <see cref="Multiset{T}"/> of <see cref="Timed{T}"/>.
/// </summary>
internal static class TimedMultiset
{
    /// <summary>
    /// The distinct colours that have at least <paramref name="atLeast"/> ready tokens
    /// (time stamp ≤ <paramref name="clock"/>) in <paramref name="marking"/>, in first-seen order.
    /// This is the untimed projection of the ready part of the marking, which is what
    /// a binding element is enabled against ("colour enabled and ready").
    /// </summary>
    public static IEnumerable<T> ReadyColours<T>(Multiset<Timed<T>> marking, CpnTime clock, int atLeast)
        where T : notnull
    {
        var counts = new Dictionary<T, int>();
        var order  = new List<T>();
        foreach (var token in marking.DistinctItems())
        {
            if (token.ReadyAt > clock) continue;
            if (!counts.ContainsKey(token.Value)) order.Add(token.Value);
            counts[token.Value] = (counts.TryGetValue(token.Value, out var n) ? n : 0) + marking.Count(token);
        }
        foreach (var colour in order)
            if (counts[colour] >= atLeast) yield return colour;
    }

    /// <summary>
    /// Removes <paramref name="count"/> ready tokens of colour <paramref name="colour"/> from
    /// <paramref name="marking"/>, taking the tokens with the <b>smallest time stamps first</b>
    /// (the policy of the CPN Tools simulator). Returns <see langword="null"/> when fewer than
    /// <paramref name="count"/> ready tokens of that colour exist.
    /// </summary>
    public static Multiset<Timed<T>>? RemoveOldestReady<T>(
        Multiset<Timed<T>> marking, T colour, int count, CpnTime clock)
        where T : notnull
    {
        var eq = EqualityComparer<T>.Default;
        var candidates = marking.DistinctItems()
            .Where(tok => tok.ReadyAt <= clock && eq.Equals(tok.Value, colour))
            .OrderBy(tok => tok.ReadyAt.Value);

        var remaining = marking;
        var need      = count;
        foreach (var tok in candidates)
        {
            var take = Math.Min(need, marking.Count(tok));
            remaining = remaining.Remove(tok, take);
            need -= take;
            if (need == 0) return remaining;
        }
        return null;
    }
}

// ── TimedVarInputArc<T> ───────────────────────────────────────────────────────

/// <summary>
/// Pattern input arc on a timed place that binds a <see cref="Var{T}"/> to the
/// <em>colour</em> of ready tokens. Binding elements are distinguished by colour only,
/// never by time stamp (a binding is a function on <c>Var(t)</c>, Definition 4.3 (4));
/// when the arc consumes, the ready tokens with the smallest time stamps are removed.
/// </summary>
internal sealed class TimedVarInputArc<T> : IInputArc where T : notnull
{
    private readonly Place<Timed<T>> _place;
    private readonly Var<T>          _valueVar;
    private readonly int             _count;
    private readonly Func<CpnTime>   _getClock;

    public TimedVarInputArc(Place<Timed<T>> place, Var<T> valueVar, Func<CpnTime> getClock, int count = 1)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        _place    = place;
        _valueVar = valueVar;
        _getClock = getClock;
        _count    = count;
    }

    public IPlaceInternal Place => _place;
    public string Inscription   => _count == 1 ? _valueVar.Name : $"{_count}`{_valueVar.Name}";
    public IReadOnlyList<IVar> BoundVariables => [_valueVar];

    public IEnumerable<(object, Action, Action)> EnumerateCandidates(object available)
    {
        if (_valueVar.IsBound)
        {
            // Unification with an earlier arc: only that colour, ownership stays there.
            var remaining = TryConsume(available);
            if (remaining is not null) yield return (remaining, () => { }, () => { });
            yield break;
        }

        var marking = (Multiset<Timed<T>>)available;
        var clock   = _getClock();
        foreach (var colour in TimedMultiset.ReadyColours(marking, clock, _count))
        {
            var c = colour; // capture
            var remaining = TimedMultiset.RemoveOldestReady(marking, c, _count, clock)!;
            yield return (remaining, () => _valueVar.Bind(c), () => _valueVar.Unbind());
        }
    }

    public object? TryConsume(object available)
        => TimedMultiset.RemoveOldestReady((Multiset<Timed<T>>)available, _valueVar.Val, _count, _getClock());
}

// ── TimedOutputArc<T> ────────────────────────────────────────────────────────

/// <summary>
/// Output arc producing a <see cref="Timed{T}"/> token. Its time stamp is
/// <c>clock + transition delay + arc delay</c> (Jensen &amp; Kristensen 2009, Section 10.1:
/// the time stamp of a produced token is the global clock plus the time delay inscription
/// of the transition plus the time delay inscription of the output arc, <c>e @+ d</c>).
/// </summary>
internal sealed class TimedOutputArc<T> : IOutputArc where T : notnull
{
    private readonly Place<Timed<T>> _place;
    private readonly Func<T>         _valueExpr;
    private readonly Func<int>       _arcDelay;
    private readonly Func<int>       _transitionDelay;
    private readonly Func<CpnTime>   _getClock;

    public TimedOutputArc(
        Place<Timed<T>> place, Func<T> valueExpr, Func<int> arcDelay,
        Func<int> transitionDelay, Func<CpnTime> getClock)
    {
        _place           = place;
        _valueExpr       = valueExpr;
        _arcDelay        = arcDelay;
        _transitionDelay = transitionDelay;
        _getClock        = getClock;
    }

    public IPlaceInternal Place => _place;
    public string Inscription   => "";   // delay expression can't be introspected

    public object Produce()
    {
        var delay = _transitionDelay() + _arcDelay();
        if (delay < 0)
            throw new InvalidOperationException(
                $"Negative time delay ({delay}) on output arc to '{_place.Name}': time stamps cannot lie in the past.");
        return Multiset.Of(Timed<T>.After(_valueExpr(), _getClock(), delay));
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
