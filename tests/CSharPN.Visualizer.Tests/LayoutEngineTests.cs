using CSharPN.Core;
using CSharPN.Visualizer.Layout;
using TimedExamples;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace CSharPN.Visualizer.Tests;

/// <summary>End-to-end quality checks of the automatic layout on the example models.</summary>
public class LayoutEngineTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Models() => new List<CpnModel>
    {
        new SimpleProtocol(),
        new AlternatingBitProtocol(),
        new ResourceAllocation(),
        new ReadersWriters(),
        new Simple(),
        new DiningPhilosophers(),
        new PhoneSystem(),
        new ManufacturingSystem(),
        new NetworkProtocolTimed(),
    }.Select(m => new object[] { m });

    [Theory, MemberData(nameof(Models))]
    public void Every_node_is_placed_once_inside_the_canvas(CpnModel model)
    {
        var r = LayoutEngine.Compute(model);
        r.Nodes.Select(n => n.Id).Should().OnlyHaveUniqueItems();
        r.Nodes.Should().HaveCount(model.Places.Count + model.Transitions.Count);
        foreach (var n in r.Nodes)
        {
            var box = LayoutGeometry.Footprint(n);
            box.Left.Should().BeGreaterThanOrEqualTo(0);
            box.Top.Should().BeGreaterThanOrEqualTo(0);
            box.Right.Should().BeLessThanOrEqualTo(r.Width);
            box.Bottom.Should().BeLessThanOrEqualTo(r.Height);
        }
    }

    [Theory, MemberData(nameof(Models))]
    public void Node_footprints_never_overlap(CpnModel model)
    {
        var r = LayoutEngine.Compute(model);
        LayoutGeometry.OverlappingNodes(r).Should().BeEmpty();
    }

    [Theory, MemberData(nameof(Models))]
    public void Arcs_never_pass_through_other_nodes(CpnModel model)
    {
        var r = LayoutEngine.Compute(model);
        LayoutGeometry.ArcsThroughNodes(r).Select(x => $"{x.Arc.FromId}→{x.Arc.ToId} through {x.Node.Id}")
            .Should().BeEmpty();
    }

    [Theory, MemberData(nameof(Models))]
    public void Places_and_transitions_occupy_alternating_columns(CpnModel model)
    {
        var r = LayoutEngine.Compute(model);
        var columns = r.Nodes.Select(n => n.X).Distinct().OrderBy(x => x).ToList();
        var column = r.Nodes.ToDictionary(n => n.Id, n => columns.IndexOf(n.X));

        foreach (var n in r.Nodes)
            (column[n.Id] % 2 == 0).Should().Be(n.IsPlace, $"{n.Id} should be in a {(n.IsPlace ? "place" : "transition")} column");

    }

    [Theory, MemberData(nameof(Models))]
    public void Every_model_arc_is_drawn_exactly_once(CpnModel model)
    {
        var r = LayoutEngine.Compute(model);
        var expected = model.Transitions
            .SelectMany(t => t.GetArcViews().Select(av => av.Direction == ArcDirection.Input ? (av.Place.Name, t.Name) : (t.Name, av.Place.Name)))
            .Distinct().ToList();
        r.Arcs.Select(a => (a.FromId, a.ToId)).Should().BeEquivalentTo(expected);
    }

    [Theory, MemberData(nameof(Models))]
    public void Layout_is_deterministic(CpnModel model)
    {
        var a = LayoutEngine.Compute(model);
        var b = LayoutEngine.Compute(model);
        a.Nodes.Should().Equal(b.Nodes);
        a.Arcs.Select(x => (x.FromId, x.ToId, string.Join(";", x.Waypoints)))
            .Should().Equal(b.Arcs.Select(x => (x.FromId, x.ToId, string.Join(";", x.Waypoints))));
    }

    [Fact]
    public void Planar_protocol_is_drawn_without_crossings()
    {
        // The simple protocol of Jensen & Kristensen is planar; the layout must find a plane drawing.
        var r = LayoutEngine.Compute(new SimpleProtocol());
        LayoutGeometry.CountCrossings(r).Should().Be(0);
    }

    [Fact]
    public void Double_arcs_are_drawn_as_two_distinct_lines()
    {
        // SendPacket both consumes and produces on PacketsToSend and NextSend.
        var r = LayoutEngine.Compute(new SimpleProtocol());
        var forth = r.Arcs.Single(a => a.FromId == "PacketsToSend" && a.ToId == "SendPacket");
        var back  = r.Arcs.Single(a => a.FromId == "SendPacket" && a.ToId == "PacketsToSend");
        forth.Waypoints.Should().NotBeEmpty();
        back.Waypoints.Should().NotBeEmpty();
        forth.Waypoints.Should().NotBeEquivalentTo(back.Waypoints);
    }

    [Fact]
    public void Flow_starts_at_the_initially_marked_places()
    {
        // Thinking (marked) → TakeForks → Eating → PutDownForks → back to Thinking.
        var r = LayoutEngine.Compute(new DiningPhilosophers());
        var x = r.Nodes.ToDictionary(n => n.Id, n => n.X);
        x["Thinking"].Should().BeLessThan(x["Eating"]);
    }

    [Fact]
    public void Disconnected_components_are_stacked_without_overlap()
    {
        var model = new TwoComponents();
        var r = LayoutEngine.Compute(model);
        LayoutGeometry.OverlappingNodes(r).Should().BeEmpty();
        var y = r.Nodes.ToDictionary(n => n.Id, n => n.Y);
        y["A"].Should().Be(y["T1"]);           // a chain is drawn on one horizontal line
        y["C"].Should().BeGreaterThan(y["A"]); // second component below the first
    }

    [Fact]
    public void Empty_model_yields_empty_layout_of_minimum_size()
    {
        var r = LayoutEngine.Compute(new EmptyNet(), minW: 640, minH: 480);
        r.Nodes.Should().BeEmpty();
        r.Arcs.Should().BeEmpty();
        r.Width.Should().Be(640);
        r.Height.Should().Be(480);
    }

    [Fact]
    public void Unknown_edge_endpoints_are_ignored()
    {
        var r = LayoutEngine.Compute([("P", true), ("T", false)], [("P", "T"), ("P", "Ghost")]);
        r.Arcs.Should().ContainSingle();
    }

    [Theory, MemberData(nameof(Models))]
    public void Report_layout_quality(CpnModel model)
    {
        var r = LayoutEngine.Compute(model);
        var columns = r.Nodes.Select(n => n.X).Distinct().Count();
        output.WriteLine($"{model.Name,-22} nodes={r.Nodes.Count,3} arcs={r.Arcs.Count,3} columns={columns,2} " +
                         $"crossings={LayoutGeometry.CountCrossings(r),3} (layered {LayoutEngine.Diagnostics.LayeredCrossings,2}, " +
                         $"dummies {LayoutEngine.Diagnostics.DummyCount,2}) bends={r.Arcs.Sum(a => a.Waypoints.Count),3} " +
                         $"size={r.Width:F0}×{r.Height:F0}");
    }

    [Theory, MemberData(nameof(Models))]
    public void Report_layout_details(CpnModel model)
    {
        var r = LayoutEngine.Compute(model);
        foreach (var n in r.Nodes) output.WriteLine($"  {(n.IsPlace ? "P" : "T")} {n.Id,-22} ({n.X,7:F1}, {n.Y,7:F1})");
        foreach (var a in r.Arcs) output.WriteLine($"  {a.FromId} → {a.ToId}: {string.Join(" ", a.Waypoints.Select(w => $"({w.X:F0},{w.Y:F0})"))}");
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private sealed class TwoComponents : CpnModel
    {
        public TwoComponents()
        {
            var a = AddPlace("A", Multiset.Of(1));
            var b = AddPlace<int>("B");
            var c = AddPlace("C", Multiset.Of(1));
            var d = AddPlace<int>("D");
            var x = new Var<int>("x");
            var y = new Var<int>("y");
            AddTransition("T1").Input(a, x).Output(b, x).Build();
            AddTransition("T2").Input(c, y).Output(d, y).Build();
        }
    }

    private sealed class EmptyNet : CpnModel { }
}
