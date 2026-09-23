using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Graph;
using Xunit;

namespace SharpDeps.Analysis.Tests;

public sealed class CycleDetectorTests
{
    private static CodeMapEdge Edge(string source, string target)
        => new(source, target, source, target, 1);

    private static IReadOnlyDictionary<string, string> Names(params string[] keys)
        => keys.ToDictionary(key => key, key => key, StringComparer.Ordinal);

    [Fact]
    public void ReportsNoCyclesForADirectedAcyclicGraph()
    {
        var result = CycleDetector.Analyze(
            ["A", "B", "C"],
            [Edge("A", "B"), Edge("B", "C"), Edge("A", "C")],
            Names("A", "B", "C"));

        Assert.Empty(result.CycleNodeKeys);
        Assert.Empty(result.CycleEdgeKeys);
        Assert.Empty(result.ToCycles("project"));
    }

    [Fact]
    public void FindsATwoNodeCycleAndBothOfItsEdges()
    {
        var result = CycleDetector.Analyze(
            ["A", "B"],
            [Edge("A", "B"), Edge("B", "A")],
            Names("A", "B"));

        Assert.Equal(new[] { "A", "B" }, result.CycleNodeKeys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Contains(("A", "B"), result.CycleEdgeKeys);
        Assert.Contains(("B", "A"), result.CycleEdgeKeys);

        var cycle = Assert.Single(result.ToCycles("project"));
        Assert.Equal(2, cycle.Length);
        Assert.Equal(new[] { "A", "B" }, cycle.Nodes);
    }

    [Fact]
    public void FindsASelfLoop()
    {
        var result = CycleDetector.Analyze(["A"], [Edge("A", "A")], Names("A"));

        Assert.Contains("A", result.CycleNodeKeys);
        Assert.Contains(("A", "A"), result.CycleEdgeKeys);
    }

    [Fact]
    public void ExcludesNodesThatOnlyLeadIntoTheCycle()
    {
        var result = CycleDetector.Analyze(
            ["Tail", "A", "B", "C"],
            [Edge("Tail", "A"), Edge("A", "B"), Edge("B", "C"), Edge("C", "A")],
            Names("Tail", "A", "B", "C"));

        Assert.DoesNotContain("Tail", result.CycleNodeKeys);
        Assert.Equal(3, result.CycleNodeKeys.Count);
        Assert.DoesNotContain(("Tail", "A"), result.CycleEdgeKeys);
    }

    [Fact]
    public void ReportsSeparateComponentsIndependently()
    {
        var result = CycleDetector.Analyze(
            ["A", "B", "C", "D"],
            [Edge("A", "B"), Edge("B", "A"), Edge("C", "D"), Edge("D", "C")],
            Names("A", "B", "C", "D"));

        var cycles = result.ToCycles("namespace");

        Assert.Equal(2, cycles.Count);
        Assert.All(cycles, cycle => Assert.Equal("namespace", cycle.Scope));
    }

    [Fact]
    public void SortsCycleMembersAlphabeticallyForDisplay()
    {
        var result = CycleDetector.Analyze(
            ["C", "A", "B"],
            [Edge("C", "A"), Edge("A", "B"), Edge("B", "C")],
            Names("A", "B", "C"));

        var cycle = Assert.Single(result.ToCycles("project"));

        // Sorted names are a display convenience only; the members are a set.
        Assert.Equal(new[] { "A", "B", "C" }, cycle.Nodes);
    }

    [Fact]
    public void IsDeterministicForTheSameInput()
    {
        var first = CycleDetector.Analyze(["A", "B"], [Edge("A", "B"), Edge("B", "A")], Names("A", "B"));
        var second = CycleDetector.Analyze(["A", "B"], [Edge("A", "B"), Edge("B", "A")], Names("A", "B"));

        Assert.Equal(
            first.CycleNodeKeys.OrderBy(key => key, StringComparer.Ordinal),
            second.CycleNodeKeys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(first.CycleEdgeKeys.OrderBy(edge => edge.Source, StringComparer.Ordinal),
            second.CycleEdgeKeys.OrderBy(edge => edge.Source, StringComparer.Ordinal));
    }

    [Fact]
    public void HandlesDanglingEdgesToUnknownNodes()
    {
        var result = CycleDetector.Analyze(["A"], [Edge("A", "Missing")], Names("A"));

        Assert.Empty(result.CycleNodeKeys);
    }
}
