namespace SharpDeps.Query.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Index;
using Xunit;

public sealed class QueryTests
{
    private static readonly QueryBudget Generous = new(MaxNodes: 100, MaxEdges: 100, MaxDepth: 8,
        MaxMilliseconds: 10000, MaxChars: 100000, MaxBytes: 400000);

    [Fact]
    public void CursorPinsRealReaderGenerationAndRejectsChangedFiltersAndTampering()
    {
        using var fixture = new StoreFixture();
        using var writer = fixture.Writer();
        writer.Commit(IndexStage.Create(fixture.Snapshot(1)));
        using var oldReader = fixture.Reader();
        var oldQuery = fixture.Query(oldReader);
        var request = new QueryRequest("first", QueryKind.Search, Term: "Service", PageSize: 1, Budget: Generous);
        var first = oldQuery.Execute(request);
        Assert.True(first.Succeeded);
        var cursor = Assert.IsType<string>(first.Envelope.NextCursor);
        var firstId = Assert.Single(first.Envelope.Items).Id;
        var next = oldQuery.Execute(request with { RequestId = "next", Cursor = cursor });
        Assert.True(next.Succeeded);
        Assert.NotEqual(firstId, Assert.Single(next.Envelope.Items).Id);
        Error(oldQuery.Execute(request with { Cursor = cursor, Scope = new(ProjectId: fixture.ProjectId) }), "CURSOR_FILTER_CHANGED");
        Error(oldQuery.Execute(request with { Cursor = cursor[..^2] + "AA" }), "INVALID_CURSOR");

        writer.Commit(IndexStage.Create(fixture.Snapshot(2)));
        var stillPinned = oldQuery.Execute(request with { Cursor = cursor });
        Assert.True(stillPinned.Succeeded);
        Assert.Equal(1, stillPinned.Envelope.Snapshot.Generation);
        using var newReader = fixture.Reader();
        var newQuery = fixture.Query(newReader);
        Error(newQuery.Execute(request with { Cursor = cursor }), "CURSOR_SNAPSHOT_EXPIRED");
        Assert.Equal(2, newQuery.Execute(request).Envelope.Snapshot.Generation);
    }

