using CSharPN.Core;
using FluentAssertions;
using Xunit;

namespace CSharPN.Core.Tests;

public class MultisetTests
{
    // ── Factory ───────────────────────────────────────────────────────────────

    [Fact]
    public void Of_creates_correct_multiplicities()
    {
        var m = Multiset.Of(1, 2, 2, 3);
        m.Count(1).Should().Be(1);
        m.Count(2).Should().Be(2);
        m.Count(3).Should().Be(1);
        m.TotalCount.Should().Be(4);
    }

    [Fact]
    public void Empty_has_zero_total_count()
    {
        Multiset<int>.Empty.TotalCount.Should().Be(0);
        Multiset<int>.Empty.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Repeat_creates_n_copies()
    {
        var m = Multiset.Repeat("x", 5);
        m.Count("x").Should().Be(5);
        m.TotalCount.Should().Be(5);
    }

    [Fact]
    public void Repeat_zero_returns_empty()
    {
        (Multiset.Repeat(42, 0) == Multiset<int>.Empty).Should().BeTrue();
    }

    // ── Add / Remove ──────────────────────────────────────────────────────────

    [Fact]
    public void Add_increases_count()
    {
        var m = Multiset.Of(1, 2).Add(2);
        m.Count(2).Should().Be(2);
    }

    [Fact]
    public void Remove_decreases_count()
    {
        var m = Multiset.Of(1, 2, 2).Remove(2);
        m.Count(2).Should().Be(1);
    }

    [Fact]
    public void Remove_last_copy_eliminates_item()
    {
        var m = Multiset.Of(1).Remove(1);
        m.Count(1).Should().Be(0);
        m.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Remove_more_than_available_throws()
    {
        var m = Multiset.Of(1);
        var act = () => m.Remove(1, 2);
        act.Should().Throw<InvalidOperationException>();
    }

    // ── Operators ─────────────────────────────────────────────────────────────

    [Fact]
    public void Plus_operator_unions_multisets()
    {
        var a = Multiset.Of(1, 2);
        var b = Multiset.Of(2, 3);
        var result = a + b;
        result.Count(1).Should().Be(1);
        result.Count(2).Should().Be(2);
        result.Count(3).Should().Be(1);
    }

    [Fact]
    public void Minus_operator_removes_tokens()
    {
        var a = Multiset.Of(1, 2, 2, 3);
        var b = Multiset.Of(2);
        var result = a - b;
        result.Count(2).Should().Be(1);
        result.Count(1).Should().Be(1);
        result.Count(3).Should().Be(1);
    }

    [Fact]
    public void Minus_operator_throws_on_underflow()
    {
        var a = Multiset.Of(1);
        var b = Multiset.Of(1, 1);
        var act = () => _ = a - b;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Scalar_multiply_scales_all_counts()
    {
        var m = Multiset.Of(1, 2, 2);
        var result = 3 * m;
        result.Count(1).Should().Be(3);
        result.Count(2).Should().Be(6);
    }

    [Fact]
    public void Scalar_multiply_by_zero_returns_empty()
    {
        (0 * Multiset.Of(1, 2) == Multiset<int>.Empty).Should().BeTrue();
    }

    [Fact]
    public void Subset_operator_true_when_subset()
    {
        var a = Multiset.Of(1, 2);
        var b = Multiset.Of(1, 2, 3);
        (a <= b).Should().BeTrue();
    }

    [Fact]
    public void Subset_operator_false_when_not_subset()
    {
        var a = Multiset.Of(1, 2, 2);
        var b = Multiset.Of(1, 2);
        (a <= b).Should().BeFalse();
    }

    [Fact]
    public void Empty_is_subset_of_everything()
    {
        (Multiset<int>.Empty <= Multiset.Of(1)).Should().BeTrue();
    }

    // ── Equality and hashing ──────────────────────────────────────────────────

    [Fact]
    public void Equality_is_structural_and_order_independent()
    {
        var a = Multiset.Of(1, 2, 2);
        var b = Multiset.Of(2, 1, 2);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Different_multiplicities_are_not_equal()
    {
        (Multiset.Of(1, 1) != Multiset.Of(1)).Should().BeTrue();
    }

    [Fact]
    public void Empty_equals_empty()
    {
        (Multiset<string>.Empty == Multiset<string>.Empty).Should().BeTrue();
    }

    // ── Enumeration ───────────────────────────────────────────────────────────

    [Fact]
    public void Enumeration_yields_tokens_with_multiplicity()
    {
        var m = Multiset.Of(1, 2, 2);
        m.OrderBy(x => x).Should().Equal(new[] { 1, 2, 2 });
    }

    [Fact]
    public void DistinctItems_returns_each_value_once()
    {
        var m = Multiset.Of(1, 2, 2, 3);
        m.DistinctItems().OrderBy(x => x).Should().Equal(new[] { 1, 2, 3 });
    }

    // ── ToString ──────────────────────────────────────────────────────────────

    [Fact]
    public void ToString_of_empty_is_empty_set_symbol()
    {
        Multiset<int>.Empty.ToString().Should().Be("∅");
    }

    [Fact]
    public void ToString_includes_multiplicity_prefix_when_count_gt_1()
    {
        var m = Multiset.Repeat(42, 3);
        m.ToString().Should().Contain("3`42");
    }
}
