using CSharPN.Core;

namespace TimedExamples;

// ── ManufacturingSystem ───────────────────────────────────────────────────────
//
// Timed CPN of a two-stage flow-shop (Jensen Vol. 1 §4.3 style).
//
// N jobs arrive at regular intervals, each passes through Department A then B.
// Each department has a fixed number of machines; processing takes a fixed time.
//
// Colour sets:
//   int = job ID
//   WorkItem(JobId, MachineId) = job currently being processed on a machine
//
// Places:
//   Arrivals   : Timed<int>   – pre-scheduled job arrivals
//   QueueA     : int          – jobs waiting for Dept A
//   InProcessA : Timed<int>   – jobs on a Dept-A machine (ready when done)
//   QueueB     : int          – jobs waiting for Dept B
//   InProcessB : Timed<int>   – jobs on a Dept-B machine (ready when done)
//   Done       : int          – completed jobs
//   MachinesA  : int          – available machine tokens (capacity)
//   MachinesB  : int          – available machine tokens (capacity)

public sealed class ManufacturingSystem : TimedCpnModel
{
    // ── Constants ─────────────────────────────────────────────────────────────
    public const int NumJobs           = 8;
    public const int ArrivalInterval   = 5;   // new job every 5 time units
    public const int ProcTimeA         = 10;  // Dept A processing time
    public const int ProcTimeB         = 14;  // Dept B processing time
    public const int CapacityA         = 2;   // machines in Dept A
    public const int CapacityB         = 1;   // machines in Dept B

    // ── Public places ─────────────────────────────────────────────────────────
    public readonly Place<Timed<int>> Arrivals;
    public readonly Place<int>        QueueA;
    public readonly Place<Timed<int>> InProcessA;
    public readonly Place<int>        QueueB;
    public readonly Place<Timed<int>> InProcessB;
    public readonly Place<int>        Done;
    public readonly Place<int>        MachinesA;
    public readonly Place<int>        MachinesB;

    public ManufacturingSystem()
    {
        // Pre-schedule job arrivals: job i+1 arrives at time i * ArrivalInterval.
        var arrivals = Enumerable.Range(0, NumJobs)
            .Aggregate(Multiset<Timed<int>>.Empty,
                (m, i) => m.Add(Timed<int>.At(i + 1, i * ArrivalInterval), 1));

        Arrivals   = AddTimedPlace<int>("Arrivals",   arrivals);
        QueueA     = AddPlace<int>("QueueA");
        InProcessA = AddTimedPlace<int>("InProcessA");
        QueueB     = AddPlace<int>("QueueB");
        InProcessB = AddTimedPlace<int>("InProcessB");
        Done       = AddPlace<int>("Done");

        // Machine capacity tokens: one token per machine.
        MachinesA = AddPlace<int>("MachinesA",
            Enumerable.Range(1, CapacityA).Aggregate(Multiset<int>.Empty, (m, i) => m.Add(i, 1)));
        MachinesB = AddPlace<int>("MachinesB",
            Enumerable.Range(1, CapacityB).Aggregate(Multiset<int>.Empty, (m, i) => m.Add(i, 1)));

        // ── Variables ─────────────────────────────────────────────────────────
        var job = new Var<int>("job");
        var mch = new Var<int>("mch");

        // ── Transitions ───────────────────────────────────────────────────────

        // Arrive: timed arrival event → job enters Dept A queue.
        AddTransition("Arrive")
            .TimedInput(Arrivals, job)
            .Output(QueueA, () => job.Val)
            .Build();

        // StartA: free machine picks up job; machine token consumed until FinishA.
        AddTransition("StartA")
            .Input(QueueA, job)
            .Input(MachinesA, mch)
            .TimedOutput(InProcessA, () => job.Val, ProcTimeA)
            .Build();

        // FinishA: job done in Dept A → machine freed, job moves to Dept B queue.
        AddTransition("FinishA")
            .TimedInput(InProcessA, job)
            .Output(QueueB, () => job.Val)
            .Output(MachinesA, () => 1)
            .Build();

        // StartB: free Dept-B machine picks up job.
        AddTransition("StartB")
            .Input(QueueB, job)
            .Input(MachinesB, mch)
            .TimedOutput(InProcessB, () => job.Val, ProcTimeB)
            .Build();

        // FinishB: job done → machine freed, job is complete.
        AddTransition("FinishB")
            .TimedInput(InProcessB, job)
            .Output(Done, () => job.Val)
            .Output(MachinesB, () => 1)
            .Build();
    }

    // ── Result inspection ──────────────────────────────────────────────────────
    public int  CompletedJobs => Done.Marking.TotalCount;
    public bool AllDone       => CompletedJobs == NumJobs;

    // Average completion time – only valid after all jobs done.
    public string QueueStats()
        => $"QueueA={QueueA.Marking.TotalCount}  " +
           $"QueueB={QueueB.Marking.TotalCount}  " +
           $"Done={Done.Marking.TotalCount}/{NumJobs}";
}