    [Fact]
    public void SearchPrioritizesExactNamesAndKeepsProjectVariantPathAndJapaneseScopes()
    {
        using var fixture = new StoreFixture();
        using var writer = fixture.Writer();
        writer.Commit(IndexStage.Create(fixture.Snapshot(1, partialDeclarations: true)));
        using var reader = fixture.Reader();
        var query = fixture.Query(reader);
        var all = query.Execute(new("names", QueryKind.Search, Term: "Service", Budget: Generous));
        Assert.True(all.Succeeded);
        Assert.Equal(new[] { fixture.A, fixture.D }.Order(StringComparer.Ordinal), all.Envelope.Items.Take(2).Select(i => i.Id));
        Assert.All(all.Envelope.Items.Take(2), i => Assert.Equal("exact name/signature", i.MatchMethod));
        Assert.Equal(fixture.B, all.Envelope.Items.Last().Id);

        var scoped = query.Execute(new("scope", QueryKind.Search, Term: "Service",
            Scope: new(HarnessNodeKind.Member, fixture.ProjectId, "src", [fixture.Variant]), Budget: Generous));
        Assert.Equal(new[] { fixture.A, fixture.B }, scoped.Envelope.Items.Select(i => i.Id));
        var other = query.Execute(new("other", QueryKind.Search, Term: "Service",
            Scope: new(ProjectId: fixture.OtherProjectId, VariantIds: [fixture.OtherVariant]), Budget: Generous));
        Assert.Equal(fixture.D, Assert.Single(other.Envelope.Items).Id);
        var exact = query.Execute(new("id", QueryKind.Search, Term: fixture.A, Budget: Generous));
        Assert.Equal("exact ID", Assert.Single(exact.Envelope.Items).MatchMethod);

        var japanese = query.Execute(new("japanese", QueryKind.Search, Term: "設定の手順",
            Scope: new(Kind: HarnessNodeKind.Section, PathPrefix: "readme.md"), Budget: Generous));
        Assert.Equal(fixture.SectionId, Assert.Single(japanese.Envelope.Items).Id);
        Assert.Equal("readme.md", japanese.Envelope.Items[0].Path);
        var detail = query.Execute(new("declarations", QueryKind.Symbol, NodeId: fixture.A,
            Scope: new(VariantIds: [fixture.Variant]), Budget: Generous));
        Assert.Equal(fixture.Occurrence(fixture.A), Assert.Single(detail.Envelope.Items, i => i.Occurrence is not null).Id);
        var partial = query.Execute(new("partial", QueryKind.Search, Term: "Service",
            Scope: new(HarnessNodeKind.Member, fixture.ProjectId, "other", [fixture.AlternateVariant]), Budget: Generous));
        var partialNode = Assert.Single(partial.Envelope.Items);
        Assert.Equal(fixture.A, partialNode.Id);
        Assert.Equal("other/Other.cs", partialNode.Path);
        var wrongVariant = query.Execute(new("partial-wrong", QueryKind.Search, Term: "Service",
            Scope: new(HarnessNodeKind.Member, fixture.ProjectId, "other", [fixture.Variant]), Budget: Generous));
        Assert.Empty(wrongVariant.Envelope.Items);

        // A blank tree request uses Browse; literal Search still requires a term.
        var browse = new QueryRequest("browse", QueryKind.Browse,
            Scope: new(HarnessNodeKind.Member, fixture.ProjectId, "src", [fixture.Variant]), PageSize: 1, Budget: Generous);
        var expected = new[] { fixture.A, fixture.B, fixture.C, fixture.E }.Order(StringComparer.Ordinal).ToArray();
        var firstPage = query.Execute(browse);
        Assert.Equal(expected[0], Assert.Single(firstPage.Envelope.Items).Id);
        var browseCursor = Assert.IsType<string>(firstPage.Envelope.NextCursor);
        var secondPage = query.Execute(browse with { Cursor = browseCursor });
        Assert.Equal(expected[1], Assert.Single(secondPage.Envelope.Items).Id);
        Error(query.Execute(browse with { Cursor = browseCursor, Scope = new(VariantIds: [fixture.AlternateVariant]) }), "CURSOR_FILTER_CHANGED");

        // MaxNodes applies per page, so continuations must pass the first capped node.
        var boundedBrowse = browse with { PageSize = 100, Budget = Generous with { MaxNodes = 1 } };
        var seen = new List<string>();
        string? continuation = null;
        do
        {
            var page = query.Execute(boundedBrowse with { Cursor = continuation });
            Assert.True(page.Succeeded);
            Assert.Equal(1, page.Envelope.Snapshot.Generation);
            Assert.Equal(1, page.Envelope.Budget.UsedNodes);
            Assert.Empty(page.Envelope.Candidates);
            Assert.Empty(page.Envelope.Unresolved);
            var item = Assert.Single(page.Envelope.Items);
            Assert.NotNull(item.Node);
            Assert.Null(item.Edge);
            Assert.Equal("browse", item.Reason);
            seen.Add(item.Id);
            continuation = page.Envelope.NextCursor;
            Assert.True(seen.Count <= expected.Length); // Detect a cursor that loops or duplicates pages.
        } while (continuation is not null);
        Assert.Equal(expected, seen);
        var namespaces = browse with { Scope = new(Kind: HarnessNodeKind.Namespace, VariantIds: [fixture.Variant]), PageSize = 100 };
        Assert.Equal(new[] { fixture.OuterNamespaceId, fixture.NamespaceId }.Order(StringComparer.Ordinal),
            query.Execute(namespaces).Envelope.Items.Select(i => i.Id));
        Assert.Empty(query.Execute(namespaces with { Scope = namespaces.Scope! with { ProjectId = fixture.OtherProjectId } }).Envelope.Items);
        Assert.Empty(query.Execute(namespaces with { Scope = namespaces.Scope! with { PathPrefix = "src" } }).Envelope.Items);
        // Browse adds containers using saved ancestry; ordinary symbol search keeps occurrence filtering.
        Assert.Empty(query.Execute(new("namespace-search", QueryKind.Search, Term: "App",
            Scope: namespaces.Scope, Budget: Generous)).Envelope.Items);
        Assert.Throws<ArgumentException>(() => query.Execute(browse with { Term = "Service" }));
        Assert.Throws<ArgumentException>(() => query.Execute(new("empty-search", QueryKind.Search, Budget: Generous)));
    }

    [Fact]
    public void CallsConstructsCandidatesAndUnresolvedRemainSeparateAcrossCyclesAndBudgets()
    {
        using var fixture = new StoreFixture();
        using var writer = fixture.Writer();
        writer.Commit(IndexStage.Create(fixture.Snapshot(1)));
        using var reader = fixture.Reader();
        var query = fixture.Query(reader);
        var request = new QueryRequest("walk", QueryKind.Callees, NodeId: fixture.A,
            IncludeCandidates: true, Budget: Generous);
        var reply = query.Execute(request);
        Assert.True(reply.Succeeded);
        Assert.Contains(reply.Envelope.Items, i => i.Edge?.Id == "edge-construct");
        Assert.Contains(reply.Envelope.Candidates, i => i.Edge?.Id == "edge-candidate");
        Assert.Contains(reply.Envelope.Unresolved, i => i.Edge?.Id == "edge-unresolved");
        Assert.DoesNotContain(reply.Envelope.Items, i => i.Edge?.Certainty is HarnessCertainty.Candidate or HarnessCertainty.Unresolved);
        Assert.All(reply.Envelope.Candidates, i => Assert.Equal(HarnessCertainty.Candidate, i.Certainty));
        Assert.All(reply.Envelope.Unresolved, i => Assert.Equal(HarnessCertainty.Unresolved, i.Certainty));
        Assert.Equal(6, All(reply).Count(i => i.Edge is not null));
        Assert.Equal(6, All(reply).Where(i => i.Edge is not null).Select(i => i.Id).Distinct().Count());
        Assert.DoesNotContain(All(reply), i => i.Edge?.Id == "edge-behind-candidate");

        var strict = query.Execute(request with { IncludeCandidates = false });
        Assert.Empty(strict.Envelope.Candidates);
        Assert.Empty(strict.Envelope.Unresolved);
        Assert.Contains(strict.Envelope.Items, i => i.Edge?.Id == "edge-construct");
        var callers = query.Execute(new("callers", QueryKind.Callers, NodeId: fixture.B, Budget: Generous));
        Assert.Contains(callers.Envelope.Items, i => i.Edge?.Id == "edge-call");
        var impact = query.Execute(new("impact", QueryKind.Impact, NodeId: fixture.B, Budget: Generous));
        Assert.Contains(impact.Envelope.Items, i => i.Edge is not null && i.Reason.StartsWith("impact review candidate", StringComparison.Ordinal) && i.ViaEdgeIds.Count > 0);

        var nodes = query.Execute(request with { Budget = Generous with { MaxNodes = 1 } });
        Assert.Contains("NODE_LIMIT", nodes.Envelope.TruncationReasons);
        Assert.True(nodes.Envelope.Budget.UsedNodes <= 1);
        var edges = query.Execute(request with { Budget = Generous with { MaxEdges = 1 } });
        Assert.Contains("EDGE_LIMIT", edges.Envelope.TruncationReasons);
        Assert.True(edges.Envelope.Budget.UsedEdges <= 1);
        var depth = query.Execute(request with { IncludeCandidates = false, Budget = Generous with { MaxDepth = 1 } });
        Assert.Contains("DEPTH_LIMIT", depth.Envelope.TruncationReasons);
        Assert.DoesNotContain(All(depth), i => i.Edge?.Id == "edge-next");
    }

