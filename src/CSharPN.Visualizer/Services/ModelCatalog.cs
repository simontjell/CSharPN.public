using CSharPN.Core;

namespace CSharPN.Visualizer.Services;

/// <summary>
/// Registry of built-in example models available for visualization.
/// Each entry is a (Name, Factory) pair so that multiple separate simulations
/// can be created from the same model definition.
/// </summary>
public sealed class ModelCatalog
{
    public sealed record Entry(string Name, Func<CpnModel> Factory);

    public IReadOnlyList<Entry> Models { get; } = BuildCatalog();

    private static List<Entry> BuildCatalog() =>
    [
        new("Dining Philosophers (3)",  DiningPhilosophers),
        new("Simple Protocol",          SimpleProtocol),
        new("Producer-Consumer",        ProducerConsumer),
        new("Resource Allocation",      ResourceAllocation),
        new("Service Queue (Timed)",    ServiceQueueTimed),
        new("Job Shop (Hierarchical)",  JobShopHierarchical),
    ];

    // ── Dining Philosophers (3 philosophers) ──────────────────────────────────
    //
    // Each philosopher Pi takes forks i and (i%3)+1 simultaneously.
    // Deadlock (all holding one fork) cannot occur because both forks must be
    // grabbed atomically.

    private static CpnModel DiningPhilosophers()
    {
        var m = new DinPhilModel();
        return m;
    }

    private sealed class DinPhilModel : CpnModel
    {
        public DinPhilModel()
        {
            var thinking  = AddPlace<int>("Thinking",  Multiset.Of(1, 2, 3));
            var eating    = AddPlace<int>("Eating");
            // Split into LeftFork / RightFork so each transition has at most
            // one input arc from each place (required for unique (FromId,ToId) keys).
            var leftFork  = AddPlace<int>("LeftFork",  Multiset.Of(1, 2, 3));
            var rightFork = AddPlace<int>("RightFork", Multiset.Of(1, 2, 3));

            for (int i = 1; i <= 3; i++)
            {
                int ph = i;
                int lf = i;
                int rf = i % 3 + 1;

                var p  = new Var<int>($"p{ph}");
                var f1 = new Var<int>($"fl{ph}");
                var f2 = new Var<int>($"fr{ph}");

                AddTransition($"P{ph}_TakeForks")
                    .Input(thinking,  p)
                    .Input(leftFork,  f1)
                    .Input(rightFork, f2)
                    .Guard(() => p.Val == ph && f1.Val == lf && f2.Val == rf,
                           $"p{ph}.Val == {ph} && fl{ph}.Val == {lf} && fr{ph}.Val == {rf}")
                    .Output(eating, () => ph, $"{ph}")
                    .Build();

                AddTransition($"P{ph}_ReleaseForks")
                    .Input(eating, p)
                    .Guard(() => p.Val == ph, $"p{ph}.Val == {ph}")
                    .Output(thinking,  () => ph, $"{ph}")
                    .Output(leftFork,  () => lf, $"{lf}")
                    .Output(rightFork, () => rf, $"{rf}")
                    .Build();
            }
        }
    }

    // ── Service Queue (Timed) ─────────────────────────────────────────────────
    //
    // A simple M/D/1-style service queue with pre-scheduled arrivals and a
    // fixed service time.  Demonstrates timed CPN (Jensen Vol. 2 §1):
    //
    //   Arrivals (Timed<int>) --Arrive--> Queue (int)
    //                                         |
    //                               +--Idle --+
    //                               |
    //                           StartService
    //                               |
    //                           Busy (Timed<int>)
    //                               |
    //                            Finish
    //                           /        \
    //                       Idle (int)  Done (int)
    //
    // 8 customers arrive at t = 0, 4, 8, 12, 16, 20, 24, 28.
    // Service takes 5 time units.  At most one customer served at a time.

    private static CpnModel ServiceQueueTimed() => new ServiceQueueModel();

    private sealed class ServiceQueueModel : TimedCpnModel
    {
        public ServiceQueueModel()
        {
            const int serviceTime    = 5;
            const int numCustomers   = 8;
            const int arrivalInterval = 4;

            // Pre-schedule customer arrivals.
            var arrivals = Enumerable.Range(1, numCustomers)
                .Aggregate(Multiset<Timed<int>>.Empty,
                    (m, i) => m.Add(Timed<int>.At(i, (i - 1) * arrivalInterval), 1));

            var arrivalPlace = AddTimedPlace<int>("Arrivals", arrivals);
            var queue        = AddPlace<int>("Queue");
            var busy         = AddTimedPlace<int>("Busy");
            var idle         = AddPlace<int>("Idle", Multiset.Of(1));   // one server
            var done         = AddPlace<int>("Done");

            var customer    = new Var<int>("cust");
            var serverToken = new Var<int>("srv");

            AddTransition("Arrive")
                .TimedInput(arrivalPlace, customer)
                .Output(queue, () => customer.Val, "cust.Val")
                .Build();

            AddTransition("StartService")
                .Input(queue,  customer)
                .Input(idle,   serverToken)
                .TimedOutput(busy, () => customer.Val, serviceTime)
                .Build();

            AddTransition("Finish")
                .TimedInput(busy, customer)
                .Output(idle, () => 1, "1")
                .Output(done, () => customer.Val, "cust.Val")
                .Build();
        }
    }

