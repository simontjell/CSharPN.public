using CSharPN.Core;
using FluentAssertions;
using Xunit;

// ─────────────────────────────────────────────────────────────────────────────
// Tests of the binding / enabling / occurrence semantics, organised after the
// sections of Jensen & Kristensen, "Coloured Petri Nets: Modelling and Validation
// of Concurrent Systems" (Springer 2009):
//
//   Chapter 2   Non-hierarchical CPN — the simple protocol, enabling, occurrence,
//               concurrency and conflict, guards (illustrated by example).
//   Chapter 4   Formal definition:
//               Def. 4.2  CPN = (P, T, A, Σ, V, C, G, E, I)
//               Def. 4.3  marking, Var(t), binding, binding element, step
//               Def. 4.4  enabling of a step
//               Def. 4.5  occurrence of a step
//   Chapter 10  Timed CPN — ready tokens, global clock, time delays.
//
// Where CPN Tools' binding rules go beyond the book (free variables of small
// colour sets, "variable cannot be bound", arc order) the CPN Tools behaviour is
// the reference (see SEMANTICS.md).
// ─────────────────────────────────────────────────────────────────────────────
namespace CSharPN.Core.Tests.Semantics;

// ── The simple protocol (Chapter 2, Fig. 2.1) ─────────────────────────────────
// The model lives in examples/ClassicExamples/SimpleProtocol.cs.

using Packet = SimpleProtocol.Packet;

internal static class TransitionExtensions
{
    /// <summary>The single enabled binding of <paramref name="t"/> satisfying <paramref name="pred"/>.</summary>
    public static BindingSnapshot Binding(this Transition t, Func<BindingSnapshot, bool> pred)
        => t.GetEnabledBindings().Single(pred);
}

// ── Section 2.2 / Definition 4.3: markings, Var(t), bindings, binding elements ─

public class Definition_4_3_Variables_and_bindings
{
    [Fact]
    public void Var_t_consists_of_the_variables_of_the_guard_and_of_all_arc_expressions()
    {
        // Def. 4.3 (3): Var(t) includes variables that occur only on output arcs.
        var net = new SimpleProtocol();
        net.TransmitPacket.VariableNames.Should().BeEquivalentTo(["p", "success"]);
        net.TransmitPacket.FreeVariableNames.Should().BeEquivalentTo(["success"]);
        net.ReceivePacket.VariableNames.Should().BeEquivalentTo(["q", "k", "data"]);
        net.ReceivePacket.FreeVariableNames.Should().BeEmpty();
    }

    [Fact]
    public void A_binding_maps_every_variable_of_the_transition_to_a_value()
    {
        // Def. 4.3 (4): b : Var(t) → values, b(v) ∈ Type[v].
        var net = new SimpleProtocol();
        net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());

        foreach (var b in net.TransmitPacket.GetEnabledBindings())
        {
            b.Values.Keys.Should().BeEquivalentTo(["p", "success"]);
            b.Values["p"].Should().BeOfType<Packet>();
            b.Values["success"].Should().BeOfType<bool>();
        }
    }

    [Fact]
    public void Binding_elements_are_pairs_of_transition_and_binding()
    {
        // Def. 4.3 (5): (t, b) with b ∈ B(t).
        var net = new SimpleProtocol();
        var b = net.SendPacket.GetEnabledBindings().Single();
        b.Transition.Should().BeSameAs(net.SendPacket);
        b.ToString().Should().Be("p=Packet { No = 1, Data = COL }");
    }

    [Fact]
    public void Bindings_are_functions_on_variables_not_on_token_instances()
    {
        // Two identical tokens give one binding, not two.
        var net = new SimpleProtocol();
        net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());
        net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());
        net.A.Marking.ShouldBe(Multiset.Repeat(new Packet(1, "COL"), 2));

        net.TransmitPacket.GetEnabledBindings().Should().HaveCount(2); // success = true / false only
    }
}

// ── Section 2.2 / Definition 4.4: enabling ────────────────────────────────────

public class Definition_4_4_Enabling
{
    [Fact]
    public void In_the_initial_marking_only_SendPacket_is_enabled_with_the_binding_n_1()
    {
        var net = new SimpleProtocol();

        var bindings = net.SendPacket.GetEnabledBindings();
        bindings.Should().ContainSingle();
        bindings[0].Values["p"].Should().Be(new Packet(1, "COL"));

        net.TransmitPacket.GetEnabledBindings().Should().BeEmpty();
        net.ReceivePacket.GetEnabledBindings().Should().BeEmpty();
        net.TransmitAck.GetEnabledBindings().Should().BeEmpty();
        net.ReceiveAck.GetEnabledBindings().Should().BeEmpty();
    }

    [Fact]
    public void Enabling_requires_that_every_input_place_holds_the_tokens_demanded_by_the_arc_expression()
    {
        // ∀p ∈ P: E(p,t)⟨b⟩ ≤ M(p). NextSend holds 1`1, so p must be packet 1 —
        // packets 2..6 are colour candidates for p but their demand on NextSend fails.
        var net = new SimpleProtocol();
        net.NextSend.Marking = Multiset.Of(3);
        var b = net.SendPacket.GetEnabledBindings().Single();
        b.Values["p"].Should().Be(new Packet(3, "ED "));
    }

