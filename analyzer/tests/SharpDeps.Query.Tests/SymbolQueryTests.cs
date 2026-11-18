namespace SharpDeps.Query.Tests;

using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using Xunit;

public sealed partial class ExplorationTests
{
    [Fact]
    public void SymbolFindIsSourceFreeAndKeepsEveryCompilationIdentity()
    {
        using var fixture = new Fixture(addScopes: true);
        var engine = new ExplorationService(new FixedIndex(fixture.Snapshot));
        var result = engine.ExecuteSymbols(new(SymbolQueryOperation.FindSymbols, Query: "Pump")
        { MaxResults = 32 });
        Assert.True(result.Succeeded, result.Json);
        Assert.NotEmpty(result.Envelope.Items);
        Assert.True(result.Envelope.Items.Select(i => i.VariantId).Distinct().Count() > 1);
        Assert.All(result.Envelope.Items, item =>
        {
            Assert.StartsWith("hocc_", item.Selector);
            Assert.NotNull(item.ProjectId);
            Assert.All(item.Declarations, d => { Assert.Null(d.Text); Assert.False(d.CompleteSavedRange); });
            Assert.Empty(item.Relations);
        });
        Assert.Equal("unverified", result.Envelope.Snapshot!.Freshness);
    }

    [Fact]
    public void SymbolReadUsesWholeDeclarationsWithoutImplicitRelatedBodies()
    {
        using var fixture = new Fixture();
        var selector = fixture.Occurrence(fixture.RunInt);
        var request = new SymbolQueryRequest(SymbolQueryOperation.ReadSymbols, Selectors: [selector],
            ProjectId: fixture.Project, VariantId: fixture.Variant);
        var result = fixture.Engine().ExecuteSymbols(request);
        Assert.True(result.Succeeded, result.Json);
        var item = Assert.Single(result.Envelope.Items);
        Assert.Equal("read", item.Outcome);
        Assert.Equal(selector, item.Selector);
        Assert.All(item.Declarations, d => { Assert.True(d.CompleteSavedRange); Assert.Equal(d.Span.Length, d.Text!.Length); });
        Assert.Equal(fixture.RunSpan, Assert.Single(item.Declarations).Span);
        Assert.Empty(item.Relations);
        Assert.Equal(result.Json, JsonSerializer.Serialize(result.Envelope, QueryJson.Options));
    }

