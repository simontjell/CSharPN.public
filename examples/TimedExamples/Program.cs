using CSharPN.Core;
using TimedExamples;

// ═══════════════════════════════════════════════════════════════════════════════
//  Timed CPN Examples — Jensen Vol. 1 §4 / Vol. 2 §1
//  Demonstrates global-clock advancement and timestamp-based token ordering.
// ═══════════════════════════════════════════════════════════════════════════════

RunNetworkProtocolTimed();
RunManufacturingSystem();
RunPhoneSystem();

// ── Example 1: Simple Protocol with delays ────────────────────────────────────

static void RunNetworkProtocolTimed()
{
    Console.WriteLine(new string('═', 70));
    Console.WriteLine("  Timed Simple Protocol (Jensen Vol. 1 §4.1)");
    Console.WriteLine("  Channel delays: data=7  ack=5  RetransmitTimeout=15");
    Console.WriteLine(new string('─', 70));

    var model = new NetworkProtocolTimed();
    var sim   = new TimedCpnSimulator(model);
    var log   = new List<string>();

    sim.TransitionFired += (_, e) =>
        log.Add($"  t={model.GlobalClock.Value,5}  {e.Transition.Name,-22} [{e.Binding}]");

    var result = sim.Run(new TimedSimulationOptions
    {
        MaxSteps = 500,
        MaxTime  = new CpnTime(300),
        Random   = new Random(42),
        Logger   = msg => Console.WriteLine(msg)
    });

    Console.WriteLine(new string('─', 70));
    Console.WriteLine($"  Steps:    {result.Steps}");
    Console.WriteLine($"  FinalTime:{result.FinalTime}");
    Console.WriteLine($"  Delivered:{model.PacketsDelivered}/8  ({model.ReceivedItems})");
    Console.WriteLine($"  Complete: {model.IsComplete}");
    Console.WriteLine($"  Reason:   {result.TerminationReason}");
    Console.WriteLine();
}

// ── Example 2: Two-stage flow-shop ────────────────────────────────────────────

static void RunManufacturingSystem()
{
    Console.WriteLine(new string('═', 70));
    Console.WriteLine("  Timed Manufacturing System (Jensen Vol. 1 §4.3)");
    Console.WriteLine($"  Jobs={ManufacturingSystem.NumJobs}  " +
                      $"ArrivalInterval={ManufacturingSystem.ArrivalInterval}  " +
                      $"ProcA={ManufacturingSystem.ProcTimeA}  " +
                      $"ProcB={ManufacturingSystem.ProcTimeB}");
    Console.WriteLine($"  CapacityA={ManufacturingSystem.CapacityA}  " +
                      $"CapacityB={ManufacturingSystem.CapacityB}");
    Console.WriteLine(new string('─', 70));

    var model = new ManufacturingSystem();
    var sim   = new TimedCpnSimulator(model);

    var result = sim.Run(new TimedSimulationOptions
    {
        MaxSteps = 2000,
        MaxTime  = new CpnTime(500),
        Random   = new Random(7),
        Logger   = msg => Console.WriteLine(msg)
    });

    Console.WriteLine(new string('─', 70));
    Console.WriteLine($"  Steps:    {result.Steps}");
    Console.WriteLine($"  FinalTime:{result.FinalTime}");
    Console.WriteLine($"  {model.QueueStats()}");
    Console.WriteLine($"  AllDone:  {model.AllDone}");
    Console.WriteLine($"  Reason:   {result.TerminationReason}");
    Console.WriteLine();
}

// ── Example 3: Telephone exchange ─────────────────────────────────────────────

static void RunPhoneSystem()
{
    Console.WriteLine(new string('═', 70));
    Console.WriteLine("  Timed Phone System (Jensen Vol. 1 §4.2)");
    Console.WriteLine($"  Phones={PhoneSystem.NumPhones}  " +
                      $"Lines={PhoneSystem.LineCapacity}  " +
                      $"CallDuration={PhoneSystem.CallDuration}  " +
                      $"Requests={PhoneSystem.NumRequests}");
    Console.WriteLine(new string('─', 70));

    var model = new PhoneSystem();
    var sim   = new TimedCpnSimulator(model);

    // Snapshot max active calls during the run.
    int peakActive = 0;
    sim.TransitionFired += (_, _) =>
    {
        int active = model.ActiveCallCount;
        if (active > peakActive) peakActive = active;
    };

    var result = sim.Run(new TimedSimulationOptions
    {
        MaxSteps = 1000,
        MaxTime  = new CpnTime(300),
        Random   = new Random(13),
        Logger   = msg => Console.WriteLine(msg)
    });

    Console.WriteLine(new string('─', 70));
    Console.WriteLine($"  Steps:          {result.Steps}");
    Console.WriteLine($"  FinalTime:      {result.FinalTime}");
    Console.WriteLine($"  {model.Summary()}");
    Console.WriteLine($"  Peak active:    {peakActive}  (capacity={PhoneSystem.LineCapacity})");
    Console.WriteLine($"  Reason:         {result.TerminationReason}");

    // Verify line capacity was never exceeded.
    bool capacityRespected = peakActive <= PhoneSystem.LineCapacity;
    Console.WriteLine($"  Capacity OK:    {capacityRespected}");
    Console.WriteLine();
}
