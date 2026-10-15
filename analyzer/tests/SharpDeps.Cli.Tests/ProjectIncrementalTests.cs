namespace SharpDeps.Cli.Tests;

using System.Text.Json;
using SharpDeps.Analysis.Roslyn.Evidence;
using SharpDeps.Index;
using Xunit;

public sealed class ProjectIncrementalTests
{
    [Fact]
    public void SharedCompilerInputAndBothReferenceRelationsInvalidateEveryDependent()
    {
        var oldFiles = new[] { File("shared", 'a'), File("a", 'a'), File("b", 'a'), File("c", 'a') };
        var current = oldFiles.Select(f => f.SourceId == "shared" ? File("shared", 'b') : f)
            .ToDictionary(f => f.SourceId);
        var data = JsonSerializer.SerializeToElement(new CliProjectReuse.Extraction(new([], [], [], false), [],
            new OperationCollectionStats(0, 0, 0, 0)));
        // A removed its old reference to B; D gained one. Both must be invalidated.
        var selections = new CliProjectSelection[] { new("A", "vA", "pA", ["a"], []),
            new("B", "vB", "pB", ["shared", "b"], []), new("C", "vC", "pC", ["shared", "c"], []),
            new("D", "vD", "pD", [], ["B"]), new("E", "vE", "pE", [], []) };
        var proof = new IndexProjectReuse(CliProjectReuse.Version, new string('f', 64), selections.Select(s =>
            new IndexProjectExtraction(s.Key, s.VariantId, s.ProjectId, s.SourceIds,
                s.Key == "A" ? ["B"] : [], data)).ToArray());
        var snapshot = new IndexSnapshot(null!, oldFiles, [], [], []);
        var plan = CliProjectReuse.Select(snapshot, proof, proof.StructureHash, selections, current, default);
        Assert.NotNull(plan);
        Assert.Equal(["A", "B", "C", "D"], plan.Dirty.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("E", plan.Dirty);
        Assert.Null(CliProjectReuse.Select(snapshot, null, proof.StructureHash, selections, current, default));
        Assert.Null(CliProjectReuse.Select(snapshot, proof, new string('e', 64), selections, current, default));
    }

    private static IndexFile File(string id, char hash) => new(id, id + ".cs", "source", new string(hash, 64), 1, 1);
}
