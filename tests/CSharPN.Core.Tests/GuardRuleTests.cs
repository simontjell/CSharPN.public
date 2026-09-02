using FluentAssertions;
using Xunit;

namespace CSharPN.Core.Tests;

/// <summary>
/// A guard is an expression over the values of the transition's variables and over
/// constants of the net, and over nothing that could carry state (CPN Tools: guards must
/// not depend on reference variables). Because it arrives as an expression tree,
/// <see cref="TransitionBuilder.Build"/> derives which variables it uses and checks what
/// else it captures. The one route the tree cannot show is a method that reads model
/// state inside its own body; <see cref="GuardScope"/> covers that at runtime when on.
/// </summary>
public class GuardRuleTests : IDisposable
{
    // GuardScope.Strict is process-wide, so each test restores it.
    private readonly bool _original = GuardScope.Strict;
    public void Dispose() => GuardScope.Strict = _original;

    // ── Compliant guards ──────────────────────────────────────────────────────

    private sealed class BoundValue : CpnModel
    {
        public readonly Transition T;
        public BoundValue()
        {
            var source = AddPlace("Source", Multiset.Of(1, 2, 3));
            var x = new Var<int>("x");
            T = AddTransition("Guarded").Input(source, x).Guard(() => x.Val > 1).Build();
        }
    }

    [Fact]
    public void Guard_over_a_bound_value_builds_and_filters()
    {
        var bindings = new BoundValue().T.GetEnabledBindings();
        bindings.Select(b => b.Values["x"]).Should().BeEquivalentTo([2, 3]);
    }

    private sealed class StaticsAndCapturedConstant : CpnModel
    {
        private static readonly int[] Limits = [1, 2, 3];
        private const int Cap = 2;

        public readonly Transition T;
        public StaticsAndCapturedConstant()
        {
            var source = AddPlace("Source", Multiset.Of(1, 2, 3));
            var x = new Var<int>("x");
            int floor = 0;                                      // a captured value type
            T = AddTransition("Guarded")
                .Input(source, x)
                .Guard(() => x.Val > floor && x.Val <= Cap && Limits.Length == 3)
                .Build();
        }
    }

    [Fact]
    public void Static_members_and_captured_constants_are_allowed()
    {
        // Static members and value-type constants are constants of the net, like a CPN
        // declaration. Neither can carry marking state.
        var build = () => new StaticsAndCapturedConstant();
        build.Should().NotThrow();
    }

    private sealed class ImplicitConversion : CpnModel
    {
        public readonly Transition T;
        public ImplicitConversion()
        {
            var source = AddPlace("Source", Multiset.Of(1, 2, 3));
            var x = new Var<int>("x");
            T = AddTransition("Guarded").Input(source, x).Guard(() => x < 3).Build();
        }
    }

    [Fact]
    public void The_implicit_conversion_form_is_recognised()
    {
        // `x < 3` goes through Var<T>'s implicit operator rather than .Val.
        new ImplicitConversion().T.GetEnabledBindings().Should().HaveCount(2);
    }

    private sealed class FreeVariableInGuard : CpnModel
    {
        public readonly Transition T;
        public FreeVariableInGuard()
        {
            var source = AddPlace("Source", Multiset.Of(1, 2));
            var x = new Var<int>("x");
            var b = new Var<bool>("b");                         // bound by no arc: a free variable
            T = AddTransition("Guarded").Input(source, x).Guard(() => b.Val || x.Val == 2).Build();
        }
    }

    [Fact]
    public void A_free_variable_with_an_enumerable_colour_set_is_allowed_in_the_guard()
    {
        // Not a violation of the rule: the guard still depends on the binding only.
        var net = new FreeVariableInGuard();
        net.T.FreeVariableNames.Should().BeEquivalentTo(["b"]);
        net.T.GetEnabledBindings().Should().HaveCount(3);    // (1,true), (2,true), (2,false)
    }

    // ── Rejected when the transition is built ─────────────────────────────────

    private sealed class ReadsAPlace : CpnModel
    {
        public ReadsAPlace()
        {
            var source = AddPlace("Source", Multiset.Of(1, 2));
            var x = new Var<int>("x");
            AddTransition("Guarded")
                .Input(source, x)
                .Guard(() => source.Marking.TotalCount > 1)
                .Build();
        }
    }

    [Fact]
    public void Guard_reading_a_place_is_rejected()
    {
        var build = () => new ReadsAPlace();
        build.Should().Throw<InvalidOperationException>()
             .WithMessage("*Guarded*")
             .WithMessage("*Source*");
    }

    private sealed class CallsInstanceHelper : CpnModel
    {
        private readonly Place<int> _source;
        private bool Busy() => _source.Marking.TotalCount > 1;

        public CallsInstanceHelper()
        {
            _source = AddPlace("Source", Multiset.Of(1, 2));
            var x = new Var<int>("x");
            AddTransition("Guarded").Input(_source, x).Guard(() => Busy()).Build();
        }
    }

    [Fact]
    public void Guard_calling_an_instance_helper_on_the_model_is_rejected()
    {
        // The helper reads a place, but that is invisible here — what gets rejected is
        // the capture of the model that reaching it requires.
        var build = () => new CallsInstanceHelper();
        build.Should().Throw<InvalidOperationException>().WithMessage("*model*");
    }