    [Fact]
    public void The_guard_must_evaluate_to_true_in_the_binding()
    {
        // G(t)⟨b⟩ — Section 2.3 (guards) / Def. 4.4 (1).
        var net = new GuardedNet();
        var bindings = net.T.GetEnabledBindings();
        bindings.Select(b => (int)b.Values["x"]).Should().BeEquivalentTo([2, 4]);
    }

    [Fact]
    public void Free_variables_give_one_binding_element_per_value_of_their_colour_set()
    {
        // Section 2.2 (TransmitPacket): "success" occurs only on the output arc, so it can be
        // bound to an arbitrary value of BOOL: two binding elements TP+ and TP−.
        var net = new SimpleProtocol();
        net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());

        var bindings = net.TransmitPacket.GetEnabledBindings();
        bindings.Should().HaveCount(2);
        bindings.Select(b => (bool)b.Values["success"]).Should().BeEquivalentTo([true, false]);
        bindings.Should().OnlyContain(b => b.Values["p"].Equals(new Packet(1, "COL")));
    }

    [Fact]
    public void A_snapshot_can_be_revalidated_against_the_current_marking()
    {
        var net = new SimpleProtocol();
        var b = net.SendPacket.GetEnabledBindings().Single();
        net.SendPacket.IsEnabled(b).Should().BeTrue();

        net.NextSend.Marking = Multiset<int>.Empty;
        net.SendPacket.IsEnabled(b).Should().BeFalse();
        var act = () => net.SendPacket.Fire(b);
        act.Should().Throw<InvalidOperationException>().WithMessage("*not enabled*");
    }

    private sealed class GuardedNet : CpnModel
    {
        public readonly Transition T;
        public GuardedNet()
        {
            var p = AddPlace("P", Multiset.Of(1, 2, 3, 4, 5));
            var x = new Var<int>("x");
            T = AddTransition("T").Input(p, x).Guard(() => x.Val % 2 == 0).Build();
        }
    }
}

// ── Section 2.2 / Definition 4.5: occurrence ──────────────────────────────────

public class Definition_4_5_Occurrence
{
    [Fact]
    public void Occurrence_of_SendPacket_adds_a_token_to_A_and_leaves_PacketsToSend_and_NextSend_unchanged()
    {
        // Section 2.2: "The packet is not removed from PacketsToSend and the NextSend counter
        // is not changed" — because the same tokens are consumed and produced.
        var net = new SimpleProtocol();
        net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());

        net.PacketsToSend.Marking.ShouldBe(SimpleProtocol.AllPackets);
        net.NextSend.Marking.ShouldBe(Multiset.Of(1));
        net.A.Marking.ShouldBe(Multiset.Of(new Packet(1, "COL")));
    }

    [Fact]
    public void Occurrence_of_TransmitPacket_with_success_false_leads_back_to_the_initial_marking()
    {
        // Section 2.2: TP− removes the packet from A and adds nothing to B ("empty").
        var net = new SimpleProtocol();
        var m0 = net.GetState();
        net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());

        var tpMinus = net.TransmitPacket.Binding(b => (bool)b.Values["success"] == false);
        net.TransmitPacket.Fire(tpMinus);

        net.GetState().Should().Be(m0);
    }

    [Fact]
    public void Occurrence_of_TransmitPacket_with_success_true_moves_the_packet_from_A_to_B()
    {
        var net = new SimpleProtocol();
        net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());

        var tpPlus = net.TransmitPacket.Binding(b => (bool)b.Values["success"]);
        net.TransmitPacket.Fire(tpPlus);

        net.A.Marking.ShouldBe(Multiset<Packet>.Empty);
        net.B.Marking.ShouldBe(Multiset.Of(new Packet(1, "COL")));
    }

    [Fact]
    public void M2_equals_M1_minus_consumed_plus_produced_for_every_place()
    {
        // Def. 4.5: M2(p) = (M1(p) − E(p,t)⟨b⟩) + E(t,p)⟨b⟩, illustrated by ReceivePacket
        // which reads three places and writes three places with if-then-else expressions.
        var net = new SimpleProtocol();
        net.B.Marking = Multiset.Of(new Packet(1, "COL"));

        net.ReceivePacket.Fire(net.ReceivePacket.GetEnabledBindings().Single());

        net.B.Marking.ShouldBe(Multiset<Packet>.Empty);
        net.NextRec.Marking.ShouldBe(Multiset.Of(2));
        net.DataReceived.Marking.ShouldBe(Multiset.Of("COL"));
        net.C.Marking.ShouldBe(Multiset.Of(2));
    }

    [Fact]
    public void Receiving_a_packet_with_the_wrong_number_leaves_the_receiver_state_unchanged()
    {
        var net = new SimpleProtocol();
        net.B.Marking = Multiset.Of(new Packet(4, "PET"));

        net.ReceivePacket.Fire(net.ReceivePacket.GetEnabledBindings().Single());

        net.NextRec.Marking.ShouldBe(Multiset.Of(1));
        net.DataReceived.Marking.ShouldBe(Multiset.Of(""));
        net.C.Marking.ShouldBe(Multiset.Of(1));
    }

    [Fact]
    public void The_whole_protocol_delivers_all_data_when_every_transmission_succeeds()
    {
        var net = new SimpleProtocol();
        for (int i = 0; i < 6; i++)
        {
            net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());
            net.TransmitPacket.Fire(net.TransmitPacket.Binding(b => (bool)b.Values["success"]));
            net.ReceivePacket.Fire(net.ReceivePacket.GetEnabledBindings().Single());
            net.TransmitAck.Fire(net.TransmitAck.Binding(b => (bool)b.Values["success2"]));
            net.ReceiveAck.Fire(net.ReceiveAck.GetEnabledBindings().Single());
        }
        net.DataReceived.Marking.ShouldBe(Multiset.Of("COLOURED PETRI NET"));
        net.NextSend.Marking.ShouldBe(Multiset.Of(7));
        net.SendPacket.GetEnabledBindings().Should().BeEmpty(); // no packet 7
    }

    [Fact]
    public void Occurrence_is_atomic_when_an_output_expression_throws()
    {
        var net = new ThrowingOutputNet();
        var before = net.GetState();
        var act = () => net.T.Fire(net.T.GetEnabledBindings().Single());
        act.Should().Throw<InvalidOperationException>().WithMessage("boom");
        net.GetState().Should().Be(before);
    }

    private sealed class ThrowingOutputNet : CpnModel
    {
        public readonly Transition T;
        public ThrowingOutputNet()
        {
            var p = AddPlace("P", Multiset.Of(1));
            var q = AddPlace<int>("Q");
            var x = new Var<int>("x");
            T = AddTransition("T").Input(p, x)
                .Output(q, () => Boom(), "boom")
                .Build();
        }

        private static int Boom() => throw new InvalidOperationException("boom");
    }
}

