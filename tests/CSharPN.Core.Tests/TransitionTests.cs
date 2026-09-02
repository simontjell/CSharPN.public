using CSharPN.Core;
using FluentAssertions;
using Xunit;

namespace CSharPN.Core.Tests;

public class TransitionTests
{
    // ── Simple net helper ─────────────────────────────────────────────────────

    private class SimpleNet : CpnModel
    {
        public readonly Place<int> P1;
        public readonly Place<int> P2;
        public readonly Transition T;

        public SimpleNet(Multiset<int> initial)
        {
            P1 = AddPlace("P1", initial);
            P2 = AddPlace<int>("P2");
            var x = new Var<int>("x");
            T = AddTransition("Move")
                .Input(P1, x)
                .Output(P2, () => Multiset.Of(x.Val))
                .Build();
        }
    }

    // ── GetEnabledBindings ────────────────────────────────────────────────────

    [Fact]
    public void Empty_place_produces_no_enabled_bindings()
    {
        var net = new SimpleNet(Multiset<int>.Empty);
        net.T.GetEnabledBindings().Should().BeEmpty();
    }

    [Fact]
    public void One_token_produces_one_enabled_binding()
    {
        var net = new SimpleNet(Multiset.Of(42));
        net.T.GetEnabledBindings().Should().HaveCount(1);
    }

    [Fact]
    public void Three_distinct_tokens_produce_three_bindings()
    {
        var net = new SimpleNet(Multiset.Of(1, 2, 3));
        net.T.GetEnabledBindings().Should().HaveCount(3);
    }

    [Fact]
    public void Two_copies_of_same_token_produce_one_binding()
    {
        // There's only one distinct value to bind x to
        var net = new SimpleNet(Multiset.Repeat(7, 2));
        net.T.GetEnabledBindings().Should().HaveCount(1);
    }

    // ── Guard ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Guard_filters_bindings()
    {
        var net = new SimpleNet(Multiset.Of(1, 2, 3));
        var x = new Var<int>("x");
        // Build a transition with a guard on a separate model
        var model = new FilteredNet(Multiset.Of(1, 2, 3, 4, 5));
        var bindings = model.T.GetEnabledBindings();
        // Only even numbers pass the guard
        bindings.Should().HaveCount(2);
        bindings.All(b => (int)b.Values["x"] % 2 == 0).Should().BeTrue();
    }

    private class FilteredNet : CpnModel
    {
        public readonly Place<int> P;
        public readonly Transition T;

        public FilteredNet(Multiset<int> initial)
        {
            P = AddPlace("P", initial);
            var x = new Var<int>("x");
            T = AddTransition("EvenOnly")
                .Input(P, x)
                .Guard(() => x.Val % 2 == 0)
                .Output(P, () => Multiset.Of(x.Val))
                .Build();
        }
    }

    // ── Fire ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Fire_moves_token_from_input_to_output_place()
    {
        var net = new SimpleNet(Multiset.Of(99));
        var binding = net.T.GetEnabledBindings().Single();

        net.T.Fire(binding);

        (net.P1.Marking == Multiset<int>.Empty).Should().BeTrue();
        (net.P2.Marking == Multiset.Of(99)).Should().BeTrue();
    }

    [Fact]
    public void Fire_decrements_multiplicity_correctly()
    {
        var net = new SimpleNet(Multiset.Repeat(5, 3));
        var binding = net.T.GetEnabledBindings().Single(); // only one distinct value

        net.T.Fire(binding);

        net.P1.Marking.Count(5).Should().Be(2);
        net.P2.Marking.Count(5).Should().Be(1);
    }

    // ── Two-arc binding ───────────────────────────────────────────────────────

    [Fact]
    public void Two_input_arcs_on_same_place_enumerate_distinct_pairs()
    {
        var model = new PairNet(Multiset.Of(1, 2, 3));
        // Should enumerate all ordered pairs (x,y) where x!=y from {1,2,3}
        // After guard x < y: (1,2), (1,3), (2,3) => 3 bindings
        var bindings = model.T.GetEnabledBindings();
        bindings.Should().HaveCount(3);
    }

    private class PairNet : CpnModel
    {
        public readonly Place<int> P;
        public readonly Transition T;

