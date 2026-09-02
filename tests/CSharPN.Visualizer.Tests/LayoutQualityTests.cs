using CSharPN.Core;
using CSharPN.Visualizer.Layout;
using CSharPN.Visualizer.Layout.Sugiyama;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace CSharPN.Visualizer.Tests;

/// <summary>
/// Quality of the crossing minimisation measured against the exact optimum for the
/// layering the engine chose, plus optional SVG previews for eyeballing.
/// </summary>
public class LayoutQualityTests(ITestOutputHelper output)
{
    /// <summary>Largest layer for which the exact dynamic programme is still cheap.</summary>
    private const int ExactLimit = 6;

    [Theory, MemberData(nameof(LayoutEngineTests.Models), MemberType = typeof(LayoutEngineTests))]
    public void Crossing_count_is_optimal_for_the_chosen_layering_where_verifiable(CpnModel model)
    {
        _ = LayoutEngine.Compute(model);
        var components = LayoutEngine.Diagnostics.ComponentLayers;
        long heuristic = LayoutEngine.Diagnostics.LayeredCrossings;

        if (components.Any(c => c.Any(l => l.Count > ExactLimit)))
        {
            output.WriteLine($"{model.Name}: layers too large for exact verification (heuristic {heuristic})");
            return;
        }

        long optimum = components.Sum(ExactMinimumCrossings);
        output.WriteLine($"{model.Name}: heuristic {heuristic}, optimum {optimum}");
        heuristic.Should().Be(optimum);
    }

    [Theory, MemberData(nameof(LayoutEngineTests.Models), MemberType = typeof(LayoutEngineTests))]
    public void Write_svg_preview(CpnModel model)
    {
        var dir = Environment.GetEnvironmentVariable("CSHARPN_LAYOUT_PREVIEW_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var r = LayoutEngine.Compute(model);
        File.WriteAllText(Path.Combine(dir, model.Name + ".svg"), LayoutSvg.Render(r));
    }

    /// <summary>
    /// Exact minimum number of weighted crossings over all orderings of the given layers
    /// (layer-by-layer dynamic programme over permutations: the crossings between two
    /// layers depend only on their two orderings). Restores the input ordering afterwards.
    /// </summary>
    private static long ExactMinimumCrossings(List<List<LNode>> layers)
    {
        var saved = layers.Select(l => l.ToList()).ToList();
        var perms = layers.Select(l => Permutations(l).ToList()).ToList();

        // best[p] = minimum crossings of layers 0..l with layer l in permutation p
        var best = new long[perms[0].Count];
        for (int l = 1; l < layers.Count; l++)
        {
            var next = new long[perms[l].Count];
            for (int q = 0; q < perms[l].Count; q++)
            {
                Apply(layers[l], perms[l][q]);
                long min = long.MaxValue;
                for (int p = 0; p < perms[l - 1].Count; p++)
                {
                    Apply(layers[l - 1], perms[l - 1][p]);
                    min = Math.Min(min, best[p] + CrossingMinimizer.CountBilayer(layers[l - 1], layers[l]));
                }
                next[q] = min;
            }
            best = next;
        }

        for (int l = 0; l < layers.Count; l++) Apply(layers[l], saved[l]);
        return best.Min();
    }

    private static void Apply(List<LNode> layer, List<LNode> perm)
    {
        layer.Clear();
        layer.AddRange(perm);
        for (int i = 0; i < layer.Count; i++) layer[i].Pos = i;
    }

    private static IEnumerable<List<LNode>> Permutations(List<LNode> items)
    {
        if (items.Count <= 1) { yield return items.ToList(); yield break; }
        for (int i = 0; i < items.Count; i++)
        {
            var rest = items.Where((_, j) => j != i).ToList();
            foreach (var p in Permutations(rest)) { p.Insert(0, items[i]); yield return p; }
        }
    }
}