// ── Section 2.2 (concurrency and conflict) / Definitions 4.3 (6), 4.4, 4.5: steps ─

public class Definitions_4_4_and_4_5_Steps
{
    [Fact]
    public void SendPacket_and_TransmitPacket_are_concurrently_enabled_in_M1()
    {
        // Section 2.2: in M1 the step 1`SP ++ 1`TP+ is enabled: both binding elements can
        // get the tokens they need without interfering with each other.
        var net = new SimpleProtocol();
        net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());

        var sp     = net.SendPacket.GetEnabledBindings().Single();
        var tpPlus = net.TransmitPacket.Binding(b => (bool)b.Values["success"]);

        net.IsEnabled(sp, tpPlus).Should().BeTrue();
    }

    [Fact]
    public void Two_bindings_of_TransmitPacket_are_in_conflict_over_the_single_token_on_A()
    {
        var net = new SimpleProtocol();
        net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());

        var tpPlus  = net.TransmitPacket.Binding(b => (bool)b.Values["success"]);
        var tpMinus = net.TransmitPacket.Binding(b => !(bool)b.Values["success"]);

        net.IsEnabled(tpPlus).Should().BeTrue();
        net.IsEnabled(tpMinus).Should().BeTrue();
        net.IsEnabled(tpPlus, tpMinus).Should().BeFalse();   // Σ E(A,t)⟨b⟩ = 2`(1,"COL") > M(A)
    }

    [Fact]
    public void A_binding_element_is_concurrently_enabled_with_itself_when_there_are_enough_tokens()
    {
        // Def. 4.3 (6): a step is a multiset, so the same binding element may occur twice.
        var net = new SimpleProtocol();
        net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());
        var tpPlus = net.TransmitPacket.Binding(b => (bool)b.Values["success"]);

        net.IsEnabled(tpPlus, tpPlus).Should().BeFalse();    // one token on A

        net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());
        net.IsEnabled(tpPlus, tpPlus).Should().BeTrue();     // two tokens on A
    }

    [Fact]
    public void Occurrence_of_a_step_has_the_summed_effect_of_its_binding_elements()
    {
        // Def. 4.5: M2(p) = (M1(p) − Σ E(p,t)⟨b⟩) + Σ E(t,p)⟨b⟩ — the marking M2 of
        // Section 2.2 reached from M1 by the step 1`SP ++ 1`TP+.
        var net = new SimpleProtocol();
        net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());
        var sp     = net.SendPacket.GetEnabledBindings().Single();
        var tpPlus = net.TransmitPacket.Binding(b => (bool)b.Values["success"]);

        net.Occur(sp, tpPlus);

        net.A.Marking.ShouldBe(Multiset.Of(new Packet(1, "COL")));  // removed by TP+, re-added by SP
        net.B.Marking.ShouldBe(Multiset.Of(new Packet(1, "COL")));
        net.NextSend.Marking.ShouldBe(Multiset.Of(1));
    }

    [Fact]
    public void Occurrence_of_a_step_equals_sequential_occurrence_of_its_elements()
    {
        var stepNet = new SimpleProtocol();
        var seqNet  = new SimpleProtocol();
        foreach (var net in new[] { stepNet, seqNet })
            net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());

        stepNet.Occur(
            stepNet.SendPacket.GetEnabledBindings().Single(),
            stepNet.TransmitPacket.Binding(b => (bool)b.Values["success"]));

        seqNet.TransmitPacket.Fire(seqNet.TransmitPacket.Binding(b => (bool)b.Values["success"]));
        seqNet.SendPacket.Fire(seqNet.SendPacket.GetEnabledBindings().Single());

        stepNet.GetState().Should().Be(seqNet.GetState());
    }

    [Fact]
    public void A_step_that_is_not_enabled_cannot_occur_and_leaves_the_marking_untouched()
    {
        var net = new SimpleProtocol();
        net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());
        var tpPlus  = net.TransmitPacket.Binding(b => (bool)b.Values["success"]);
        var tpMinus = net.TransmitPacket.Binding(b => !(bool)b.Values["success"]);
        var before  = net.GetState();

        var act = () => net.Occur(tpPlus, tpMinus);

        act.Should().Throw<InvalidOperationException>();
        net.GetState().Should().Be(before);
    }

    [Fact]
    public void The_empty_step_is_not_a_step()
    {
        var net = new SimpleProtocol();
        var act = () => net.IsEnabled(Array.Empty<BindingSnapshot>());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Guards_are_checked_for_every_binding_element_of_a_step()
    {
        var net = new GuardStepNet();
        var b = net.T.GetEnabledBindings().Single();
        net.IsEnabled(b, b).Should().BeTrue();
        net.Flag = false;                      // guard now false — the step is no longer enabled
        net.IsEnabled(b, b).Should().BeFalse();
    }

    private sealed class GuardStepNet : CpnModel
    {
        public readonly Transition T;
        public bool Flag = true;
        public GuardStepNet()
        {
            var p = AddPlace("P", Multiset.Repeat(1, 2));
            var x = new Var<int>("x");
            T = AddTransition("T").Input(p, x).Guard(() => Flag, "[Flag]").Build();
        }
    }
}