        public PairNet(Multiset<int> initial)
        {
            P = AddPlace("P", initial);
            var x = new Var<int>("x");
            var y = new Var<int>("y");
            T = AddTransition("Pair")
                .Input(P, x)
                .Input(P, y)
                .Guard(() => x.Val < y.Val)
                .Output(P, () => Multiset.Of(x.Val))
                .Output(P, () => Multiset.Of(y.Val))
                .Build();
        }
    }

    // ── Transition with no input arcs ─────────────────────────────────────────

    [Fact]
    public void Transition_with_no_inputs_is_always_enabled_once()
    {
        var model = new SourceNet();
        model.T.GetEnabledBindings().Should().HaveCount(1);
    }

    [Fact]
    public void Source_transition_produces_token()
    {
        var model = new SourceNet();
        var b = model.T.GetEnabledBindings().Single();
        model.T.Fire(b);
        (model.P.Marking == Multiset.Of(42)).Should().BeTrue();
    }

    private class SourceNet : CpnModel
    {
        public readonly Place<int> P;
        public readonly Transition T;

        public SourceNet()
        {
            P = AddPlace<int>("P");
            T = AddTransition("Source")
                .Output(P, () => 42)
                .Build();
        }
    }

    // ── Var state after enumeration ───────────────────────────────────────────

    [Fact]
    public void Vars_are_unbound_after_GetEnabledBindings()
    {
        var x = new Var<int>("x");
        var model = new VarCheckNet(x, Multiset.Of(1, 2, 3));
        _ = model.T.GetEnabledBindings();
        x.IsBound.Should().BeFalse();
    }

    private class VarCheckNet : CpnModel
    {
        public readonly Place<int> P;
        public readonly Transition T;

        public VarCheckNet(Var<int> x, Multiset<int> initial)
        {
            P = AddPlace("P", initial);
            T = AddTransition("T")
                .Input(P, x)
                .Output(P, () => Multiset.Of(x.Val))
                .Build();
        }
    }

    class Variables : CpnModel
    {
        public readonly Place<int> Input1;
        public readonly Place<int> Input2;
        public readonly Place<int> Output;

        public Variables()
        {
            Input1 = AddPlace("Input1", Multiset.Of(1, 2, 3));
            Input2 = AddPlace("Input2", Multiset.Of(1, 2, 3));
            Output = AddPlace<int>("Output");

            // Variables
            var a = new Var<int>("a");

            Add = AddTransition("Add")
                .Input(Input1, a)
                .Input(Input2, a)
                .Output(Output, () => a + a)
                .Build();

        }

        public Transition Add { get; }
    }

    [Fact]
    public void Single_variable_is_bound()
    {
        var net = new Variables();
        var bindings = net.Add.GetEnabledBindings();
        bindings.Count.Should().Be(3);
    }

    class Variables2 : CpnModel
    {
        public readonly Place<(int, int)> Input1;
        public readonly Place<(int, int)> Input2;
        public readonly Place<int> Output;

        public Variables2()
        {
            Input1 = AddPlace("Input1", Multiset.Of((1, 2)));
            Input2 = AddPlace("Input2", Multiset.Of((2, 1)));
            Output = AddPlace<int>("Output");

            // Variables
            var a = new Var<(int, int)>("a");
            var b = new Var<(int, int)>("b");

            Add = AddTransition("Add")
                .Input(Input1, a)
                .Input(Input2, b)
                .Output(Output, () => a.Val.Item1 + b.Val.Item1)
                .Guard(() => a.Val.Item1 == b.Val.Item2)
                .Build();

        }

        public Transition Add { get; }
    }

    [Fact]
    public void Complex_variable_is_bound()
    {
        var net = new Variables2();
        var bindings = net.Add.GetEnabledBindings();
        bindings.Count.Should().Be(1);
    }

    class Variables3 : CpnModel
    {
        public record Value(int A, int B);
        public readonly Place<Value> Input1;
        public readonly Place<Value> Input2;
        public readonly Place<int> Output;

