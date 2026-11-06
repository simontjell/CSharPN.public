using CSharPN.Visualizer.Services;
using FluentAssertions;
using Xunit;

namespace CSharPN.Visualizer.Tests;

public class SourceLocatorTests
{
    private const string Source = """
        public sealed class M : CpnModel
        {
            public readonly Var<int> Field = new([0, 1]);
            public M() : base("M")
            {
                var k    = new Var<int>();
                Var<bool> ok = new();
                var x    = new Var<int>("n");
                var other = new Packet(1, "x");
                AddTransition("T1").Input(A, k).Build();
            }
            private sealed class Page : CpnPage
            {
                public Page()
                {
                    var k = new Var<int>();
                    AddTransition("T2").Input(B, k).Build();
                }
            }
        }
        """;

    [Fact]
    public void Finds_the_declarations_of_variables_under_the_names_bindings_use()
    {
        var decls = SourceLocator.FindVariableDeclarations(Source);
        decls.Keys.Should().BeEquivalentTo(["Field", "k", "ok", "n"]);
        decls["Field"].Should().Equal(3);
        decls["k"].Should().Equal(6, 16);
        decls["ok"].Should().Equal(7);
        decls["n"].Should().Equal(8);   // an explicit name wins over the C# identifier
    }

    [Fact]
    public void Picks_the_declaration_nearest_before_the_transition()
    {
        var decls = SourceLocator.FindVariableDeclarations(Source);
        SourceLocator.DeclarationLine(decls, "k", nearLine: 10).Should().Be(6);
        SourceLocator.DeclarationLine(decls, "k", nearLine: 17).Should().Be(16);
        SourceLocator.DeclarationLine(decls, "k", nearLine: null).Should().Be(6);
        SourceLocator.DeclarationLine(decls, "Field", nearLine: 1).Should().Be(3);
        SourceLocator.DeclarationLine(decls, "missing", nearLine: 10).Should().BeNull();
    }

    [Fact]
    public void Splits_an_inscription_into_variables_and_other_text()
    {
        var parts = SourceLocator.SplitInscription(
            "IIF((q.No == k), (k + 1), k) + \"k\"", new HashSet<string> { "q", "k", "No" });
        parts.Where(p => p.IsVariable).Select(p => p.Text).Should().Equal("q", "k", "k", "k");
        string.Concat(parts.Select(p => p.Text)).Should().Be("IIF((q.No == k), (k + 1), k) + \"k\"");
    }
}
