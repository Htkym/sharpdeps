namespace SharpDeps.Cli.Tests;

using SharpDeps.Analysis.Contracts.Harness;
using Xunit;

public sealed class CliGraphAccumulatorTests
{
    [Fact]
    public void SharedRecordsKeepTheirIdentityAndCoverageWithoutDroppingConflictingEvidence()
    {
        var graph = Graph();
        var accumulator = new CliGraphAccumulator(graph);
        accumulator.Add(graph with { Coverage = HarnessCoverage.Partial });
        accumulator.Add(graph with
        {
            Nodes = [new("a", HarnessNodeKind.Document, "Guide", null, null)],
            LegacyReferences = [new(1, "old-a", "a", null)],
            Coverage = HarnessCoverage.Failed
        });
        var frozen = accumulator.Freeze();
        Assert.Equal(new[] { "a", "z" }, frozen.Nodes.Select(n => n.Id));
        Assert.Equal(graph.Variants, frozen.Variants);
        Assert.Equal(graph.SymbolOccurrences, frozen.SymbolOccurrences);
        Assert.Equal(graph.Edges, frozen.Edges);
        Assert.Equal(new[] { "old-z", "old-a" }, frozen.LegacyReferences.Select(l => l.LegacyId));
        Assert.Equal(HarnessCoverage.Failed, frozen.Coverage);
        Assert.Same(graph.Diagnostics, frozen.Diagnostics);
        Assert.Equal(graph.Markdown, frozen.Markdown);

        HarnessGraphEnvelope[] conflicts =
        [
            graph with { Nodes = [graph.Nodes[0] with { Name = "Different" }] },
            graph with { Variants = [graph.Variants[0] with { TargetFramework = "net8.0" }] },
            graph with { SymbolOccurrences = [graph.SymbolOccurrences[0] with { VariantId = "other" }] },
            graph with { Edges = [graph.Edges[0] with { Certainty = HarnessCertainty.Unresolved }] }
        ];
        foreach (var conflict in conflicts)
        {
            var error = Assert.Throws<CliIndexException>(() => new CliGraphAccumulator(graph).Add(conflict));
            Assert.Equal("CLI_GRAPH_MERGE_CONFLICT", error.Code);
        }
    }

    [Fact]
    public void ProjectionMustKeepTheWorkspaceGenerationAndParserStamp()
    {
        var graph = Graph();
        HarnessGraphEnvelope[] conflicts =
        [
            graph with { WorkspaceId = "other" },
            graph with { SnapshotId = "other" },
            graph with { Generation = graph.Generation + 1 },
            graph with { Markdown = graph.Markdown! with { OptionsHash = "other" } }
        ];
        foreach (var conflict in conflicts)
        {
            var accumulator = new CliGraphAccumulator(graph with { Markdown = null });
            accumulator.Add(graph);
            var error = Assert.Throws<CliIndexException>(() => accumulator.Add(conflict));
            Assert.Equal("CLI_GRAPH_MERGE_CONFLICT", error.Code);
        }
    }

    private static HarnessGraphEnvelope Graph() => new(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
        HarnessGraphContract.IdentityVersion, "hw_11111111222243338444555555555555", "snapshot", 1,
        HarnessCoverage.CompleteWithinScope, [new("variant", "project", "net10.0", "Debug", null, null, null)],
        [new("z", HarnessNodeKind.Type, "Service", null, null)],
        [new("occurrence", "z", "variant", null)],
        [new("edge", "z", "z", "occurrence", "occurrence", "variant", "calls", HarnessCertainty.Candidate, "roslyn", null)],
        [new(1, "old-z", "z", "occurrence")], new("component", "source-hash", "parser", "contract", "profile", "options"),
        [new("semantic-diagnostic", 1)]);
}
