namespace CSharPN.Core;

// ── Options and result ────────────────────────────────────────────────────────

/// <summary>Options for a <see cref="TimedCpnSimulator"/> run.</summary>
public sealed record TimedSimulationOptions
{
    /// <summary>Maximum number of transition firings (default 10 000).</summary>
    public int MaxSteps { get; init; } = 10_000;

    /// <summary>Stop when the global clock reaches or exceeds this value (null = unlimited).</summary>
    public CpnTime? MaxTime { get; init; }

    /// <summary>Source of randomness. Null = new <see cref="Random"/>() per run.</summary>
    public Random? Random { get; init; }

    /// <summary>Optional logger called at each step and on every clock advancement.</summary>
    public Action<string>? Logger { get; init; }
}

/// <summary>Result of a <see cref="TimedCpnSimulator"/> run.</summary>
public sealed record TimedSimulationResult(
    int Steps,
    bool IsDeadlock,
    CpnTime FinalTime,
    string TerminationReason);

// ── TimedCpnSimulator ─────────────────────────────────────────────────────────

/// <summary>
/// Simulator for <see cref="TimedCpnModel"/> instances. Extends the behaviour of
/// <see cref="CpnSimulator"/> with automatic global-clock advancement:
/// when no transition is immediately enabled, the clock jumps to the earliest
/// future token timestamp and the enabled check is retried.
/// The simulation terminates when either MaxSteps or MaxTime is reached,
/// or when no future tokens exist (true deadlock in time).
/// </summary>
public sealed class TimedCpnSimulator
{
    private readonly TimedCpnModel _model;
    private readonly CpnSimulator  _inner;

    public TimedCpnSimulator(TimedCpnModel model)
    {
        _model = model;
        _inner = new CpnSimulator(model);
    }

    /// <summary>The current global simulation clock.</summary>
    public CpnTime GlobalClock => _model.GlobalClock;

    /// <summary>Raised after each transition firing (proxied from inner simulator).</summary>
    public event EventHandler<TransitionFiredEventArgs>? TransitionFired
    {
        add    => _inner.TransitionFired += value;
        remove => _inner.TransitionFired -= value;
    }

    /// <summary>
    /// Returns all currently enabled (transition, binding) pairs at the current clock.
    /// </summary>
    public IReadOnlyList<(Transition Transition, BindingSnapshot Binding)> GetEnabled()
        => _inner.GetEnabled();

    /// <summary>
    /// Advances the clock until at least one transition is enabled or no
    /// future tokens remain.  Returns <c>true</c> if the clock was advanced.
    /// </summary>
    public bool AdvanceClock()
    {
        bool advanced = false;
        while (_inner.GetEnabled().Count == 0)
        {
            var next = _model.GetNextReadyTime(_model.GlobalClock);
            if (next == null) break;
            _model.SetClock(next.Value);
            advanced = true;
        }
        return advanced;
    }

    /// <summary>
    /// Fires a random enabled transition, advancing the clock first if no transition is
    /// currently enabled but future-timestamped tokens exist.
    /// Returns <c>false</c> only when there is a true deadlock (no tokens anywhere).
    /// </summary>
    public bool Step()
    {
        while (true)
        {
            var enabled = _inner.GetEnabled();
            if (enabled.Count > 0)
            {
                var (t, b) = enabled[Random.Shared.Next(enabled.Count)];
                _inner.Step(t, b);
                return true;
            }
            var next = _model.GetNextReadyTime(_model.GlobalClock);
            if (next == null) return false;
            _model.SetClock(next.Value);
        }
    }

    /// <summary>
    /// Fires the given (transition, binding) pair directly.
    /// Call <see cref="AdvanceClock"/> first if the binding requires a future clock.
    /// </summary>
    public void Step(Transition t, BindingSnapshot b) => _inner.Step(t, b);

    /// <summary>Resets the global clock to time zero.</summary>
    public void ResetClock() => _model.SetClock(CpnTime.Zero);

    /// <summary>
    /// Runs the simulation until <see cref="TimedSimulationOptions.MaxSteps"/>,
    /// <see cref="TimedSimulationOptions.MaxTime"/>, or true deadlock.
    /// </summary>
    public TimedSimulationResult Run(TimedSimulationOptions? options = null)
    {
        options ??= new TimedSimulationOptions();
        var rng   = options.Random ?? new Random();
        var steps = 0;

        while (steps < options.MaxSteps)
        {
            // Check time limit
            if (options.MaxTime.HasValue && _model.GlobalClock >= options.MaxTime.Value)
                return new TimedSimulationResult(steps, false, _model.GlobalClock,
                    $"MaxTime ({options.MaxTime.Value}) reached.");

            var enabled = _inner.GetEnabled();

            if (enabled.Count > 0)
            {
                var (t, b) = enabled[rng.Next(enabled.Count)];
                options.Logger?.Invoke(
                    $"  t={_model.GlobalClock.Value,5} step {steps + 1,4}: {t.Name} [{b}]");
                _inner.Step(t, b);
                steps++;
                continue;
            }

            // No enabled transitions — try to advance the clock.
            var nextTime = _model.GetNextReadyTime(_model.GlobalClock);
            if (nextTime == null)
                return new TimedSimulationResult(steps, true, _model.GlobalClock,
                    "Deadlock – no future tokens.");

            options.Logger?.Invoke(
                $"  [Clock: {_model.GlobalClock} → {nextTime.Value}]");
            _model.SetClock(nextTime.Value);
        }

        return new TimedSimulationResult(steps, false, _model.GlobalClock,
            $"MaxSteps ({options.MaxSteps}) reached.");
    }
}
