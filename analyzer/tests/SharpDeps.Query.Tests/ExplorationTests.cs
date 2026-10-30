namespace SharpDeps.Query.Tests;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Index;
using Xunit;

public sealed class ExplorationTests
{
    private static readonly QueryBudget Generous = new(MaxMilliseconds: 10000, MaxChars: 100000, MaxBytes: 400000);

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void OwnerObservationIsOptInAndBoundToTheActualReplyPin(ExploreFormat format)
    {
        using var fixture = new Fixture(format: format);
        var index = new FixedIndex(fixture.Snapshot);
        var graph = fixture.Snapshot.Graph;
        var request = fixture.Request(fixture.RunInt);
        var baseline = new ExplorationService(index).Execute(request);
        var same = new ExploreOwnerObservation("idle", graph.WorkspaceId, graph.SnapshotId, graph.Generation, 1, 1);
        ExploreOwnerObservation? observation = same;
        var calls = 0;
        var engine = new ExplorationService(index)
        { OwnerStatusProvider = _ => { calls++; return observation!; } };
        Assert.False(request.ObserveOwner);
        Assert.Equal(baseline.Text, engine.Execute(request).Text);
        Assert.Equal(0, calls);
        Assert.DoesNotContain("Owner observation ", baseline.Text);

        var cases = new (ExploreOwnerObservation? Value, string Expected)[]
        {
            (same, $"state=idle generation={graph.Generation} pin=same"),
            (same with { State = "dirty", DesiredRevision = 2 }, $"state=dirty generation={graph.Generation} pin=same"),
            (same with { State = "updating", SnapshotId = graph.SnapshotId + "-other" }, $"state=updating generation={graph.Generation} pin=different"),
            (same with { State = "degraded", WorkspaceId = HarnessIdentity.WorkspaceId(Guid.Parse("00000000-0000-0000-0000-000000000001")) }, $"state=degraded generation={graph.Generation} pin=different"),
            (same with { State = "stopping", Generation = 0 }, "state=stopping generation=0 pin=different"),
            (new("idle", Revision: 0, DesiredRevision: 0), "state=idle generation=unknown pin=unknown"),
            (new("absent"), "state=absent generation=unknown pin=unknown"),
            (new("unavailable"), "state=unavailable generation=unknown pin=unknown"),
            (new("unexpected-owner-payload\nverified-current"), "state=unavailable generation=unknown pin=unknown"),
            (new("idle", WorkspaceId: graph.WorkspaceId), "state=unavailable generation=unknown pin=unknown"),
            (same with { Revision = 2, DesiredRevision = 1 }, "state=unavailable generation=unknown pin=unknown"),
            (null, "state=unavailable generation=unknown pin=unknown")
        };
        foreach (var item in cases)
        {
            observation = item.Value;
            var previous = calls;
            var reply = engine.Execute(request with { ObserveOwner = true });
            Assert.True(reply.Succeeded);
            Assert.Equal(previous + 1, calls);
            var line = Assert.Single(reply.Text.Split('\n'), line => line.StartsWith("Owner observation ", StringComparison.Ordinal)).TrimEnd('\r');
            Assert.StartsWith("Owner observation " + item.Expected, line, StringComparison.Ordinal);
            Assert.EndsWith("; status only, not freshness proof.", line, StringComparison.Ordinal);
            Assert.Contains("Freshness=unverified", reply.Text);
            Assert.Contains("WORKSPACE_UNTRUSTED", reply.OmissionReasons);
            Assert.Empty(reply.Units);
            Assert.DoesNotContain("unexpected-owner-payload", reply.Text);
            AssertWireBudget(reply, request.Budget!);
        }

        var longSnapshot = fixture.Snapshot with { Graph = graph with { SnapshotId = new string('s', 2048) } };
        var longObservation = same with { SnapshotId = longSnapshot.Graph.SnapshotId };
        var longEngine = new ExplorationService(new FixedIndex(longSnapshot))
        { OwnerStatusProvider = _ => longObservation };
        Assert.Contains($"Owner observation state=idle generation={graph.Generation} pin=same", longEngine.Execute(request with { ObserveOwner = true }).Text);
        longObservation = longObservation with { SnapshotId = " " + longSnapshot.Graph.SnapshotId[1..] };
        Assert.Contains("Owner observation state=unavailable generation=unknown pin=unknown", longEngine.Execute(request with { ObserveOwner = true }).Text);

        foreach (var caller in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var interrupted = new ExplorationService(index)
            { OwnerStatusProvider = _ => { cancellation.Cancel(); return new("idle"); } };
            var reply = interrupted.Execute(request with { ObserveOwner = true }, caller ? cancellation.Token : default,
                caller ? default : cancellation.Token);
            Assert.Contains(reply.Errors, error => error.Code == (caller ? "CANCELLED" : "QUERY_TIMEOUT"));
            Assert.Empty(reply.Units);
        }
    }

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void OwnerObservationUsesTheWireBudgetWithoutReplacingFreshnessOrSourceChecks(ExploreFormat format)
    {
        using var fixture = new Fixture(huge: true, format: format);
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with { Edges = [] } };
        var graph = snapshot.Graph;
        var observations = 0;
        var proofs = 0;
        var failProof = 0;
        var workspace = new QueryWorkspace(fixture.Root, _ => new(fixture.Inventory(), ++proofs != failProof));
        var observation = new ExploreOwnerObservation("dirty", graph.WorkspaceId, graph.SnapshotId, graph.Generation, 1, 2);
        var engine = new ExplorationService(new FixedIndex(snapshot), workspace, true)
        { OwnerStatusProvider = _ => { observations++; return observation; } };
        var request = fixture.Request(fixture.RunInt) with { ObserveOwner = true };
        var full = engine.Execute(request);
        Assert.True(full.Succeeded);
        Assert.Contains(full.Units, unit => unit.SymbolIds.Contains(fixture.RunInt) && unit.Text.Contains("😀", StringComparison.Ordinal));
        Assert.Contains("Owner observation state=dirty", full.Text);
        Assert.Contains("Freshness=unverified", full.Text);
        Assert.Equal(0, proofs);
        Assert.Equal(1, observations);
        AssertWireBudget(full, request.Budget!);

        foreach (var budget in new[]
        {
            Generous with { MaxChars = full.WireChars - 1, MaxBytes = full.WireBytes },
            Generous with { MaxChars = 2048, MaxBytes = 8192 }
        })
        {
            var previous = observations;
            var bounded = engine.Execute(request with { Budget = budget });
            Assert.Equal(previous + 1, observations);
            AssertWireBudget(bounded, budget);
            if (bounded.Errors.Any(error => error.Code == "OUTPUT_BUDGET_TOO_SMALL")) Assert.Empty(bounded.Units);
            else Assert.Contains("Owner observation state=dirty", bounded.Text);
            Assert.All(bounded.Units, unit => Assert.Equal(fixture.Sources[unit.Path].Substring(unit.Span.Start, unit.Span.Length), unit.Text));
        }

        observation = observation with { State = "idle", DesiredRevision = 1 };
        var fresh = engine.Execute(request with { Freshness = QueryFreshnessPolicy.RequireFresh });
        Assert.True(fresh.Succeeded);
        Assert.Contains("Freshness=verified-current", fresh.Text);
        Assert.NotEmpty(fresh.Units);
        Assert.Equal(2, proofs);
        Assert.Equal(4, observations);

        proofs = 0; failProof = 2;
        var invalidated = engine.Execute(request with { Freshness = QueryFreshnessPolicy.RequireFresh });
        Assert.False(invalidated.Succeeded);
        Assert.Empty(invalidated.Units);
        Assert.Contains(invalidated.Errors, error => error.Code == "FRESHNESS_REQUIREMENT_NOT_MET");
        Assert.Contains("Owner observation state=idle", invalidated.Text);
        Assert.Equal(2, proofs);
        Assert.Equal(5, observations);