    private sealed class CapturesAMutableReference : CpnModel
    {
        public CapturesAMutableReference()
        {
            var source = AddPlace("Source", Multiset.Of(1, 2));
            var x = new Var<int>("x");
            var seen = new List<int>();                         // could carry state
            AddTransition("Guarded").Input(source, x).Guard(() => seen.Count == 0).Build();
        }
    }

    [Fact]
    public void Guard_capturing_an_arbitrary_reference_is_rejected()
    {
        var build = () => new CapturesAMutableReference();
        build.Should().Throw<InvalidOperationException>()
             .WithMessage("*seen*")
             .WithMessage("*List*");
    }

    private sealed class UsesAVariableNoArcBinds : CpnModel
    {
        public UsesAVariableNoArcBinds()
        {
            var source = AddPlace("Source", Multiset.Of(1));
            var x     = new Var<int>("x");
            var stray = new Var<int>("stray");                  // never bound, and int is not enumerable
            AddTransition("Guarded").Input(source, x).Guard(() => stray.Val > 0).Build();
        }
    }

    [Fact]
    public void Guard_using_an_unbindable_variable_is_rejected()
    {
        // The variable set is derived from the expression, so this is caught without
        // the author having declared anything.
        var build = () => new UsesAVariableNoArcBinds();
        build.Should().Throw<InvalidOperationException>()
             .WithMessage("*stray*")
             .WithMessage("*Guarded*");
    }

    // ── Labels ────────────────────────────────────────────────────────────────

    [Fact]
    public void An_omitted_label_is_derived_from_the_expression()
    {
        new BoundValue().T.GuardLabel.Should().Be("[x > 1]");
    }

    private sealed class LabelledGuard : CpnModel
    {
        public readonly Transition T;
        public LabelledGuard()
        {
            var source = AddPlace("Source", Multiset.Of(1, 2));
            var x = new Var<int>("x");
            T = AddTransition("Guarded").Input(source, x).Guard(() => x.Val > 1, "[big enough]").Build();
        }
    }

    [Fact]
    public void An_explicit_label_is_kept()
    {
        new LabelledGuard().T.GuardLabel.Should().Be("[big enough]");
    }

    // ── Runtime backstop for what the tree cannot show ────────────────────────

    private static readonly Place<int> Sneaky = new("Sneaky", Multiset.Of(7));
    private static bool StaticHelperThatReadsAPlace(int x) => Sneaky.Marking.TotalCount > 0;

    private sealed class IndirectThroughStaticMethod : CpnModel
    {
        public readonly Transition T;
        public IndirectThroughStaticMethod()
        {
            var source = AddPlace("Source", Multiset.Of(1, 2));
            var x = new Var<int>("x");
            T = AddTransition("Guarded").Input(source, x).Guard(() => StaticHelperThatReadsAPlace(x.Val)).Build();
        }
    }

    [Fact]
    public void A_static_helper_that_reads_a_place_passes_the_build_check()
    {
        // The expression tree shows only a call; the read is inside the method body.
        var build = () => new IndirectThroughStaticMethod();
        build.Should().NotThrow();
    }

    [Fact]
    public void GuardScope_catches_the_indirect_read_at_runtime()
    {
        GuardScope.Strict = true;
        var net = new IndirectThroughStaticMethod();
        net.Invoking(n => n.T.GetEnabledBindings())
           .Should().Throw<InvalidOperationException>()
           .WithMessage("*Sneaky*");
    }

    [Fact]
    public void The_indirect_read_is_ignored_while_strict_is_off()
    {
        GuardScope.Strict = false;
        new IndirectThroughStaticMethod().T.GetEnabledBindings().Should().HaveCount(2);
    }
}

// ── Bounded enumeration and model lock ────────────────────────────────────────

public class EnumerationAndLockTests
{
    private sealed class ManyBindings : CpnModel
    {
        public readonly Transition T;
        public ManyBindings()
        {
            var p = AddPlace("P", Multiset.Of(Enumerable.Range(0, 50)));
            var q = AddPlace("Q", Multiset.Of(Enumerable.Range(0, 50)));
            var x = new Var<int>("x");
            var y = new Var<int>("y");
            T = AddTransition("T").Input(p, x).Input(q, y).Build();
        }
    }

    [Fact]
    public void GetEnabledBindings_with_max_stops_early()
    {
        var net = new ManyBindings();
        net.T.GetEnabledBindings(max: 1).Should().HaveCount(1);
        net.T.GetEnabledBindings(max: 7).Should().HaveCount(7);
        net.T.GetEnabledBindings().Should().HaveCount(2500);
    }

    [Fact]
    public void Max_must_be_positive()
    {
        var net = new ManyBindings();
        net.Invoking(n => n.T.GetEnabledBindings(max: 0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Concurrent_simulators_on_one_model_never_fire_a_stale_binding()
    {
        // Two drivers step the same model in parallel; every step is enumerate-then-fire
        // under the model lock, so a binding picked by one thread cannot be invalidated
        // by the other before it fires.
        var net = new ManyBindings();
        var simA = new CpnSimulator(net);
        var simB = new CpnSimulator(net);
        var tasks = new[] { simA, simB }
            .Select(sim => Task.Run(() => { for (int i = 0; i < 20; i++) sim.Step(); }))
            .ToArray();
        var run = () => Task.WaitAll(tasks);
        run.Should().NotThrow();
        net.Places.Sum(p => p.TotalTokenCount).Should().Be(100 - 2 * 40);
    }
}