    [Fact]
    public void SymbolBatchKeepsUnknownAndUntrustedOutcomesSeparate()
    {
        using var fixture = new Fixture();
        var engine = new ExplorationService(new FixedIndex(fixture.Snapshot));
        var request = new SymbolQueryRequest(SymbolQueryOperation.ReadSymbols,
            Selectors: [fixture.Occurrence(fixture.RunInt), "hocc_" + new string('a', 64)], ProjectId: fixture.Project, VariantId: fixture.Variant);
        var result = engine.ExecuteSymbols(request);
        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "unavailable", "not-found" }, result.Envelope.Items.Select(i => i.Outcome));
        Assert.All(result.Envelope.Items, i => Assert.Empty(i.Declarations));
        Assert.Contains("WORKSPACE_UNTRUSTED", result.Envelope.Items[0].Omissions);
    }

    [Fact]
    public void SymbolReadNeverCutsOversizedSavedDeclarationAndSharesBatchNodeLimit()
    {
        using var fixture = new Fixture(huge: true);
        var request = new SymbolQueryRequest(SymbolQueryOperation.ReadSymbols,
            Selectors: [fixture.Occurrence(fixture.RunInt), fixture.Occurrence(fixture.Helper)],
            ProjectId: fixture.Project, VariantId: fixture.Variant, Budget: new(MaxNodes: 1, MaxChars: 4096, MaxBytes: 8192));
        var result = fixture.Engine().ExecuteSymbols(request);
        Assert.True(result.Succeeded, result.Json);
        Assert.Equal(2, result.Envelope.Items.Count);
        Assert.All(result.Envelope.Items, i => Assert.Empty(i.Declarations));
        Assert.Contains("SOURCE_BUDGET", result.Envelope.Items[0].Omissions);
        Assert.Contains("NODE_LIMIT", result.Envelope.Items[1].Omissions);
        var wire = JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = result.Json } }, isError = false });
        Assert.True(wire.Length <= request.Budget!.MaxChars);
        Assert.True(Encoding.UTF8.GetByteCount(wire) <= request.Budget.MaxBytes);
    }

    [Fact]
    public void SymbolDirtySourceDoesNotBecomeAnEmptySuccessfulRead()
    {
        using var fixture = new Fixture();
        File.AppendAllText(Path.Combine(fixture.Root, Fixture.CorePath), "// changed");
        var result = fixture.Engine().ExecuteSymbols(new(SymbolQueryOperation.ReadSymbols,
            Selectors: [fixture.Occurrence(fixture.RunInt)], ProjectId: fixture.Project, VariantId: fixture.Variant));
        Assert.True(result.Succeeded);
        var item = Assert.Single(result.Envelope.Items);
        Assert.Equal("omitted", item.Outcome);
        Assert.Contains("SOURCE_CHANGED_SINCE_SNAPSHOT", item.Omissions);
        Assert.Empty(item.Declarations);
        Assert.Equal("dirty", result.Envelope.Snapshot!.Freshness);
    }

    [Fact]
    public void SymbolRefreshAndCancellationFailClosed()
    {
        using var fixture = new Fixture();
        var request = new SymbolQueryRequest(SymbolQueryOperation.FindSymbols, Query: "Pump", Freshness: QueryFreshnessPolicy.Refresh);
        var result = fixture.Engine().ExecuteSymbols(request);
        Assert.Contains(result.Envelope.Errors, e => e.Code == "UPDATE_REQUIRED");
        Assert.Empty(result.Envelope.Items);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        result = fixture.Engine().ExecuteSymbols(request with { Freshness = QueryFreshnessPolicy.AllowStale }, stop.Token);
        Assert.Contains(result.Envelope.Errors, e => e.Code == "CANCELLED");
        Assert.Empty(result.Envelope.Items);
    }

    [Fact]
    public void SymbolValidationRejectsLogicalSeedsAndUnboundedBudgetsBeforeReading()
    {
        using var fixture = new Fixture();
        var engine = fixture.Engine();
        var request = new SymbolQueryRequest(SymbolQueryOperation.ReadSymbols, Selectors: [fixture.RunInt],
            ProjectId: fixture.Project, VariantId: fixture.Variant);
        Assert.Throws<ArgumentException>(() => engine.ExecuteSymbols(request));
        Assert.Throws<ArgumentException>(() => engine.ExecuteSymbols(new(SymbolQueryOperation.FindSymbols, Query: "Pump", Budget: new(MaxMilliseconds: 0))));
        Assert.Throws<ArgumentException>(() => engine.ExecuteSymbols(new(SymbolQueryOperation.FindSymbols, Query: "Pump") { EdgeKinds = ["calls"] }));
        var failure = ExplorationService.SymbolsFailure(request with { Operation = (SymbolQueryOperation)999, Budget = new(MaxChars: -1) }, "C:\\private\\exception");
        Assert.Equal("INTERNAL_ERROR", Assert.Single(failure.Envelope.Errors).Code);
        Assert.DoesNotContain("private", failure.Json);
        Assert.Equal("ARGUMENT_INVALID", Assert.Single(ExplorationService.SymbolsFailure(null!, "ARGUMENT_INVALID").Envelope.Errors).Code);
    }

    [Fact]
    public void SymbolScopeMismatchDoesNotReturnForeignDeclaration()
    {
        using var fixture = new Fixture(addScopes: true);
        var result = fixture.Engine().ExecuteSymbols(new(SymbolQueryOperation.ReadSymbols,
            Selectors: [fixture.Occurrence(fixture.RunInt)], ProjectId: fixture.OtherProject, VariantId: fixture.OtherVariant));
        var item = Assert.Single(result.Envelope.Items);
        Assert.Equal("outside-scope", item.Outcome);
        Assert.Null(item.LogicalId);
        Assert.Empty(item.Declarations);
    }

    [Fact]
    public void SymbolRelationsRetainCandidateCertaintyAndEndpointLabelsWithoutBodies()
    {
        using var fixture = new Fixture();
        var source = fixture.Occurrence(fixture.RunInt);
        var target = fixture.Occurrence(fixture.Helper);
        var edges = fixture.Snapshot.Graph.Edges.Select(edge => edge.SourceOccurrenceId == source && edge.TargetOccurrenceId == target
            ? edge with { Certainty = HarnessCertainty.Candidate } : edge).ToArray();
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with { Edges = edges } };
        var engine = new ExplorationService(new FixedIndex(snapshot));
        var result = engine.ExecuteSymbols(new(SymbolQueryOperation.Relations, Selectors: [source],
            ProjectId: fixture.Project, VariantId: fixture.Variant));
        Assert.True(result.Succeeded, result.Json);
        var item = Assert.Single(result.Envelope.Items);
        var relation = Assert.Single(item.Relations, edge => edge.TargetSelector == target);
        Assert.Equal(HarnessCertainty.Candidate, relation.Certainty);
        Assert.Equal(fixture.Project, relation.TargetProjectId);
        Assert.Equal(fixture.Variant, relation.TargetVariantId);
        Assert.Contains("Helper", relation.TargetName);
        Assert.Empty(item.Declarations);
    }

    [Fact]
    public void SymbolRequireFreshUsesCompleteInventoryProof()
    {
        using var fixture = new Fixture();
        var request = new SymbolQueryRequest(SymbolQueryOperation.ReadSymbols, Selectors: [fixture.Occurrence(fixture.RunInt)],
            ProjectId: fixture.Project, VariantId: fixture.Variant, Freshness: QueryFreshnessPolicy.RequireFresh);
        var unverified = fixture.Engine().ExecuteSymbols(request);
        Assert.False(unverified.Succeeded);
        Assert.Empty(unverified.Envelope.Items);
        var workspace = new QueryWorkspace(fixture.Root, _ => new(fixture.Inventory(), true));
        var verified = fixture.Engine(workspace).ExecuteSymbols(request);
        Assert.True(verified.Succeeded, verified.Json);
        Assert.Equal("verified-current", verified.Envelope.Snapshot!.Freshness);
        File.AppendAllText(Path.Combine(fixture.Root, Fixture.CorePath), "// modified");
        var changed = fixture.Engine(workspace).ExecuteSymbols(request);
        Assert.False(changed.Succeeded);
        Assert.Empty(changed.Envelope.Items);
    }
}