    // ── Job Shop (Hierarchical) ────────────────────────────────────────────────
    //
    // A two-department job shop where jobs flow through Dept A then Dept B.
    // Demonstrates hierarchical CPN (Jensen Vol. 2 §2):
    //
    //   [Top page]
    //   Jobs --> [DeptA] --> AfterA --> [DeptB] --> Done
    //
    //   [DeptA / DeptB sub-pages]
    //   InputQueue + Workers --> StartDept --> InProcess --> FinishDept --> Workers + OutputQueue

    private static CpnModel JobShopHierarchical() => BuildJobShop();

    private static HierarchicalCpnModel BuildJobShop()
    {
        const int numJobs    = 5;
        const int workersA   = 2;
        const int workersB   = 1;

        var model  = new HierarchicalCpnModel("JobShop");
        var jobs   = model.AddPlace<int>("Jobs",
            Enumerable.Range(1, numJobs)
                      .Aggregate(Multiset<int>.Empty, (m, i) => m.Add(i, 1)));
        var afterA = model.AddPlace<int>("AfterA");
        var done   = model.AddPlace<int>("Done");

        model.AddSubPage(new DeptPage("DeptA", jobs,   afterA, workersA), "Department_A");
        model.AddSubPage(new DeptPage("DeptB", afterA, done,   workersB), "Department_B");
        return model;
    }

    private sealed class DeptPage : CpnPage
    {
        public DeptPage(string name, Place<int> input, Place<int> output, int numWorkers)
            : base(name)
        {
            In(input);
            Out(output);

            var workers   = AddPlace<int>($"Workers_{name}",
                Enumerable.Range(1, numWorkers)
                          .Aggregate(Multiset<int>.Empty, (m, i) => m.Add(i, 1)));
            var inProcess = AddPlace<int>($"InProcess_{name}");

            var job = new Var<int>("job");
            var w   = new Var<int>("w");

            AddTransition($"Start_{name}")
                .Input(input,    job)
                .Input(workers,  w)
                .Output(inProcess, () => job.Val, "job.Val")
                .Build();

            AddTransition($"Finish_{name}")
                .Input(inProcess, job)
                .Output(workers,  () => 1,       "1")
                .Output(output,   () => job.Val, "job.Val")
                .Build();
        }
    }

    // ── Simple Protocol (Alternating Bit) ─────────────────────────────────────

    private static CpnModel SimpleProtocol()
    {
        var m = new SimpleProtocolModel();
        return m;
    }

    private sealed class SimpleProtocolModel : CpnModel
    {
        private static readonly string[] Data = ["a", "b", "c", "d", "e", "f"];

        public sealed record Packet(int Seq, string Data);

        public SimpleProtocolModel()
        {
            var nextToSend = AddPlace<int>("NextToSend", Multiset.Of(0));
            var a          = AddPlace<Packet>("A");
            var b          = AddPlace<int>("B");
            var nextRec    = AddPlace<int>("NextRec", Multiset.Of(0));
            var received   = AddPlace<string>("Received");

            var n = new Var<int>("n");
            var p = new Var<Packet>("p");
            var k = new Var<int>("k");

            AddTransition("SendPacket")
                .Input(nextToSend, n)
                .Guard(() => n.Val < Data.Length, "n.Val < Data.Length")
                .Output(nextToSend, () => n.Val,                            "n.Val")
                .Output(a,          () => new Packet(n.Val % 2, Data[n.Val]), "new Packet(n.Val % 2, Data[n.Val])")
                .Build();

            AddTransition("LosePacket")
                .Input(a, p)
                .Build();

            AddTransition("ReceivePacket_New")
                .Input(a, p)
                .Input(nextRec, k)
                .Guard(() => p.Val.Seq == k.Val % 2, "p.Val.Seq == k.Val % 2")
                .Output(nextRec,  () => k.Val + 1,   "k.Val + 1")
                .Output(received, () => p.Val.Data,  "p.Val.Data")
                .Output(b,        () => k.Val + 1,   "k.Val + 1")
                .Build();

            AddTransition("ReceivePacket_Dup")
                .Input(a, p)
                .Input(nextRec, k)
                .Guard(() => p.Val.Seq != k.Val % 2, "p.Val.Seq != k.Val % 2")
                .Output(nextRec, () => k.Val, "k.Val")
                .Output(b,       () => k.Val, "k.Val")
                .Build();

            AddTransition("LoseAck")
                .Input(b, k)
                .Build();

            AddTransition("ReceiveAck_Correct")
                .Input(nextToSend, n)
                .Input(b, k)
                .Guard(() => n.Val < Data.Length && k.Val == n.Val + 1,
                       "n.Val < Data.Length && k.Val == n.Val + 1")
                .Output(nextToSend, () => n.Val + 1, "n.Val + 1")
                .Build();

            AddTransition("ReceiveAck_Dup")
                .Input(nextToSend, n)
                .Input(b, k)
                .Guard(() => k.Val != n.Val + 1, "k.Val != n.Val + 1")
                .Output(nextToSend, () => n.Val, "n.Val")
                .Build();
        }
    }