    [Fact]
    public void WalkKeepsOccurrenceContinuityAcrossVariantsDirectionsAndProjects()
    {
        using var fixture = new StoreFixture();
        using var writer = fixture.Writer();
        var saved = fixture.Snapshot(1, withoutEdges: true, partialDeclarations: true);
        var alternateLocation = saved.Graph.SymbolOccurrences.Single(o => o.LogicalSymbolId == fixture.D).Location!;
        saved = saved with { Graph = saved.Graph with { SymbolOccurrences = [.. saved.Graph.SymbolOccurrences,
            .. saved.Graph.SymbolOccurrences.Where(o => new[] { fixture.B, fixture.C, fixture.E }.Contains(o.LogicalSymbolId))
                .Select(o => o with { Id = fixture.Occurrence(o.LogicalSymbolId, fixture.AlternateVariant),
                    VariantId = fixture.AlternateVariant, Location = alternateLocation, Declarations = [alternateLocation] })] } };
        void Commit(long generation, params HarnessEdge[] edges)
            => writer.Commit(IndexStage.Create(saved with { Graph = saved.Graph with {
                Generation = generation, SnapshotId = "snapshot-" + generation, Edges = edges } }));
        HarnessEdge Edge(string id, string from, string to, string sourceVariant, string? targetVariant)
            => new(id, from, to, fixture.Occurrence(from, sourceVariant),
                targetVariant is null ? null : fixture.Occurrence(to, targetVariant), sourceVariant,
                "calls", HarnessCertainty.Resolved, "roslyn-operation", null);

        var first = Edge("edge-00-first", fixture.A, fixture.B, fixture.Variant, fixture.Variant);
        var incompatible = Edge("edge-02-tail", fixture.B, fixture.C, fixture.AlternateVariant, fixture.AlternateVariant);
        Commit(1, first, incompatible);
        using var disconnectedReader = fixture.Reader();
        var disconnected = fixture.Query(disconnectedReader);
        foreach (var scope in new QueryScope?[] { null, new(VariantIds: [fixture.Variant, fixture.AlternateVariant]) })
        {
            foreach (var kind in new[] { QueryKind.Callees, QueryKind.Context, QueryKind.Impact })
            {
                var request = new QueryRequest("disconnected", kind, NodeId: fixture.A, Scope: scope,
                    Dependents: false, Budget: Generous);
                var reply = disconnected.Execute(request);
                Assert.True(reply.Succeeded);
                Assert.Contains(All(reply), i => i.Edge?.Id == first.Id);
                Assert.DoesNotContain(All(reply), i => i.Id == fixture.C || i.Edge?.Id == incompatible.Id
                    || i.Occurrence?.Id == fixture.Occurrence(fixture.B, fixture.AlternateVariant));
                var shallow = disconnected.Execute(request with { Budget = Generous with { MaxDepth = 1 } });
                Assert.DoesNotContain("DEPTH_LIMIT", shallow.Envelope.TruncationReasons);
            }
            foreach (var kind in new[] { QueryKind.Callers, QueryKind.Impact })
            {
                var reply = disconnected.Execute(new("reverse-disconnected", kind, NodeId: fixture.C,
                    Scope: scope, Budget: Generous));
                Assert.True(reply.Succeeded);
                Assert.Contains(All(reply), i => i.Edge?.Id == incompatible.Id);
                Assert.DoesNotContain(All(reply), i => i.Id == fixture.A || i.Edge?.Id == first.Id);
            }
        }

        // Visiting B in one variant must not suppress a separately reachable B occurrence.
        var alternate = Edge("edge-01-alternate", fixture.A, fixture.B, fixture.AlternateVariant, fixture.AlternateVariant);
        Commit(2, first, alternate, incompatible);
        using var dualReader = fixture.Reader();
        var dual = fixture.Query(dualReader).Execute(new("dual", QueryKind.Callees, NodeId: fixture.A, Budget: Generous));
        Assert.True(dual.Succeeded);
        Assert.Equal(new[] { alternate.Id, incompatible.Id }, Assert.Single(All(dual), i => i.Edge?.Id == incompatible.Id).ViaEdgeIds);
        Assert.Contains(dual.Envelope.Items, i => i.Id == fixture.C && i.Certainty == HarnessCertainty.Resolved);

        // Explicit cross-project targets select the real destination occurrence, not the source variant.
        var cross = Edge("edge-00-cross", fixture.A, fixture.D, fixture.Variant, fixture.OtherVariant);
        var back = Edge("edge-01-back", fixture.D, fixture.C, fixture.OtherVariant, fixture.AlternateVariant);
        var tail = Edge("edge-02-selected-tail", fixture.C, fixture.E, fixture.AlternateVariant, fixture.AlternateVariant);
        var wrong = Edge("edge-03-wrong-tail", fixture.C, fixture.B, fixture.Variant, fixture.Variant);
        var outside = Edge("edge-00-outside", fixture.D, fixture.C, fixture.OtherVariant, fixture.Variant);
        Commit(3, cross, back, tail, wrong);
        using var crossReader = fixture.Reader();
        var crossQuery = fixture.Query(crossReader);
        foreach (var scope in new QueryScope?[] { null, new(VariantIds: [fixture.Variant, fixture.AlternateVariant, fixture.OtherVariant]) })
        {
            var forward = crossQuery.Execute(new("cross", QueryKind.Callees, NodeId: fixture.A, Scope: scope, Budget: Generous));
            Assert.True(forward.Succeeded);
            Assert.Equal(new[] { cross.Id, back.Id, tail.Id }, Assert.Single(All(forward), i => i.Edge?.Id == tail.Id).ViaEdgeIds);
            Assert.DoesNotContain(All(forward), i => i.Edge?.Id == wrong.Id);
            var reverse = crossQuery.Execute(new("cross-reverse", QueryKind.Callers, NodeId: fixture.E, Scope: scope, Budget: Generous));
            Assert.True(reverse.Succeeded);
            Assert.Equal(new[] { tail.Id, back.Id, cross.Id }, Assert.Single(All(reverse), i => i.Edge?.Id == cross.Id).ViaEdgeIds);
            var context = fixture.Query(crossReader, fixture.WorkspaceReader(), trusted: true).Execute(
                new("cross-context", QueryKind.Context, NodeId: fixture.A, Scope: scope, Budget: Generous));
            Assert.True(context.Succeeded);
            Assert.Equal(alternateLocation, Assert.Single(All(context), i => i.Id == fixture.C).Evidence);
            Assert.All(All(context).Where(i => i.Occurrence?.LogicalSymbolId == fixture.C),
                i => Assert.Equal(fixture.AlternateVariant, i.Occurrence!.VariantId));
            Assert.All(All(context).Where(i => i.Snippet is not null && i.Path == "src/App.cs"),
                i => Assert.Equal(new HarnessRawSpan(0, 10), i.Snippet!.Span));
        }
        Commit(4, back, tail, outside);
        using var pathReader = fixture.Reader();
        var pathScoped = fixture.Query(pathReader).Execute(new("occurrence-path", QueryKind.Callees, NodeId: fixture.D,
            Scope: new(PathPrefix: "other", VariantIds: [fixture.Variant, fixture.AlternateVariant, fixture.OtherVariant]), Budget: Generous));
        Assert.True(pathScoped.Succeeded);
        Assert.Contains(All(pathScoped), i => i.Edge?.Id == back.Id);
        Assert.DoesNotContain(All(pathScoped), i => i.Edge?.Id == outside.Id);

        // A missing target occurrence does not authorize guessing B's variant from its logical ID.
        Commit(5, first with { TargetOccurrenceId = null },
            Edge("edge-01-guessed-tail", fixture.B, fixture.C, fixture.Variant, fixture.Variant));
        using var unknownReader = fixture.Reader();
        var unknown = fixture.Query(unknownReader).Execute(new("unknown", QueryKind.Callees, NodeId: fixture.A, Budget: Generous));
        Assert.True(unknown.Succeeded);
        Assert.Contains(All(unknown), i => i.Edge?.Id == first.Id);
        Assert.DoesNotContain(All(unknown), i => i.Id == fixture.C || i.Edge?.Id == "edge-01-guessed-tail");
        Assert.Null(Assert.Single(All(unknown), i => i.Id == fixture.B).Evidence);

        // Occurrence-less Markdown nodes also cannot silently connect two project variants.
        var document = new HarnessEdge("edge-00-document", fixture.A, fixture.SectionId, fixture.Occurrence(fixture.A),
            null, fixture.Variant, "mentions", HarnessCertainty.Resolved, "fixture", null);
        var otherMention = new HarnessEdge("edge-01-other-mention", fixture.SectionId, fixture.B, null,
            fixture.Occurrence(fixture.B, fixture.AlternateVariant), fixture.AlternateVariant,
            "mentions", HarnessCertainty.Resolved, "fixture", null);
        Commit(6, document, otherMention);
        using var documentReader = fixture.Reader();
        var documentReply = fixture.Query(documentReader).Execute(new("document", QueryKind.Context, NodeId: fixture.A, Budget: Generous));
        Assert.True(documentReply.Succeeded);
        Assert.Contains(All(documentReply), i => i.Edge?.Id == document.Id);
        Assert.DoesNotContain(All(documentReply), i => i.Id == fixture.B || i.Edge?.Id == otherMention.Id);

        // The real Markdown producer's variant-unspecified logical relationship stays visible as a leaf.
        var logicalMention = new HarnessEdge("edge-00-logical-mention", fixture.SectionId, fixture.A, null, null,
            null, "mentions_symbol", HarnessCertainty.Resolved, "markdown-symbol-resolver", null);
        Commit(7, logicalMention, otherMention);
        using var logicalReader = fixture.Reader();
        var logical = fixture.Query(logicalReader).Execute(new("logical-mention", QueryKind.Context, NodeId: fixture.A, Budget: Generous));
        Assert.True(logical.Succeeded);
        Assert.Contains(All(logical), i => i.Edge?.Id == logicalMention.Id);
        Assert.Contains(All(logical), i => i.Id == fixture.SectionId && i.Evidence?.SourceId == "readme.md");
        Assert.DoesNotContain(All(logical), i => i.Id == fixture.B || i.Edge?.Id == otherMention.Id);
    }

