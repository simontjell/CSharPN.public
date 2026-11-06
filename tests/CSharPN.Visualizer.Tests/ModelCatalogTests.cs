using CSharPN.Visualizer.Services;
using FluentAssertions;
using Xunit;

namespace CSharPN.Visualizer.Tests;

public class ModelCatalogTests
{
    public static TheoryData<string> ModelNames()
    {
        var data = new TheoryData<string>();
        foreach (var e in new ModelCatalog().Models) data.Add(e.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ModelNames))]
    public void Every_built_in_model_builds_with_named_variables(string name)
    {
        var model = new ModelCatalog().Models.Single(e => e.Name == name).Factory();
        model.Transitions.Should().NotBeEmpty();
        model.Transitions.SelectMany(t => t.VariableNames).Should().NotContain("");
    }
}
