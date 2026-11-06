using CSharPN.Core;
using FluentAssertions;
using Xunit;

namespace CSharPN.Core.Tests;

/// <summary>
/// Arc inscriptions derived from expression trees are shown in CPN ML multiset notation, as
/// markings are: <c>1`a++2`b</c> for a multiset, <c>∅</c> for the empty one. A single element
/// stands alone (<c>k</c> means <c>1`k</c>), as in CPN Tools.
/// </summary>
public class InscriptionLabelTests
{
    public sealed record Phil(int Id);

    private sealed class Net : CpnModel
    {
        public readonly Transition T;
        public Net(Action<TransitionBuilder, Place<int>, Var<Phil>, Var<bool>, Var<int>> outputs) : base("Net")
        {
            var phils = AddPlace("Phils", Multiset.Of(new Phil(1)));
            var n = AddPlace("N", Multiset.Of(1));
            var target = AddPlace<int>("Target");
            var p = new Var<Phil>();
            var ok = new Var<bool>();
            var k = new Var<int>();
            var b = AddTransition("T").Input(phils, p).Input(n, k);
            outputs(b, target, p, ok, k);
            T = b.Build();
        }
    }

    private static string Label(Action<TransitionBuilder, Place<int>, Var<Phil>, Var<bool>, Var<int>> outputs)
        => new Net(outputs).T.GetArcViews().Single(a => a.Direction == ArcDirection.Output).Inscription;

    [Fact]
    public void Multiset_Of_is_shown_as_a_sum_of_its_elements()
        => Label((b, t, p, ok, k) => b.Output(t, () => Multiset.Of(p.Val.Id, (p.Val.Id % 3) + 1)))
            .Should().Be("1`p.Id++1`(p.Id % 3 + 1)");

    [Fact]
    public void A_single_element_is_shown_alone()
        => Label((b, t, p, ok, k) => b.Output(t, () => Multiset.Of(k.Val)))
            .Should().Be("k");

    [Fact]
    public void Repeated_elements_get_a_count()
        => Label((b, t, p, ok, k) => b.Output(t, () => Multiset.Of(k.Val, 1, k.Val)))
            .Should().Be("2`k++1`1");

    [Fact]
    public void Repeat_is_shown_with_a_count()
        => Label((b, t, p, ok, k) => b.Output(t, () => Multiset.Repeat(k.Val + 1, 3)))
            .Should().Be("3`(k + 1)");

    [Fact]
    public void A_conditional_with_the_empty_multiset_is_shown_with_the_empty_set_symbol()
        => Label((b, t, p, ok, k) => b.Output(t, () => ok.Val ? Multiset.Of(k.Val) : Multiset.Empty<int>()))
            .Should().Be("ok ? k : ∅");

    [Fact]
    public void Multiset_operators_are_shown_as_CPN_ML_operators()
        => Label((b, t, p, ok, k) => b.Output(t, () => Multiset.Of(k.Val) + 2 * Multiset.Of(1) - Multiset.Of(k.Val + 1)))
            .Should().Be("k++2**1--(k + 1)");

    [Fact]
    public void Constants_are_shown_as_CPN_ML_values()
        => Label((b, t, p, ok, k) => b.Output(t, () => ok.Val == true && p.Val.Id.ToString() != "" ? k.Val : 0))
            .Should().Be("ok == true && p.Id.ToString() != \"\" ? k : 0");

    [Fact]
    public void Parentheses_follow_operator_precedence()
        => Label((b, t, p, ok, k) => b.Output(t, () => (k.Val + 1) * 2 - (k.Val - 1)))
            .Should().Be("(k + 1) * 2 - (k - 1)");

    [Fact]
    public void Redundant_parentheses_are_dropped()
        => Label((b, t, p, ok, k) => b.Output(t, () => k.Val + (k.Val * 2)))
            .Should().Be("k + k * 2");
}