    [Fact]
    public void FinalJsonIncludesEnvelopeAndEscapesInCapsAndIncompleteQueriesFail()
    {
        using var fixture = new StoreFixture();
        using var writer = fixture.Writer();
        var snapshot = fixture.Snapshot(1, wideLabel: true);
        writer.Commit(IndexStage.Create(snapshot));
        using var reader = fixture.Reader();
        var query = fixture.Query(reader);
        var cap = Generous with { MaxChars = 3000, MaxBytes = 3000 };
        var request = new QueryRequest("wire", QueryKind.Symbol, NodeId: fixture.A, Budget: cap);
        var reply = query.Execute(request);
        using var json = JsonDocument.Parse(reply.Json);
        Assert.Equal("1", json.RootElement.GetProperty("apiVersion").GetString());
        Assert.Equal(reply.Json.Length, reply.Envelope.Budget.UsedChars);
        Assert.Equal(Encoding.UTF8.GetByteCount(reply.Json), reply.Envelope.Budget.UsedBytes);
        Assert.True(reply.Json.Length <= cap.MaxChars);
        Assert.True(Encoding.UTF8.GetByteCount(reply.Json) <= cap.MaxBytes);
        Assert.True(reply.Envelope.Truncated);
        Assert.Contains("OUTPUT_LIMIT", reply.Envelope.TruncationReasons);
        Assert.Equal("estimate", reply.Envelope.Budget.TokenCountKind);
        Error(query.Execute(request with { RequireComplete = true }), "INCOMPLETE_RESULT");

        writer.Commit(IndexStage.Create(snapshot with { Graph = snapshot.Graph with {
            Generation = 2, SnapshotId = "snapshot-2", Coverage = HarnessCoverage.Partial } }));
        using var partialReader = fixture.Reader();
        Error(fixture.Query(partialReader).Execute(new("coverage", QueryKind.Status, RequireComplete: true, Budget: Generous)), "INCOMPLETE_RESULT");

        // A legal long snapshot ID can exceed a nominally valid minimum envelope budget.
        // Rejection must happen before output trimming; an endless minimal-error loop is not a response.
        writer.Commit(IndexStage.Create(snapshot with { Graph = snapshot.Graph with {
            Generation = 3, SnapshotId = new string('s', 2048) } }));
        using var longReader = fixture.Reader();
        var longQuery = fixture.Query(longReader);
        Assert.Throws<ArgumentException>(() => longQuery.Execute(new("small", QueryKind.Status,
            Budget: Generous with { MaxChars = 2048, MaxBytes = 2048 })));
        var longPageRequest = new QueryRequest("long-cursor", QueryKind.Search, Term: "Service", PageSize: 1, Budget: Generous);
        var longPage = longQuery.Execute(longPageRequest);
        var longCursor = Assert.IsType<string>(longPage.Envelope.NextCursor);
        Assert.True(longCursor.Length <= 4096);
        Assert.True(longQuery.Execute(longPageRequest with { Cursor = longCursor }).Succeeded);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Error(query.Execute(new("cancel", QueryKind.Status, Budget: Generous), cancelled.Token), "CANCELLED");
    }