        public Variables3()
        {
            Input1 = AddPlace("Input1", Multiset.Of(new Value(1, 2)));
            Input2 = AddPlace("Input2", Multiset.Of(new Value(2, 1)));
            Output = AddPlace<int>("Output");

            // Variables
            var a = new Var<Value>("a");

            Add = AddTransition("Add")
                .Input(Input1, a)
                .Input(Input2, () => new Value(a.Val.B, a.Val.A))    // This can depend on the previously bound variable a
                .Output(Output, () => a.Val.A + a.Val.B)
                .Build();

        }

        public Transition Add { get; }
    }

    [Fact]
    public void Declaration_order_makes_expression_binding_work()
    {
        var net = new Variables3();
        var bindings = net.Add.GetEnabledBindings();
        bindings.Count.Should().Be(1);
    }

    // ── Variable-name uniqueness ──────────────────────────────────────────────

    private class DuplicateNameSameTransition : CpnModel
    {
        public DuplicateNameSameTransition()
        {
            var a = AddPlace("A", Multiset.Of(1));
            var b = AddPlace("B", Multiset.Of(1));
            AddTransition("T")
                .Input(a, new Var<int>("x"))
                .Input(b, new Var<int>("x"))   // distinct instance, same name
                .Build();
        }
    }

    private class DuplicateNameAcrossTransitions : CpnModel
    {
        public DuplicateNameAcrossTransitions()
        {
            var a = AddPlace("A", Multiset.Of(1));
            var b = AddPlace("B", Multiset.Of(1));
            AddTransition("T1").Input(a, new Var<int>("x")).Build();
            AddTransition("T2").Input(b, new Var<int>("x")).Build(); // distinct instance, same name
        }
    }

    private class SharedInstanceAcrossTransitions : CpnModel
    {
        public SharedInstanceAcrossTransitions()
        {
            var a = AddPlace("A", Multiset.Of(1));
            var b = AddPlace("B", Multiset.Of(1));
            var x = new Var<int>("x");
            AddTransition("T1").Input(a, x).Build();
            AddTransition("T2").Input(b, x).Build(); // same instance — allowed
        }
    }

    [Fact]
    public void Distinct_variables_with_same_name_in_one_transition_throws()
    {
        var act = () => new DuplicateNameSameTransition();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate variable name 'x'*");
    }

    [Fact]
    public void Distinct_variables_with_same_name_across_transitions_throws()
    {
        var act = () => new DuplicateNameAcrossTransitions();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate variable name 'x'*");
    }

    [Fact]
    public void Reusing_the_same_variable_instance_across_transitions_is_allowed()
    {
        var act = () => new SharedInstanceAcrossTransitions();
        act.Should().NotThrow();
    }

    // ── Guard label derivation ────────────────────────────────────────────────

    private record Owner(string Name);
    private record Account(Owner Holder) : IEquatable<Account>;

    private class GuardDerivedLabelNet : CpnModel
    {
        public GuardDerivedLabelNet()
        {
            var owners   = AddPlace("Owners", Multiset.Of(new Owner("x")));
            var accounts = AddPlace("Accounts", Multiset.Of(new Account(new Owner("x"))));
            var p = new Var<Owner>("p");
            var a = new Var<Account>("a");
            T = AddTransition("MakeDeposit")
                .Input(owners, p)
                .Input(accounts, a)
                .Guard(() => p.Val == a.Val.Holder)   // no explicit label → derived
                .Build();
        }

        public Transition T { get; }
    }

    [Fact]
    public void Guard_label_is_derived_from_the_expression()
    {
        var net = new GuardDerivedLabelNet();
        net.T.GuardLabel.Should().Be("[p == a.Holder]");
    }

    private class GuardExplicitLabelNet : CpnModel
    {
        public GuardExplicitLabelNet()
        {
            var owners = AddPlace("Owners", Multiset.Of(new Owner("x")));
            var p = new Var<Owner>("p");
            T = AddTransition("T")
                .Input(owners, p)
                .Guard(() => p.Val.Name == "x", "[custom]")   // explicit label
                .Build();
        }

        public Transition T { get; }
    }

    [Fact]
    public void Explicit_guard_label_overload_is_used_verbatim()
    {
        var net = new GuardExplicitLabelNet();
        net.T.GuardLabel.Should().Be("[custom]");
    }
}
