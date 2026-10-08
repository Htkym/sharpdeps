using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using Xunit;

namespace SharpDeps.Analysis.Tests;

public sealed class HarnessContractTests
{
    private static readonly Guid Workspace = Guid.Parse("11111111-2222-4333-8444-555555555555");

    [Fact]
    public void SeparatesDurableLogicalIdentityFromVariantOccurrenceAndLegacyIds()
    {
        var project = HarnessIdentity.ProjectId(Workspace, "src/App/App.csproj");
        Assert.Equal(project, HarnessIdentity.ProjectId(Workspace, @"src\App\.\App.csproj"));
        var symbol = HarnessIdentity.LogicalSymbolId(Workspace, project, "method", "M:App.Service.Run(System.String)");
        // Independent contract fixture pins UUID/path/signature bytes; root/source paths are attributes.
        Assert.Equal("hpr_f16c63705a1074a8b908eb1c93cecad71f020f382b58389262ba7221f1b95683", project);
        Assert.Equal("hsym_78c4d83a1356dec3d24b4db70d11eac59c2cedbf87974eae1b3178bfc48a40ce", symbol);
        Assert.NotEqual(Identity.WorkspaceRootId("C:/repo"), Identity.WorkspaceRootId("D:/moved/repo"));
        var net10 = HarnessIdentity.VariantId(Workspace, project, "net10.0", "Debug");
        var net9 = HarnessIdentity.VariantId(Workspace, project, "net9.0", "Debug");
        Assert.Equal("hvar_967d141905b1dccd2487ed22381d5867f742fd582efa1c745408e2c1f685a51b", net10);
        Assert.Equal("hocc_ba49230bfc6fc6a7f1e7e9e6b642dd4e30705122164faaf78eded6589fb0f894", HarnessIdentity.SymbolOccurrenceId(Workspace, symbol, net10));
        Assert.NotEqual(net10, net9);
        Assert.NotEqual(HarnessIdentity.SymbolOccurrenceId(Workspace, symbol, net10),
            HarnessIdentity.SymbolOccurrenceId(Workspace, symbol, net9));
        Assert.Equal(net10, HarnessIdentity.VariantId(Workspace, project, "net10.0", "Debug", ""));
        Assert.Equal(net10, HarnessIdentity.VariantId(Workspace, project, "net10.0", "Debug", "  ", ""));
        Assert.NotEqual(project, HarnessIdentity.ProjectId(Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"), "src/App/App.csproj"));
        var document = HarnessIdentity.DocumentId(Workspace, Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"));
        Assert.StartsWith("hdoc_", document);
        Assert.NotEqual(HarnessIdentity.SectionId(Workspace, document, "explicit:intro"),
            HarnessIdentity.SectionId(Workspace, document, "persisted:other"));
        Assert.Equal("wrk_65aa6de15d22cae5", Identity.WorkspaceRootId("C:/repo"));
        Assert.Equal("prj_e69fd820b24f1b1f", Identity.ProjectLogicalId(Identity.WorkspaceRootId("C:/repo"), "src/App/App.csproj"));
        Assert.Throws<ArgumentException>(() => HarnessIdentity.WorkspaceId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => HarnessIdentity.ProjectId(Workspace, "../Outside.csproj"));
    }

    [Fact]
    public void RoundTripsTheNewEnvelopeWithoutReplacingReportV2()
    {
        var project = HarnessIdentity.ProjectId(Workspace, "src/App/App.csproj");
        var symbol = HarnessIdentity.LogicalSymbolId(Workspace, project, "type", "T:App.Service");
        var variant = HarnessIdentity.VariantId(Workspace, project, "net10.0", "Debug");
        var occurrence = HarnessIdentity.SymbolOccurrenceId(Workspace, symbol, variant);
        var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
            HarnessGraphContract.IdentityVersion, HarnessIdentity.WorkspaceId(Workspace), "snapshot-1", 1,
            HarnessCoverage.Partial,
            [new(variant, project, "net10.0", "Debug", null, null, null)],
            [new(symbol, HarnessNodeKind.Type, "Service", null, null)],
            [new(occurrence, symbol, variant, null)], [],
            [new(2, "ty_0123456789abcdef", symbol, occurrence)], null);
        var json = JsonSerializer.Serialize(graph, HarnessGraphJsonContext.Default.HarnessGraphEnvelope);
        var restored = HarnessGraphContract.Read(json);
        HarnessGraphContract.ValidateHeader(restored);
        Assert.Throws<JsonException>(() => HarnessGraphContract.ValidateHeader(restored with { SchemaVersion = 2 }));
        Assert.Equal(HarnessGraphContract.Format, restored.Format);
        Assert.Equal(symbol, Assert.Single(restored.Nodes).Id);
        Assert.Equal(variant, Assert.Single(restored.SymbolOccurrences).VariantId);
        Assert.Equal(2, Assert.Single(restored.LegacyReferences).SchemaVersion);
        Assert.Null(Assert.Single(restored.Nodes).Location);
        var missingCoverage = JsonSerializer.Serialize(graph with { Coverage = HarnessCoverage.Partial }, HarnessGraphJsonContext.Default.HarnessGraphEnvelope)
            .Replace("\"coverage\": \"Partial\",", "", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => HarnessGraphContract.Read(missingCoverage));
        var withEdge = graph with { Edges = [new("edge-1", symbol, symbol, occurrence, occurrence,
            variant, "references", HarnessCertainty.Candidate, "fixture", null)] };
        var missingCertainty = JsonSerializer.Serialize(withEdge, HarnessGraphJsonContext.Default.HarnessGraphEnvelope)
            .Replace("\"certainty\": \"Candidate\",", "", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => HarnessGraphContract.Read(missingCertainty));
        var withSpan = graph with { Nodes = [new(symbol, HarnessNodeKind.Type, "Service", null,
            new("source-1", null, null, new HarnessRawSpan(1, 0)))] };
        var badSpan = JsonSerializer.Serialize(withSpan, HarnessGraphJsonContext.Default.HarnessGraphEnvelope)
            .Replace("\"start\": 1", "\"start\": -1", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => HarnessGraphContract.Read(badSpan));
        var knownEmpty = new HarnessRawSpan(3, 0);
        Assert.Equal(3, knownEmpty.End);
        Assert.Throws<OverflowException>(() => new HarnessRawSpan(int.MaxValue, 1));
    }

    [Fact]
    public void KeepsReadsSeparateFromWritesAndFailsClosedForUnknownOperations()
    {
        Assert.True(HarnessTrustPolicy.Allows(HarnessOperation.ReadSaved, false));
        foreach (var operation in Enum.GetValues<HarnessOperation>().Where(o => o != HarnessOperation.ReadSaved))
        {
            Assert.False(HarnessTrustPolicy.Allows(operation, false));
            Assert.True(HarnessTrustPolicy.Allows(operation, true));
        }
        Assert.False(HarnessTrustPolicy.Allows((HarnessOperation)99, true));
    }
}