    [Fact]
    public void SameSizeSameMtimeChangeOmitsOldSpanAndFailsRequiredFreshness()
    {
        using var fixture = new StoreFixture();
        using var writer = fixture.Writer();
        writer.Commit(IndexStage.Create(fixture.Snapshot(1, withoutEdges: true)));
        using var reader = fixture.Reader();
        var query = fixture.Query(reader, fixture.WorkspaceReader(), trusted: true);
        var request = new QueryRequest("source", QueryKind.Context, NodeId: fixture.A,
            Scope: new(VariantIds: [fixture.Variant]), Budget: Generous);
        var before = query.Execute(request);
        Assert.Equal(StoreFixture.Code[..10], Assert.Single(before.Envelope.Items, i => i.Snippet is not null).Snippet?.Text);
        var timestamp = File.GetLastWriteTimeUtc(fixture.CodePath);
        var bytes = new FileInfo(fixture.CodePath).Length;
        File.WriteAllText(fixture.CodePath, StoreFixture.Code.Replace('0', 'X'), StoreFixture.Utf8);
        File.SetLastWriteTimeUtc(fixture.CodePath, timestamp);
        Assert.Equal(bytes, new FileInfo(fixture.CodePath).Length);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(fixture.CodePath));

        var stale = query.Execute(request);
        Assert.True(stale.Succeeded);
        Assert.All(All(stale), i => Assert.Null(i.Snippet));
        Assert.Contains(stale.Envelope.Diagnostics, d => d.Code == "SOURCE_CHANGED_SINCE_SNAPSHOT" && d.Path == "src/App.cs");
        Assert.Equal("dirty", stale.Envelope.Snapshot.Freshness);
        var fresh = query.Execute(request with { Freshness = QueryFreshnessPolicy.RequireFresh });
        Error(fresh, "FRESHNESS_REQUIREMENT_NOT_MET");
        Assert.Empty(All(fresh));
    }

    [Fact]
    public void UntrustedSavedQueriesNeverReadSourcesOrInvokeInventory()
    {
        using var fixture = new StoreFixture();
        using var writer = fixture.Writer();
        writer.Commit(IndexStage.Create(fixture.Snapshot(1, withoutEdges: true)));
        using var reader = fixture.Reader();
        foreach (var relative in StoreFixture.InputPaths) File.Delete(Path.Combine(fixture.Root, relative));
        var inspections = 0;
        var workspace = new QueryWorkspace(fixture.Root, _ => { inspections++; throw new InvalidOperationException("Source inventory must not be called."); });
        var query = fixture.Query(reader, workspace, trusted: false);
        var request = new QueryRequest("saved", QueryKind.Context, NodeId: fixture.A, Budget: Generous);
        var reply = query.Execute(request);
        Assert.True(reply.Succeeded);
        Assert.Equal(fixture.A, Assert.Single(reply.Envelope.Items, i => i.Node is not null).Id);
        Assert.All(All(reply), i => Assert.Null(i.Snippet));
        Assert.Empty(reply.Envelope.Diagnostics);
        Assert.Equal("unverified", reply.Envelope.Snapshot.Freshness);
        Error(query.Execute(request with { Freshness = QueryFreshnessPolicy.RequireFresh }), "WORKSPACE_UNTRUSTED");
        Error(query.Execute(request with { Freshness = QueryFreshnessPolicy.Refresh }), "UPDATE_REQUIRED");
        Assert.Equal(0, inspections);
    }

    [Fact]
    public void RequiredFreshnessNeedsCompleteInventoryAndConfigurationProof()
    {
        using var fixture = new StoreFixture();
        using var writer = fixture.Writer();
        writer.Commit(IndexStage.Create(fixture.Snapshot(1)));
        using var reader = fixture.Reader();
        var request = new QueryRequest("fresh", QueryKind.Status, Freshness: QueryFreshnessPolicy.RequireFresh, Budget: Generous);
        var noInventory = fixture.Query(reader, new QueryWorkspace(fixture.Root), trusted: true).Execute(request);
        Error(noInventory, "FRESHNESS_REQUIREMENT_NOT_MET");
        Assert.Contains("INPUT_INVENTORY_UNVERIFIED", noInventory.Envelope.Snapshot.Unverified);
        var noConfig = fixture.Query(reader, fixture.WorkspaceReader(configurationVerified: false), trusted: true).Execute(request);
        Error(noConfig, "FRESHNESS_REQUIREMENT_NOT_MET");
        Assert.Contains("CONFIGURATION_UNVERIFIED", noConfig.Envelope.Snapshot.Unverified);
        var verified = fixture.Query(reader, fixture.WorkspaceReader(), trusted: true).Execute(request);
        Assert.True(verified.Succeeded);
        Assert.Equal("verified-current", verified.Envelope.Snapshot.Freshness);
        Assert.NotNull(verified.Envelope.Snapshot.CheckedAt);

        File.WriteAllText(Path.Combine(fixture.Root, "src/New.cs"), "new input", StoreFixture.Utf8);
        var changedSet = new QueryWorkspace(fixture.Root, _ => new([.. StoreFixture.InputPaths, "src/New.cs"], true));
        var changed = fixture.Query(reader, changedSet, trusted: true).Execute(request);
        Error(changed, "FRESHNESS_REQUIREMENT_NOT_MET");
        Assert.Contains("src/New.cs", changed.Envelope.Snapshot.DirtyPaths);
    }

    [Fact]
    public void ContextUnionsBridgeOverlapsOnceAndKeepsOriginalHashAndBoundaries()
    {
        using var fixture = new StoreFixture();
        using var writer = fixture.Writer();
        writer.Commit(IndexStage.Create(fixture.Snapshot(1, withoutEdges: true)));
        using var reader = fixture.Reader();
        var query = fixture.Query(reader, fixture.WorkspaceReader(), trusted: true);
        // Two disjoint snippets are connected by the last evidence range, not just extended at one end.
        var reply = query.Execute(new("union", QueryKind.Context, Ids: [fixture.A, fixture.B, fixture.C],
            Scope: new(VariantIds: [fixture.Variant]), Budget: Generous));
        Assert.True(reply.Succeeded);
        Assert.All(reply.Envelope.Items, i => { Assert.NotEmpty(i.Reason); Assert.NotNull(i.Evidence); });
        var snippet = Assert.Single(All(reply), i => i.Snippet is not null).Snippet!;
        Assert.Equal(new HarnessRawSpan(0, 30), snippet.Span);
        Assert.Equal(StoreFixture.Code[..30], snippet.Text);
        Assert.Equal(StoreFixture.Hash(StoreFixture.Code), snippet.ContentHash);
        Assert.False(snippet.OmittedBefore);
        Assert.True(snippet.OmittedAfter);
        Assert.Equal("unverified", reply.Envelope.Snapshot.Freshness);
    }

    private static QueryItem[] All(QueryReply reply)
        => [.. reply.Envelope.Items, .. reply.Envelope.Candidates, .. reply.Envelope.Unresolved];

    private static void Error(QueryReply reply, string code)
    {
        Assert.False(reply.Succeeded);
        Assert.Contains(reply.Envelope.Errors, e => e.Code == code);
    }

    private sealed class StoreFixture : IDisposable
    {
        public static readonly UTF8Encoding Utf8 = new(false, true);
        public const string Code = "01234567890123456789012345678901234567890123456789\n";
        private const string Markdown = "# 設定\r\n\r\n😀 設定の手順を説明する。\r\n";
        private const string Configuration = "<Project />\n";
        public static readonly string[] InputPaths = ["src/App.cs", "other/Other.cs", "readme.md", "config.props"];
        private static readonly string FixtureBase = OperatingSystem.IsWindows()
            ? @"D:\DevData\AgentOps\work-cli-20261007\runs\sharpdeps-sd206-20261010-01\fixtures"
            : OperatingSystem.IsMacOS() ? "/private/tmp/sharpdeps-sd206-fixtures"
            : Path.Combine(Path.GetTempPath(), "sharpdeps-sd206-fixtures");
        private static readonly byte[] CursorKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        public Guid Workspace { get; } = Guid.NewGuid();
        private Guid Document { get; } = Guid.NewGuid();
        public string Root { get; } = Path.GetFullPath(Path.Combine(FixtureBase, Guid.NewGuid().ToString("N")));
        private string IndexPath => Path.Combine(Root, ".sharpdeps", "index.sqlite");
        public string CodePath => Path.Combine(Root, "src", "App.cs");
        public string ProjectId => HarnessIdentity.ProjectId(Workspace, "src/App.csproj");
        public string OtherProjectId => HarnessIdentity.ProjectId(Workspace, "other/Other.csproj");
        public string Variant => HarnessIdentity.VariantId(Workspace, ProjectId, "net10.0", "Debug");
        public string OtherVariant => HarnessIdentity.VariantId(Workspace, OtherProjectId, "net10.0", "Debug");
        public string AlternateVariant => HarnessIdentity.VariantId(Workspace, ProjectId, "net8.0", "Debug");
        public string A => Symbol(ProjectId, "Service");
        public string B => Symbol(ProjectId, "ServiceExtra");
        public string C => Symbol(ProjectId, "Bridge");
        public string D => Symbol(OtherProjectId, "Service");
        public string E => Symbol(ProjectId, "UnknownTarget");
        public string OuterNamespaceId => HarnessIdentity.LogicalSymbolId(Workspace, ProjectId, "namespace", "N:App");
        public string NamespaceId => HarnessIdentity.LogicalSymbolId(Workspace, ProjectId, "namespace", "N:App.Inner");
        private string OtherNamespaceId => HarnessIdentity.LogicalSymbolId(Workspace, OtherProjectId, "namespace", "N:Other");
        private string DocumentId => HarnessIdentity.DocumentId(Workspace, Document);
        public string SectionId => HarnessIdentity.SectionId(Workspace, DocumentId, "intro-token");

        public StoreFixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CodePath)!);
            Directory.CreateDirectory(Path.Combine(Root, "other"));
            File.WriteAllText(CodePath, Code, Utf8);
            File.WriteAllText(Path.Combine(Root, "other/Other.cs"), Code, Utf8);
            File.WriteAllText(Path.Combine(Root, "readme.md"), Markdown, Utf8);
            File.WriteAllText(Path.Combine(Root, "config.props"), Configuration, Utf8);
        }

        public IndexWriter Writer() => IndexWriter.Open(Root, IndexPath, Workspace, true);
        public IndexReader Reader() => IndexReader.Open(IndexPath, Workspace);
        public QueryService Query(IndexReader reader, QueryWorkspace? workspace = null, bool trusted = false)
            => new(new PinnedQueryIndex(reader), workspace, trusted, CursorKey);
        public QueryWorkspace WorkspaceReader(bool configurationVerified = true)
            => new(Root, _ => new(InputPaths, configurationVerified));
        public string Occurrence(string id) => Occurrence(id, id == D ? OtherVariant : Variant);
        public string Occurrence(string id, string variant) => HarnessIdentity.SymbolOccurrenceId(Workspace, id, variant);
        private string Symbol(string project, string name) => HarnessIdentity.LogicalSymbolId(Workspace, project, "method", "M:" + name);
        private HarnessLocation CodeLocation(int start, int length, bool other = false)
            => new(other ? "other/Other.cs" : "src/App.cs", "fixture-v1", Hash(Code), new(start, length));

        public IndexSnapshot Snapshot(long generation, bool wideLabel = false, bool withoutEdges = false, bool partialDeclarations = false)
        {
            var workspace = HarnessIdentity.WorkspaceId(Workspace);
            var markdown = new HarnessLocation("readme.md", "markdown-v1", Hash(Markdown), new(0, Markdown.Length));
            var members = new[] {
                new HarnessNode(A, HarnessNodeKind.Member, wideLabel ? string.Concat(Enumerable.Repeat("設定😀\\\"", 800)) : "Service", ProjectId, partialDeclarations ? null : CodeLocation(0, 10), "M:Service"),
                new HarnessNode(B, HarnessNodeKind.Member, "ServiceExtra", ProjectId, CodeLocation(20, 10), "M:ServiceExtra"),
                new HarnessNode(C, HarnessNodeKind.Member, "Bridge", ProjectId, CodeLocation(8, 14), "M:Bridge"),
                new HarnessNode(D, HarnessNodeKind.Member, "Service", OtherProjectId, CodeLocation(0, 10, true), "M:Service"),
                new HarnessNode(E, HarnessNodeKind.Member, "UnknownTarget", ProjectId, CodeLocation(40, 5), "M:UnknownTarget") };
            var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
                HarnessGraphContract.IdentityVersion, workspace, "snapshot-" + generation, generation,
                HarnessCoverage.CompleteWithinScope,
                [new(Variant, ProjectId, "net10.0", "Debug", null, null, Hash(Configuration)),
                    new(OtherVariant, OtherProjectId, "net10.0", "Debug", null, null, Hash(Configuration)),
                    .. (partialDeclarations ? new[] { new HarnessVariant(AlternateVariant, ProjectId, "net8.0", "Debug", null, null, Hash(Configuration)) } : [])],
                [new(workspace, HarnessNodeKind.Workspace, "Workspace", null, null),
                    new(ProjectId, HarnessNodeKind.Project, "App", workspace, null),
                    new(OtherProjectId, HarnessNodeKind.Project, "Other", workspace, null), .. members,
                    .. (partialDeclarations ? new[] {
                        new HarnessNode(OuterNamespaceId, HarnessNodeKind.Namespace, "App", ProjectId, null),
                        new HarnessNode(NamespaceId, HarnessNodeKind.Namespace, "App.Inner", OuterNamespaceId, null),
                        new HarnessNode(OtherNamespaceId, HarnessNodeKind.Namespace, "Other", OtherProjectId, null) } : []),
                    new(DocumentId, HarnessNodeKind.Document, "readme.md", workspace, markdown),
                    new(SectionId, HarnessNodeKind.Section, "設定", DocumentId, markdown)],
                members.Select(n => new HarnessSymbolOccurrence(Occurrence(n.Id), n.Id, n.Id == D ? OtherVariant : Variant,
                    n.Id == A ? CodeLocation(0, 10) : n.Location,
                    [n.Id == A ? CodeLocation(0, 10) : n.Location!]))
                    .Concat(partialDeclarations ? new[] { new HarnessSymbolOccurrence(
                        HarnessIdentity.SymbolOccurrenceId(Workspace, A, AlternateVariant), A, AlternateVariant,
                        CodeLocation(0, 10, true), [CodeLocation(0, 10, true)]) } : []).ToArray(),
                withoutEdges ? [] : [Edge("edge-call", A, B, "calls"), Edge("edge-cycle", B, A, "calls"),
                    Edge("edge-construct", A, C, "constructs"), Edge("edge-next", C, E, "calls"),
                    Edge("edge-candidate", A, D, "candidate_relation", HarnessCertainty.Candidate),
                    Edge("edge-unresolved", A, E, "candidate_relation", HarnessCertainty.Unresolved),
                    Edge("edge-behind-candidate", D, C, "calls")], [],
                new("2.0.0-preview.3", Hash("fixed-source"), "2.0.0-preview.3", "1.0.0", "lithosharp-markdown/1", Hash("options")));
            return new(graph,
                [Manifest("src/App.cs", "code", Code), Manifest("other/Other.cs", "code", Code),
                    Manifest("readme.md", "markdown", Markdown), Manifest("config.props", "config", Configuration)],
                [new(Document, "readme.md", null, Hash(Markdown), "scope", "2.0.0-preview.3", "1.0.0",
                    "lithosharp-markdown/1", Hash("options"), [new("section-local", "intro-token", "1:設定", Hash(Markdown))])], [],
                [.. members.Select(n => new IndexSearchText(n.Id, n.Name, n.Signature!, "", "",
                    n.Id == D ? "other/Other.cs" : "src/App.cs")), new(SectionId, "", "", "設定", "設定の手順を説明する。", "readme.md")]);
        }

        private HarnessEdge Edge(string id, string from, string to, string kind, HarnessCertainty certainty = HarnessCertainty.Resolved)
            => new(id, from, to, Occurrence(from), Occurrence(to), from == D ? OtherVariant : Variant,
                kind, certainty, "roslyn-operation", CodeLocation(0, 10, from == D));
        private static IndexFile Manifest(string path, string kind, string text)
            => new(path, path, kind, Hash(text), Utf8.GetByteCount(text), text.Length);
        public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(text))).ToLowerInvariant();

        public void Dispose()
        {
            var allowed = Path.GetFullPath(FixtureBase) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(Root);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!resolved.StartsWith(allowed, comparison)) throw new InvalidOperationException("Fixture cleanup escaped its dedicated run.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