// ── Definition 4.2 (8): arc expressions; E(p,t) sums parallel arcs (CPN Tools) ─

public class Arc_expressions_and_parallel_arcs
{
    [Fact]
    public void Two_input_arcs_from_the_same_place_demand_the_sum_of_their_expressions()
    {
        // E(p,t) = 1`x ++ 1`y. With M(P) = 2`7 the binding x=7, y=7 is enabled ...
        var net = new TwoArcsNet(Multiset.Repeat(7, 2));
        var b = net.T.GetEnabledBindings().Single();
        b.Values["x"].Should().Be(7);
        b.Values["y"].Should().Be(7);

        // ... with M(P) = 1`7 it is not (2`7 ≰ 1`7).
        new TwoArcsNet(Multiset.Of(7)).T.GetEnabledBindings().Should().BeEmpty();
    }

    [Fact]
    public void Firing_removes_the_summed_demand_from_the_place()
    {
        var net = new TwoArcsNet(Multiset.Of(7, 7, 8));
        net.T.Fire(net.T.GetEnabledBindings().Single(b => (int)b.Values["x"] == 7 && (int)b.Values["y"] == 7));
        net.P.Marking.ShouldBe(Multiset.Of(8));
    }

    [Fact]
    public void An_expression_arc_and_a_pattern_arc_on_the_same_place_are_summed()
    {
        // E(P,t) = 1`x ++ 1`(x+1): x=1 needs {1,2}, x=2 needs {2,3}; only x=1 fits {1,2,4}.
        var net = new MixedArcsNet(Multiset.Of(1, 2, 4));
        net.T.GetEnabledBindings().Select(b => (int)b.Values["x"]).Should().BeEquivalentTo([1]);
    }

    [Fact]
    public void An_arc_expression_evaluating_to_the_empty_multiset_demands_nothing()
    {
        var net = new EmptyDemandNet();
        net.T.GetEnabledBindings().Should().ContainSingle();
    }

    [Fact]
    public void Multiplicity_on_an_input_arc_demands_that_many_copies_of_the_bound_colour()
    {
        // 2`x
        var net = new CountNet(Multiset.Of(1, 1, 2));
        net.T.GetEnabledBindings().Select(b => (int)b.Values["x"]).Should().BeEquivalentTo([1]);
        net.T.Fire(net.T.GetEnabledBindings().Single());
        net.P.Marking.ShouldBe(Multiset.Of(2));
    }

    private sealed class TwoArcsNet : CpnModel
    {
        public readonly Place<int> P;
        public readonly Transition T;
        public TwoArcsNet(Multiset<int> initial)
        {
            P = AddPlace("P", initial);
            var x = new Var<int>("x");
            var y = new Var<int>("y");
            T = AddTransition("T").Input(P, x).Input(P, y).Build();
        }
    }

    private sealed class MixedArcsNet : CpnModel
    {
        public readonly Transition T;
        public MixedArcsNet(Multiset<int> initial)
        {
            var p = AddPlace("P", initial);
            var x = new Var<int>("x");
            T = AddTransition("T").Input(p, x).Input(p, () => x.Val + 1).Build();
        }
    }

    private sealed class EmptyDemandNet : CpnModel
    {
        public readonly Transition T;
        public EmptyDemandNet()
        {
            var p = AddPlace<int>("P");
            T = AddTransition("T").Input(p, () => Multiset.Empty<int>()).Build();
        }
    }

