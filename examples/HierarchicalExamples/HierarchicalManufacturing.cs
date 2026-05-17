using CSharPN.Core;

namespace HierarchicalExamples;

// ── HierarchicalManufacturing ─────────────────────────────────────────────────
//
// Hierarchical CPN of a two-department job shop (Jensen Vol. 2 §2 style).
//
// Top page:
//   Jobs (int) → [Dept A substitution] → AfterA (int) → [Dept B substitution] → Done (int)
//
// Each department sub-page encapsulates:
//   • A pool of worker tokens (capacity)
//   • A local "InProcess" place
//   • StartDept / FinishDept transitions
//
// The port places (input queue + output queue) are owned by the top model
// and injected into each sub-page.  This cleanly separates concerns:
//   - Top page shows the high-level flow.
//   - Sub-pages show the internal scheduling details.

public static class HierarchicalManufacturing
{
    // ── DepartmentPage ────────────────────────────────────────────────────────
    private sealed class DepartmentPage : CpnPage
    {
        public DepartmentPage(
            string      deptName,
            Place<int>  inputQueue,
            Place<int>  outputQueue,
            int         numWorkers)
            : base(deptName)
        {
            In(inputQueue);
            Out(outputQueue);

            // Local: worker capacity tokens (one token per worker).
            var workers = AddPlace<int>($"Workers_{deptName}",
                Enumerable.Range(1, numWorkers)
                          .Aggregate(Multiset<int>.Empty, (m, i) => m.Add(i, 1)));

            // Local: jobs currently being processed (job-id stored here).
            var inProcess = AddPlace<int>($"InProcess_{deptName}");

            var job = new Var<int>("job");
            var w   = new Var<int>("w");

            // Start: take job from input, consume a worker, put job in InProcess.
            AddTransition($"Start_{deptName}")
                .Input(inputQueue, job)
                .Input(workers, w)
                .Output(inProcess, () => job.Val)
                .Build();

            // Finish: job done → release worker, move job to output.
            AddTransition($"Finish_{deptName}")
                .Input(inProcess, job)
                .Output(workers, () => 1)
                .Output(outputQueue, () => job.Val)
                .Build();
        }
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    public const int NumJobs     = 6;
    public const int WorkersA    = 2;
    public const int WorkersB    = 1;

    /// <summary>
    /// Builds the hierarchical job-shop model.
    /// </summary>
    public static (HierarchicalCpnModel model,
                   Place<int> jobs,
                   Place<int> done) Build()
    {
        var model = new HierarchicalCpnModel("HierarchicalManufacturing");

        // Top-level flow places.
        var jobs   = model.AddPlace<int>("Jobs",
            Enumerable.Range(1, NumJobs)
                      .Aggregate(Multiset<int>.Empty, (m, i) => m.Add(i, 1)));
        var afterA = model.AddPlace<int>("AfterDeptA");
        var done   = model.AddPlace<int>("Done");

        // Sub-pages each inject the connecting places as ports.
        var deptA = new DepartmentPage("DeptA", jobs,   afterA, WorkersA);
        var deptB = new DepartmentPage("DeptB", afterA, done,   WorkersB);

        model.AddSubPage(deptA, "Department_A");
        model.AddSubPage(deptB, "Department_B");

        return (model, jobs, done);
    }
}
