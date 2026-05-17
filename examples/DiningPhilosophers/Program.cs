using CSharPN.Core;

// ── Entry point ───────────────────────────────────────────────────────────────

var model = new DiningPhilosophers();
var sim = new CpnSimulator(model);

Console.WriteLine("=== Dining Philosophers (3 philosophers) ===");
Console.WriteLine($"Initial state:\n  {model.GetState()}\n");

sim.TransitionFired += (_, e) =>
    Console.WriteLine($"  Step {e.StepNumber,3}: {e.Transition.Name,-15} [{e.Binding}]");

sim.DeadlockReached += (_, _) =>
    Console.WriteLine("\n  *** DEADLOCK ***");

var result = sim.Run(new SimulationOptions { MaxSteps = 30 });

Console.WriteLine($"\nFinal state:\n  {model.GetState()}");
Console.WriteLine($"\n{result.TerminationReason}");
Console.WriteLine($"Total steps fired: {result.Steps}");

// ── Model ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Classic Dining Philosophers in CPN style.
///
/// Colour sets:
///   Philosopher  – record type (C# record, no separate CPN type declaration)
///   int          – fork identifiers (1, 2, 3)
///
/// Places:
///   Thinking  – philosophers currently thinking
///   Hungry    – philosophers waiting for forks
///   Eating    – philosophers currently eating
///   Forks     – available fork tokens
///
/// Transitions:
///   GetHungry    – philosopher leaves Thinking, enters Hungry
///   StartEating  – hungry philosopher picks up both adjacent forks
///   StopEating   – eating philosopher puts down forks, returns to Thinking
/// </summary>
public class DiningPhilosophers : CpnModel
{
    // Colour set: just a C# record – no separate CPN type declaration needed
    public record Philosopher(int Id)
    {
        public override string ToString() => $"P{Id}";
    }

    // Places – type parameter IS the colour set
    public readonly Place<Philosopher> Thinking;
    public readonly Place<Philosopher> Hungry;
    public readonly Place<Philosopher> Eating;
    public readonly Place<int> Forks;   // token value = fork id (1, 2, 3)

    public DiningPhilosophers() : base("DiningPhilosophers")
    {
        Thinking = AddPlace("Thinking", Multiset.Of(
            new Philosopher(1),
            new Philosopher(2),
            new Philosopher(3)));

        Hungry = AddPlace<Philosopher>("Hungry");
        Eating = AddPlace<Philosopher>("Eating");

        // Fork i is shared between philosopher i (right) and philosopher i%3+1 (left)
        Forks = AddPlace("Forks", Multiset.Of(1, 2, 3));

        // ── Binding variables ─────────────────────────────────────────────────
        var p  = new Var<Philosopher>("p");
        var f1 = new Var<int>("f1");
        var f2 = new Var<int>("f2");

        // ── Transitions ───────────────────────────────────────────────────────

        AddTransition("GetHungry")
            .Input(Thinking, p)
            .Output(Hungry, () => Multiset.Of(p.Val))
            .Build();

        AddTransition("StartEating")
            .Input(Hungry, p)
            .Input(Forks, f1)
            .Input(Forks, f2)
            // Philosopher p.Id uses fork p.Id (right) and fork p.Id%3+1 (left)
            .Guard(() =>
                f1.Val != f2.Val &&
                f1.Val == p.Val.Id &&
                f2.Val == (p.Val.Id % 3) + 1)
            .Output(Eating, () => Multiset.Of(p.Val))
            .Build();

        AddTransition("StopEating")
            .Input(Eating, p)
            .Output(Thinking, () => Multiset.Of(p.Val))
            // Return both forks – expression multiset output
            .Output(Forks, () => Multiset.Of(p.Val.Id, (p.Val.Id % 3) + 1))
            .Build();
    }
}
