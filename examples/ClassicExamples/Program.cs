using CSharPN.Core;

// ── Helper ────────────────────────────────────────────────────────────────────

static void PrintHeader(string title)
{
    var line = new string('=', title.Length + 6);
    Console.WriteLine();
    Console.WriteLine(line);
    Console.WriteLine($"=  {title}  =");
    Console.WriteLine(line);
}

// ═══════════════════════════════════════════════════════════════════════════════
// 1. Alternating Bit Protocol
// ═══════════════════════════════════════════════════════════════════════════════

PrintHeader("Alternating Bit Protocol (Jensen Vol. 1, Ch. 2)");
Console.WriteLine("Models a simple data-link protocol with alternating sequence bits.");
Console.WriteLine("Sender transmits 8 packets (a..h); both channels may lose messages.");

var abp   = new AlternatingBitProtocol();
var abpSim = new CpnSimulator(abp);

Console.WriteLine($"\nInitial state:");
Console.WriteLine($"  NextToSend : {abp.NextToSend.Marking}");
Console.WriteLine($"  NextRec    : {abp.NextRec.Marking}");
Console.WriteLine($"  A (data)   : {abp.A.Marking}");
Console.WriteLine($"  B (acks)   : {abp.B.Marking}");
Console.WriteLine($"  Received   : {abp.Received.Marking}");

Console.WriteLine();
int abpStep = 0;
abpSim.TransitionFired += (_, e) =>
{
    abpStep++;
    Console.WriteLine($"  Step {e.StepNumber,3}: {e.Transition.Name,-22} [{e.Binding}]");
};

var abpResult = abpSim.Run(new SimulationOptions
{
    MaxSteps = 150,
    Random = new Random(17),
    StopOnDeadlock = true
});

Console.WriteLine($"\nFinal state:");
Console.WriteLine($"  NextToSend : {abp.NextToSend.Marking}");
Console.WriteLine($"  NextRec    : {abp.NextRec.Marking}");
Console.WriteLine($"  A (data)   : {abp.A.Marking}");
Console.WriteLine($"  B (acks)   : {abp.B.Marking}");
Console.WriteLine($"  Received   : {abp.Received.Marking}");

int receivedCount = abp.Received.Marking.TotalCount;
Console.WriteLine($"\nPackets delivered : {receivedCount} / {AlternatingBitProtocol.TotalPackets}");
Console.WriteLine($"Protocol complete : {abp.IsComplete}");
Console.WriteLine($"Steps fired       : {abpResult.Steps}");
Console.WriteLine($"Termination       : {abpResult.TerminationReason}");

// ═══════════════════════════════════════════════════════════════════════════════
// 2. Resource Allocation
// ═══════════════════════════════════════════════════════════════════════════════

PrintHeader("Resource Allocation (Jensen Vol. 1, Ch. 1)");
Console.WriteLine("Three processes compete for two resource types (R1 and R2).");
Console.WriteLine("p1 needs R1 only, p2 needs R2 only, p3 needs both R1 and R2.");
Console.WriteLine("Cyclic model: processes return to Idle after completion.");

var ra    = new ResourceAllocation();
var raSim = new CpnSimulator(ra);

Console.WriteLine($"\nInitial state:");
Console.WriteLine($"  Idle    : {ra.Idle.Marking}");
Console.WriteLine($"  Running : {ra.Running.Marking}");
Console.WriteLine($"  R1      : {ra.R1.Marking}");
Console.WriteLine($"  R2      : {ra.R2.Marking}");

Console.WriteLine();
raSim.TransitionFired += (_, e) =>
    Console.WriteLine($"  Step {e.StepNumber,3}: {e.Transition.Name,-18} [{e.Binding}]");

var raResult = raSim.Run(new SimulationOptions
{
    MaxSteps = 40,
    Random = new Random(42),
    StopOnDeadlock = true
});

Console.WriteLine($"\nFinal state:");
Console.WriteLine($"  Idle    : {ra.Idle.Marking}");
Console.WriteLine($"  Running : {ra.Running.Marking}");
Console.WriteLine($"  R1      : {ra.R1.Marking}");
Console.WriteLine($"  R2      : {ra.R2.Marking}");
Console.WriteLine($"Steps fired   : {raResult.Steps}");
Console.WriteLine($"Termination   : {raResult.TerminationReason}");

// ═══════════════════════════════════════════════════════════════════════════════
// 3. Readers-Writers
// ═══════════════════════════════════════════════════════════════════════════════

PrintHeader("Readers-Writers (Jensen)");
Console.WriteLine("3 readers and 2 writers share a resource via slot tokens.");
Console.WriteLine($"Readers take 1 slot each; a writer takes all {ReadersWriters.N} slots exclusively.");

var rw    = new ReadersWriters();
var rwSim = new CpnSimulator(rw);

Console.WriteLine($"\nInitial state:");
Console.WriteLine($"  FreeReaders : {rw.FreeReaders.Marking}");
Console.WriteLine($"  FreeWriters : {rw.FreeWriters.Marking}");
Console.WriteLine($"  Reading     : {rw.Reading.Marking}");
Console.WriteLine($"  Writing     : {rw.Writing.Marking}");
Console.WriteLine($"  Slots       : {rw.Slots.Marking}");

Console.WriteLine();
rwSim.TransitionFired += (_, e) =>
    Console.WriteLine($"  Step {e.StepNumber,3}: {e.Transition.Name,-12} [{e.Binding}]");

var rwResult = rwSim.Run(new SimulationOptions
{
    MaxSteps = 30,
    Random = new Random(7),
    StopOnDeadlock = true
});

Console.WriteLine($"\nFinal state:");
Console.WriteLine($"  FreeReaders : {rw.FreeReaders.Marking}");
Console.WriteLine($"  FreeWriters : {rw.FreeWriters.Marking}");
Console.WriteLine($"  Reading     : {rw.Reading.Marking}");
Console.WriteLine($"  Writing     : {rw.Writing.Marking}");
Console.WriteLine($"  Slots       : {rw.Slots.Marking}");

int activeReaders = rw.Reading.Marking.TotalCount;
int activeWriters = rw.Writing.Marking.TotalCount;
Console.WriteLine($"\nConcurrent readers : {activeReaders}");
Console.WriteLine($"Active writers     : {activeWriters}");
Console.WriteLine($"Mutual exclusion   : {(activeWriters == 0 || activeReaders == 0 ? "OK" : "VIOLATED")}");
Console.WriteLine($"Steps fired        : {rwResult.Steps}");
Console.WriteLine($"Termination        : {rwResult.TerminationReason}");
Console.WriteLine();