    private sealed class CountNet : CpnModel
    {
        public readonly Place<int> P;
        public readonly Transition T;
        public CountNet(Multiset<int> initial)
        {
            P = AddPlace("P", initial);
            var x = new Var<int>("x");
            T = AddTransition("T").Input(P, x, count: 2).Build();
        }
    }
}

// ── Binding rules of CPN Tools: unification, arc order, bindable variables ────

public class CPN_Tools_binding_rules
{
    [Fact]
    public void A_variable_occurring_on_several_input_arcs_is_bound_to_the_same_value_on_all_of_them()
    {
        // Unification: x on P and Q → only common colours.
        var net = new UnifyNet(Multiset.Of(1, 2, 3), Multiset.Of(2, 3, 4));
        net.T.GetEnabledBindings().Select(b => (int)b.Values["x"]).Should().BeEquivalentTo([2, 3]);
    }

    [Fact]
    public void Match_in_the_Simple_example_is_enabled_only_for_the_value_both_places_hold()
    {
        // examples/ClassicExamples/Simple.cs: x on the arcs from Input {1,2,3} and Constants {1,5,10}.
        var net = new Simple();
        var match = net.Transitions.Single(t => t.Name == "Match");
        var bindings = match.GetEnabledBindings();
        bindings.Should().ContainSingle().Which.Values["x"].Should().Be(1);

        match.Fire(bindings[0]);
        net.Input.Marking.ShouldBe(Multiset.Of(2, 3));
        net.Constants.Marking.ShouldBe(Multiset.Of(1, 5, 10));          // the constant is put back
        net.Results.Marking.ShouldBe(Multiset.Of(new Simple.Result(1, 1)));
    }

    [Fact]
    public void Unification_respects_the_multiplicity_demanded_on_each_arc()
    {
        // x on P (1`x) and on Q (2`x)
        var net = new UnifyCountNet(Multiset.Of(1, 2), Multiset.Of(1, 2, 2));
        net.T.GetEnabledBindings().Select(b => (int)b.Values["x"]).Should().BeEquivalentTo([2]);
    }