    // ── Producer-Consumer (bounded buffer, capacity 3) ────────────────────────

    private static CpnModel ProducerConsumer()
    {
        var m = new ProducerConsumerModel();
        return m;
    }

    private sealed class ProducerConsumerModel : CpnModel
    {
        public ProducerConsumerModel()
        {
            // Buffer slots are represented by int tokens (slot IDs 1..3).
            var freeSlots  = AddPlace<int>("FreeSlots",  Multiset.Of(1, 2, 3));
            var buffer     = AddPlace<int>("Buffer");
            var idle       = AddPlace<int>("Idle",       Multiset.Of(1));
            var producing  = AddPlace<int>("Producing");  // holds slot being filled
            var ready      = AddPlace<int>("Ready",      Multiset.Of(1));
            var consuming  = AddPlace<int>("Consuming");

            var slot  = new Var<int>("slot");
            var item  = new Var<int>("item");
            var cons  = new Var<int>("cons");

            AddTransition("StartProduce")
                .Input(idle, new Var<int>("p"))
                .Input(freeSlots, slot)
                .Output(producing, () => slot.Val, "slot.Val")
                .Build();

            AddTransition("Deposit")
                .Input(producing, slot)
                .Output(idle,   () => 1,        "1")
                .Output(buffer, () => slot.Val, "slot.Val")
                .Build();

            AddTransition("StartConsume")
                .Input(ready, cons)
                .Input(buffer, item)
                .Output(consuming, () => item.Val, "item.Val")
                .Build();

            AddTransition("Finish")
                .Input(consuming, item)
                .Output(ready,     () => 1,        "1")
                .Output(freeSlots, () => item.Val, "item.Val")
                .Build();
        }
    }

    // ── Resource Allocation (Jensen Vol. 1 Ch. 1, simplified) ─────────────────

    private static CpnModel ResourceAllocation()
    {
        var m = new ResourceAllocationModel();
        return m;
    }

    private sealed class ResourceAllocationModel : CpnModel
    {
        public ResourceAllocationModel()
        {
            // 3 processes, 2 resource types R1 (1 unit) and R2 (2 units).
            var idle    = AddPlace<int>("Idle",    Multiset.Of(1, 2, 3));
            var running = AddPlace<int>("Running");
            var r1      = AddPlace<int>("R1",      Multiset.Of(1));
            var r2      = AddPlace<int>("R2",      Multiset.Of(1, 2));

            var p   = new Var<int>("p");
            var r   = new Var<int>("r");
            var r2a = new Var<int>("r2a");
            var r2b = new Var<int>("r2b");

            // P1 needs R1 only
            AddTransition("Start_P1")
                .Input(idle, p).Guard(() => p.Val == 1, "p.Val == 1")
                .Input(r1, r)
                .Output(running, () => 1, "1")
                .Build();
            AddTransition("Stop_P1")
                .Input(running, p).Guard(() => p.Val == 1, "p.Val == 1")
                .Output(idle, () => 1, "1")
                .Output(r1,   () => 1, "1")
                .Build();

            // P2 needs R2 only (one unit)
            AddTransition("Start_P2")
                .Input(idle, p).Guard(() => p.Val == 2, "p.Val == 2")
                .Input(r2, r)
                .Output(running, () => 2, "2")
                .Build();
            AddTransition("Stop_P2")
                .Input(running, p).Guard(() => p.Val == 2, "p.Val == 2")
                .Output(idle, () => 2, "2")
                .Output(r2,   () => 1, "1")
                .Build();

            // P3 needs both R1 and one unit of R2
            AddTransition("Start_P3")
                .Input(idle, p).Guard(() => p.Val == 3, "p.Val == 3")
                .Input(r1, r)
                .Input(r2, r2a)
                .Output(running, () => 3, "3")
                .Build();
            AddTransition("Stop_P3")
                .Input(running, p).Guard(() => p.Val == 3, "p.Val == 3")
                .Output(idle, () => 3, "3")
                .Output(r1,   () => 1, "1")
                .Output(r2,   () => 1, "1")
                .Build();
        }
    }
}
