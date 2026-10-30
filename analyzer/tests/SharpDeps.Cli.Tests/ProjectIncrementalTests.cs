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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceMembershipUsesBothActualOwnersAndRejectsUnprovenInventory(bool delete)
    {
        var before = new[] { File("a", 'a'), File("b", 'a'), File("shared", 'a') };
        var after = before.Append(File("added", 'a')).ToArray();
        var oldSelections = new CliProjectSelection[] { new("A", "vA", "pA", ["a"], ["B"]),
            new("B", "vB", "pB", ["b", "shared"], []), new("C", "vC", "pC", ["shared"], []),
            new("D", "vD", "pD", [], []) };
        var newSelections = oldSelections.Select(s => s.Key == "B" ? s with { SourceIds = ["b", "shared", "added"] } : s).ToArray();
        if (delete) { (before, after) = (after, before); (oldSelections, newSelections) = (newSelections, oldSelections); }
        // Retained shared content also changes: both B and C must be dirty; D remains reusable.
        var current = after.Select(f => f.SourceId == "shared" ? File("shared", 'b') : f).ToDictionary(f => f.SourceId);
        var data = JsonSerializer.SerializeToElement(new CliProjectReuse.Extraction(new([], [], [], false), [],
            new OperationCollectionStats(0, 0, 0, 0)));
        var proof = new IndexProjectReuse(CliProjectReuse.Version, new string('f', 64), oldSelections.Select(s =>
            new IndexProjectExtraction(s.Key, s.VariantId, s.ProjectId, s.SourceIds, s.References, data)).ToArray());
        var snapshot = new IndexSnapshot(null!, before, [], [], []);
        var plan = CliProjectReuse.Select(snapshot, proof, proof.StructureHash, newSelections, current, default);
        Assert.NotNull(plan);
        Assert.Equal(["A", "B", "C"], plan.Dirty.Order(StringComparer.Ordinal));
        Assert.Null(CliProjectReuse.Select(snapshot, proof with { Version = "sharpdeps-cli-project-reuse/3" },
            proof.StructureHash, newSelections, current, default));
        Assert.Null(CliProjectReuse.Select(snapshot, proof, new string('e', 64), newSelections, current, default));
        var unowned = new Dictionary<string, IndexFile>(current) { ["unowned"] = File("unowned", 'a') };
        Assert.Null(CliProjectReuse.Select(snapshot, proof, proof.StructureHash, newSelections, unowned, default));
        // Simultaneous add/delete uses independent actual owners, without inferred rename history.
        var replacement = current.Where(p => p.Key != "b").ToDictionary();
        replacement["replacement"] = File("replacement", 'a');
        var batched = newSelections.Select(s => s.Key == "B" ? s with
        { SourceIds = s.SourceIds.Where(id => id != "b").Append("replacement").ToArray() } : s).ToArray();
        var batchPlan = CliProjectReuse.Select(snapshot, proof, proof.StructureHash, batched, replacement, default);
        Assert.NotNull(batchPlan);
        Assert.Equal(["A", "B", "C"], batchPlan.Dirty.Order(StringComparer.Ordinal));
    }

    private static IndexFile File(string id, char hash) => new(id, id + ".cs", "source", new string(hash, 64), 1, 1);
}