    [Fact]
    public void The_order_in_which_arcs_are_declared_does_not_matter()
    {
        // The same transition built with the arcs in every order yields the same bindings,
        // even when an expression arc or the guard uses a variable bound by a later arc.
        var forwards  = new OrderNet(exprFirst: false);
        var backwards = new OrderNet(exprFirst: true);

        var expected = new[] { (1, 2), (2, 4) };
        forwards .T.GetEnabledBindings().Select(b => ((int)b.Values["x"], (int)b.Values["y"])).Should().BeEquivalentTo(expected);
        backwards.T.GetEnabledBindings().Select(b => ((int)b.Values["x"], (int)b.Values["y"])).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void The_guard_may_use_a_variable_bound_by_any_arc_regardless_of_order()
    {
        var net = new GuardBeforeArcNet();
        net.T.GetEnabledBindings().Select(b => (int)b.Values["x"]).Should().BeEquivalentTo([3]);
    }

    [Fact]
    public void Free_variable_of_an_enumeration_colour_set_is_bound_to_every_value()
    {
        var net = new EnumFreeNet();
        net.T.GetEnabledBindings().Select(b => (Coin)b.Values["c"]).Should().BeEquivalentTo([Coin.Heads, Coin.Tails]);
        net.T.Fire(net.T.GetEnabledBindings().Single(b => (Coin)b.Values["c"] == Coin.Tails));
        net.Out.Marking.ShouldBe(Multiset.Of("Tails"));
    }

    [Fact]
    public void Free_variable_with_an_explicit_domain_is_bound_to_every_value_of_the_domain()
    {
        // CPN Tools: "r ∈ 1..10" on an output arc gives ten binding elements per input binding.
        var net = new RangeFreeNet();
        net.T.GetEnabledBindings().Select(b => (int)b.Values["r"]).Should().BeEquivalentTo(Enumerable.Range(1, 10));
    }

    [Fact]
    public void Free_variable_only_in_the_guard_is_bound_from_its_domain_and_filtered_by_the_guard()
    {
        var net = new GuardOnlyFreeNet();
        net.T.GetEnabledBindings().Select(b => (int)b.Values["r"]).Should().BeEquivalentTo([2, 4, 6, 8, 10]);
    }

    [Fact]
    public void Free_variable_of_a_large_colour_set_cannot_be_bound_and_is_rejected_when_the_model_is_built()
    {
        // CPN Tools reports "variable cannot be bound" for a free variable of a large colour set.
        var act = () => new UnbindableNet();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'r'*cannot be bound*");
    }

    [Fact]
    public void Variable_used_only_inside_a_plain_lambda_must_be_declared_with_Free()
    {
        // A Func<T> lambda cannot be inspected, so the framework only learns about the
        // variable when it is read. The error names the transition and the remedy.
        var net = new UndeclaredFuncNet();
        var act = () => net.T.Fire(net.T.GetEnabledBindings().Single());
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Transition 'T'*'b'*.Free(*");
    }

    [Fact]
    public void Declaring_a_variable_with_Free_makes_it_part_of_Var_t()
    {
        var net = new DeclaredFuncNet();
        net.T.FreeVariableNames.Should().BeEquivalentTo(["b"]);
        net.T.GetEnabledBindings().Should().HaveCount(2);
    }

    [Fact]
    public void Free_declaration_of_a_variable_that_an_input_arc_binds_is_harmless()
    {
        var net = new RedundantFreeNet();
        net.T.FreeVariableNames.Should().BeEmpty();
        net.T.GetEnabledBindings().Should().ContainSingle();
    }

    [Fact]
    public void Variables_are_unbound_outside_enumeration_and_firing()
    {
        var x = new Var<int>("x");
        var s = new Var<bool>("s");
        var net = new ExposedVarsNet(x, s);
        _ = net.T.GetEnabledBindings();
        x.IsBound.Should().BeFalse();
        s.IsBound.Should().BeFalse();
        net.T.Fire(net.T.GetEnabledBindings().First());
        x.IsBound.Should().BeFalse();
        s.IsBound.Should().BeFalse();
        var act = () => x.Val;
        act.Should().Throw<UnboundVariableException>();
    }

    public enum Coin { Heads, Tails }

    private sealed class UnifyNet : CpnModel
    {
        public readonly Transition T;
        public UnifyNet(Multiset<int> p, Multiset<int> q)
        {
            var P = AddPlace("P", p); var Q = AddPlace("Q", q);
            var x = new Var<int>("x");
            T = AddTransition("T").Input(P, x).Input(Q, x).Build();
        }
    }

    private sealed class UnifyCountNet : CpnModel
    {
        public readonly Transition T;
        public UnifyCountNet(Multiset<int> p, Multiset<int> q)
        {
            var P = AddPlace("P", p); var Q = AddPlace("Q", q);
            var x = new Var<int>("x");
            T = AddTransition("T").Input(P, x).Input(Q, x, count: 2).Build();
        }
    }

    private sealed class OrderNet : CpnModel
    {
        public readonly Transition T;
        public OrderNet(bool exprFirst)
        {
            var P = AddPlace("P", Multiset.Of(1, 2, 3));
            var Q = AddPlace("Q", Multiset.Of(2, 4, 5));
            var R = AddPlace("R", Multiset.Of(2, 4));
            var x = new Var<int>("x");
            var y = new Var<int>("y");
            var b = AddTransition("T");
            if (exprFirst)
                b.Guard(() => y.Val == 2 * x.Val).Input(R, () => y.Val).Input(Q, y).Input(P, x);
            else
                b.Input(P, x).Input(Q, y).Input(R, () => y.Val).Guard(() => y.Val == 2 * x.Val);
            T = b.Build();
        }
    }

    private sealed class GuardBeforeArcNet : CpnModel
    {
        public readonly Transition T;
        public GuardBeforeArcNet()
        {
            var P = AddPlace("P", Multiset.Of(1, 2, 3));
            var x = new Var<int>("x");
            T = AddTransition("T").Guard(() => x.Val == 3).Input(P, x).Build();
        }
    }

    private sealed class EnumFreeNet : CpnModel
    {
        public readonly Place<string> Out;
        public readonly Transition T;
        public EnumFreeNet()
        {
            Out = AddPlace<string>("Out");
            var c = new Var<Coin>("c");
            T = AddTransition("T").Output(Out, () => c.Val.ToString()).Build();
        }
    }

    private sealed class RangeFreeNet : CpnModel
    {
        public readonly Transition T;
        public RangeFreeNet()
        {
            var P   = AddPlace("P", Multiset.Of(0));
            var Out = AddPlace<int>("Out");
            var x   = new Var<int>("x");
            var r   = new Var<int>("r", Enumerable.Range(1, 10));
            T = AddTransition("T").Input(P, x).Output(Out, () => x.Val + r.Val).Build();
        }
    }

    private sealed class GuardOnlyFreeNet : CpnModel
    {
        public readonly Transition T;
        public GuardOnlyFreeNet()
        {
            var r = new Var<int>("r", Enumerable.Range(1, 10));
            T = AddTransition("T").Guard(() => r.Val % 2 == 0).Build();
        }
    }

    private sealed class UnbindableNet : CpnModel
    {
        public UnbindableNet()
        {
            var Out = AddPlace<int>("Out");
            var r = new Var<int>("r");   // int has no default domain
            AddTransition("T").Output(Out, () => r.Val).Build();
        }
    }

    private sealed class UndeclaredFuncNet : CpnModel
    {
        public readonly Transition T;
        public UndeclaredFuncNet()
        {
            var P   = AddPlace("P", Multiset.Of(1));
            var Out = AddPlace<bool>("Out");
            var x   = new Var<int>("x");
            var b   = new Var<bool>("b");
            T = AddTransition("T").Input(P, x).Output(Out, () => b.Val, "b").Build();
        }
    }

    private sealed class DeclaredFuncNet : CpnModel
    {
        public readonly Transition T;
        public DeclaredFuncNet()
        {
            var P   = AddPlace("P", Multiset.Of(1));
            var Out = AddPlace<bool>("Out");
            var x   = new Var<int>("x");
            var b   = new Var<bool>("b");
            T = AddTransition("T").Input(P, x).Output(Out, () => b.Val, "b").Free(b).Build();
        }
    }

    private sealed class RedundantFreeNet : CpnModel
    {
        public readonly Transition T;
        public RedundantFreeNet()
        {
            var P = AddPlace("P", Multiset.Of(true));
            var b = new Var<bool>("b");
            T = AddTransition("T").Input(P, b).Free(b).Build();
        }
    }

    private sealed class ExposedVarsNet : CpnModel
    {
        public readonly Transition T;
        public ExposedVarsNet(Var<int> x, Var<bool> s)
        {
            var P   = AddPlace("P", Multiset.Of(1));
            var Out = AddPlace<int>("Out");
            T = AddTransition("T").Input(P, x).Output(Out, () => s.Val ? x.Val : -x.Val).Build();
        }
    }
}

// ── Chapter 10: timed CPN ─────────────────────────────────────────────────────

public class Chapter_10_Timed_CPN
{
    /// <summary>The timed simple protocol of Section 10.1 (sender side only).</summary>
    private sealed class TimedProtocol : TimedCpnModel
    {
        public const int Wait = 100;
        public readonly Place<Timed<Packet>> PacketsToSend, A;
        public readonly Place<int> NextSend;
        public readonly Transition SendPacket;

        public TimedProtocol()
        {
            PacketsToSend = AddTimedPlace("PacketsToSend",
                Multiset.Of(SimpleProtocol.AllPackets.Select(p => Timed<Packet>.At(p, 0))));
            NextSend = AddPlace("NextSend", Multiset.Of(1));
            A        = AddTimedPlace<Packet>("A");

            var p = new Var<Packet>("p");
            SendPacket = AddTransition("SendPacket")
                .TimedInput(PacketsToSend, p)
                .Input(NextSend, () => p.Val.No)
                .Delay(9)                                        // @+9 on the transition
                .TimedOutput(PacketsToSend, () => p.Val, Wait)   // (n,d) @+Wait
                .TimedOutput(A, () => p.Val, 0)
                .Output(NextSend, () => p.Val.No)
                .Build();
        }
    }

    [Fact]
    public void Produced_tokens_get_time_stamp_clock_plus_transition_delay_plus_arc_delay()
    {
        // Section 10.1: the token on A gets 0 + 9 + 0 = 9, the one on PacketsToSend 0 + 9 + 100 = 109.
        var net = new TimedProtocol();
        net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());

        net.A.Marking.ShouldBe(Multiset.Of(Timed<Packet>.At(new Packet(1, "COL"), 9)));
        net.PacketsToSend.Marking.Count(Timed<Packet>.At(new Packet(1, "COL"), 109)).Should().Be(1);
        net.PacketsToSend.Marking.Count(Timed<Packet>.At(new Packet(1, "COL"), 0)).Should().Be(0);
    }

    [Fact]
    public void A_binding_element_must_be_colour_enabled_and_ready()
    {
        // After SendPacket, packet 1 has time stamp 109: colour enabled but not ready until 109.
        var net = new TimedProtocol();
        net.SendPacket.Fire(net.SendPacket.GetEnabledBindings().Single());
        net.SendPacket.GetEnabledBindings().Should().BeEmpty();

        var sim = new TimedCpnSimulator(net);
        sim.AdvanceClock().Should().BeTrue();
        sim.GlobalClock.Should().Be(new CpnTime(109));
        net.SendPacket.GetEnabledBindings().Should().ContainSingle();
    }

    [Fact]
    public void The_clock_advances_to_the_earliest_time_at_which_a_binding_element_is_enabled()
    {
        var net = new ReadyNet(Timed<int>.At(1, 10), Timed<int>.At(2, 5));
        var sim = new TimedCpnSimulator(net);
        net.T.GetEnabledBindings().Should().BeEmpty();

        sim.AdvanceClock();

        sim.GlobalClock.Should().Be(new CpnTime(5));
        net.T.GetEnabledBindings().Single().Values["x"].Should().Be(2);
    }

    [Fact]
    public void Binding_elements_are_distinguished_by_colour_not_by_time_stamp()
    {
        var net = new ReadyNet(Timed<int>.At(5, 0), Timed<int>.At(5, 1), Timed<int>.At(7, 0));
        new TimedCpnSimulator(net).AdvanceClock();
        net.T.GetEnabledBindings().Select(b => (int)b.Values["x"]).Should().BeEquivalentTo([5, 7]);
    }

    [Fact]
    public void Occurrence_removes_the_ready_token_with_the_smallest_time_stamp()
    {
        var net = new ReadyNet(Timed<int>.At(5, 3), Timed<int>.At(5, 0), Timed<int>.At(5, 10));
        net.SetClockForTest(new CpnTime(3));

        net.T.Fire(net.T.GetEnabledBindings().Single());

        net.P.Marking.ShouldBe(Multiset.Of(Timed<int>.At(5, 3), Timed<int>.At(5, 10)));
        net.Out.Marking.ShouldBe(Multiset.Of(5));
    }

    [Fact]
    public void Multiplicity_on_a_timed_arc_needs_that_many_ready_tokens_of_the_colour()
    {
        var net = new CountReadyNet(Timed<int>.At(5, 0), Timed<int>.At(5, 8));
        net.T.GetEnabledBindings().Should().BeEmpty();               // only one ready at time 0
        net.SetClockForTest(new CpnTime(8));
        net.T.GetEnabledBindings().Should().ContainSingle();
        net.T.Fire(net.T.GetEnabledBindings().Single());
        net.P.Marking.ShouldBe(Multiset<Timed<int>>.Empty);
    }

    [Fact]
    public void Binding_elements_at_the_same_time_may_be_concurrently_enabled()
    {
        var net = new ReadyNet(Timed<int>.At(5, 0), Timed<int>.At(5, 0));
        var b = net.T.GetEnabledBindings().Single();
        net.IsEnabled(b, b).Should().BeTrue();
        net.Occur(b, b);
        net.P.Marking.ShouldBe(Multiset<Timed<int>>.Empty);
        net.Out.Marking.ShouldBe(Multiset.Repeat(5, 2));
    }

    [Fact]
    public void Two_binding_elements_cannot_share_a_single_ready_token()
    {
        var net = new ReadyNet(Timed<int>.At(5, 0), Timed<int>.At(5, 9));
        var b = net.T.GetEnabledBindings().Single();
        net.IsEnabled(b, b).Should().BeFalse();
    }

    [Fact]
    public void Untimed_arcs_on_a_timed_place_also_respect_readiness()
    {
        // A CPN Tools user cannot bypass time by binding the whole timed token.
        var net = new WholeTokenNet(Timed<int>.At(1, 0), Timed<int>.At(2, 7));
        net.T.GetEnabledBindings().Select(b => ((Timed<int>)b.Values["t"]).Value).Should().BeEquivalentTo([1]);
        net.U.GetEnabledBindings().Should().BeEmpty();      // demands 2@7 which is not ready
        net.SetClockForTest(new CpnTime(7));
        net.U.GetEnabledBindings().Should().ContainSingle();
    }

    [Fact]
    public void A_variable_may_be_unified_across_a_timed_and_an_untimed_arc()
    {
        var net = new UnifyTimedNet();
        net.T.GetEnabledBindings().Select(b => (int)b.Values["x"]).Should().BeEquivalentTo([2]);
    }

    [Fact]
    public void Negative_time_delays_are_rejected()
    {
        var net = new NegativeDelayNet();
        var act = () => net.T.Fire(net.T.GetEnabledBindings().Single());
        act.Should().Throw<InvalidOperationException>().WithMessage("*Negative time delay*");
    }

    private abstract class TestTimedModel : TimedCpnModel
    {
        public void SetClockForTest(CpnTime t) => SetClock(t);
    }

    private sealed class ReadyNet : TestTimedModel
    {
        public readonly Place<Timed<int>> P;
        public readonly Place<int> Out;
        public readonly Transition T;
        public ReadyNet(params Timed<int>[] tokens)
        {
            P   = AddTimedPlace("P", Multiset.Of(tokens));
            Out = AddPlace<int>("Out");
            var x = new Var<int>("x");
            T = AddTransition("T").TimedInput(P, x).Output(Out, x).Build();
        }
    }

    private sealed class CountReadyNet : TestTimedModel
    {
        public readonly Place<Timed<int>> P;
        public readonly Transition T;
        public CountReadyNet(params Timed<int>[] tokens)
        {
            P = AddTimedPlace("P", Multiset.Of(tokens));
            var x = new Var<int>("x");
            T = AddTransition("T").TimedInput(P, x, count: 2).Build();
        }
    }

    private sealed class WholeTokenNet : TestTimedModel
    {
        public readonly Transition T, U;
        public WholeTokenNet(params Timed<int>[] tokens)
        {
            var P = AddTimedPlace("P", Multiset.Of(tokens));
            var t = new Var<Timed<int>>("t");
            T = AddTransition("T").Input(P, t).Build();
            U = AddTransition("U").Input(P, () => Timed<int>.At(2, 7)).Build();
        }
    }

    private sealed class UnifyTimedNet : TestTimedModel
    {
        public readonly Transition T;
        public UnifyTimedNet()
        {
            var P = AddTimedPlace("P", Multiset.Of(Timed<int>.At(1, 0), Timed<int>.At(2, 0), Timed<int>.At(3, 5)));
            var Q = AddPlace("Q", Multiset.Of(2, 3));
            var x = new Var<int>("x");
            T = AddTransition("T").Input(Q, x).TimedInput(P, x).Build();
        }
    }

    private sealed class NegativeDelayNet : TestTimedModel
    {
        public readonly Transition T;
        public NegativeDelayNet()
        {
            var P = AddTimedPlace("P", Multiset.Of(Timed<int>.At(1, 0)));
            var x = new Var<int>("x");
            T = AddTransition("T").TimedInput(P, x).TimedOutput(P, () => x.Val, -1).Build();
        }
    }
}

// ── Assertion helper ──────────────────────────────────────────────────────────

internal static class MultisetAssertions
{
    /// <summary>Multiset equality (FluentAssertions would otherwise compare the flat enumeration).</summary>
    public static void ShouldBe<T>(this Multiset<T> actual, Multiset<T> expected) where T : notnull
        => actual.Equals(expected).Should().BeTrue($"expected marking {expected} but found {actual}");
}
