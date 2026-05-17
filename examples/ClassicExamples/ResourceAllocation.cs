using CSharPN.Core;

/// <summary>
/// Jensen's resource allocation example (Coloured Petri Nets, Vol. 1, Ch. 1).
///
/// Three processes compete for two resource types (R1 and R2):
///   p1 needs only R1
///   p2 needs only R2
///   p3 needs both R1 and R2
///
/// The model is cyclic: after a process completes, it returns to Idle
/// to demonstrate perpetual concurrent behaviour.
///
/// Places:
///   Idle    – processes not currently running
///   Running – processes currently executing
///   R1      – R1 resource tokens (1 unit available)
///   R2      – R2 resource tokens (2 units available)
///
/// Transitions:
///   Start_NeedsR1   – p1 acquires R1 and starts
///   Start_NeedsR2   – p2 acquires R2 and starts
///   Start_NeedsBoth – p3 acquires R1 and R2 and starts
///   Stop_R1         – p1 finishes, releases R1
///   Stop_R2         – p2 finishes, releases R2
///   Stop_Both       – p3 finishes, releases R1 and R2
/// </summary>
public class ResourceAllocation : CpnModel
{
    public enum ResourceNeeds { R1Only, R2Only, BothR1AndR2 }

    public record Process(string Id, ResourceNeeds Needs)
    {
        public override string ToString() => Id;
    }

    // Places
    public readonly Place<Process> Idle;
    public readonly Place<Process> Running;
    public readonly Place<int>     R1;
    public readonly Place<int>     R2;

    public ResourceAllocation() : base("ResourceAllocation")
    {
        Idle = AddPlace("Idle", Multiset.Of(
            new Process("p1", ResourceNeeds.R1Only),
            new Process("p2", ResourceNeeds.R2Only),
            new Process("p3", ResourceNeeds.BothR1AndR2)));

        Running = AddPlace<Process>("Running");

        // R1: 1 unit; R2: 2 units (represented as token value 0 for identity)
        R1 = AddPlace("R1", Multiset.Repeat(0, 1));
        R2 = AddPlace("R2", Multiset.Repeat(0, 2));

        // Variables
        var p  = new Var<Process>("p");
        var r  = new Var<int>("r");
        var r1 = new Var<int>("r1");
        var r2 = new Var<int>("r2");

        // ── Start transitions ──────────────────────────────────────────────────

        AddTransition("Start_NeedsR1")
            .Input(Idle, p)
            .Input(R1, r)
            .Guard(() => p.Val.Needs == ResourceNeeds.R1Only)
            .Output(Running, () => Multiset.Of(p.Val))
            .Build();

        AddTransition("Start_NeedsR2")
            .Input(Idle, p)
            .Input(R2, r)
            .Guard(() => p.Val.Needs == ResourceNeeds.R2Only)
            .Output(Running, () => Multiset.Of(p.Val))
            .Build();

        AddTransition("Start_NeedsBoth")
            .Input(Idle, p)
            .Input(R1, r1)
            .Input(R2, r2)
            .Guard(() => p.Val.Needs == ResourceNeeds.BothR1AndR2)
            .Output(Running, () => Multiset.Of(p.Val))
            .Build();

        // ── Stop transitions (cyclic: process returns to Idle) ─────────────────

        AddTransition("Stop_R1")
            .Input(Running, p)
            .Guard(() => p.Val.Needs == ResourceNeeds.R1Only)
            .Output(Idle, () => Multiset.Of(p.Val))
            .Output(R1,   () => Multiset.Of(0))
            .Build();

        AddTransition("Stop_R2")
            .Input(Running, p)
            .Guard(() => p.Val.Needs == ResourceNeeds.R2Only)
            .Output(Idle, () => Multiset.Of(p.Val))
            .Output(R2,   () => Multiset.Of(0))
            .Build();

        AddTransition("Stop_Both")
            .Input(Running, p)
            .Guard(() => p.Val.Needs == ResourceNeeds.BothR1AndR2)
            .Output(Idle, () => Multiset.Of(p.Val))
            .Output(R1,   () => Multiset.Of(0))
            .Output(R2,   () => Multiset.Of(0))
            .Build();
    }
}
