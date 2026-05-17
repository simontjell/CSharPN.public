namespace CSharPN.Core;

// ── Event args ────────────────────────────────────────────────────────────────

public sealed class TransitionFiredEventArgs(
    Transition transition,
    BindingSnapshot binding,
    int stepNumber) : EventArgs
{
    public Transition Transition { get; } = transition;
    public BindingSnapshot Binding { get; } = binding;
    public int StepNumber { get; } = stepNumber;
}

// ── Options and result ────────────────────────────────────────────────────────

public sealed record SimulationOptions
{
    /// <summary>Maximum number of transition firings before stopping.</summary>
    public int MaxSteps { get; init; } = 10_000;

    /// <summary>Source of randomness. Null = new <see cref="Random"/>() each run.</summary>
    public Random? Random { get; init; }

    /// <summary>When true (default), the simulator stops as soon as no transition is enabled.</summary>
    public bool StopOnDeadlock { get; init; } = true;

    /// <summary>Optional logger called with a description string at each step.</summary>
    public Action<string>? Logger { get; init; }
}

public sealed record SimulationResult(int Steps, bool IsDeadlock, string TerminationReason);

// ── Simulator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Drives the execution of a <see cref="CpnModel"/> by selecting and firing
/// enabled (transition, binding) pairs.
/// </summary>
public sealed class CpnSimulator
{
    private Random _random = new();
    private int _stepCount;

    public CpnModel Model { get; }

    /// <summary>Raised after each transition firing.</summary>
    public event EventHandler<TransitionFiredEventArgs>? TransitionFired;

    /// <summary>Raised when no transitions are enabled (deadlock state reached).</summary>
    public event EventHandler? DeadlockReached;

    public CpnSimulator(CpnModel model)
    {
        Model = model;
    }

    // ── Inspection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all currently enabled (transition, binding) pairs.
    /// </summary>
    public IReadOnlyList<(Transition Transition, BindingSnapshot Binding)> GetEnabled()
    {
        var result = new List<(Transition, BindingSnapshot)>();
        foreach (var t in Model.Transitions)
            foreach (var b in t.GetEnabledBindings())
                result.Add((t, b));
        return result;
    }

    // ── Single step ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fires a uniformly random enabled (transition, binding) pair.
    /// Returns <c>false</c> if no transition is enabled (deadlock).
    /// </summary>
    public bool Step()
    {
        var enabled = GetEnabled();
        if (enabled.Count == 0)
        {
            DeadlockReached?.Invoke(this, EventArgs.Empty);
            return false;
        }

        var (t, b) = enabled[_random.Next(enabled.Count)];
        FireInternal(t, b);
        return true;
    }

    /// <summary>
    /// Fires a specific (transition, binding) pair. Use this for deterministic
    /// step-by-step control, e.g. in tests or interactive simulators.
    /// </summary>
    public void Step(Transition transition, BindingSnapshot binding)
        => FireInternal(transition, binding);

    // ── Full run ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the simulation until deadlock or <see cref="SimulationOptions.MaxSteps"/> is reached.
    /// </summary>
    public SimulationResult Run(SimulationOptions? options = null)
    {
        options ??= new SimulationOptions();
        _random = options.Random ?? new Random();

        for (int i = 0; i < options.MaxSteps; i++)
        {
            var enabled = GetEnabled();

            if (enabled.Count == 0)
            {
                const string reason = "Deadlock – no enabled transitions.";
                options.Logger?.Invoke(reason);
                DeadlockReached?.Invoke(this, EventArgs.Empty);
                return new SimulationResult(_stepCount, IsDeadlock: true, reason);
            }

            var (t, b) = enabled[_random.Next(enabled.Count)];
            options.Logger?.Invoke($"Step {_stepCount + 1,4}: {t.Name} [{b}]");
            FireInternal(t, b);
        }

        var maxReason = $"Maximum steps ({options.MaxSteps}) reached.";
        options.Logger?.Invoke(maxReason);
        return new SimulationResult(_stepCount, IsDeadlock: false, maxReason);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private void FireInternal(Transition t, BindingSnapshot b)
    {
        t.Fire(b);
        _stepCount++;
        TransitionFired?.Invoke(this, new TransitionFiredEventArgs(t, b, _stepCount));
    }
}
