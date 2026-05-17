using CSharPN.Core;
using HierarchicalExamples;

// ═══════════════════════════════════════════════════════════════════════════════
//  Hierarchical CPN Examples — Jensen Vol. 2 §2
//  Demonstrates substitution transitions, port places and page composition.
// ═══════════════════════════════════════════════════════════════════════════════

RunHierarchicalProtocol();
RunHierarchicalManufacturing();

// ── Example 1: Hierarchical Simple Protocol ───────────────────────────────────

static void RunHierarchicalProtocol()
{
    Console.WriteLine(new string('═', 70));
    Console.WriteLine("  Hierarchical Simple Protocol (Jensen Vol. 2 §2.1)");
    Console.WriteLine(new string('─', 70));

    var (model, received, nextToSend) = HierarchicalProtocol.Build();

    // Print page structure.
    Console.WriteLine("  Page structure:");
    foreach (var (sub, page) in model.SubPageLinks)
        Console.WriteLine($"    [{page}]  (substitution: {sub})");
    Console.WriteLine($"  Total places:      {model.Places.Count}");
    Console.WriteLine($"  Total transitions: {model.Transitions.Count}");
    Console.WriteLine(new string('─', 70));

    // Simulate.
    var sim = new CpnSimulator(model);
    var result = sim.Run(new SimulationOptions
    {
        MaxSteps = 120,
        Random   = new Random(17),
        Logger   = msg => Console.WriteLine($"  {msg}")
    });

    Console.WriteLine(new string('─', 70));
    Console.WriteLine($"  Steps:     {result.Steps}");
    Console.WriteLine($"  Delivered: {received.Marking.TotalCount}/8  " +
                      $"({string.Join("+", received.Marking.DistinctItems().Order())})");
    Console.WriteLine($"  NextToSend:{nextToSend.Marking}");
    Console.WriteLine($"  Complete:  {nextToSend.Marking.Count(8) > 0}");
    Console.WriteLine($"  Reason:    {result.TerminationReason}");
    Console.WriteLine();
}

// ── Example 2: Hierarchical Two-Department Manufacturing ──────────────────────

static void RunHierarchicalManufacturing()
{
    Console.WriteLine(new string('═', 70));
    Console.WriteLine("  Hierarchical Manufacturing (Jensen Vol. 2 §2 style)");
    Console.WriteLine($"  Jobs={HierarchicalManufacturing.NumJobs}  " +
                      $"WorkersA={HierarchicalManufacturing.WorkersA}  " +
                      $"WorkersB={HierarchicalManufacturing.WorkersB}");
    Console.WriteLine(new string('─', 70));

    var (model, jobsPlace, donePlace) = HierarchicalManufacturing.Build();

    Console.WriteLine("  Page structure:");
    foreach (var (sub, page) in model.SubPageLinks)
        Console.WriteLine($"    [{page}]  (substitution: {sub})");
    Console.WriteLine($"  Total places:      {model.Places.Count}");
    Console.WriteLine($"  Total transitions: {model.Transitions.Count}");
    Console.WriteLine(new string('─', 70));

    var sim = new CpnSimulator(model);
    int step = 0;
    sim.TransitionFired += (_, e) =>
    {
        step++;
        Console.WriteLine($"  step {step,3}: {e.Transition.Name,-22} [{e.Binding}]");
    };

    var result = sim.Run(new SimulationOptions
    {
        MaxSteps      = 200,
        StopOnDeadlock = true,
        Random        = new Random(99)
    });

    Console.WriteLine(new string('─', 70));
    Console.WriteLine($"  Steps:   {result.Steps}");
    Console.WriteLine($"  Done:    {donePlace.Marking.TotalCount}/{HierarchicalManufacturing.NumJobs}  " +
                      $"(jobs: {donePlace.Marking})");
    Console.WriteLine($"  Reason:  {result.TerminationReason}");

    // Verify invariant: jobs in + done = total.
    int remaining = jobsPlace.Marking.TotalCount + donePlace.Marking.TotalCount;
    Console.WriteLine($"  Invariant (Jobs+Done=={HierarchicalManufacturing.NumJobs}): {remaining == HierarchicalManufacturing.NumJobs || result.IsDeadlock}");
    Console.WriteLine();
}