        File.WriteAllText(fixture.CoreFullPath, fixture.Sources[Fixture.CorePath].Replace("value + count", "value - count", StringComparison.Ordinal), Fixture.Utf8);
        var dirty = engine.Execute(request);
        Assert.Empty(dirty.Units);
        Assert.Contains("SOURCE_CHANGED_SINCE_SNAPSHOT", dirty.OmissionReasons);
        Assert.Contains("Freshness=dirty", dirty.Text);
        Assert.Contains("Owner observation state=idle", dirty.Text);
        Assert.Equal(6, observations);
    }

    [Fact]
    public void DerivedRankingUsesDistinctTermsIdentifierComponentsAndTheExactCachedPayload()
    {
        using var fixture = new Fixture(format: ExploreFormat.Agent);
        var current = RankingSnapshot(fixture,
            (fixture.RunInt, "GetUser", "M:Demo.Pump.GetUser()"),
            (fixture.RunString, "GetGetGetOrder", "M:Demo.Pump.GetGetGetOrder()"),
            (fixture.Helper, "HTTPClient", "M:Demo.Pump.HTTPClient()"),
            (fixture.Count, "order_client", "F:Demo.Pump.order_client"),
            (fixture.Total, "Clientele", "F:Demo.Pump.Clientele"),
            (fixture.Suspect, "OrderClient", "M:Demo.Suspect.OrderClient()"));
        var engine = new ExplorationService(new ObservedIndex(() => current), null, false);
        var one = Generous with { MaxNodes = 1 };
        AssertRankingSelectors(engine.Execute(fixture.Request("get user", one)), fixture.Occurrence(fixture.RunInt));
        AssertRankingSelectors(engine.Execute(fixture.Request("get get user", one)), fixture.Occurrence(fixture.RunInt));
        AssertRankingSelectors(engine.Execute(fixture.Request("http client", one)), fixture.Occurrence(fixture.Helper));
        AssertRankingSelectors(engine.Execute(fixture.Request("client", Generous with { MaxNodes = 3 })),
            fixture.Occurrence(fixture.Helper), fixture.Occurrence(fixture.Count), fixture.Occurrence(fixture.Suspect));

        // Keep the same snapshot/generation identities: the full changed payload must rebuild the name metadata.
        current = RenameRankingNodes(current, (fixture.RunInt, "Absent", "M:Demo.Pump.Absent()"));
        AssertRankingSelectors(engine.Execute(fixture.Request("get user", one)), fixture.Occurrence(fixture.RunString));
    }

    [Fact]
    public void RankingTiersAndSavedTiesDoNotDependOnCandidateOrHitEnumerationOrder()
    {
        using var fixture = new Fixture(format: ExploreFormat.Agent);
        var snapshot = RankingSnapshot(fixture,
            (fixture.RunInt, "Order", "M:Demo.Pump.Order()"),
            (fixture.RunString, "OrderUser", "M:Demo.Pump.OrderUser()"),
            (fixture.Helper, "Other", "M:Demo.Pump.OrderUserUserUser(Token)"),
            (fixture.Count, "Opaque", "F:Demo.Pump.Opaque"),
            (fixture.Total, "prefixstoredtermsuffix", "F:Demo.Pump.prefixstoredtermsuffix"));
        var index = new RankingIndex(snapshot, fixture.Count);
        var engine = new ExplorationService(index, null, false);
        var one = Generous with { MaxNodes = 1 };
        AssertRankingSelectors(engine.Execute(fixture.Request("Order user", one)), fixture.Occurrence(fixture.RunInt));
        AssertRankingSelectors(engine.Execute(fixture.Request("user", one)), fixture.Occurrence(fixture.RunString));
        AssertRankingSelectors(engine.Execute(fixture.Request("token", one)), fixture.Occurrence(fixture.Helper));
        AssertRankingSelectors(engine.Execute(fixture.Request("M:Demo.Pump.OrderUserUserUser(Token)", one)), fixture.Occurrence(fixture.Helper));
        AssertRankingSelectors(engine.Execute(fixture.Request("storedterm", one)), fixture.Occurrence(fixture.Count));
        AssertRankingSelectors(engine.Execute(fixture.Request(fixture.Occurrence(fixture.Total) + " storedterm", one)),
            fixture.Occurrence(fixture.Total));

        var contextual = RenameRankingNodes(snapshot,
            (fixture.Pump, "Demo.Client", "T:Demo.Client"),
            (fixture.Helper, "Use", "M:Demo.Client.Use(HttpClient)"));
        AssertRankingSelectors(new ExplorationService(new RankingIndex(contextual, fixture.Count), null, false)
            .Execute(fixture.Request("HttpClient", one)), fixture.Occurrence(fixture.Helper));

        var tied = RenameRankingNodes(snapshot,
            (fixture.RunInt, "TwinClient", "M:Demo.Pump.TwinClient()"),
            (fixture.RunString, "TwinClient", "M:Demo.Pump.TwinClient()"));
        var shared = tied.Graph.SymbolOccurrences.Single(item => item.Id == fixture.Occurrence(fixture.RunInt));
        tied = tied with { Graph = tied.Graph with { SymbolOccurrences = tied.Graph.SymbolOccurrences.Select(item =>
            item.Id == fixture.Occurrence(fixture.RunString) ? item with { Location = shared.Location, Declarations = shared.Declarations } : item).ToArray() } };
        var expected = new[] { fixture.RunInt, fixture.RunString }.Order(StringComparer.Ordinal).First();
        AssertRankingSelectors(new ExplorationService(new RankingIndex(tied, fixture.RunString, fixture.RunInt), null, false)
            .Execute(fixture.Request("client", one)), fixture.Occurrence(expected));
        var reversed = tied with
        {
            Graph = tied.Graph with { Nodes = tied.Graph.Nodes.Reverse().ToArray(), SymbolOccurrences = tied.Graph.SymbolOccurrences.Reverse().ToArray() },
            SearchText = tied.SearchText.Reverse().ToArray()
        };
        Assert.Equal(tied.Graph.SnapshotId, reversed.Graph.SnapshotId);
        AssertRankingSelectors(new ExplorationService(new RankingIndex(reversed, fixture.RunInt, fixture.RunString), null, false)
            .Execute(fixture.Request("client", one)), fixture.Occurrence(expected));
    }

    [Fact]
    public void WeakPathPreferenceUsesOnlyTheSelectedVariantAndNeverExcludesExplicitTargets()
    {
        using var fixture = new Fixture(addScopes: true, format: ExploreFormat.Agent);
        var other = fixture.Snapshot.Graph.SymbolOccurrences.Single(item => item.VariantId == fixture.OtherVariant
            && fixture.Snapshot.Graph.Nodes.Single(node => node.Id == item.LogicalSymbolId).Kind == HarnessNodeKind.Member).LogicalSymbolId;
        var snapshot = RankingSnapshot(fixture,
            (fixture.RunInt, "AlphaClient", "M:Demo.Pump.AlphaClient()"),
            (fixture.Suspect, "ZetaClient", "M:Demo.Suspect.ZetaClient()"),
            (other, "OmegaClient", "M:Demo.Pump.OmegaClient()"));
        snapshot = snapshot with { Files = snapshot.Files.Select(file => file.SourceId == Fixture.CorePath
            ? file with { RelativePath = "benchmarks/Alpha.cs" } : file.SourceId == Fixture.SuspectPath
                ? file with { RelativePath = "src/Zeta.cs" } : file).ToArray() };
        var normal = snapshot.Graph.SymbolOccurrences.Single(item => item.Id == fixture.Occurrence(fixture.Pump)).Declarations!
            .Single(location => location.SourceId == Fixture.PartialPath);
        snapshot = snapshot with { Graph = snapshot.Graph with { SymbolOccurrences = snapshot.Graph.SymbolOccurrences.Select(item =>
            item.Id == fixture.Occurrence(fixture.RunInt, fixture.AlternateVariant)
                ? item with { Declarations = item.Declarations!.Append(normal).ToArray() } : item).ToArray() } };
        var one = Generous with { MaxNodes = 1 };
        var engine = new ExplorationService(new FixedIndex(snapshot), null, false);
        AssertRankingSelectors(engine.Execute(fixture.Request("client", one)), fixture.Occurrence(fixture.Suspect));
        AssertRankingSelectors(engine.Execute(fixture.Request("AlphaClient", one)), fixture.Occurrence(fixture.RunInt));
        AssertRankingSelectors(engine.Execute(fixture.Request(fixture.Occurrence(fixture.RunInt), one)), fixture.Occurrence(fixture.RunInt));
        AssertRankingSelectors(engine.Execute(fixture.Request(fixture.RunInt, one)), fixture.Occurrence(fixture.RunInt));
        AssertRankingSelectors(engine.Execute(fixture.Request("benchmarks/Alpha.cs client", one)), fixture.Occurrence(fixture.RunInt));
        AssertRankingSelectors(engine.Execute(fixture.Request("client", one, "benchmarks")), fixture.Occurrence(fixture.RunInt));

        // A normal declaration in Release does not rescue Debug; a normal Debug declaration does.
        var mixed = snapshot with { Graph = snapshot.Graph with { SymbolOccurrences = snapshot.Graph.SymbolOccurrences.Select(item =>
            item.Id == fixture.Occurrence(fixture.RunInt) ? item with { Declarations = item.Declarations!.Append(normal).ToArray() } : item).ToArray() } };
        AssertRankingSelectors(new ExplorationService(new FixedIndex(mixed), null, false).Execute(fixture.Request("client", one)),
            fixture.Occurrence(fixture.RunInt));
        var inferred = snapshot with { Graph = snapshot.Graph with
        {
            Variants = snapshot.Graph.Variants.Where(item => item.Id != fixture.AlternateVariant).ToArray(),
            SymbolOccurrences = snapshot.Graph.SymbolOccurrences.Where(item => item.VariantId != fixture.AlternateVariant).ToArray()
        } };
        AssertRankingSelectors(new ExplorationService(new FixedIndex(inferred), null, false)
            .Execute(new ExploreRequest("client", ProjectId: fixture.Project, Budget: one, Format: ExploreFormat.Agent)),
            fixture.Occurrence(fixture.Suspect));
        var ambiguous = engine.Execute(new ExploreRequest("client", Budget: one, Format: ExploreFormat.Agent));
        Assert.Contains("SCOPE_AMBIGUOUS", ambiguous.OmissionReasons);
        Assert.Contains(fixture.Project, ambiguous.Text);
        Assert.Contains(fixture.OtherProject, ambiguous.Text);
        Assert.Contains(fixture.AlternateVariant, ambiguous.Text);
        Assert.Empty(ambiguous.Units);
    }

    [Fact]
    public void IdentifierWildcardsKeepLiteralPathsNamesJapaneseAndSavedIdentitiesDistinct()
    {
        using var fixture = new Fixture(format: ExploreFormat.Agent);
        var snapshot = RankingSnapshot(fixture,
            (fixture.RunInt, "OrderClient", "M:Demo.Pump.OrderClient()"),
            (fixture.RunString, "OrderService", "M:Demo.Pump.OrderService()"),
            (fixture.Helper, "設定Loader", "M:Demo.Pump.設定Loader()"));
        var index = new RankingIndex(snapshot);
        var engine = new ExplorationService(index, null, false);
        var one = Generous with { MaxNodes = 1 };
        AssertRankingSelectors(engine.Execute(fixture.Request("Order*", Generous with { MaxNodes = 2 })),
            fixture.Occurrence(fixture.RunInt), fixture.Occurrence(fixture.RunString));
        AssertRankingSelectors(engine.Execute(fixture.Request("@Order*", Generous with { MaxNodes = 2 })),
            fixture.Occurrence(fixture.RunInt), fixture.Occurrence(fixture.RunString));
        AssertRankingSelectors(engine.Execute(fixture.Request("*Service", one)), fixture.Occurrence(fixture.RunString));
        AssertRankingSelectors(engine.Execute(fixture.Request("設定", one)), fixture.Occurrence(fixture.Helper));
        AssertRankingSelectors(engine.Execute(fixture.Request("設定Loader", one)), fixture.Occurrence(fixture.Helper));
        foreach (var query in new[] { "O*", "Ord*er", "*Order*", "Order* other", fixture.RunInt + "*", fixture.Occurrence(fixture.RunInt) + "*" })
            Assert.Throws<ArgumentException>(() => engine.Execute(fixture.Request(query, one)));
        var missingPath = engine.Execute(fixture.Request("src/*.cs", one));
        Assert.Contains(missingPath.Errors, issue => issue.Code == "SYMBOL_NOT_FOUND");
        Assert.DoesNotContain(index.Literals, literal => literal.Contains('*'));

        var literalName = RenameRankingNodes(snapshot, (fixture.Helper, "Order*", "M:Demo.Pump.LiteralStar()"));
        AssertRankingSelectors(new ExplorationService(new FixedIndex(literalName), null, false)
            .Execute(fixture.Request("Order*", one)), fixture.Occurrence(fixture.Helper));
        var literalPath = snapshot with { Files = snapshot.Files.Select(file => file.SourceId == Fixture.SuspectPath
            ? file with { RelativePath = "src/Order*.cs" } : file).ToArray() };
        var pathReply = new ExplorationService(new FixedIndex(literalPath), null, false)
            .Execute(fixture.Request("src/Order*.cs", one));
        Assert.True(pathReply.Succeeded);
        Assert.Contains("src/Order*.cs", pathReply.Text);
        Assert.Empty(pathReply.Units);
    }

    [Fact]
    public void LongExploreTermsDoNotExceedTheSavedIndexLiteralLimit()
    {
        using var fixture = new Fixture();
        var reply = new ExplorationService(new ObservedIndex(() => fixture.Snapshot), new QueryWorkspace(fixture.Root), true)
            .Execute(fixture.Request(new string('z', 129)));
        Assert.Contains(reply.Errors, error => error.Code == "SYMBOL_NOT_FOUND");
        Assert.Empty(reply.Units);
    }

    [Fact]
    public void PreparationCacheUsesTheExactPayloadAndKeepsFailuresOutOfTheSharedEntry()
    {
        using var fixture = new Fixture();
        var first = ExplorationSnapshotCache.Get(fixture.Snapshot, default);
        var same = JsonSerializer.Deserialize<IndexSnapshot>(JsonSerializer.Serialize(fixture.Snapshot, QueryJson.Options), QueryJson.Options)!;
        Assert.Same(first, ExplorationSnapshotCache.Get(same, default));
        var changedText = fixture.Sources[Fixture.CorePath].Replace("value + count", "value - count", StringComparison.Ordinal);
        var changed = fixture.NewGeneration(changedText);
        var replacement = changed with { Graph = changed.Graph with
        { Generation = fixture.Snapshot.Graph.Generation, SnapshotId = fixture.Snapshot.Graph.SnapshotId } };
        var changedEntry = ExplorationSnapshotCache.Get(replacement, default);
        Assert.NotSame(first, changedEntry);
        Assert.Equal(Fixture.Hash(changedText), changedEntry.files[Fixture.CorePath].ContentHash);
        Assert.NotSame(changedEntry, ExplorationSnapshotCache.Get(changed, default));
        var stable = ExplorationSnapshotCache.Get(fixture.Snapshot, default);
        Assert.Throws<IndexStoreException>(() => ExplorationSnapshotCache.Get(fixture.Snapshot with { Files = [] }, default));
        Assert.Same(stable, ExplorationSnapshotCache.Get(fixture.Snapshot, default));
    }

    [Fact]
    public void PreparationIsLazyAndDistinguishesCallerCancellationFromAnExternalDeadline()
    {
        using var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        var reads = 0;
        var observed = new ObservedIndex(() => { reads++; return fixture.Snapshot; });
        var engine = new ExplorationService(observed, new QueryWorkspace(fixture.Root), true);
        Assert.Equal(0, reads);
        caller.Cancel();
        var cancelled = engine.Execute(fixture.Request(fixture.RunInt), caller.Token);
        Assert.Contains(cancelled.Errors, error => error.Code == "CANCELLED");
        Assert.Empty(cancelled.Units);
        Assert.Equal(0, reads);

        using var deadline = new CancellationTokenSource();
        var expiring = new ObservedIndex(() => { deadline.Cancel(); return fixture.Snapshot; });
        var expired = new ExplorationService(expiring, new QueryWorkspace(fixture.Root), true)
            .Execute(fixture.Request(fixture.RunInt), deadlineToken: deadline.Token);
        Assert.Contains(expired.Errors, error => error.Code == "QUERY_TIMEOUT");
        Assert.DoesNotContain(expired.Errors, error => error.Code == "CANCELLED");
        Assert.Empty(expired.Units);

        using var duringPreparation = new CancellationTokenSource();
        var interrupted = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with
        { Nodes = new ObservedNodes(fixture.Snapshot.Graph.Nodes, duringPreparation.Cancel) } };
        var interruptedReply = new ExplorationService(new FixedIndex(interrupted), new QueryWorkspace(fixture.Root), true)
            .Execute(fixture.Request(fixture.RunInt), duringPreparation.Token);
        Assert.True(duringPreparation.IsCancellationRequested);
        Assert.Contains(interruptedReply.Errors, error => error.Code == "CANCELLED");
        Assert.Empty(interruptedReply.Units);
        var recovered = engine.Execute(fixture.Request(fixture.RunInt));
        Assert.True(recovered.Succeeded);
        Assert.Contains(recovered.Units, unit => unit.SymbolIds.Contains(fixture.RunInt));
    }

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void ReturnedIdsCanBeBatchedWithLogicalIdsNamesAndDuplicateOccurrences(ExploreFormat format)
    {
        using var fixture = new Fixture(format: format);
        var marker = format == ExploreFormat.Audit ? " id=" : " selector=";
        var first = fixture.Explore("count total");
        Assert.True(first.Succeeded);
        var count = ReadId(first.Text, "Demo.Pump.count");
        var total = ReadId(first.Text, "Demo.Pump.total");
        Assert.NotEqual(count, total);

        var logicalIds = Assert.Single(first.Units, unit => unit.Text == Fixture.Field).SymbolIds;
        foreach (var query in new[] { count + " " + total, string.Join(" ", logicalIds) })
        {
            var second = fixture.Explore(query);
            Assert.True(second.Succeeded);
            var field = Assert.Single(second.Units, unit => unit.Text == Fixture.Field);
            Assert.Contains(fixture.Count, field.SymbolIds);
            Assert.Contains(fixture.Total, field.SymbolIds);
        }

        var mixedQuery = count + " " + fixture.Count + " " + total + " " + count + " count";
        var mixed = fixture.Explore(mixedQuery);
        Assert.True(mixed.Succeeded);
        var mixedField = Assert.Single(mixed.Units, unit => unit.Text == Fixture.Field);
        Assert.Equal(2, mixedField.SymbolIds.Count);
        Assert.Equal(1, mixed.Text.Split(Fixture.Field, StringSplitOptions.None).Length - 1);
        Assert.Equal(1, mixed.Text.Split(marker + count + " ", StringSplitOptions.None).Length - 1);

        var withPath = fixture.Explore(mixedQuery + " " + Fixture.PartialPath);
        Assert.True(withPath.Succeeded);
        Assert.Contains(withPath.Units, unit => unit.Path == Fixture.PartialPath && unit.SymbolIds.Contains(fixture.Pump));
        Assert.Contains(withPath.Units, unit => unit.SymbolIds.Contains(fixture.Count));
        Assert.Contains(withPath.Units, unit => unit.SymbolIds.Contains(fixture.Total));

        string ReadId(string text, string label)
        {
            var line = Assert.Single(text.Split('\n'), line => line.Contains(" " + label + marker, StringComparison.Ordinal));
            return line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Split(' ')[0];
        }
    }

    [Theory]
    [InlineData(ExploreScopePolicy.RequireUnique)]
    [InlineData(ExploreScopePolicy.PreferDebug)]
    public void BatchedOccurrencesCannotMixOrOverrideCompilationAndPathScopes(ExploreScopePolicy scopePolicy)
    {
        using var fixture = new Fixture(addScopes: true, scopePolicy: scopePolicy);
        var first = fixture.Occurrence(fixture.RunInt);
        var other = fixture.Occurrence(fixture.RunString, fixture.AlternateVariant);
        var ambiguous = fixture.Engine().Execute(new(first + " " + other, Budget: Generous, ScopePolicy: scopePolicy));
        Assert.True(ambiguous.Succeeded);
        Assert.Empty(ambiguous.Units);
        Assert.Contains("SCOPE_AMBIGUOUS", ambiguous.OmissionReasons);
        Assert.Contains(fixture.Variant, ambiguous.Text);
        Assert.Contains(fixture.AlternateVariant, ambiguous.Text);

        var selected = fixture.Engine().Execute(new(first + " " + fixture.RunString, Budget: Generous, ScopePolicy: scopePolicy));
        Assert.True(selected.Succeeded);
        Assert.Contains(selected.Units, unit => unit.SymbolIds.Contains(fixture.RunInt));
        Assert.Contains(selected.Units, unit => unit.SymbolIds.Contains(fixture.RunString));
        Assert.DoesNotContain(selected.Units, unit => unit.Path == Fixture.OtherProjectPath);

        var variantConflict = fixture.Explore(first + " " + other);
        Assert.False(variantConflict.Succeeded);
        Assert.Empty(variantConflict.Units);
        Assert.Contains(variantConflict.Errors, error => error.Code == "SYMBOL_OUTSIDE_SCOPE");
        var projectConflict = fixture.Engine().Execute(new(first + " " + fixture.RunString,
            fixture.OtherProject, fixture.OtherVariant, Budget: Generous, ScopePolicy: scopePolicy));
        Assert.False(projectConflict.Succeeded);
        Assert.Empty(projectConflict.Units);
        Assert.Contains(projectConflict.Errors, error => error.Code == "SYMBOL_OUTSIDE_SCOPE");
        var pathConflict = fixture.Explore(first + " " + fixture.RunString, pathPrefix: Fixture.PartialPath);
        Assert.False(pathConflict.Succeeded);
        Assert.Empty(pathConflict.Units);
        Assert.Contains(pathConflict.Errors, error => error.Code == "SYMBOL_OUTSIDE_SCOPE");

        var missing = fixture.Explore(first + " hocc_" + new string('0', 64));
        Assert.False(missing.Succeeded);
        Assert.Empty(missing.Units);
        Assert.Contains(missing.Errors, error => error.Code == "SYMBOL_NOT_FOUND");
        var missingDocument = fixture.Explore(first + " hsec_" + new string('0', 64));
        Assert.False(missingDocument.Succeeded);
        Assert.Empty(missingDocument.Units);
        Assert.Contains(missingDocument.Errors, error => error.Code == "SYMBOL_NOT_FOUND");
    }

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void NamedMembersKeepOverloadsLocalStateAndBoundHelpersWithoutTheUnrelatedNeighbour(ExploreFormat format)
    {
        using var fixture = new Fixture(format: format);
        var reply = fixture.Explore("Pump Run");
        Assert.True(reply.Succeeded);
        foreach (var id in new[] { fixture.RunInt, fixture.RunString, fixture.Count, fixture.Helper })
            Assert.Contains(reply.Units, unit => unit.SymbolIds.Contains(id));
        Assert.Contains(reply.Units, unit => unit.Text.Contains("int Next()", StringComparison.Ordinal));
        Assert.DoesNotContain(reply.Units, unit => unit.Text.Contains("Unrelated", StringComparison.Ordinal));
        Assert.DoesNotContain(reply.Units, unit => unit.Role == "saved-owner-declaration");
        Assert.All(reply.Units, unit =>
        {
            Assert.True(unit.CompleteSavedRange);
            if (unit.Path.EndsWith(".cs", StringComparison.Ordinal)) Assert.Equal("unknown", unit.BodyAvailability);
            Assert.Equal(fixture.Sources[unit.Path].Substring(unit.Span.Start, unit.Span.Length), unit.Text);
            Assert.Equal(Fixture.Hash(fixture.Sources[unit.Path]), unit.ContentHash);
            Assert.Equal(fixture.Sources[unit.Path][..unit.Span.Start].Count(character => character == '\n') + 1, unit.FirstLine);
        });
        Assert.Single(reply.Text.Split('\n'), line => line.Contains("completeSavedRange=true", StringComparison.Ordinal));
        Assert.Single(reply.Text.Split('\n'), line => line.Contains("body=unknown", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void IncomingCallersAndSignatureTypesRemainPointersWhileForwardBodiesExpand(ExploreFormat format)
    {
        using var fixture = new Fixture(format: format);
        var helper = fixture.Snapshot.Graph.Edges.Single(edge => edge.Id == "direct-helper");
        var suspectType = fixture.Snapshot.Graph.Nodes.Single(node => node.Id == fixture.Suspect).ParentId!;
        var incoming = helper with
        {
            Id = "incoming-caller", SourceNodeId = fixture.Suspect, SourceOccurrenceId = fixture.Occurrence(fixture.Suspect),
            TargetNodeId = fixture.RunInt, TargetOccurrenceId = fixture.Occurrence(fixture.RunInt),
            Evidence = fixture.Snapshot.Graph.SymbolOccurrences.Single(item => item.Id == fixture.Occurrence(fixture.Suspect)).Location
        };
        var signature = helper with
        {
            Id = "signature-type", Kind = "signature", TargetNodeId = suspectType,
            TargetOccurrenceId = fixture.Occurrence(suspectType)
        };
        var reference = helper with
        {
            Id = "delegate-reference", Kind = "references_member", TargetNodeId = fixture.RunString,
            TargetOccurrenceId = fixture.Occurrence(fixture.RunString)
        };
        var unknown = signature with { Id = "unknown-relation", Kind = "future_relation" };
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with
        { Edges = new[] { incoming, signature, reference, unknown }.Concat(fixture.Snapshot.Graph.Edges).ToArray() } };
        var reply = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true)
            .Execute(fixture.Request(fixture.RunInt));

        Assert.True(reply.Succeeded);
        foreach (var id in new[] { fixture.RunInt, fixture.Helper, fixture.Count, fixture.RunString })
            Assert.Contains(reply.Units, unit => unit.SymbolIds.Contains(id));
        Assert.DoesNotContain(reply.Units, unit => unit.Path == Fixture.SuspectPath);
        if (format == ExploreFormat.Audit)
        {
            Assert.Contains(" id=" + fixture.Occurrence(fixture.Suspect) + " signature=M:Demo.Suspect.Hidden()", reply.Text);
            Assert.Contains(" id=" + fixture.Occurrence(suspectType) + " signature=T:Demo.Suspect", reply.Text);
        }
        else
        {
            Assert.Contains("signature=M:Demo.Suspect.Hidden()", reply.Text);
            Assert.Contains("signature=T:Demo.Suspect", reply.Text);
            Assert.Contains(":unknown", reply.Text);
            Assert.Matches(@" at=F\d+:\d+\b", reply.Text);
        }
        foreach (var kind in new[] { "calls", "signature", "references_member", "future_relation" })
            Assert.Contains((format == ExploreFormat.Audit ? "--" : "-") + kind
                + (format == ExploreFormat.Audit ? "/Resolved-->" : "/Resolved->"), reply.Text);
    }

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void DeclarationMemberFactsRemainPointersWithoutBecomingCalls(ExploreFormat format)
    {
        using var fixture = new Fixture(addScopes: true, huge: true, format: format);
        var graph = fixture.Snapshot.Graph;
        var other = graph.SymbolOccurrences.Single(occurrence => occurrence.VariantId == fixture.OtherVariant
            && graph.Nodes.Any(node => node.Id == occurrence.LogicalSymbolId && node.Kind == HarnessNodeKind.Member));
        var overrides = SavedPathEdge(fixture, "decl-override", fixture.RunInt, fixture.Suspect) with
        { Kind = "overrides", Producer = "roslyn-declaration" };
        var implementation = SavedPathEdge(fixture, "decl-explicit", fixture.Suspect, fixture.RunInt) with
        { Kind = "implements_explicitly", Producer = "roslyn-declaration" };
        var snapshot = fixture.Snapshot with { Graph = graph with { Edges =
        [
            overrides, implementation,
            overrides with { Id = "decl-candidate", Kind = "implements_explicitly", Certainty = HarnessCertainty.Candidate,
                TargetNodeId = fixture.RunString, TargetOccurrenceId = fixture.Occurrence(fixture.RunString) },
            overrides with { Id = "decl-outside", TargetNodeId = other.LogicalSymbolId, TargetOccurrenceId = other.Id },
            implementation with { Id = "decl-release", SourceOccurrenceId = fixture.Occurrence(fixture.Suspect, fixture.AlternateVariant),
                TargetOccurrenceId = fixture.Occurrence(fixture.RunInt, fixture.AlternateVariant), VariantId = fixture.AlternateVariant,
                Certainty = HarnessCertainty.Unresolved }
        ] } };
        var engine = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true);
        var request = fixture.Request(fixture.Occurrence(fixture.RunInt));
        var reply = engine.Execute(request);

        Assert.True(reply.Succeeded);
        var unit = Assert.Single(reply.Units);
        Assert.Equal(new[] { fixture.RunInt }, unit.SymbolIds);
        Assert.Contains("😀", unit.Text);
        Assert.Equal(fixture.Sources[unit.Path].Substring(unit.Span.Start, unit.Span.Length), unit.Text);
        Assert.Contains("Scope project=" + fixture.Project + " variant=" + fixture.Variant, reply.Text);
        var arrow = format == ExploreFormat.Audit ? "-->" : "->";
        Assert.Contains("overrides/Resolved" + arrow, reply.Text);
        Assert.Contains("implements_explicitly/Resolved" + arrow, reply.Text);
        Assert.Contains("implements_explicitly/Candidate" + arrow, reply.Text);
        Assert.DoesNotContain("/Unresolved" + arrow, reply.Text);
        Assert.Contains("outside-selected-scope; not expanded", reply.Text);
        if (format == ExploreFormat.Agent)
            Assert.Contains("kind=Member callerSymbols=0 callerOccurrences=0 savedCallEdges=0", reply.Text);
        AssertWireBudget(reply, request.Budget!);

        var path = engine.Execute(request with { PathTo = fixture.Occurrence(fixture.Suspect) });
        Assert.True(path.Succeeded);
        Assert.Contains("status=no-witness", RetainedPathHeader(path));
        Assert.DoesNotContain("Path hop ", path.Text);
        Assert.Equal(new[] { fixture.RunInt }, Assert.Single(path.Units).SymbolIds);
        AssertWireBudget(path, request.Budget!);

        var budget = Generous with { MaxChars = 2048, MaxBytes = 8192 };
        var bounded = engine.Execute(request with { Budget = budget });
        AssertWireBudget(bounded, budget);
        Assert.All(bounded.Units, saved => Assert.Equal(fixture.Sources[saved.Path].Substring(saved.Span.Start, saved.Span.Length), saved.Text));
    }

    [Fact]
    public void AgentTypeHubKeepsRelationlessMemberSelectorsWithoutReadingTheirSource()
    {
        using var fixture = new Fixture(format: ExploreFormat.Agent);
        var declaration = fixture.Snapshot.Graph.SymbolOccurrences.Single(item => item.Id == fixture.Occurrence(fixture.Suspect)).Location!;
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with
        {
            Nodes = [.. fixture.Snapshot.Graph.Nodes,
                new(fixture.Orphan, HarnessNodeKind.Member, "Orphan", fixture.Pump, null, "M:Demo.Pump.Orphan()")],
            SymbolOccurrences = [.. fixture.Snapshot.Graph.SymbolOccurrences,
                new(fixture.Occurrence(fixture.Orphan), fixture.Orphan, fixture.Variant, declaration, [declaration])],
            Edges = []
        } };
        foreach (var path in fixture.Sources.Keys) File.Delete(Path.Combine(fixture.Root, path));
        var engine = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), false);
        var request = fixture.Request(fixture.Occurrence(fixture.Pump));
        var agent = engine.Execute(request);

        Assert.True(agent.Succeeded);
        Assert.Empty(agent.Units);
        Assert.Contains("WORKSPACE_UNTRUSTED", agent.OmissionReasons);
        var symbol = Assert.Single(agent.Text.Split('\n'), line => line.Contains(" selector=" + fixture.Occurrence(fixture.Orphan), StringComparison.Ordinal));
        var alias = symbol.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var member = Assert.Single(agent.Text.Split('\n'), line => line.StartsWith("Member @", StringComparison.Ordinal)
            && line.Contains(" -> " + alias + " signature=M:Demo.Pump.Orphan() ", StringComparison.Ordinal));
        Assert.Contains("variant=" + fixture.Variant, member);
        Assert.Contains(":unknown source=not-requested", member);
        AssertWireBudget(agent, request.Budget!);

        var audit = engine.Execute(request with { Format = ExploreFormat.Audit });
        Assert.Empty(audit.Units);
        Assert.DoesNotContain("Retained overview", audit.Text);
        Assert.DoesNotContain("Retained root ", audit.Text);
        Assert.DoesNotContain("Member @", audit.Text);
        Assert.DoesNotContain(fixture.Occurrence(fixture.Orphan), audit.Text);
        AssertWireBudget(audit, request.Budget!);
    }

    [Fact]
    public void AgentCallerSummaryCountsBoundRetainedEdgesWithoutExpandingUnconfirmedPointers()
    {
        using var fixture = new Fixture(format: ExploreFormat.Agent);
        var template = fixture.Snapshot.Graph.Edges.Single(edge => edge.Id == "direct-helper");
        HarnessLocation Location(string id) => fixture.Snapshot.Graph.SymbolOccurrences
            .Single(item => item.Id == fixture.Occurrence(id)).Location!;
        HarnessEdge Incoming(string id, string source, HarnessCertainty certainty = HarnessCertainty.Resolved) => template with
        {
            Id = id, SourceNodeId = source, SourceOccurrenceId = fixture.Occurrence(source),
            TargetNodeId = fixture.RunInt, TargetOccurrenceId = fixture.Occurrence(fixture.RunInt),
            Certainty = certainty, Evidence = Location(source)
        };
        var caller = Incoming("caller-first", fixture.Helper);
        var candidate = template with
        {
            Id = "candidate-start", TargetNodeId = fixture.RunString, TargetOccurrenceId = fixture.Occurrence(fixture.RunString),
            Certainty = HarnessCertainty.Candidate
        };
        var unbound = template with
        {
            Id = "unbound-start", SourceOccurrenceId = null,
            TargetNodeId = fixture.Total, TargetOccurrenceId = fixture.Occurrence(fixture.Total)
        };
        var tail = template with
        {
            Id = "candidate-tail", SourceNodeId = fixture.RunString, SourceOccurrenceId = fixture.Occurrence(fixture.RunString),
            TargetNodeId = fixture.Count, TargetOccurrenceId = fixture.Occurrence(fixture.Count), Evidence = Location(fixture.RunString)
        };
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with { Edges =
        [
            caller, caller with { Id = "caller-second" },
            caller with { Id = "caller-outside-evidence", Evidence = Location(fixture.Suspect) },
            Incoming("candidate-caller", fixture.RunString, HarnessCertainty.Candidate),
            Incoming("unbound-caller", fixture.Count) with { SourceOccurrenceId = null },
            Incoming("outside-caller", fixture.Suspect),
            candidate, tail, unbound,
            tail with { Id = "unbound-tail", SourceNodeId = fixture.Total, SourceOccurrenceId = fixture.Occurrence(fixture.Total), Evidence = Location(fixture.Total) }
        ] } };
        var engine = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true);
        var request = fixture.Request(fixture.Occurrence(fixture.RunInt), pathPrefix: Fixture.CorePath);
        var agent = engine.Execute(request);

        Assert.True(agent.Succeeded);
        var unit = Assert.Single(agent.Units);
        Assert.Equal(new[] { fixture.RunInt }, unit.SymbolIds);
        Assert.Equal(fixture.Sources[Fixture.CorePath].Substring(unit.Span.Start, unit.Span.Length), unit.Text);
        Assert.True(unit.CompleteSavedRange);
        var symbol = Assert.Single(agent.Text.Split('\n'), line => line.Contains(" selector=" + fixture.Occurrence(fixture.RunInt), StringComparison.Ordinal));
        var alias = symbol.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Contains("Retained root " + alias + " kind=Member callerSymbols=1 callerOccurrences=1 savedCallEdges=2", agent.Text);
        var callerSymbol = Assert.Single(agent.Text.Split('\n'), line => line.StartsWith("@", StringComparison.Ordinal)
            && line.Contains(" Demo.Pump.Helper ", StringComparison.Ordinal));
        var callerAlias = callerSymbol.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Equal(3, agent.Text.Split('\n').Count(line => line.StartsWith(callerAlias + " -calls/Resolved-> " + alias + " ", StringComparison.Ordinal)));
        Assert.Contains("/Candidate->", agent.Text);
        Assert.Contains("outside-selected-scope; not expanded", agent.Text);
        AssertWireBudget(agent, request.Budget!);

        var audit = engine.Execute(request with { Format = ExploreFormat.Audit });
        Assert.Equal(JsonSerializer.Serialize(audit.Units, QueryJson.Options), JsonSerializer.Serialize(agent.Units, QueryJson.Options));
        Assert.DoesNotContain("Retained overview", audit.Text);
        AssertWireBudget(audit, request.Budget!);
    }

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void RetainedPathKeepsDirectionAndCyclesWithoutSelectingAnotherSourceRoot(ExploreFormat format)
    {
        using var fixture = new Fixture(dense: true, implicitConstructor: true, format: format);
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with { Edges =
        [
            SavedPathEdge(fixture, "a-construct", fixture.RunInt, fixture.Constructor) with { Kind = "constructs" },
            SavedPathEdge(fixture, "b-call", fixture.Constructor, fixture.Clamp),
            SavedPathEdge(fixture, "c-cycle", fixture.Clamp, fixture.Constructor),
            SavedPathEdge(fixture, "z-alternate", fixture.RunInt, fixture.Finish),
            SavedPathEdge(fixture, "z-tail", fixture.Finish, fixture.Clamp)
        ] } };
        var engine = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true);
        var request = fixture.Request(fixture.Occurrence(fixture.RunInt), Generous with { MaxDepth = 4 });
        var baseline = engine.Execute(request);
        var path = engine.Execute(request with { PathTo = fixture.Occurrence(fixture.Clamp) });

        Assert.DoesNotContain("Retained path ", baseline.Text);
        Assert.True(path.Succeeded);
        Assert.Contains("status=found source=" + fixture.Occurrence(fixture.RunInt)
            + " target=" + fixture.Occurrence(fixture.Clamp) + " hops=2 shown=2/2", RetainedPathHeader(path));
        var hops = path.Text.Split('\n').Where(line => line.StartsWith("Path hop ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, hops.Length);
        Assert.Contains("edge=a-construct from=" + fixture.Occurrence(fixture.RunInt)
            + " to=" + fixture.Occurrence(fixture.Constructor) + " kind=constructs certainty=Resolved", hops[0]);
        Assert.Contains("edge=b-call from=" + fixture.Occurrence(fixture.Constructor)
            + " to=" + fixture.Occurrence(fixture.Clamp) + " kind=calls certainty=Resolved", hops[1]);
        Assert.Equal(JsonSerializer.Serialize(baseline.Units, QueryJson.Options), JsonSerializer.Serialize(path.Units, QueryJson.Options));
        if (format == ExploreFormat.Agent) Assert.Contains("Retained overview roots=1 ", path.Text);
        AssertWireBudget(path, request.Budget!);

        var reverse = fixture.Request(fixture.Occurrence(fixture.Clamp), request.Budget);
        var reverseBaseline = engine.Execute(reverse);
        var absent = engine.Execute(reverse with { PathTo = fixture.Occurrence(fixture.RunInt) });
        Assert.True(absent.Succeeded);
        Assert.Contains("status=no-witness", RetainedPathHeader(absent));
        Assert.DoesNotContain("Path hop ", absent.Text);
        Assert.Equal(JsonSerializer.Serialize(reverseBaseline.Units, QueryJson.Options), JsonSerializer.Serialize(absent.Units, QueryJson.Options));
        AssertWireBudget(absent, request.Budget!);

        foreach (var symbolId in new[] { fixture.RunInt, fixture.Pump })
        {
            var occurrenceId = fixture.Occurrence(symbolId);
            var zeroRequest = fixture.Request(occurrenceId, request.Budget);
            var zeroBaseline = engine.Execute(zeroRequest);
            var same = engine.Execute(zeroRequest with { PathTo = occurrenceId });
            Assert.True(same.Succeeded);
            Assert.Contains("status=found source=" + occurrenceId
                + " target=" + occurrenceId + " hops=0 shown=0/0", RetainedPathHeader(same));
            Assert.DoesNotContain("Path hop ", same.Text);
            Assert.Equal(JsonSerializer.Serialize(zeroBaseline.Units, QueryJson.Options), JsonSerializer.Serialize(same.Units, QueryJson.Options));
        }
    }

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void RetainedPathRequiresExactOccurrenceEndpointsWithinTheExplicitScope(ExploreFormat format)
    {
        using var fixture = new Fixture(addScopes: true, format: format);
        var engine = fixture.Engine();
        var request = fixture.Request(fixture.Occurrence(fixture.RunInt)) with { PathTo = fixture.Occurrence(fixture.Helper) };
        foreach (var invalid in new[]
        {
            request with { Query = "Run" }, request with { Query = fixture.RunInt },
            request with { PathTo = "Helper" }, request with { PathTo = fixture.Helper }
        }) Assert.Throws<ArgumentException>(() => engine.Execute(invalid));

        var missing = engine.Execute(request with { PathTo = "hocc_" + new string('0', 64) });
        Assert.False(missing.Succeeded);
        Assert.Empty(missing.Units);
        Assert.Contains(missing.Errors, error => error.Code == "SYMBOL_NOT_FOUND");
        AssertWireBudget(missing, request.Budget!);

        var otherTarget = fixture.Snapshot.Graph.SymbolOccurrences.Single(occurrence => occurrence.VariantId == fixture.OtherVariant
            && fixture.Snapshot.Graph.Nodes.Any(node => node.Id == occurrence.LogicalSymbolId && node.Kind == HarnessNodeKind.Member)).Id;
        foreach (var outside in new[]
        {
            request with { PathTo = fixture.Occurrence(fixture.Helper, fixture.AlternateVariant) },
            request with { PathTo = otherTarget },
            request with { PathTo = fixture.Occurrence(fixture.Suspect), PathPrefix = Fixture.CorePath },
            request with { PathPrefix = Fixture.PartialPath }
        })
        {
            var reply = engine.Execute(outside);
            Assert.False(reply.Succeeded);
            Assert.Empty(reply.Units);
            Assert.Contains(reply.Errors, error => error.Code == "SYMBOL_OUTSIDE_SCOPE");
            AssertWireBudget(reply, request.Budget!);
        }
    }

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void RetainedPathKeepsQualifiedLeavesOutOfTheWitnessAndDistinguishesCutoffs(ExploreFormat format)
    {
        using var fixture = new Fixture(dense: true, format: format);
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with { Edges =
        [
            SavedPathEdge(fixture, "a-bound-start", fixture.RunInt, fixture.Helper),
            SavedPathEdge(fixture, "a-candidate-tail", fixture.Helper, fixture.Clamp) with { Certainty = HarnessCertainty.Candidate },
            SavedPathEdge(fixture, "b-unbound-start", fixture.RunInt, fixture.RunString) with { SourceOccurrenceId = null },
            SavedPathEdge(fixture, "b-tail", fixture.RunString, fixture.Clamp),
            SavedPathEdge(fixture, "c-outside-start", fixture.RunInt, fixture.Suspect),
            SavedPathEdge(fixture, "c-tail", fixture.Suspect, fixture.Clamp),
            SavedPathEdge(fixture, "d-outside-evidence", fixture.RunInt, fixture.Finish) with
            { Evidence = fixture.Snapshot.Graph.SymbolOccurrences.Single(item => item.Id == fixture.Occurrence(fixture.Suspect)).Location },
            SavedPathEdge(fixture, "d-tail", fixture.Finish, fixture.Clamp)
        ] } };
        var engine = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true);
        var request = fixture.Request(fixture.Occurrence(fixture.RunInt), pathPrefix: Fixture.CorePath);
        var baseline = engine.Execute(request);
        var absent = engine.Execute(request with { PathTo = fixture.Occurrence(fixture.Clamp) });
        Assert.True(absent.Succeeded);
        Assert.Contains("status=no-witness", RetainedPathHeader(absent));
        Assert.DoesNotContain("Path hop ", absent.Text);
        Assert.Equal(JsonSerializer.Serialize(baseline.Units, QueryJson.Options), JsonSerializer.Serialize(absent.Units, QueryJson.Options));
        AssertWireBudget(absent, request.Budget!);

        var chain = snapshot with { Graph = snapshot.Graph with { Edges =
        [ SavedPathEdge(fixture, "start", fixture.RunInt, fixture.Helper), SavedPathEdge(fixture, "tail", fixture.Helper, fixture.Clamp) ] } };
        var chainEngine = new ExplorationService(new FixedIndex(chain), new QueryWorkspace(fixture.Root), true);
        var pathRequest = request with { PathTo = fixture.Occurrence(fixture.Clamp) };
        Assert.Contains("status=found", RetainedPathHeader(chainEngine.Execute(pathRequest)));
        foreach (var (budget, reason) in new[]
        {
            (Generous with { MaxDepth = 1 }, "DEPTH_LIMIT"),
            (Generous with { MaxEdges = 1 }, "EDGE_LIMIT")
        })
        {
            var incomplete = chainEngine.Execute(pathRequest with { Budget = budget });
            Assert.True(incomplete.Succeeded);
            Assert.Contains("status=incomplete", RetainedPathHeader(incomplete));
            Assert.Contains(reason, incomplete.OmissionReasons);
            Assert.DoesNotContain("Path hop ", incomplete.Text);
            AssertWireBudget(incomplete, budget);
        }
        var partial = snapshot with { Graph = snapshot.Graph with { Coverage = HarnessCoverage.Partial } };
        var partialReply = new ExplorationService(new FixedIndex(partial), new QueryWorkspace(fixture.Root), true)
            .Execute(pathRequest);
        Assert.True(partialReply.Succeeded);
        Assert.Contains("status=incomplete", RetainedPathHeader(partialReply));
        AssertWireBudget(partialReply, request.Budget!);
    }

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void RetainedPathPreservesStatusAndSourceTrustWithinTheEscapedUnicodeWireBudget(ExploreFormat format)
    {
        using var fixture = new Fixture(dense: true, huge: true, format: format);
        var engine = fixture.Engine();
        var request = fixture.Request(fixture.Occurrence(fixture.RunInt)) with { PathTo = fixture.Occurrence(fixture.Clamp) };
        void AssertRequestedPath(ExploreReply reply, QueryBudget budget)
        {
            Assert.True(reply.Succeeded);
            var header = RetainedPathHeader(reply);
            Assert.Contains("status=found source=" + fixture.Occurrence(fixture.RunInt)
                + " target=" + fixture.Occurrence(fixture.Clamp) + " hops=2 ", header);
            var shown = reply.Text.Split('\n').Count(line => line.StartsWith("Path hop ", StringComparison.Ordinal));
            Assert.InRange(shown, 0, 2);
            Assert.Contains("shown=" + shown.ToString(CultureInfo.InvariantCulture) + "/2 ", header);
            Assert.Contains(shown < 2 ? "details=omitted" : "details=complete", header);
            var unit = Assert.Single(reply.Units, unit => unit.SymbolIds.Contains(fixture.RunInt));
            Assert.Equal(Fixture.CorePath, unit.Path);
            Assert.True(unit.CompleteSavedRange);
            Assert.True(unit.Span.Start <= fixture.RunSpan.Start && unit.Span.End >= fixture.RunSpan.End);
            Assert.Equal(fixture.Sources[unit.Path].Substring(unit.Span.Start, unit.Span.Length), unit.Text);
            Assert.Equal(Fixture.Hash(fixture.Sources[unit.Path]), unit.ContentHash);
            Assert.Contains("😀", unit.Text);
            AssertWireBudget(reply, budget);
        }
        var full = engine.Execute(request);
        AssertRequestedPath(full, request.Budget!);
        var exactBudget = Generous with { MaxChars = full.WireChars, MaxBytes = full.WireBytes };
        var exact = engine.Execute(request with { Budget = exactBudget });
        AssertRequestedPath(exact, exactBudget);

        var tightBudget = exactBudget with { MaxChars = exactBudget.MaxChars - 1 };
        var tight = engine.Execute(request with { Budget = tightBudget });
        AssertRequestedPath(tight, tightBudget);

        var minimumBudget = Generous with { MaxChars = 2048, MaxBytes = 8192 };
        var minimum = engine.Execute(request with { Budget = minimumBudget });
        AssertWireBudget(minimum, minimumBudget);
        if (minimum.Errors.Any(error => error.Code == "OUTPUT_BUDGET_TOO_SMALL")) Assert.Empty(minimum.Units);
        else
        {
            Assert.True(minimum.Succeeded);
            var header = RetainedPathHeader(minimum);
            Assert.Contains("status=found", header);
            Assert.Contains("hops=2 ", header);
            var shown = minimum.Text.Split('\n').Count(line => line.StartsWith("Path hop ", StringComparison.Ordinal));
            Assert.InRange(shown, 0, 2);
            Assert.Contains("shown=" + shown.ToString(CultureInfo.InvariantCulture) + "/2 ", header);
            Assert.Contains(shown < 2 ? "details=omitted" : "details=complete", header);
        }

        foreach (var path in fixture.Sources.Keys) File.Delete(Path.Combine(fixture.Root, path));
        var untrusted = new ExplorationService(new FixedIndex(fixture.Snapshot), new QueryWorkspace(fixture.Root), false).Execute(request);
        Assert.True(untrusted.Succeeded);
        Assert.Empty(untrusted.Units);
        Assert.Contains("WORKSPACE_UNTRUSTED", untrusted.OmissionReasons);
        Assert.Contains("status=found", RetainedPathHeader(untrusted));
        Assert.Contains("Freshness=unverified", untrusted.Text);
        Assert.Contains("body=unknown", untrusted.Text);
        AssertWireBudget(untrusted, request.Budget!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DenseOwnerPromotionPreservesRangesWithinTheEscapedWireBudget(int gapKind)
    {
        var ownerGap = gapKind switch
        {
            0 => "",
            1 => "// <検証😀> \" \\\r\n// <検証😀> \" \\\r\n",
            _ => "// " + new string('検', 80) + "\r\n"
        };
        using var fixture = new Fixture(addScopes: true, dense: true, ownerGap: ownerGap);
        var query = fixture.RunInt + " " + fixture.RunString;
        var request = fixture.Request(query, pathPrefix: Fixture.CorePath);
        var withoutSavedParent = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with
        {
            SymbolOccurrences = fixture.Snapshot.Graph.SymbolOccurrences.Select(item =>
                item.LogicalSymbolId == fixture.Pump && item.VariantId == fixture.Variant
                    ? item with { Declarations = null } : item).ToArray()
        } };
        var fragments = new ExplorationService(new FixedIndex(withoutSavedParent), new QueryWorkspace(fixture.Root), true)
            .Execute(request);
        var reply = fixture.Engine().Execute(request);

        Assert.True(fragments.Succeeded);
        Assert.True(reply.Succeeded);
        Assert.True(fragments.Units.Count >= 2);
        Assert.DoesNotContain(fragments.Units, unit => unit.Role == "saved-owner-declaration");
        Assert.All(reply.Units, unit => Assert.Equal(Fixture.CorePath, unit.Path));
        foreach (var fragment in fragments.Units)
            Assert.Contains(reply.Units, unit => unit.SourceId == fragment.SourceId && unit.ContentHash == fragment.ContentHash
                && unit.Span.Start <= fragment.Span.Start && unit.Span.End >= fragment.Span.End
                && fragment.SymbolIds.All(unit.SymbolIds.Contains));
        Assert.DoesNotContain(reply.Units, unit => unit.SymbolIds.Contains(fixture.Pump));
        var owner = Assert.Single(reply.Units);
        var declaration = fixture.Snapshot.Graph.SymbolOccurrences.Single(item => item.Id == fixture.Occurrence(fixture.Pump))
            .Declarations!.Single(location => location.SourceId == Fixture.CorePath);
        Assert.Equal(declaration.RawSpan, owner.Span);
        Assert.Equal("saved-owner-declaration", owner.Role);
        Assert.Equal("unknown", owner.BodyAvailability);
        Assert.True(owner.CompleteSavedRange);
        Assert.Equal(fixture.Sources[Fixture.CorePath].Substring(owner.Span.Start, owner.Span.Length), owner.Text);
        Assert.Contains("role=saved-owner-declaration", reply.Text);
        if (gapKind == 0)
        {
            Assert.True(reply.WireChars <= fragments.WireChars);
            Assert.True(reply.WireBytes <= fragments.WireBytes);
            Assert.True(reply.WireChars < fragments.WireChars || reply.WireBytes < fragments.WireBytes);
        }
        if (gapKind == 2)
            Assert.True(reply.WireChars > fragments.WireChars || reply.WireBytes > fragments.WireBytes);
        AssertWireBudget(reply, Generous);
        var exactBudget = Generous with { MaxChars = Math.Max(2048, reply.WireChars), MaxBytes = Math.Max(2048, reply.WireBytes) };
        var bounded = fixture.Explore(query, exactBudget, Fixture.CorePath);
        Assert.True(bounded.Succeeded);
        foreach (var id in new[] { fixture.RunInt, fixture.RunString })
            Assert.Contains(bounded.Units, unit => unit.SymbolIds.Contains(id));
        AssertWireBudget(bounded, exactBudget);
    }

    [Fact]
    public void OwnerPromotionRetainsBudgetOmittedMemberIdentityAndTheOtherPartialOmission()
    {
        using var fixture = new Fixture(dense: true, splitRun: true);
        var ids = new[] { fixture.RunInt, fixture.RunString, fixture.Count, fixture.Total, fixture.Helper, fixture.Finish, fixture.Clamp };
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with { Edges = [] } };
        var withoutSavedParent = snapshot with { Graph = snapshot.Graph with
        {
            SymbolOccurrences = snapshot.Graph.SymbolOccurrences.Select(item => item.LogicalSymbolId == fixture.Pump
                ? item with { Declarations = null } : item).ToArray()
        } };
        var engine = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true);
        var fragmentsEngine = new ExplorationService(new FixedIndex(withoutSavedParent), new QueryWorkspace(fixture.Root), true);
        var request = fixture.Request(string.Join(" ", ids), Generous with { MaxChars = 8192, MaxBytes = 32768 });
        var full = engine.Execute(request);
        Assert.True(full.Succeeded);
        Assert.Equal("saved-owner-declaration", Assert.Single(full.Units).Role);
        ExploreReply? promoted = null;
        ExploreReply? fragments = null;

        // Find header pressure without pinning the response's display size.
        for (var chars = Math.Max(2048, full.WireChars); chars >= Math.Max(2048, full.WireChars - 512); chars -= 16)
        {
            var budget = Generous with { MaxChars = chars, MaxBytes = 32768 };
            var candidate = engine.Execute(request with { Budget = budget });
            var baseline = fragmentsEngine.Execute(request with { Budget = budget });
            if (!candidate.Succeeded || !candidate.Units.Any(unit => unit.Role == "saved-owner-declaration")
                || ids.Any(id => !candidate.Units.Any(unit => unit.SymbolIds.Contains(id)))
                || ids.All(id => baseline.Units.Any(unit => unit.SymbolIds.Contains(id)))) continue;
            AssertWireBudget(candidate, budget);
            promoted = candidate;
            fragments = baseline;
            break;
        }

        Assert.NotNull(promoted);
        Assert.NotNull(fragments);
        var owner = Assert.Single(promoted.Units);
        Assert.Equal(Fixture.CorePath, owner.Path);
        foreach (var id in ids)
        {
            Assert.Contains(id, owner.SymbolIds);
            Assert.Contains(" id=" + fixture.Occurrence(id) + " source", promoted.Text);
        }
        Assert.Contains(ids, id => !fragments.Units.Any(unit => unit.SymbolIds.Contains(id)));
        Assert.DoesNotContain(promoted.Units, unit => unit.Path == Fixture.PartialPath);
        Assert.Contains("Omitted saved units=1;", promoted.Text);
        Assert.Contains("OMITTED_UNITS", promoted.OmissionReasons);
    }

    [Fact]
    public void WholeOwnerRequestsAndImplicitConstructorsShareOneVerifiedRange()
    {
        using var fixture = new Fixture(splitRun: true, implicitConstructor: true,
            ownerGap: "// Additional owner context.\r\n");
        var ids = new[] { fixture.Constructor, fixture.RunInt, fixture.RunString, fixture.Count,
            fixture.Total, fixture.Helper };
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with { Edges = [] } };
        var engine = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true);
        var request = fixture.Request(string.Join(" ", ids), Generous with { MaxChars = 8192, MaxBytes = 32768 });
        var reply = engine.Execute(request);
        Assert.True(reply.Succeeded);
        var owner = Assert.Single(reply.Units);
        var declaration = snapshot.Graph.SymbolOccurrences.Single(item => item.Id == fixture.Occurrence(fixture.Pump))
            .Declarations!.Single(location => location.SourceId == Fixture.CorePath);
        Assert.Equal(declaration.RawSpan, owner.Span);
        Assert.Equal("unknown", owner.BodyAvailability);
        Assert.True(owner.CompleteSavedRange);
        Assert.Equal(Fixture.Hash(fixture.Sources[Fixture.CorePath]), owner.ContentHash);
        Assert.Equal(fixture.Sources[Fixture.CorePath].Substring(owner.Span.Start, owner.Span.Length), owner.Text);
        foreach (var id in ids) Assert.Contains(id, owner.SymbolIds);
        Assert.DoesNotContain(reply.Units, unit => unit.Path == Fixture.PartialPath);
        Assert.Contains("OMITTED_UNITS", reply.OmissionReasons);
        AssertWireBudget(reply, request.Budget!);

        var memberSpans = snapshot.Graph.SymbolOccurrences.Where(item => item.VariantId == fixture.Variant
                && item.LogicalSymbolId != fixture.Constructor && ids.Contains(item.LogicalSymbolId))
            .SelectMany(item => item.Declarations ?? []).Where(location => location.SourceId == Fixture.CorePath)
            .Select(location => location.RawSpan!.Value).Distinct().ToArray();
        Assert.True(memberSpans.Sum(span => (long)span.Length) * 4 < owner.Span.Length * 3);
        ExploreReply? pressured = null;
        var attempts = new List<string>();
        for (var step = 1; step <= 32; step++)
        {
            var chars = reply.WireChars - step * 16;
            if (chars < 2048) break;
            var budget = request.Budget! with { MaxChars = chars };
            var candidate = engine.Execute(request with { Budget = budget });
            attempts.Add($"budget={chars} wire={candidate.WireChars}/{candidate.WireBytes} units="
                + string.Join(";", candidate.Units.Select(unit => $"{unit.Path}:{unit.Span.Start}:{unit.Span.Length}:{unit.Role}:aliases={unit.SymbolIds.Count}"))
                + " missing=" + ids.Count(id => !candidate.Units.Any(unit => unit.SymbolIds.Contains(id)))
                + " omissions=" + string.Join(",", candidate.OmissionReasons));
            if (!candidate.Succeeded || !candidate.Units.Any(unit => unit.SourceId == owner.SourceId
                && unit.Span == owner.Span && ids.All(unit.SymbolIds.Contains))) continue;
            Assert.True(chars < reply.WireChars);
            AssertWireBudget(candidate, budget);
            pressured = candidate;
            break;
        }
        Assert.True(pressured is not null, "Whole-owner pressure diagnostics; full wire=" + reply.WireChars + "\n" + string.Join("\n", attempts));
        var pressuredOwner = Assert.Single(pressured.Units);
        Assert.Equal("unknown", pressuredOwner.BodyAvailability);
        Assert.True(pressuredOwner.CompleteSavedRange);
        Assert.Equal(owner.SourceId, pressuredOwner.SourceId);
        Assert.Equal(owner.Span, pressuredOwner.Span);
        Assert.Equal(owner.ContentHash, pressuredOwner.ContentHash);
        Assert.Equal(owner.Text, pressuredOwner.Text);
        foreach (var id in ids)
        {
            Assert.Contains(id, pressuredOwner.SymbolIds);
            Assert.Contains(" id=" + fixture.Occurrence(id) + " source", pressured.Text);
        }
        Assert.DoesNotContain(pressured.Units, unit => unit.Path == Fixture.PartialPath);
        Assert.Contains("Omitted saved units=1;", pressured.Text);
        Assert.Contains("OMITTED_UNITS", pressured.OmissionReasons);

        var exact = request.Budget! with { MaxChars = pressured.WireChars, MaxBytes = pressured.WireBytes };
        var bounded = engine.Execute(request with { Budget = exact });
        Assert.True(bounded.Succeeded);
        var boundedOwner = Assert.Single(bounded.Units);
        Assert.Equal(owner.SourceId, boundedOwner.SourceId);
        Assert.Equal(owner.Span, boundedOwner.Span);
        Assert.Equal(owner.ContentHash, boundedOwner.ContentHash);
        Assert.Equal(owner.Text, boundedOwner.Text);
        Assert.Equal("unknown", boundedOwner.BodyAvailability);
        Assert.True(boundedOwner.CompleteSavedRange);
        foreach (var id in ids) Assert.Contains(id, boundedOwner.SymbolIds);
        Assert.DoesNotContain(bounded.Units, unit => unit.Path == Fixture.PartialPath);
        Assert.Contains("Omitted saved units=1;", bounded.Text);
        AssertWireBudget(bounded, exact);

        var small = Generous with { MaxChars = 2048, MaxBytes = 8192 };
        var limited = engine.Execute(request with { Budget = small });
        AssertWireBudget(limited, small);
        Assert.Contains("OMITTED_UNITS", limited.OmissionReasons);
        Assert.All(limited.Units, unit =>
            Assert.Equal(fixture.Sources[unit.Path].Substring(unit.Span.Start, unit.Span.Length), unit.Text));
    }

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void PartialDeclarationsRemainSeparateAndAFileScopeIsAHardBoundary(ExploreFormat format)
    {
        using var fixture = new Fixture(format: format);
        var all = fixture.Explore("Pump");
        Assert.Contains(all.Units, unit => unit.Path == Fixture.CorePath && unit.SymbolIds.Contains(fixture.Pump));
        Assert.Contains(all.Units, unit => unit.Path == Fixture.PartialPath && unit.SymbolIds.Contains(fixture.Pump));
        foreach (var path in new[] { Fixture.CorePath, Fixture.PartialPath })
        {
            var part = Assert.Single(all.Units, unit => unit.Path == path);
            Assert.Equal(Fixture.Hash(fixture.Sources[path]), part.ContentHash);
            Assert.Equal(fixture.Sources[path].Substring(part.Span.Start, part.Span.Length), part.Text);
        }

        var file = fixture.Explore(Fixture.PartialPath, pathPrefix: Fixture.PartialPath);
        Assert.NotEmpty(file.Units);
        Assert.All(file.Units, unit => Assert.Equal(Fixture.PartialPath, unit.Path));
    }

    [Fact]
    public void UnspecifiedProjectAndVariantReturnChoicesInsteadOfMixingSource()
    {
        using var fixture = new Fixture(addScopes: true);
        var ambiguous = fixture.Engine().Execute(new("Pump Run", Budget: Generous));
        Assert.Empty(ambiguous.Units);
        Assert.Contains(fixture.Variant, ambiguous.Text);
        Assert.Contains(fixture.AlternateVariant, ambiguous.Text);
        Assert.Contains(fixture.OtherVariant, ambiguous.Text);

        var selected = fixture.Explore("Pump Run");
        Assert.NotEmpty(selected.Units);
        Assert.DoesNotContain(selected.Units, unit => unit.Path == Fixture.OtherProjectPath);
        var inconsistent = new ExploreRequest("Run", fixture.OtherProject, fixture.Variant, Budget: Generous);
        Assert.Throws<ArgumentException>(() => fixture.Engine().Execute(inconsistent));
    }

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void PreferDebugReportsAlternativesAndKeepsExplicitVariantAndOccurrencePrecedence(ExploreFormat format)
    {
        using var fixture = new Fixture(addScopes: true);
        var request = new ExploreRequest("Pump Run", fixture.Project, Budget: Generous, Format: format,
            ScopePolicy: ExploreScopePolicy.PreferDebug);
        Assert.Throws<ArgumentException>(() => fixture.Engine().Execute(request with { ScopePolicy = (ExploreScopePolicy)99 }));
        var preferred = fixture.Engine().Execute(request);
        Assert.True(preferred.Succeeded);
        Assert.NotEmpty(preferred.Units);
        Assert.Contains("Scope project=" + fixture.Project + " variant=" + fixture.Variant, preferred.Text);
        Assert.Contains("decision=selected-unique-saved-debug", preferred.Text);
        Assert.Contains("Alternative scopes=1", preferred.Text);
        Assert.Contains("variant=" + fixture.AlternateVariant, preferred.Text);
        Assert.DoesNotContain(preferred.Units, unit => unit.Path == Fixture.OtherProjectPath);
        AssertWireBudget(preferred, Generous);

        var explicitRelease = fixture.Engine().Execute(request with { VariantId = fixture.AlternateVariant });
        Assert.True(explicitRelease.Succeeded);
        Assert.Contains("Scope project=" + fixture.Project + " variant=" + fixture.AlternateVariant, explicitRelease.Text);
        Assert.Contains(fixture.Occurrence(fixture.RunInt, fixture.AlternateVariant), explicitRelease.Text);
        Assert.DoesNotContain("Alternative scopes=", explicitRelease.Text);
        AssertWireBudget(explicitRelease, Generous);
        var occurrenceRelease = fixture.Engine().Execute(request with
        { Query = fixture.Occurrence(fixture.RunInt, fixture.AlternateVariant) + " Run" });
        Assert.True(occurrenceRelease.Succeeded);
        Assert.Contains("Scope project=" + fixture.Project + " variant=" + fixture.AlternateVariant, occurrenceRelease.Text);
        Assert.Contains("decision=explicit-occurrence", occurrenceRelease.Text);
        AssertWireBudget(occurrenceRelease, Generous);
        Assert.Throws<ArgumentException>(() => fixture.Engine().Execute(request with
        { ProjectId = fixture.OtherProject, VariantId = fixture.Variant }));
    }

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void PreferDebugKeepsMultipleProjectsAndDebugVariantsAmbiguousRegardlessOfSavedOrder(ExploreFormat format)
    {
        using var fixture = new Fixture(addScopes: true);
        var request = new ExploreRequest("Pump Run", Budget: Generous, Format: format, ScopePolicy: ExploreScopePolicy.PreferDebug);
        var projects = fixture.Engine().Execute(request);
        Assert.Empty(projects.Units);
        Assert.Contains("SCOPE_AMBIGUOUS", projects.OmissionReasons);
        Assert.Contains("decision=ambiguous-multiple-projects", projects.Text);
        AssertWireBudget(projects, Generous);

        var secondDebug = new HarnessVariant(fixture.SecondDebugVariant, fixture.Project, "net10.0", "Debug", "x64", "win-x64", Fixture.Hash("debug-x64"));
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with
        {
            Variants = [.. fixture.Snapshot.Graph.Variants, secondDebug],
            SymbolOccurrences = [.. fixture.Snapshot.Graph.SymbolOccurrences,
                .. fixture.Snapshot.Graph.SymbolOccurrences.Where(occurrence => occurrence.VariantId == fixture.Variant)
                    .Select(occurrence => occurrence with { Id = fixture.Occurrence(occurrence.LogicalSymbolId, secondDebug.Id), VariantId = secondDebug.Id })]
        } };
        request = request with { ProjectId = fixture.Project };
        var variants = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true).Execute(request);
        Assert.Empty(variants.Units);
        Assert.Contains("SCOPE_AMBIGUOUS", variants.OmissionReasons);
        Assert.Contains("decision=ambiguous-multiple-debug-variants", variants.Text);
        Assert.Contains(fixture.Variant, variants.Text);
        Assert.Contains(fixture.SecondDebugVariant, variants.Text);
        var platformChoice = Assert.Single(variants.Text.Split('\n'), line => line.Contains("variant=" + fixture.SecondDebugVariant, StringComparison.Ordinal));
        Assert.Contains("net10.0 Debug platform=x64 rid=win-x64", platformChoice);
        AssertWireBudget(variants, Generous);
        var uniquePolicy = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true)
            .Execute(request with { ScopePolicy = ExploreScopePolicy.RequireUnique });
        Assert.DoesNotContain(" platform=", uniquePolicy.Text);
        var reordered = snapshot with { Graph = snapshot.Graph with
        { Variants = snapshot.Graph.Variants.Reverse().ToArray(), SymbolOccurrences = snapshot.Graph.SymbolOccurrences.Reverse().ToArray() } };
        var repeated = new ExplorationService(new FixedIndex(reordered), new QueryWorkspace(fixture.Root), true).Execute(request);
        Assert.Equal(variants.Text, repeated.Text);

        var secondRelease = secondDebug with { Id = fixture.SecondReleaseVariant, Configuration = "Release", AnalysisFingerprint = Fixture.Hash("release-x64") };
        var noDebug = snapshot with { Graph = snapshot.Graph with
        {
            Variants = snapshot.Graph.Variants.Where(scope => scope.Id != fixture.Variant && scope.Id != secondDebug.Id).Append(secondRelease).ToArray(),
            SymbolOccurrences = snapshot.Graph.SymbolOccurrences.Where(occurrence => occurrence.VariantId != fixture.Variant && occurrence.VariantId != secondDebug.Id)
                .Concat(snapshot.Graph.SymbolOccurrences.Where(occurrence => occurrence.VariantId == secondDebug.Id)
                    .Select(occurrence => occurrence with { Id = fixture.Occurrence(occurrence.LogicalSymbolId, secondRelease.Id), VariantId = secondRelease.Id })).ToArray(),
            Edges = snapshot.Graph.Edges.Where(edge => edge.VariantId != fixture.Variant).ToArray()
        } };
        var noPreferred = new ExplorationService(new FixedIndex(noDebug), new QueryWorkspace(fixture.Root), true).Execute(request);
        Assert.Empty(noPreferred.Units);
        Assert.Contains("SCOPE_AMBIGUOUS", noPreferred.OmissionReasons);
        Assert.Contains("decision=ambiguous-no-debug-variant", noPreferred.Text);
        Assert.Contains(fixture.AlternateVariant, noPreferred.Text);
        Assert.Contains(fixture.SecondReleaseVariant, noPreferred.Text);
        AssertWireBudget(noPreferred, Generous);
    }

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void PreferDebugIntersectsExplicitLogicalAndDocumentScopesWithoutChangingTheDefault(ExploreFormat format)
    {
        using var fixture = new Fixture(addScopes: true);
        var removed = new HashSet<string>(StringComparer.Ordinal)
        { fixture.Occurrence(fixture.RunInt, fixture.AlternateVariant), fixture.Occurrence(fixture.RunString) };
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with
        { SymbolOccurrences = fixture.Snapshot.Graph.SymbolOccurrences.Where(occurrence => !removed.Contains(occurrence.Id)).ToArray() } };
        var engine = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true);
        var budget = Generous with { MaxChars = 8192, MaxBytes = 12288 };
        var request = new ExploreRequest(fixture.RunInt + " Pump", Budget: budget, Format: format);
        var unchanged = engine.Execute(request);
        Assert.Empty(unchanged.Units);
        Assert.Contains("SCOPE_AMBIGUOUS", unchanged.OmissionReasons);
        Assert.DoesNotContain("Scope policy=", unchanged.Text);
        var preferred = engine.Execute(request with { ScopePolicy = ExploreScopePolicy.PreferDebug });
        Assert.True(preferred.Succeeded);
        Assert.Contains("decision=selected-by-explicit-identities", preferred.Text);
        Assert.Contains(preferred.Units, unit => unit.SymbolIds.Contains(fixture.RunInt) && unit.Text.Contains("😀", StringComparison.Ordinal));
        Assert.DoesNotContain(preferred.Units, unit => unit.SymbolIds.Contains(fixture.RunString) || unit.Path == Fixture.OtherProjectPath);
        Assert.DoesNotContain("Alternative scopes=", preferred.Text);
        Assert.All(preferred.Units, unit => Assert.Equal(fixture.Sources[unit.Path].Substring(unit.Span.Start, unit.Span.Length), unit.Text));
        AssertWireBudget(unchanged, budget);
        AssertWireBudget(preferred, budget);

        var releaseOnly = engine.Execute(request with { Query = fixture.RunString + " Pump", ScopePolicy = ExploreScopePolicy.PreferDebug });
        Assert.True(releaseOnly.Succeeded);
        Assert.Contains("Scope project=" + fixture.Project + " variant=" + fixture.AlternateVariant, releaseOnly.Text);
        Assert.Contains("decision=selected-by-explicit-identities", releaseOnly.Text);
        Assert.Contains(releaseOnly.Units, unit => unit.SymbolIds.Contains(fixture.RunString));
        AssertWireBudget(releaseOnly, budget);

        var incompatible = engine.Execute(request with
        { Query = fixture.RunInt + " " + fixture.RunString, ScopePolicy = ExploreScopePolicy.PreferDebug });
        Assert.Empty(incompatible.Units);
        Assert.Contains(incompatible.Errors, error => error.Code == "SYMBOL_OUTSIDE_SCOPE");
        AssertWireBudget(incompatible, budget);
        var outsidePath = engine.Execute(request with { PathPrefix = Fixture.PartialPath, ScopePolicy = ExploreScopePolicy.PreferDebug });
        Assert.Empty(outsidePath.Units);
        Assert.Contains(outsidePath.Errors, error => error.Code == "SYMBOL_OUTSIDE_SCOPE");
        AssertWireBudget(outsidePath, budget);

        var document = engine.Execute(request with { Query = fixture.Section + " Pump Run", ScopePolicy = ExploreScopePolicy.PreferDebug });
        Assert.True(document.Succeeded);
        Assert.Contains("decision=selected-by-explicit-identities", document.Text);
        Assert.Contains(document.Units, unit => unit.SymbolIds.Contains(fixture.Section));
        AssertWireBudget(document, budget);
        var documentOnly = engine.Execute(request with { Query = fixture.Section, ScopePolicy = ExploreScopePolicy.PreferDebug });
        Assert.True(documentOnly.Succeeded);
        Assert.Contains(documentOnly.Units, unit => unit.SymbolIds.Contains(fixture.Section));
        Assert.DoesNotContain("SCOPE_AMBIGUOUS", documentOnly.OmissionReasons);
        Assert.Contains("decision=no-code-scope", documentOnly.Text);
        AssertWireBudget(documentOnly, budget);

        var unlinked = snapshot with { Graph = snapshot.Graph with
        { Edges = snapshot.Graph.Edges.Where(edge => edge.Id != "doc-link").ToArray() } };
        var unprovenDocument = new ExplorationService(new FixedIndex(unlinked), new QueryWorkspace(fixture.Root), true)
            .Execute(request with { Query = fixture.RunInt + " " + fixture.Section, ScopePolicy = ExploreScopePolicy.PreferDebug });
        Assert.Empty(unprovenDocument.Units);
        Assert.Contains(unprovenDocument.Errors, error => error.Code == "SYMBOL_OUTSIDE_SCOPE");
        AssertWireBudget(unprovenDocument, budget);
        var incomplete = snapshot with { Graph = snapshot.Graph with
        {
            Coverage = HarnessCoverage.Partial,
            Nodes = [.. snapshot.Graph.Nodes, new(fixture.Orphan, HarnessNodeKind.Member, "Orphan", fixture.Pump, null, "M:Demo.Pump.Orphan()")]
        } };
        var orphan = new ExplorationService(new FixedIndex(incomplete), new QueryWorkspace(fixture.Root), true)
            .Execute(request with { Query = fixture.RunInt + " " + fixture.Orphan, ScopePolicy = ExploreScopePolicy.PreferDebug });
        Assert.Empty(orphan.Units);
        Assert.Contains(orphan.Errors, error => error.Code == "SYMBOL_OUTSIDE_SCOPE");
        AssertWireBudget(orphan, budget);
    }

    [Fact]
    public void CandidateCodeIsALeafWhileLinkedMarkdownRetainsItsIdentityAndPartialCoverage()
    {
        using var fixture = new Fixture(partial: true);
        var reply = fixture.Explore(fixture.RunInt);
        Assert.Contains("Candidate", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Partial", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(reply.Units, unit => unit.SymbolIds.Contains(fixture.Suspect));
        Assert.Contains(reply.Units, unit => unit.Path == Fixture.DocumentPath && unit.SymbolIds.Contains(fixture.Section));
        Assert.Contains(fixture.Section, reply.Text);
        var section = Assert.Single(reply.Units, unit => unit.Path == Fixture.DocumentPath);
        Assert.Equal(Fixture.Hash(Fixture.Markdown), section.ContentHash);
        Assert.Equal(Fixture.Markdown.Substring(section.Span.Start, section.Span.Length), section.Text);
    }

    [Fact]
    public void SharedTypedFieldDeclarationIsReturnedOnceForBothSymbols()
    {
        using var fixture = new Fixture();
        var reply = fixture.Explore("count total");
        var fields = Assert.Single(reply.Units, unit => unit.SymbolIds.Contains(fixture.Count));
        Assert.Contains(fixture.Total, fields.SymbolIds);
        Assert.Equal(Fixture.Field, fields.Text);
        Assert.Equal(1, reply.Text.Split(Fixture.Field, StringSplitOptions.None).Length - 1);
        foreach (var group in reply.Units.GroupBy(unit => unit.SourceId))
        {
            var ranges = group.OrderBy(unit => unit.Span.Start).ToArray();
            for (var index = 1; index < ranges.Length; index++) Assert.True(ranges[index - 1].Span.End <= ranges[index].Span.Start);
        }
    }

    [Fact]
    public void NodeBudgetStopsHelperAndStateSourceSelection()
    {
        using var fixture = new Fixture();
        var budget = Generous with { MaxNodes = 1 };
        var reply = fixture.Explore(fixture.RunInt, budget: budget);
        Assert.True(reply.Succeeded);
        Assert.Single(reply.Units);
        Assert.All(reply.Units, unit => Assert.Equal(new[] { fixture.RunInt }, unit.SymbolIds));
        Assert.Contains("NODE_LIMIT", reply.OmissionReasons);
        AssertWireBudget(reply, budget);

        var type = fixture.Explore("Pump", budget: budget);
        Assert.True(type.Succeeded);
        Assert.All(type.Units, unit => Assert.Equal(new[] { fixture.Pump }, unit.SymbolIds));
        Assert.Contains("NODE_LIMIT", type.OmissionReasons);
    }

    [Fact]
    public void RequestedBodyTakesPriorityOverOptionalRelationDetails()
    {
        using var fixture = new Fixture();
        var edge = fixture.Snapshot.Graph.Edges.Single(item => item.Id == "read-state");
        var extra = Enumerable.Range(0, 12).Select(index => edge with
        { Id = "a-state-" + index.ToString("D2", CultureInfo.InvariantCulture), Origin = new string('o', 80) });
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with
        { Edges = extra.Concat(fixture.Snapshot.Graph.Edges).ToArray() } };
        var budget = Generous with { MaxChars = 3200, MaxBytes = 12800 };
        var reply = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true)
            .Execute(fixture.Request(fixture.RunInt + " " + Fixture.PartialPath, budget));
        Assert.True(reply.Succeeded);
        Assert.Contains(reply.Units, unit => unit.SymbolIds.Contains(fixture.RunInt));
        Assert.Contains(reply.Units, unit => unit.Path == Fixture.CorePath && unit.SymbolIds.Contains(fixture.Pump));
        Assert.Contains(reply.Units, unit => unit.Path == Fixture.PartialPath && unit.SymbolIds.Contains(fixture.Pump));
        Assert.Contains("Relations omitted=", reply.Text);
        AssertWireBudget(reply, budget);
    }

    [Fact]
    public void ExactBodiesAndTheMatchedPartialHaveBudgetBeforeOtherPartialSource()
    {
        using var fixture = new Fixture(ownerGap: "// " + new string('p', 1800) + "\r\n",
            suspectPadding: "/* " + new string('s', 2000) + " */");
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with { Edges = [] } };
        var engine = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true);
        var budget = Generous with { MaxChars = 5500, MaxBytes = 22000 };
        var request = fixture.Request(fixture.Suspect + " " + fixture.RunString + " " + Fixture.PartialPath, budget);
        var reply = engine.Execute(request);
        Assert.True(reply.Succeeded);
        Assert.Contains(reply.Units, unit => unit.SymbolIds.Contains(fixture.Suspect));
        Assert.Contains(reply.Units, unit => unit.SymbolIds.Contains(fixture.RunString));
        Assert.Contains(reply.Units, unit => unit.Path == Fixture.PartialPath && unit.SymbolIds.Contains(fixture.Pump));
        var otherPart = snapshot.Graph.SymbolOccurrences.Single(item => item.Id == fixture.Occurrence(fixture.Pump))
            .Declarations!.Single(location => location.SourceId == Fixture.CorePath).RawSpan;
        Assert.DoesNotContain(reply.Units, unit => unit.Path == Fixture.CorePath && unit.Span == otherPart);
        Assert.Contains("OMITTED_UNITS", reply.OmissionReasons);
        Assert.All(reply.Units, unit =>
        {
            Assert.Equal(Fixture.Hash(fixture.Sources[unit.Path]), unit.ContentHash);
            Assert.Equal(fixture.Sources[unit.Path].Substring(unit.Span.Start, unit.Span.Length), unit.Text);
        });
        AssertWireBudget(reply, budget);
    }

    [Fact]
    public void ExplicitCaseDistinctIdentifiersBothRemainRequested()
    {
        using var fixture = new Fixture(caseDistinct: true);
        var reply = fixture.Explore("Run run");
        Assert.True(reply.Succeeded);
        Assert.Contains(reply.Units, unit => unit.SymbolIds.Contains(fixture.RunInt));
        Assert.Contains(reply.Units, unit => unit.SymbolIds.Contains(fixture.RunString));
        Assert.Contains(reply.Units, unit => unit.Text.Contains("int run(string", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000001", true)]
    [InlineData("00000000-0000-0000-0000-000000000002", false)]
    public void TruncatedRelationsKeepMergedIdentitiesAndCandidateDocumentProvenance(string workspaceUuid, bool helperBeforeRun)
    {
        using var fixture = new Fixture(partial: true, workspaceUuid: Guid.Parse(workspaceUuid));
        Assert.Equal(helperBeforeRun ? -1 : 1, Math.Sign(StringComparer.Ordinal.Compare(fixture.Helper, fixture.RunInt)));
        var stateEdge = fixture.Snapshot.Graph.Edges.Single(edge => edge.Id == "read-state");
        var extra = Enumerable.Range(0, 12).Select(index => stateEdge with { Id = "a-state-" + index.ToString("D2", CultureInfo.InvariantCulture) });
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with
        { Edges = extra.Concat(fixture.Snapshot.Graph.Edges).ToArray() } };
        var reply = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true)
            .Execute(fixture.Request("Pump"));
        Assert.True(reply.Succeeded);
        var merged = Assert.Single(reply.Units, unit => unit.Path == Fixture.CorePath);
        Assert.True(merged.SymbolIds.Contains(fixture.Helper), reply.Text);
        Assert.Contains(fixture.Occurrence(fixture.Helper), reply.Text);
        Assert.Equal(Fixture.Hash(fixture.Sources[Fixture.CorePath]), merged.ContentHash);
        Assert.Equal(fixture.Sources[Fixture.CorePath].Substring(merged.Span.Start, merged.Span.Length), merged.Text);
        Assert.Contains(reply.Units, unit => unit.SymbolIds.Contains(fixture.Section));
        Assert.Contains("selection=documents_symbol Candidate certainty=Candidate", reply.Text);
        Assert.Contains("selection=requested certainty=Resolved", reply.Text);
        Assert.Single(reply.Text.Split('\n'), line => line.Contains(" id=" + fixture.Section + " ", StringComparison.Ordinal));
        Assert.Contains("Freshness=", reply.Text);
        Assert.Contains("saved-sha256=" + merged.ContentHash, reply.Text);
        Assert.DoesNotContain("--documents_symbol/", reply.Text);
        Assert.Contains("Relations omitted=", reply.Text);
    }

    [Fact]
    public void EdgeLimitDoesNotBlockTheForwardBodyOfAnAlreadyReturnedIncomingRelation()
    {
        using var fixture = new Fixture(workspaceUuid: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        Assert.True(StringComparer.Ordinal.Compare(fixture.Helper, fixture.RunInt) < 0);
        var direct = fixture.Snapshot.Graph.Edges.Single(edge => edge.Id == "direct-helper");
        var unadmitted = direct with { Id = "a-unadmitted", TargetNodeId = fixture.RunInt,
            TargetOccurrenceId = fixture.Occurrence(fixture.RunInt) };
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with { Edges = [unadmitted, direct] } };
        var budget = Generous with { MaxEdges = 1 };
        var reply = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true)
            .Execute(fixture.Request("Pump", budget));
        Assert.True(reply.Succeeded);
        var merged = Assert.Single(reply.Units, unit => unit.Path == Fixture.CorePath);
        Assert.True(merged.SymbolIds.Contains(fixture.Helper), reply.Text);
        var declaration = snapshot.Graph.SymbolOccurrences.Single(item => item.Id == fixture.Occurrence(fixture.Pump))
            .Declarations!.Single(location => location.SourceId == Fixture.CorePath);
        Assert.Equal(declaration.RawSpan, merged.Span);
        Assert.Equal(Fixture.Hash(fixture.Sources[Fixture.CorePath]), merged.ContentHash);
        Assert.Equal(fixture.Sources[Fixture.CorePath].Substring(merged.Span.Start, merged.Span.Length), merged.Text);
        Assert.Contains("EDGE_LIMIT", reply.OmissionReasons);
        var relation = Assert.Single(reply.Text.Split('\n'), line => line.Contains(" --calls/Resolved--> ", StringComparison.Ordinal));
        var from = Alias(fixture.RunInt);
        Assert.StartsWith(from + " --calls/Resolved--> " + Alias(fixture.Helper) + " ", relation);
        Assert.DoesNotContain(from + " --calls/Resolved--> " + from + " ", reply.Text);
        AssertWireBudget(reply, budget);

        string Alias(string id)
        {
            var identity = Assert.Single(reply.Text.Split('\n'), line => line.Contains(" id=" + fixture.Occurrence(id) + " ", StringComparison.Ordinal));
            return identity[..identity.IndexOf(' ')];
        }
    }

    [Fact]
    public void LogicalOnlyRelationsDoNotInventAVariantOccurrence()
    {
        using var fixture = new Fixture();
        var logical = fixture.Snapshot.Graph.Edges.Single(edge => edge.Id == "candidate") with
        { Id = "a-logical", SourceOccurrenceId = null, TargetOccurrenceId = null, VariantId = null, Kind = "unscoped_relation" };
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with
        { Edges = new[] { logical }.Concat(fixture.Snapshot.Graph.Edges).ToArray() } };
        var reply = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true)
            .Execute(fixture.Request(fixture.RunInt));
        var identity = Assert.Single(reply.Text.Split('\n'), line => line.Contains(" id=" + fixture.RunInt + " ", StringComparison.Ordinal));
        var alias = identity[..identity.IndexOf(' ')];
        Assert.Contains(alias + " --unscoped_relation/Candidate-->", reply.Text);
        Assert.DoesNotContain(reply.Units, unit => unit.SymbolIds.Contains(fixture.Suspect));
    }

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void HugeUnicodeUnitIsEitherWholeOrOmittedWithAnExactLargerBudgetRetry(ExploreFormat format)
    {
        using var fixture = new Fixture(huge: true, dense: true, format: format);
        var smallBudget = Generous with { MaxChars = 2048, MaxBytes = 8192 };
        var small = fixture.Explore(fixture.RunInt, budget: smallBudget);
        Assert.DoesNotContain(small.Units, unit => unit.SymbolIds.Contains(fixture.RunInt));
        Assert.NotEmpty(small.OmissionReasons);
        if (format == ExploreFormat.Audit)
        {
            Assert.Contains(Fixture.Hash(fixture.Sources[Fixture.CorePath]), small.Text);
            Assert.Contains(fixture.RunSpan.Length.ToString(CultureInfo.InvariantCulture), small.Text);
        }
        Assert.True(small.Text.Contains(fixture.RunInt, StringComparison.Ordinal)
            || small.Text.Contains(fixture.Occurrence(fixture.RunInt), StringComparison.Ordinal));
        AssertWireBudget(small, smallBudget);

        var full = fixture.Explore(fixture.RunInt + " " + fixture.RunString);
        var unit = Assert.Single(full.Units, unit => unit.SymbolIds.Contains(fixture.RunInt));
        Assert.True(unit.Span.Start <= fixture.RunSpan.Start && unit.Span.End >= fixture.RunSpan.End);
        Assert.Contains("😀", unit.Text);
        Assert.Equal(fixture.Sources[Fixture.CorePath].Substring(unit.Span.Start, unit.Span.Length), unit.Text);
        Assert.Contains(full.Units, item => item.SymbolIds.Contains(fixture.RunString));
        Assert.DoesNotContain(full.Units, item => item.Role == "saved-owner-declaration");
        AssertWireBudget(full, Generous);
    }

    [Theory]
    [InlineData("edit", ExploreFormat.Audit)]
    [InlineData("delete", ExploreFormat.Audit)]
    [InlineData("rename", ExploreFormat.Audit)]
    [InlineData("edit", ExploreFormat.Agent)]
    [InlineData("delete", ExploreFormat.Agent)]
    [InlineData("rename", ExploreFormat.Agent)]
    public void SourceChangesNeverReturnAnOldSlice(string change, ExploreFormat format)
    {
        using var fixture = new Fixture(dense: true, format: format);
        var engine = fixture.Engine();
        var timestamp = File.GetLastWriteTimeUtc(fixture.CoreFullPath);
        if (change == "edit")
        {
            File.WriteAllText(fixture.CoreFullPath, fixture.Sources[Fixture.CorePath].Replace("value + count", "value - count", StringComparison.Ordinal), Fixture.Utf8);
            File.SetLastWriteTimeUtc(fixture.CoreFullPath, timestamp);
        }
        else if (change == "delete") File.Delete(fixture.CoreFullPath);
        else File.Move(fixture.CoreFullPath, Path.Combine(fixture.Root, "src", "Renamed.cs"));

        var reply = engine.Execute(fixture.Request(fixture.RunInt + " " + fixture.RunString));
        Assert.DoesNotContain(reply.Units, unit => unit.Path == Fixture.CorePath);
        Assert.True(reply.OmissionReasons.Count > 0 || reply.Errors.Count > 0);
    }

    [Theory]
    [InlineData(false, ExploreFormat.Audit)]
    [InlineData(true, ExploreFormat.Audit)]
    [InlineData(false, ExploreFormat.Agent)]
    [InlineData(true, ExploreFormat.Agent)]
    public void RequireFreshClearsReturnedUnitsWhenTheSecondProofFailsOrCancels(bool cancel, ExploreFormat format)
    {
        using var fixture = new Fixture(format: format);
        foreach (var withPath in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var inspections = 0;
            var workspace = new QueryWorkspace(fixture.Root, _ =>
            {
                var current = ++inspections;
                if (cancel && current == 2) cancellation.Cancel();
                return new(fixture.Inventory(), current == 1);
            });
            var request = fixture.Request(withPath ? fixture.Occurrence(fixture.RunInt) : fixture.RunInt) with
            { Freshness = QueryFreshnessPolicy.RequireFresh, PathTo = withPath ? fixture.Occurrence(fixture.Helper) : null };
            var reply = fixture.Engine(workspace).Execute(request, cancellation.Token);
            Assert.True(inspections >= 2);
            Assert.False(reply.Succeeded);
            Assert.NotEmpty(reply.Errors);
            Assert.Empty(reply.Units);
            if (withPath)
            {
                var header = RetainedPathHeader(reply);
                Assert.Contains("status=incomplete", header);
                Assert.Contains("reason=query-error", header);
                Assert.DoesNotContain("Path hop ", reply.Text);
                AssertWireBudget(reply, request.Budget!);
            }
        }
    }

    [Theory]
    [InlineData(false, true, ExploreFormat.Audit)]
    [InlineData(true, false, ExploreFormat.Audit)]
    [InlineData(false, true, ExploreFormat.Agent)]
    [InlineData(true, false, ExploreFormat.Agent)]
    public void TrustAndOwnerProofAreIndependentRequirements(bool trusted, bool hasOwnerProof, ExploreFormat format)
    {
        using var fixture = new Fixture(format: format);
        var workspace = new QueryWorkspace(fixture.Root, hasOwnerProof ? _ => new(fixture.Inventory(), true) : null);
        var reply = new ExplorationService(new FixedIndex(fixture.Snapshot), workspace, trusted)
            .Execute(fixture.Request(fixture.RunInt) with { Freshness = QueryFreshnessPolicy.RequireFresh });
        Assert.False(reply.Succeeded);
        Assert.Empty(reply.Units);
        Assert.NotEmpty(reply.Errors);
    }

    [Theory]
    [InlineData(ExploreFormat.Audit)]
    [InlineData(ExploreFormat.Agent)]
    public void NewGenerationUsesNewHashesWhileTheOldPinCannotServeChangedSource(ExploreFormat format)
    {
        using var fixture = new Fixture(dense: true, format: format);
        var oldEngine = fixture.Engine();
        var changed = fixture.Sources[Fixture.CorePath].Replace("value + count", "value - count", StringComparison.Ordinal);
        File.WriteAllText(fixture.CoreFullPath, changed, Fixture.Utf8);
        var newer = fixture.NewGeneration(changed);

        var query = fixture.RunInt + " " + fixture.RunString;
        var oldReply = oldEngine.Execute(fixture.Request(query));
        Assert.DoesNotContain(oldReply.Units, unit => unit.Path == Fixture.CorePath);
        var newReply = new ExplorationService(new FixedIndex(newer), new QueryWorkspace(fixture.Root), true)
            .Execute(fixture.Request(query));
        var unit = Assert.Single(newReply.Units, unit => unit.SymbolIds.Contains(fixture.RunInt));
        Assert.Contains("value - count", unit.Text);
        Assert.DoesNotContain("value + count", unit.Text);
        Assert.Equal(Fixture.Hash(changed), unit.ContentHash);
        Assert.Equal("saved-owner-declaration", unit.Role);
    }

    [Theory]
    [InlineData(2048, 8192, false)]
    [InlineData(5500, 22000, false)]
    [InlineData(100000, 5500, false)]
    [InlineData(100000, 12000, true)]
    public void ProfilesKeepExactSelectorsAndUncertaintyWithinTheirOwnWireBudgets(int maxChars, int maxBytes, bool longPath)
    {
        using var fixture = new Fixture(partial: true, dense: true);
        var request = fixture.Request(fixture.RunInt + " " + fixture.RunString,
            Generous with { MaxChars = maxChars, MaxBytes = maxBytes });
        var path = string.Join("/", Enumerable.Repeat(new string('p', 100), 39)) + "/Pump.cs";
        var snapshot = longPath ? fixture.Snapshot with { Files = fixture.Snapshot.Files.Select(file => file.SourceId == Fixture.CorePath
            ? file with { RelativePath = path } : file).ToArray() } : fixture.Snapshot;
        // A saved-only query can display a long path without depending on OS path limits or reading it.
        var engine = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), !longPath);
        Assert.Throws<ArgumentException>(() => engine.Execute(request with { Format = (ExploreFormat)99 }));
        var audit = engine.Execute(request);
        var agent = engine.Execute(request with { Format = ExploreFormat.Agent });

        AssertWireBudget(audit, request.Budget!);
        AssertWireBudget(agent, request.Budget!);
        if (agent.Errors.Any(error => error.Code == "OUTPUT_BUDGET_TOO_SMALL"))
        {
            Assert.Empty(agent.Units);
            return;
        }
        foreach (var value in new[] { "generation=1", "snapshot=fixture-1", "coverage=Partial", "Freshness=unverified", "Scope project=" + fixture.Project,
            "variant=" + fixture.Variant, "Unverified:", "@n is reply-local" }) Assert.Contains(value, agent.Text);
        foreach (var id in new[] { fixture.RunInt, fixture.RunString })
            Assert.Single(agent.Text.Split('\n'), line => line.Contains(" selector=" + fixture.Occurrence(id), StringComparison.Ordinal));
        Assert.DoesNotContain("saved-sha256=", agent.Text);
        Assert.DoesNotContain(" proof=", agent.Text);
        Assert.DoesNotContain(" span=", agent.Text);
        Assert.Contains("Owner context adds no semantic targets", agent.Text);
        if (longPath)
        {
            Assert.Empty(agent.Units);
            Assert.Contains(path, agent.Text);
            Assert.Contains("WORKSPACE_UNTRUSTED", agent.OmissionReasons);
        }
        if (agent.Units.Count > 0) Assert.Matches(@"Source F\d+:\d+-\d+ @", agent.Text);
        if (agent.Units.Any(unit => unit.SymbolIds.Contains(fixture.Section)))
            Assert.Contains("selection=documents_symbol Candidate certainty=Candidate", agent.Text);
    }

    [Fact]
    public void AgentUsesItsOwnSourceBudgetWhileAuditKeepsItsExactWireBoundary()
    {
        using var fixture = new Fixture(huge: true);
        var snapshot = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with { Edges = [] } };
        var engine = new ExplorationService(new FixedIndex(snapshot), new QueryWorkspace(fixture.Root), true);
        var request = fixture.Request(fixture.RunInt);
        var fullAudit = engine.Execute(request);
        var fullAgent = engine.Execute(request with { Format = ExploreFormat.Agent });
        Assert.True(fullAudit.Succeeded);
        Assert.True(fullAgent.Succeeded);
        Assert.Equal(JsonSerializer.Serialize(fullAudit.Units, QueryJson.Options), JsonSerializer.Serialize(fullAgent.Units, QueryJson.Options));
        Assert.Single(fullAudit.Units);
        Assert.DoesNotContain("Retained overview", fullAudit.Text);
        Assert.Contains("Retained root ", fullAgent.Text);
        var tightAgentBudget = Generous with { MaxChars = fullAgent.WireChars - 1, MaxBytes = fullAgent.WireBytes };
        var tightAgent = engine.Execute(request with { Budget = tightAgentBudget, Format = ExploreFormat.Agent });
        Assert.True(tightAgent.Succeeded);
        Assert.Equal(JsonSerializer.Serialize(fullAgent.Units, QueryJson.Options), JsonSerializer.Serialize(tightAgent.Units, QueryJson.Options));
        Assert.DoesNotContain("Retained root ", tightAgent.Text);
        AssertWireBudget(tightAgent, tightAgentBudget);

        var auditBoundary = Generous with { MaxChars = fullAudit.WireChars, MaxBytes = fullAudit.WireBytes };
        var auditAtBoundary = engine.Execute(request with { Budget = auditBoundary, Format = ExploreFormat.Audit });
        Assert.Equal(fullAudit.Text, auditAtBoundary.Text);
        AssertWireBudget(auditAtBoundary, auditBoundary);
        var belowAuditBoundary = auditBoundary with { MaxChars = auditBoundary.MaxChars - 1 };
        var auditBelowBoundary = engine.Execute(request with { Budget = belowAuditBoundary });
        Assert.Empty(auditBelowBoundary.Units);
        Assert.Contains("OMITTED_UNITS", auditBelowBoundary.OmissionReasons);
        AssertWireBudget(auditBelowBoundary, belowAuditBoundary);

        var agentBoundary = Generous with { MaxChars = fullAgent.WireChars, MaxBytes = fullAgent.WireBytes };
        var agentAtBoundary = engine.Execute(request with { Budget = agentBoundary, Format = ExploreFormat.Agent });
        Assert.Equal(fullAgent.Text, agentAtBoundary.Text);
        Assert.Equal(JsonSerializer.Serialize(fullAgent.Units, QueryJson.Options), JsonSerializer.Serialize(agentAtBoundary.Units, QueryJson.Options));
        AssertWireBudget(agentAtBoundary, agentBoundary);

        var agent = engine.Execute(request with { Budget = belowAuditBoundary, Format = ExploreFormat.Agent });
        Assert.True(agent.Succeeded);
        Assert.Single(agent.Units);
        AssertWireBudget(agent, belowAuditBoundary);

        var choice = Assert.Single(auditBelowBoundary.Text.Split('\n'), line => line.Contains(" id=", StringComparison.Ordinal));
        var selector = choice[(choice.IndexOf(" id=", StringComparison.Ordinal) + 4)..].Split(' ')[0];
        var retried = engine.Execute(request with { Query = selector, Budget = belowAuditBoundary, Format = ExploreFormat.Agent });
        Assert.Equal(JsonSerializer.Serialize(agent.Units, QueryJson.Options), JsonSerializer.Serialize(retried.Units, QueryJson.Options));
        AssertWireBudget(retried, belowAuditBoundary);
    }

    private static HarnessEdge SavedPathEdge(Fixture fixture, string id, string source, string target)
        => new(id, source, target, fixture.Occurrence(source), fixture.Occurrence(target), fixture.Variant,
            "calls", HarnessCertainty.Resolved, "saved-path-fixture",
            fixture.Snapshot.Graph.SymbolOccurrences.Single(item => item.Id == fixture.Occurrence(source)).Location);

    private static string RetainedPathHeader(ExploreReply reply)
    {
        var header = Assert.Single(reply.Text.Split('\n'), line => line.StartsWith("Retained path status=", StringComparison.Ordinal));
        Assert.Matches(@"\bincomplete=(true|false)\b", header);
        Assert.Matches(@"\breason=\S+", header);
        if (header.Contains("status=no-witness", StringComparison.Ordinal))
        {
            Assert.Contains("hops=unknown", header);
            Assert.Contains("incomplete=false", header);
        }
        if (header.Contains("status=incomplete", StringComparison.Ordinal))
        {
            Assert.Contains("hops=unknown", header);
            Assert.Contains("incomplete=true", header);
        }
        return header;
    }

    private static void AssertWireBudget(ExploreReply reply, QueryBudget budget)
    {
        var conservative = JsonSerializer.Serialize(new
        { content = new[] { new { type = "text", text = reply.Text } }, isError = !reply.Succeeded });
        Assert.InRange(conservative.Length, 0, reply.WireChars);
        Assert.InRange(Encoding.UTF8.GetByteCount(conservative), 0, reply.WireBytes);
        Assert.InRange(reply.WireChars, 0, budget.MaxChars);
        Assert.InRange(reply.WireBytes, 0, budget.MaxBytes);
    }

    // These case-local snapshots exercise saved metadata only. Renamed labels/signatures do not describe current source bodies.
    private static IndexSnapshot RankingSnapshot(Fixture fixture, params (string Id, string Name, string Signature)[] names)
        => RenameRankingNodes(fixture.Snapshot with { Graph = fixture.Snapshot.Graph with { Edges = [] } }, names);

    private static IndexSnapshot RenameRankingNodes(IndexSnapshot snapshot, params (string Id, string Name, string Signature)[] names)
    {
        var changes = names.ToDictionary(item => item.Id, StringComparer.Ordinal);
        return snapshot with
        {
            Graph = snapshot.Graph with { Nodes = snapshot.Graph.Nodes.Select(node => changes.TryGetValue(node.Id, out var item)
                ? node with { Name = item.Name, Signature = item.Signature } : node).ToArray() },
            SearchText = snapshot.SearchText.Select(text => changes.TryGetValue(text.NodeId, out var item)
                ? text with { Name = item.Name, Signature = item.Signature } : text).ToArray()
        };
    }

    private static void AssertRankingSelectors(ExploreReply reply, params string[] expected)
    {
        Assert.True(reply.Succeeded);
        Assert.Empty(reply.Units);
        Assert.Contains("WORKSPACE_UNTRUSTED", reply.OmissionReasons);
        var actual = System.Text.RegularExpressions.Regex.Matches(reply.Text, @" selector=(hocc_[0-9a-f]{64})(?: |\r?$)",
            System.Text.RegularExpressions.RegexOptions.Multiline).Select(match => match.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal).ToArray(), actual);
    }

    private sealed class RankingIndex(IndexSnapshot snapshot, params string[] hits) : IQueryIndex
    {
        public IndexSnapshot Snapshot { get; } = snapshot;
        public List<string> Literals { get; } = [];
        public IReadOnlyList<IndexSearchHit> Search(string literal, int limit)
        {
            Literals.Add(literal);
            return hits.Select(id => new IndexSearchHit(id, 0, "fixture-hit")).ToArray();
        }
    }

    private sealed class FixedIndex(IndexSnapshot snapshot) : IQueryIndex
    {
        public IndexSnapshot Snapshot { get; } = snapshot;
        public IReadOnlyList<IndexSearchHit> Search(string literal, int limit) => [];
    }

    private sealed class ObservedIndex(Func<IndexSnapshot> read) : IQueryIndex
    {
        public IndexSnapshot Snapshot => read();
        public IReadOnlyList<IndexSearchHit> Search(string literal, int limit)
        {
            if (literal.Length > 128) throw new ArgumentException("The saved index accepts literals up to 128 characters.");
            return [];
        }
    }

    private sealed class ObservedNodes(IReadOnlyList<HarnessNode> nodes, Action observe) : IReadOnlyList<HarnessNode>
    {
        public int Count => nodes.Count;
        public HarnessNode this[int index] { get { observe(); return nodes[index]; } }
        public IEnumerator<HarnessNode> GetEnumerator()
        {
            foreach (var node in nodes) { observe(); yield return node; }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Fixture : IDisposable
    {
        public static readonly UTF8Encoding Utf8 = new(false, true);
        public const string CorePath = "src/Pump.cs", PartialPath = "src/Pump.Extra.cs", DocumentPath = "flow.md";
        public const string OtherProjectPath = "other/Pump.cs", SuspectPath = "src/Suspect.cs";
        public const string Field = "private int count = 0, total = 1;";
        public const string Markdown = "# Flow\r\n\r\nThe pump accepts a value.\r\n";
        private static readonly string FixtureBase = Path.GetFullPath(Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "sharpdeps-exploration-tests"));
        private readonly Guid workspace, document = Guid.NewGuid();
        private readonly bool caseDistinct, dense, implicitConstructor;
        private readonly ExploreFormat format;
        private readonly ExploreScopePolicy scopePolicy;
        private readonly string run, runString = "public int Run(string value) { return value.Length; }";
        private readonly string hidden;
        private readonly string? splitRunText;
        private const string HelperText = "private int Helper(int value) { return value + count; }";
        private const string FinishText = "private int Finish(int value) { return Clamp(value) + total; }";
        private const string ClampText = "private int Clamp(int value) { return Math.Max(value, 0); }";
        private const string ExtraText = "partial class Pump { public void Reset() { } }\r\n";
        public string Root { get; } = Path.Combine(FixtureBase, Guid.NewGuid().ToString("N"));
        public string CoreFullPath => Path.Combine(Root, "src", "Pump.cs");
        public Dictionary<string, string> Sources { get; } = new(StringComparer.Ordinal);
        public IndexSnapshot Snapshot { get; }
        public string Project => HarnessIdentity.ProjectId(workspace, "src/Demo.csproj");
        public string OtherProject => HarnessIdentity.ProjectId(workspace, "other/Demo.csproj");
        public string Variant => HarnessIdentity.VariantId(workspace, Project, "net10.0", "Debug");
        public string AlternateVariant => HarnessIdentity.VariantId(workspace, Project, "net10.0", "Release");
        public string SecondDebugVariant => HarnessIdentity.VariantId(workspace, Project, "net10.0", "Debug", "x64", "win-x64");
        public string SecondReleaseVariant => HarnessIdentity.VariantId(workspace, Project, "net10.0", "Release", "x64", "win-x64");
        public string OtherVariant => HarnessIdentity.VariantId(workspace, OtherProject, "net10.0", "Debug");
        public string Pump => Symbol(Project, "type", "T:Demo.Pump");
        public string Constructor => Symbol(Project, "method", "M:Demo.Pump.#ctor");
        public string RunInt => Symbol(Project, "method", "M:Demo.Pump.Run(System.Int32)");
        public string RunString => Symbol(Project, "method", caseDistinct ? "M:Demo.Pump.run(System.String)" : "M:Demo.Pump.Run(System.String)");
        public string Orphan => Symbol(Project, "method", "M:Demo.Pump.Orphan()");
        public string Count => Symbol(Project, "field", "F:Demo.Pump.count");
        public string Total => Symbol(Project, "field", "F:Demo.Pump.total");
        public string Helper => Symbol(Project, "method", "M:Demo.Pump.Helper(System.Int32)");
        public string Finish => Symbol(Project, "method", "M:Demo.Pump.Finish(System.Int32)");
        public string Clamp => Symbol(Project, "method", "M:Demo.Pump.Clamp(System.Int32)");
        public string Suspect => Symbol(Project, "method", "M:Demo.Suspect.Hidden()");
        public string Section => HarnessIdentity.SectionId(workspace, HarnessIdentity.DocumentId(workspace, document), "flow");
        public HarnessRawSpan RunSpan => Span(CorePath, run);

        public Fixture(bool addScopes = false, bool partial = false, bool huge = false, bool caseDistinct = false,
            bool dense = false, string ownerGap = "", bool splitRun = false, bool implicitConstructor = false,
            string suspectPadding = "", Guid? workspaceUuid = null, ExploreFormat format = ExploreFormat.Audit,
            ExploreScopePolicy scopePolicy = ExploreScopePolicy.RequireUnique)
        {
            workspace = workspaceUuid ?? Guid.NewGuid();
            this.caseDistinct = caseDistinct;
            this.dense = dense;
            this.implicitConstructor = implicitConstructor;
            this.format = format;
            this.scopePolicy = scopePolicy;
            hidden = "public void Hidden() { " + suspectPadding + "}";
            if (splitRun)
            {
                runString = "public partial int Run(string value);";
                splitRunText = "public partial int Run(string value)\r\n{\r\n"
                    + string.Concat(Enumerable.Repeat("value += \"検証😀\";\r\n", 650)) + "return value.Length;\r\n}";
            }
            if (caseDistinct) runString = runString.Replace("Run(", "run(", StringComparison.Ordinal);
            run = "public int Run(int value)\r\n{\r\nint Next() => value + count;\r\n// 😀 \" \\\r\n"
                + (huge ? string.Concat(Enumerable.Repeat("value += 1; // 検証😀\r\n", 650)) : "")
                + "return " + (dense ? "Finish(Helper(Next()))" : "Helper(Next())") + ";\r\n}";
            Sources.Add(CorePath, "namespace Demo;\r\npartial class Pump\r\n{\r\n" + Field + "\r\n" + run + "\r\n"
                + ownerGap + runString + "\r\n" + HelperText + "\r\n"
                + (dense ? FinishText + "\r\n" + ClampText + "\r\n" : "")
                + (dense ? "" : "public void Unrelated() { throw new NotImplementedException(); }\r\n") + "}\r\n");
            Sources.Add(PartialPath, splitRunText is null ? ExtraText : "partial class Pump\r\n{\r\n" + splitRunText + "\r\n}\r\n");
            Sources.Add(SuspectPath, "class Suspect { " + hidden + " }\r\n");
            Sources.Add(DocumentPath, Markdown);
            if (addScopes) Sources.Add(OtherProjectPath, "class Pump { public int Run(int value) { return value; } }\r\n");
            foreach (var (path, text) in Sources)
            {
                var full = Path.Combine(Root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text, Utf8);
            }
            Snapshot = Create(addScopes, partial);
            IndexSnapshotValidator.Validate(Snapshot);
        }

        public ExploreRequest Request(string query, QueryBudget? budget = null, string? pathPrefix = null)
            => new(query, Project, Variant, pathPrefix, Budget: budget ?? Generous, Format: format, ScopePolicy: scopePolicy);
        public ExplorationService Engine(QueryWorkspace? workspaceReader = null)
            => new(new FixedIndex(Snapshot), workspaceReader ?? new QueryWorkspace(Root), true);
        public ExploreReply Explore(string query, QueryBudget? budget = null, string? pathPrefix = null)
            => Engine().Execute(Request(query, budget, pathPrefix));
        public string[] Inventory() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        public string Occurrence(string id, string? variant = null)
            => HarnessIdentity.SymbolOccurrenceId(workspace, id, variant ?? Variant);

        public IndexSnapshot NewGeneration(string changed)
        {
            HarnessLocation? Update(HarnessLocation? location) => location?.SourceId == CorePath
                ? location with { ContentHash = Hash(changed) } : location;
            return Snapshot with
            {
                Graph = Snapshot.Graph with
                {
                    Generation = 2, SnapshotId = "fixture-2",
                    Nodes = Snapshot.Graph.Nodes.Select(node => node with { Location = Update(node.Location) }).ToArray(),
                    SymbolOccurrences = Snapshot.Graph.SymbolOccurrences.Select(occurrence => occurrence with
                    { Location = Update(occurrence.Location), Declarations = occurrence.Declarations?.Select(location => Update(location)!).ToArray() }).ToArray(),
                    Edges = Snapshot.Graph.Edges.Select(edge => edge with { Evidence = Update(edge.Evidence) }).ToArray()
                },
                Files = Snapshot.Files.Select(file => file.SourceId == CorePath
                    ? file with { ContentHash = Hash(changed), ByteLength = Utf8.GetByteCount(changed), Utf16Length = changed.Length } : file).ToArray()
            };
        }

        private IndexSnapshot Create(bool addScopes, bool partial)
        {
            var workspaceId = HarnessIdentity.WorkspaceId(workspace);
            var documentId = HarnessIdentity.DocumentId(workspace, document);
            var nodes = new List<HarnessNode>
            {
                new(workspaceId, HarnessNodeKind.Workspace, "workspace", null, null),
                new(Project, HarnessNodeKind.Project, "Demo", workspaceId, null),
                new(Pump, HarnessNodeKind.Type, "Demo.Pump", Project, null, "T:Demo.Pump"),
                new(documentId, HarnessNodeKind.Document, DocumentPath, workspaceId, Location(DocumentPath, new(0, Markdown.Length))),
                new(Section, HarnessNodeKind.Section, "Flow", documentId, Location(DocumentPath, new(0, Markdown.Length)))
            };
            var occurrences = new List<HarnessSymbolOccurrence>();
            var variants = new List<HarnessVariant> { new(Variant, Project, "net10.0", "Debug", null, null, Hash("config")) };
            AddOccurrence(Pump, Variant, [Location(CorePath, new(Sources[CorePath].IndexOf("partial class", StringComparison.Ordinal), Sources[CorePath].Length - Sources[CorePath].IndexOf("partial class", StringComparison.Ordinal) - 2)), Location(PartialPath, new(0, Sources[PartialPath].Length - 2))]);
            if (implicitConstructor)
            {
                nodes.Add(new(Constructor, HarnessNodeKind.Member, ".ctor", Pump, null, "M:Demo.Pump.#ctor"));
                AddOccurrence(Constructor, Variant, [occurrences.Single(item => item.LogicalSymbolId == Pump).Declarations![0]]);
            }
            AddMember(RunInt, "Run", "M:Demo.Pump.Run(System.Int32)", run);
            AddMember(RunString, caseDistinct ? "run" : "Run", caseDistinct ? "M:Demo.Pump.run(System.String)" : "M:Demo.Pump.Run(System.String)", runString, splitRunText);
            AddMember(Count, "count", "F:Demo.Pump.count", Field);
            AddMember(Total, "total", "F:Demo.Pump.total", Field);
            AddMember(Helper, "Helper", "M:Demo.Pump.Helper(System.Int32)", HelperText);
            if (dense)
            {
                AddMember(Finish, "Finish", "M:Demo.Pump.Finish(System.Int32)", FinishText);
                AddMember(Clamp, "Clamp", "M:Demo.Pump.Clamp(System.Int32)", ClampText);
            }
            var suspectType = Symbol(Project, "type", "T:Demo.Suspect");
            nodes.Add(new(suspectType, HarnessNodeKind.Type, "Demo.Suspect", Project, null, "T:Demo.Suspect"));
            nodes.Add(new(Suspect, HarnessNodeKind.Member, "Hidden", suspectType, null, "M:Demo.Suspect.Hidden()"));
            AddOccurrence(suspectType, Variant, [Location(SuspectPath, new(0, Sources[SuspectPath].Length - 2))]);
            AddOccurrence(Suspect, Variant, [Location(SuspectPath, Span(SuspectPath, hidden))]);
            var edges = new List<HarnessEdge>
            {
                Edge("read-state", RunInt, Count, "reads"), Edge("direct-helper", RunInt, Helper, "calls"),
                Edge("helper-state", Helper, Count, "reads"), Edge("candidate", RunInt, Suspect, "candidate_relation", HarnessCertainty.Candidate),
                new("doc-link", Section, RunInt, null, Occurrence(RunInt), Variant, "documents_symbol",
                    HarnessCertainty.Candidate, "markdown-explicit", Location(DocumentPath, new(0, Markdown.Length)))
            };
            if (dense)
            {
                edges.Add(Edge("finish", RunInt, Finish, "calls"));
                edges.Add(Edge("clamp", Finish, Clamp, "calls"));
                edges.Add(Edge("read-total", Finish, Total, "reads"));
            }
            if (addScopes)
            {
                variants.Add(new(AlternateVariant, Project, "net10.0", "Release", null, null, Hash("release")));
                foreach (var original in occurrences.Where(occurrence => occurrence.VariantId == Variant).ToArray())
                    AddOccurrence(original.LogicalSymbolId, AlternateVariant, original.Declarations!);
                nodes.Add(new(OtherProject, HarnessNodeKind.Project, "Other", workspaceId, null));
                variants.Add(new(OtherVariant, OtherProject, "net10.0", "Debug", null, null, Hash("other")));
                var otherPump = Symbol(OtherProject, "type", "T:Demo.Pump");
                var otherRun = Symbol(OtherProject, "method", "M:Demo.Pump.Run(System.Int32)");
                nodes.Add(new(otherPump, HarnessNodeKind.Type, "Demo.Pump", OtherProject, null, "T:Demo.Pump"));
                nodes.Add(new(otherRun, HarnessNodeKind.Member, "Run", otherPump, null, "M:Demo.Pump.Run(System.Int32)"));
                AddOccurrence(otherPump, OtherVariant, [Location(OtherProjectPath, new(0, Sources[OtherProjectPath].Length - 2))]);
                AddOccurrence(otherRun, OtherVariant, [Location(OtherProjectPath, Span(OtherProjectPath, "public int Run(int value) { return value; }"))]);
            }
            var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
                HarnessGraphContract.IdentityVersion, workspaceId, "fixture-1", 1,
                partial ? HarnessCoverage.Partial : HarnessCoverage.CompleteWithinScope,
                variants, nodes, occurrences, edges, [],
                new("1.0.0", Hash(Markdown), "parser-1", "contract-1", "profile-1", Hash("options")),
                partial ? [new("roslyn.dynamicReferences", 1)] : []);
            return new(graph, Sources.Select(pair => new IndexFile(pair.Key, pair.Key, pair.Key.EndsWith(".md", StringComparison.Ordinal) ? "markdown" : "source",
                    Hash(pair.Value), Utf8.GetByteCount(pair.Value), pair.Value.Length)).ToArray(),
                [new(document, DocumentPath, null, Hash(Markdown), "scope", "parser-1", "contract-1", "profile-1", Hash("options"),
                    [new("flow", "flow", "1:Flow", Hash(Markdown))])], [],
                nodes.Select(node => new IndexSearchText(node.Id, node.Name, node.Signature ?? "", "", "", "")).ToArray());

            void AddMember(string id, string name, string signature, string text, string? otherPart = null)
            {
                nodes.Add(new(id, HarnessNodeKind.Member, name, Pump, null, signature));
                var declarations = new List<HarnessLocation> { Location(CorePath, Span(CorePath, text)) };
                if (otherPart is not null) declarations.Add(Location(PartialPath, Span(PartialPath, otherPart)));
                AddOccurrence(id, Variant, declarations);
            }
            void AddOccurrence(string id, string variant, IReadOnlyList<HarnessLocation> declarations)
                => occurrences.Add(new(Occurrence(id, variant), id, variant, declarations.FirstOrDefault(), declarations));
        }

        private HarnessEdge Edge(string id, string from, string to, string kind, HarnessCertainty certainty = HarnessCertainty.Resolved)
            => new(id, from, to, Occurrence(from), Occurrence(to), Variant, kind, certainty, "roslyn-operation",
                Location(CorePath, from == Helper ? Span(CorePath, HelperText) : from == Finish ? Span(CorePath, FinishText) : RunSpan));
        private HarnessLocation Location(string path, HarnessRawSpan span) => new(path, null, Hash(Sources[path]), span);
        private HarnessRawSpan Span(string path, string text) => new(Sources[path].IndexOf(text, StringComparison.Ordinal), text.Length);
        private string Symbol(string project, string kind, string signature) => HarnessIdentity.LogicalSymbolId(workspace, project, kind, signature);
        public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(text))).ToLowerInvariant();

        public void Dispose()
        {
            var expected = FixtureBase + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its dedicated directory.");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
