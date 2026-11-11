namespace SharpDeps.Cli.Tests;

using System.Security.Cryptography;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Markdown;
using SharpDeps.Index;
using SharpDeps.Query;
using Xunit;

public sealed class CliTests
{
    [Theory]
    [InlineData("search")]
    [InlineData("context")]
    [InlineData("explore")]
    public async Task QueryMetricsAreOutOfBandAndCannotChangeSavedResponses(string command)
    {
        using var fixture = new Fixture();
        var args = new[] { command, "--root", fixture.Root, "--trusted", "--term", "Service", "--max-milliseconds", "0" };
        var expected = await Run(args);
        Assert.Equal(0, expected.Exit);
        var directory = Path.Combine(fixture.Root, ".sharpdeps", "metrics");
        var measured = await Run([.. args, "--metrics-directory", directory]);
        Assert.Equal(expected, measured);
        Assert.False(SharpDeps.Analysis.Core.Diagnostics.PhaseMetrics.Enabled);
        var metricsPath = Assert.Single(Directory.GetFiles(directory, "*.jsonl"));
        var text = File.ReadAllText(metricsPath);
        Assert.DoesNotContain(fixture.Root, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Service", text, StringComparison.Ordinal);
        var events = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        Assert.Equal(Path.GetFileNameWithoutExtension(metricsPath),
            Assert.Single(events.Select(e => e.GetProperty("runId").GetString()).Distinct()));
        var payloads = events.Select(e => e.GetProperty("payload")).ToArray();
        Assert.Equal("run.start", payloads[0].GetProperty("type").GetString());
        Assert.Equal(command == "explore" ? "explore" : "query", payloads[0].GetProperty("operation").GetString());
        Assert.Equal("run.end", payloads[^1].GetProperty("type").GetString());
        Assert.Equal("completed", payloads[^1].GetProperty("outcome").GetString());
        var phases = payloads.Where(p => p.GetProperty("type").GetString() == "phase")
            .Select(p => p.GetProperty("phase").GetString()).ToArray();
        Assert.Contains("reader.open", phases);
        Assert.Contains("reader.snapshot", phases);
        Assert.Contains(command == "explore" ? "explore.render" : "query.render", phases);
        if (command == "explore")
        {
            Assert.Contains("explore.prepare", phases);
            Assert.DoesNotContain("explore.fingerprint", phases);
            Assert.DoesNotContain(payloads, p => p.GetProperty("type").GetString() == "decision"
                && p.GetProperty("reason").GetString() is "validated-cache-hit" or "validated-cache-miss");
            Assert.Equal(1, phases.Count(phase => phase == "explore.derived-index"));
            var hosted = await CliApplication.ExploreAsync(fixture.Root, ".sharpdeps/index.sqlite", true,
                CliArguments.Parse(args).ExplorationRequest());
            Assert.Equal(expected.Exit, hosted.ExitCode);
            Assert.Equal(hosted.Text + Environment.NewLine, expected.Json);
        }
        else
        {
            Assert.Contains("query.derived-index", phases);
        }
        if (command != "search")
        {
            Assert.Contains("source.read", phases);
            Assert.Contains("source.hash", phases);
        }
        var refused = Path.Combine(fixture.Root, "refused");
        Assert.Equal(expected, await Run([.. args, "--metrics-directory", refused]));
        Assert.False(Directory.Exists(refused));
        var broken = Path.Combine(fixture.Root, ".sharpdeps", "blocked");
        File.WriteAllText(broken, "keep");
        Assert.Equal(expected, await Run([.. args, "--metrics-directory", broken]));
        Assert.Equal("keep", File.ReadAllText(broken));
        using (var sink = new SharpDeps.Analysis.Core.Diagnostics.PhaseMetrics.Run("broken-sink", "query", new StreamWriter(new FailedMetricsStream())))
        {
            Assert.Equal(expected, await Run(args));
            sink.Complete();
        }
        Assert.False(SharpDeps.Analysis.Core.Diagnostics.PhaseMetrics.Enabled);
        using var reader = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        Assert.Equal(1, reader.Snapshot().Graph.Generation);
    }

    [Fact]
    public async Task ParallelQueryMetricsKeepRunScopesAndErrorsSeparate()
    {
        using var fixture = new Fixture();
        var directory = Path.Combine(fixture.Root, ".sharpdeps", "metrics");
        var requests = new[] { new[] { "search", "--term", "Service" }, new[] { "symbol", "--id", "missing" } };
        var expected = new List<(int Exit, string Json, string Progress)>();
        foreach (var request in requests)
            expected.Add(await Run([.. request, "--root", fixture.Root, "--max-milliseconds", "0"]));
        var actual = await Task.WhenAll(requests.Select(request => Task.Run(async () =>
        {
            var reply = await Run([.. request, "--root", fixture.Root, "--max-milliseconds", "0", "--metrics-directory", directory]);
            Assert.False(SharpDeps.Analysis.Core.Diagnostics.PhaseMetrics.Enabled);
            return reply;
        })));
        Assert.Equal(expected, actual);
        var files = Directory.GetFiles(directory, "*.jsonl");
        Assert.Equal(2, files.Length);
        var outcomes = new List<string>();
        foreach (var file in files)
        {
            var events = File.ReadLines(file).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.Single(events.Select(e => e.RootElement.GetProperty("runId").GetString()).Distinct());
                Assert.Equal(Enumerable.Range(1, events.Length).Select(n => (long)n), events.Select(e => e.RootElement.GetProperty("sequence").GetInt64()));
                var phases = events.Select(e => e.RootElement.GetProperty("payload")).Where(p => p.GetProperty("type").GetString() == "phase").ToArray();
                Assert.Single(phases, p => p.GetProperty("phase").GetString() == "run");
                var ids = phases.Select(p => p.GetProperty("spanId").GetInt64()).ToHashSet();
                Assert.All(phases.Where(p => p.GetProperty("parentSpanId").ValueKind != JsonValueKind.Null),
                    p => Assert.Contains(p.GetProperty("parentSpanId").GetInt64(), ids));
                outcomes.Add(events[^1].RootElement.GetProperty("payload").GetProperty("outcome").GetString()!);
            }
            finally
            {
                foreach (var value in events)
                    value.Dispose();
            }
        }
        Assert.Equal(new[] { "completed", "failed" }, outcomes.Order());
    }

    private sealed class FailedMetricsStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("unavailable");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("unavailable");
    }
    [Fact]
    public async Task IndexTimeoutDefaultsAndRangeSupportLargeWorkspaces()
    {
        using var fixture = new Fixture(seed: false);
        File.WriteAllText(Path.Combine(fixture.Root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        foreach (var command in new[] { "index", "update" })
        {
            var arguments = new[] { command, "--root", fixture.Root, "--target", "App.csproj", "--trusted" };
            CoordinatorUpdate Parse(params string[] extra) => CoordinatorUpdate.Parse(CliArguments.Parse([.. arguments, .. extra]),
                fixture.Root, fixture.IndexPath, fixture.Uuid);
            Assert.Equal(1800, Parse().Options.TimeoutSeconds);
            Assert.Equal(3600, Parse("--timeout-seconds", "3600").Options.TimeoutSeconds);
            Assert.Equal(0, Parse("--timeout-seconds", "0").Options.TimeoutSeconds);
            var invalid = await Run([.. arguments, "--timeout-seconds", "3601"]);
            Assert.Equal(2, invalid.Exit);
            Assert.Equal("ARGUMENT_INVALID", Code(invalid.Json));
        }
        Assert.Equal(1800, new CliIndexOptions(fixture.Root, "App.csproj", fixture.IndexPath, fixture.Uuid).TimeoutSeconds);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".sharpdeps")));
    }

    [Fact]
    public void ReuseTreePreservesLegacyOrderAndDetectsSameLengthTailChanges()
    {
        using var fixture = new Fixture(seed: false);
        var tree = Path.Combine(fixture.Root, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "A"));
        Directory.CreateDirectory(Path.Combine(tree, "z"));
        for (var index = 0; index < 17; index++)
            File.WriteAllBytes(Path.Combine(tree, $"file-{index:D2}.bin"),
                Enumerable.Repeat((byte)(index + 1), 65537 + index * 97).ToArray());
        File.WriteAllBytes(Path.Combine(tree, "A", "first.bin"), Enumerable.Repeat((byte)5, 17).ToArray());
        File.WriteAllBytes(Path.Combine(tree, "A", "second.bin"), Enumerable.Repeat((byte)0xA5, 131075).ToArray());
        File.WriteAllBytes(Path.Combine(tree, "z", "tail.bin"), Enumerable.Repeat((byte)0xCC, 65539).ToArray());
        string Fingerprint()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            CliReuseProof.AddTree(tree, "fixture", hash, new CliReuseProof.ProofBudget(default));
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        // Preserve v0.2.0's OS-specific relative-path encoding.
        var expected = OperatingSystem.IsWindows()
            ? "63e01836853939132b90a6f96200e37afd7e7603e6fed62472436279f24e074e"
            : "9460bb62ff093124919eac35b22fb740f94a87f2290219dd0fd5e97358bcf6be";
        Assert.Equal(expected, Fingerprint());
        Assert.Equal(expected, Fingerprint());
        var changed = Path.Combine(tree, "file-08.bin");
        var timestamp = File.GetLastWriteTimeUtc(changed);
        using (var output = new FileStream(changed, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            output.Seek(-1, SeekOrigin.End);
            output.WriteByte(0xEF);
        }
        File.SetLastWriteTimeUtc(changed, timestamp);
        Assert.NotEqual(expected, Fingerprint());
    }

    [Fact]
    public void ConcurrentProofReadersCannotLoseByteCharges()
    {
        var budget = new CliReuseProof.ProofBudget(default, new(default), git: true);
        for (var index = 0; index < 63; index++) budget.Bytes(1024 * 1024);
        Parallel.For(0, 1000, _ => budget.Bytes(1024));
        Assert.Equal("Reuse proof byte budget exceeded.",
            Assert.Throws<IOException>(() => budget.Bytes(25000)).Message);
    }

    [Fact]
    public void MarkdownContainmentReachesCandidatesWithoutExpandingThemOrBridgingVariants()
    {
        using var fixture = new Fixture();
        using var original = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var saved = original.Snapshot();
        var primary = Assert.Single(saved.Graph.Variants);
        var alternate = HarnessIdentity.VariantId(fixture.Uuid, primary.ProjectId, "net9.0", "Debug");
        var parent = saved.Graph.Nodes.Single(n => n.Name == "Service");
        var target = HarnessIdentity.LogicalSymbolId(fixture.Uuid, primary.ProjectId, "method", "M:Service.Run");
        var tail = HarnessIdentity.LogicalSymbolId(fixture.Uuid, primary.ProjectId, "method", "M:Service.Other");
        string Occurrence(string node, string variant) => HarnessIdentity.SymbolOccurrenceId(fixture.Uuid, node, variant);
        var graph = saved.Graph with { SnapshotId = "markdown-connectivity", Generation = 2,
            Variants = [.. saved.Graph.Variants, new(alternate, primary.ProjectId, "net9.0", "Debug", null, null, null)],
            Nodes = [.. saved.Graph.Nodes, new(target, HarnessNodeKind.Member, "Run", parent.Id, parent.Location),
                new(tail, HarnessNodeKind.Member, "Other", parent.Id, parent.Location)],
            SymbolOccurrences = [.. saved.Graph.SymbolOccurrences, .. new[] { target, tail }.SelectMany(node =>
                new[] { primary.Id, alternate }.Select(variant => new HarnessSymbolOccurrence(
                    Occurrence(node, variant), node, variant, parent.Location, [parent.Location!])))],
            Edges = new[] { primary.Id, alternate }.Select(variant => new HarnessEdge("calls-" + variant,
                target, tail, Occurrence(target, variant), Occurrence(tail, variant), variant,
                "calls", HarnessCertainty.Resolved, "fixture-semantic", parent.Location)).ToArray() };
        const string raw = "---\nsharpdeps:\n  symbols:\n    - documentationId: M:Service.Run\n---\n# Guide\n\nService.Run\n";
        File.WriteAllText(Path.Combine(fixture.Root, "readme.md"), raw, new UTF8Encoding(false));
        var documentUuid = Guid.NewGuid();
        var request = new MarkdownGraphRequest(raw, fixture.Uuid, documentUuid, graph.WorkspaceId,
            "readme.md", "v1", graph.SnapshotId, graph.Generation);
        var adapter = new LithoSharpMarkdownAdapter();
        var parsed = adapter.Analyze(request, true);
        var tokens = parsed.Facts.Sections.ToDictionary(s => s.LocalKey, s => "regression:" + s.LocalKey);
        var projection = adapter.Analyze(request with { SectionTokens = tokens,
            SectionIdentityTextHash = parsed.Facts.TextHash }, true);
        graph = graph with { Nodes = graph.Nodes.Concat(projection.Graph.Nodes).DistinctBy(n => n.Id).ToArray(),
            Edges = graph.Edges.Concat(projection.Graph.Edges).ToArray(), Markdown = projection.Graph.Markdown };
        var catalog = new MarkdownSymbolCatalog(graph, new[] { primary.Id, alternate }.Select(variant =>
            new MarkdownSymbolCandidate(target, "Service.Run", "Run", "M:Service.Run", VariantId: variant)).ToArray());
        var resolution = MarkdownSymbolResolver.Resolve(projection, raw, catalog);
        var projected = MarkdownSymbolResolver.ProjectGraph(graph, resolution);
        var stamp = projected.Markdown!;
        var snapshot = saved with { Graph = projected,
            Files = [.. saved.Files, new("readme.md", "readme.md", "markdown", parsed.Facts.TextHash,
                Encoding.UTF8.GetByteCount(raw), raw.Length)],
            Documents = [new(documentUuid, "readme.md", null, parsed.Facts.TextHash, graph.WorkspaceId,
                stamp.ParserVersion, stamp.ContractVersion, stamp.ProfileId, stamp.OptionsHash,
                projection.Sections.Select(s => new IndexSectionIdentity(s.Source.LocalKey,
                    tokens[s.Source.LocalKey], null, null)).ToArray())] };
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        writer.Commit(IndexStage.Create(snapshot));
        using var reader = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var query = new QueryService(new PinnedQueryIndex(reader));
        var document = HarnessIdentity.DocumentId(fixture.Uuid, documentUuid);
        var section = Assert.Single(resolution.Mentions, m => m.Origin == MarkdownMentionOrigin.BodyText
            && m.Token == "Service.Run").OwnerNodeId;
        var budget = new QueryBudget(100, 100, 8, 10000, 100000, 400000);
        foreach (var seed in new[] { document, section })
        {
            var queryRequest = new QueryRequest("candidate", QueryKind.Impact, NodeId: seed,
                Scope: new(VariantIds: [primary.Id, alternate]), Dependents: false,
                IncludeCandidates: true, Budget: budget);
            var reply = query.Execute(queryRequest).Envelope;
            Assert.Empty(reply.Errors);
            Assert.False(reply.Truncated);
            Assert.Contains(reply.Items, i => i.Node?.Kind == HarnessNodeKind.SymbolMention);
            Assert.Contains(reply.Candidates, i => i.Node?.Id == target);
            var bindings = reply.Candidates.Where(i => i.Edge?.TargetNodeId == target).ToArray();
            Assert.Equal(seed == document ? 2 : 1, bindings.Length);
            Assert.Equal(resolution.Mentions.Where(m => m.CandidateNodeIds.Contains(target)
                && (seed == document || m.OwnerNodeId == seed)).Select(m => m.Id).Order(),
                bindings.Select(i => i.Edge!.SourceNodeId).Order());
            var binding = Assert.Single(reply.Candidates, i => i.Edge?.TargetNodeId == target
                && i.Edge.Kind == (seed == document ? "documents_symbol" : "mentions_symbol"));
            Assert.Equal(HarnessCertainty.Candidate, binding.Edge!.Certainty);
            Assert.Null(binding.Edge.VariantId);
            Assert.Null(binding.Edge.SourceOccurrenceId);
            Assert.Null(binding.Edge.TargetOccurrenceId);
            Assert.DoesNotContain(reply.Items.Concat(reply.Candidates), i => i.Edge?.Kind == "calls" || i.Node?.Id == tail);
            var strict = query.Execute(queryRequest with { IncludeCandidates = false }).Envelope;
            Assert.Contains(strict.Items, i => i.Node?.Kind == HarnessNodeKind.SymbolMention);
            Assert.Empty(strict.Candidates);
            Assert.DoesNotContain(strict.Items, i => i.Node?.Id == target);
        }
        // Only a separately complete catalog and an explicit variant may bind an occurrence.
        var scoped = MarkdownSymbolResolver.Resolve(projection, raw,
            catalog with { Graph = graph with { Coverage = HarnessCoverage.CompleteWithinScope } },
            new(VariantId: primary.Id));
        var scopedGraph = MarkdownSymbolResolver.ProjectGraph(graph, scoped);
        writer.Commit(IndexStage.Create(snapshot with { Graph = scopedGraph with { Generation = 3, SnapshotId = "scoped" } }));
        using var scopedReader = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var scopedQuery = new QueryService(new PinnedQueryIndex(scopedReader));
        var selected = new QueryRequest("selected", QueryKind.Impact, NodeId: document, Dependents: false,
            IncludeCandidates: true, Scope: new(VariantIds: [primary.Id, alternate]), Budget: budget);
        var selectedReply = scopedQuery.Execute(selected).Envelope;
        Assert.Empty(selectedReply.Errors);
        Assert.Contains(selectedReply.Items, i => i.Edge?.Id == "calls-" + primary.Id);
        Assert.DoesNotContain(selectedReply.Items.Concat(selectedReply.Candidates), i => i.Edge?.Id == "calls-" + alternate);
        var other = scopedQuery.Execute(selected with { Scope = new(VariantIds: [alternate]) }).Envelope;
        Assert.Empty(other.Errors);
        Assert.DoesNotContain(other.Items.Concat(other.Candidates), i => i.Node?.Id == target || i.Node?.Id == tail);
    }

    [Fact]
    public async Task TrustAndArgumentFailuresHappenBeforeAnyWorkspaceCreation()
    {
        using var fixture = new Fixture(seed: false);
        Assert.Equal(ExploreQueryKind.Symbol, CliArguments.Parse(["explore", "--term", "Service"]).ExplorationRequest().QueryKind);
        foreach (var literal in new[] { "", " ", "--trusted", "hocc_" + new string('a', 64), "items/*" })
        {
            foreach (var arguments in new[]
            {
                new[] { "explore", "--query-kind", "http-get-literal", "--term", literal },
                new[] { "explore", "--term", literal, "--query-kind", "http-get-literal" }
            })
            {
                var parsed = CliArguments.Parse(arguments);
                var request = parsed.ExplorationRequest();
                Assert.Equal(ExploreQueryKind.HttpGetLiteral, request.QueryKind);
                Assert.Equal(literal, request.Query);
                Assert.False(parsed.Has("trusted"));
            }
        }
        Assert.True(CliArguments.Parse(["explore", "--term", "--trusted", "--query-kind", "http-get-literal", "--trusted"]).Has("trusted"));
        Assert.Equal(ExploreFormat.Audit, CliArguments.Parse(["explore", "--term", "Service"]).ExplorationRequest().Format);
        Assert.Equal(ExploreFormat.Agent, CliArguments.Parse(["explore", "--term", "Service", "--format", "agent"]).ExplorationRequest().Format);
        Assert.Equal(ExploreFormat.Audit, CliArguments.Parse(["explore", "--term", "Service", "--format", "audit"]).ExplorationRequest().Format);
        Assert.Equal(ExploreScopePolicy.Discover, CliArguments.Parse(["explore", "--term", "Service"]).ExplorationRequest().ScopePolicy);
        Assert.Equal(ExploreScopePolicy.RequireUnique, CliArguments.Parse(["explore", "--term", "Service", "--scope-policy", "unique"]).ExplorationRequest().ScopePolicy);
        Assert.Equal(ExploreScopePolicy.PreferDebug, CliArguments.Parse(["explore", "--term", "Service", "--scope-policy", "prefer-debug"]).ExplorationRequest().ScopePolicy);
        Assert.False(CliArguments.Parse(["explore", "--term", "Service"]).ExplorationRequest().ObserveOwner);
        Assert.True(CliArguments.Parse(["explore", "--term", "Service", "--owner-status"]).ExplorationRequest().ObserveOwner);
        Assert.Null(CliArguments.Parse(["explore", "--term", "Service"]).ExplorationRequest().PathTo);
        var occurrence = "hocc_" + new string('a', 64);
        Assert.Equal(occurrence, CliArguments.Parse(["explore", "--term", occurrence, "--path-to", occurrence]).ExplorationRequest().PathTo);
        var untrusted = await Run("index", "--root", fixture.Root, "--target", "absent.csproj");
        Assert.Equal(5, untrusted.Exit);
        Assert.Equal("WORKSPACE_UNTRUSTED", Code(untrusted.Json));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".sharpdeps")));
        var missingTerm = await Run("search", "--root", fixture.Root);
        Assert.Equal(2, missingTerm.Exit);
        foreach (var arguments in new[] {
            new[] { "--term", "" },
            new[] { "--term", " " },
            new[] { "--term", "--trusted" },
            new[] { "--term", "Service", "--query-kind", "other" },
            new[] { "--term", "Service", "--query-kind", "HttpGetLiteral" },
            new[] { "--term", "Service", "--query-kind", "1" },
            new[] { "--term", "items/{id}", "--query-kind", "http-get-literal", "--path-to", occurrence },
            new[] { "--term", "unsafe\ntext", "--query-kind", "http-get-literal" },
            new[] { "--term", "\uD800", "--query-kind", "http-get-literal" },
            new[] { "--term", new string('x', 1025), "--query-kind", "http-get-literal" },
            new[] { "--term", new string('x', 1025) },
            new[] { "--term", "Service", "--variant", "first", "--variant", "second" },
            new[] { "--term", "Service", "--project-id", " " },
            new[] { "--term", "Service", "--max-chars", "2047" },
            new[] { "--term", "Service", "--format", "verbose" },
            new[] { "--term", "Service", "--format", "0" },
            new[] { "--term", "Service", "--format", "Agent" },
            new[] { "--term", "Service", "--scope-policy", "auto" },
            new[] { "--term", "Service", "--scope-policy", "PreferDebug" },
            new[] { "--term", "Service", "--scope-policy", "1" },
            new[] { "--term", "Service", "--path-to", " " },
            new[] { "--term", "Service", "--path-to", new string('x', 129) },
            new[] { "--term", "Service", "--include-candidates" } })
        {
            var invalidExplore = await Run(["explore", "--root", fixture.Root, .. arguments]);
            Assert.Equal(2, invalidExplore.Exit);
        }
        foreach (var (flag, value) in new[] { ("--format", "agent"), ("--scope-policy", "prefer-debug"), ("--path-to", occurrence), ("--query-kind", "http-get-literal") })
        {
            var nonExploreOption = await Run("status", "--root", fixture.Root, flag, value);
            Assert.Equal(2, nonExploreOption.Exit);
            Assert.Equal("ARGUMENT_UNKNOWN", Code(nonExploreOption.Json));
        }
        var nonExploreOwnerStatus = await Run("status", "--root", fixture.Root, "--owner-status");
        Assert.Equal(2, nonExploreOwnerStatus.Exit);
        Assert.Equal("ARGUMENT_UNKNOWN", Code(nonExploreOwnerStatus.Json));
        var unsupported = await Run("query", "--kind", "status", "--root", fixture.Root, "--dependencies");
        Assert.Equal(2, unsupported.Exit);
        var statusScope = await Run("status", "--root", fixture.Root, "--project-id", "ignored-project");
        Assert.Equal(2, statusScope.Exit);
        Assert.Equal("ARGUMENT_UNSUPPORTED", Code(statusScope.Json));
        var network = await Run("status", "--root", "//invalid.invalid/sharpdeps");
        Assert.Equal(5, network.Exit);
        Assert.Equal("ROOT_INVALID", Code(network.Json));
        var duplicate = await Run("status", "--root", fixture.Root, "--root", fixture.Root);
        Assert.Equal(2, duplicate.Exit);
        Assert.Equal("ARGUMENT_DUPLICATE", Code(duplicate.Json));
        var badBudget = await Run("search", "--root", fixture.Root, "--term", "Service", "--max-nodes", "0");
        Assert.Equal(2, badBudget.Exit);
        var outside = await Run("status", "--root", fixture.Root, "--index", "../outside.sqlite");
        Assert.Equal(5, outside.Exit);
        Assert.Equal("PATH_OUTSIDE_ROOT", Code(outside.Json));
        var missingWatchTarget = await Run("serve", "--root", fixture.Root, "--trusted", "--watch");
        Assert.Equal(2, missingWatchTarget.Exit);
        var invalidWatchTarget = await Run("serve", "--root", fixture.Root, "--trusted", "--watch", "--target", "absent.csproj");
        Assert.Equal("TARGET_INVALID", Code(invalidWatchTarget.Json));
        var invalidWatchPeriod = await Run("serve", "--root", fixture.Root, "--trusted", "--watch", "--target", "absent.csproj",
            "--watch-resync-seconds", "0");
        Assert.Equal(2, invalidWatchPeriod.Exit);
        var selectionWithoutWatch = await Run("serve", "--root", fixture.Root, "--trusted", "--target", "absent.csproj");
        Assert.Equal("ARGUMENT_UNSUPPORTED", Code(selectionWithoutWatch.Json));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".sharpdeps")));
    }

    [Fact]
    public void ExploreDefaultsPreserveExplicitScopeAndExactSelectorIntent()
    {
        ExploreRequest Request(params string[] arguments) => CliArguments.Parse(["explore", .. arguments]).ExplorationRequest();
        Assert.Equal(ExploreScopePolicy.RequireUnique, new ExploreRequest("Service").ScopePolicy);
        foreach (var query in new[] { "Service", "src/Service.cs" })
        {
            Assert.Equal(Request("--term", query), Request(query));
            Assert.Equal(ExploreScopePolicy.Discover, Request(query).ScopePolicy);
            foreach (var flag in new[] { "--project-id", "--variant" })
            {
                Assert.Equal(ExploreScopePolicy.RequireUnique, Request(query, flag, "saved-scope").ScopePolicy);
                Assert.Equal(ExploreScopePolicy.Discover, Request(query, flag, "saved-scope", "--scope-policy", "discover").ScopePolicy);
            }
        }
        var occurrence = "hocc_" + new string('a', 64);
        foreach (var arguments in new[]
        {
            new[] { occurrence },
            new[] { "Service " + occurrence },
            new[] { occurrence, "--path-to", occurrence },
            new[] { "--term", "items/{id}", "--query-kind", "http-get-literal" }
        })
        {
            Assert.Equal(ExploreScopePolicy.RequireUnique, Request(arguments).ScopePolicy);
            Assert.Equal("ARGUMENT_INVALID", Assert.Throws<CliFailure>(() => Request([.. arguments, "--scope-policy", "discover"])).Code);
        }
        foreach (var arguments in new[]
        {
            new[] { "Service", "Other" }, new[] { "Service", "--term", "Other" },
            new[] { "--term", "Service", "Other" }
        })
            Assert.Equal("ARGUMENT_DUPLICATE", Assert.Throws<CliFailure>(() => Request(arguments)).Code);
        Assert.Equal("ARGUMENT_INVALID", Assert.Throws<CliFailure>(() => CliArguments.Parse(["search", "Service"])).Code);
    }

    [Fact]
    public async Task ExploreUsesCurrentDirectoryWithoutSearchingForAnAncestorIndex()
    {
        using var fixture = new Fixture();
        var selectedRoot = Directory.CreateDirectory(Path.Combine(fixture.Root, "nested")).FullName;
        var testAssembly = typeof(CliTests).Assembly.Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = selectedRoot, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"), typeof(CliApplication).Assembly.Location,
            "explore", "Service" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        Assert.Equal(3, process.ExitCode);
        Assert.Empty(await stderr);
        using var result = JsonDocument.Parse(await stdout);
        var error = Assert.Single(result.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("INDEX_MISSING", error.GetProperty("code").GetString());
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains(selectedRoot, message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(".sharpdeps/index.sqlite", selectedRoot), message, StringComparison.Ordinal);
        Assert.Contains("--root/--index", message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(selectedRoot, ".sharpdeps")));
        Assert.Equal(0, (await Run("explore", "Service", "--root", fixture.Root)).Exit);
        Assert.Equal("ARGUMENT_REQUIRED", Code((await Run("status")).Json));
    }

    [Fact]
    public async Task SeparateInvocationsShareCursorKeyAndReadSavedWithoutSourceFiles()
    {
        using var fixture = new Fixture();
        var before = SHA256.HashData(File.ReadAllBytes(fixture.IndexPath));
        File.Delete(Path.Combine(fixture.Root, "Service.cs"));
        var first = await Run("search", "--root", fixture.Root, "--term", "Service", "--page-size", "1");
        Assert.Equal(0, first.Exit);
        Assert.Empty(first.Progress);
        var envelope = JsonSerializer.Deserialize<QueryEnvelope>(first.Json, QueryJson.Options)!;
        var cursor = Assert.IsType<string>(envelope.NextCursor);
        var second = await Run("search", "--root", fixture.Root, "--term", "Service", "--page-size", "1", "--cursor", cursor);
        Assert.Equal(0, second.Exit);
        var next = JsonSerializer.Deserialize<QueryEnvelope>(second.Json, QueryJson.Options)!;
        Assert.NotEqual(Assert.Single(envelope.Items).Id, Assert.Single(next.Items).Id);
        var changed = await Run("search", "--root", fixture.Root, "--term", "Other", "--page-size", "1", "--cursor", cursor);
        Assert.Equal(6, changed.Exit);
        Assert.Equal("CURSOR_FILTER_CHANGED", Code(changed.Json));
        var context = await Run("context", "--root", fixture.Root, "--id", fixture.Symbol("Service"));
        Assert.Equal(0, context.Exit);
        Assert.All(JsonSerializer.Deserialize<QueryEnvelope>(context.Json, QueryJson.Options)!.Items, item => Assert.Null(item.Snippet));
        var trusted = await Run("status", "--root", fixture.Root, "--trusted", "--freshness", "require-fresh");
        Assert.Equal(3, trusted.Exit);
        Assert.Equal("FRESHNESS_REQUIREMENT_NOT_MET", Code(trusted.Json));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.IndexPath)));
    }

    [Fact]
    public async Task ExploreUsesFreshOwnerProofAndSourceForEachInvocation()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var sourcePath = Path.Combine(fixture.Root, "Service.cs");
        var originalText = File.ReadAllText(sourcePath);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var pinned = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var originalGeneration = pinned.Snapshot().Graph.Generation;
        var proofs = 0;
        var failProof = 0;
        var observationArguments = new[] { "explore", "--root", fixture.Root, "--term", "Service",
            "--owner-status", "--max-milliseconds", "10000" };
        var absent = await Run(observationArguments);
        Assert.Equal(0, absent.Exit);
        Assert.Contains("Owner observation state=absent generation=unknown pin=unknown revision=unknown desired=unknown;", absent.Json);
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        await using var owner = new CliCoordinator(writer, (_, token) =>
        {
            using var current = IndexReader.Open(fixture.IndexPath, fixture.Uuid, token);
            var previous = current.Snapshot(token);
            var text = File.ReadAllText(sourcePath);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var generation = previous.Graph.Generation + 1;
            HarnessLocation? Updated(HarnessLocation? location) => location is null ? null
                : location with { ContentHash = hash, SourceVersion = "v" + generation };
            var snapshot = previous with
            {
                Files = previous.Files.Select(file => file with { ContentHash = hash, ByteLength = bytes.Length, Utf16Length = text.Length }).ToArray(),
                Graph = previous.Graph with
                {
                    SnapshotId = Guid.NewGuid().ToString("N"), Generation = generation,
                    Nodes = previous.Graph.Nodes.Select(node => node with { Location = Updated(node.Location) }).ToArray(),
                    SymbolOccurrences = previous.Graph.SymbolOccurrences.Select(item => item with
                    { Location = Updated(item.Location), Declarations = item.Declarations?.Select(location => Updated(location)!).ToArray() }).ToArray()
                }
            };
            return Task.FromResult(new CliIndexBuild(snapshot, () => { })
            {
                VerifyQueryInputs = proofToken =>
                {
                    proofToken.ThrowIfCancellationRequested();
                    return new(["Service.cs"], Interlocked.Increment(ref proofs) != Volatile.Read(ref failProof));
                }
            });
        });
        var emptyOwner = await Run(observationArguments);
        Assert.Equal(0, emptyOwner.Exit);
        Assert.Contains("Owner observation state=idle generation=unknown pin=unknown revision=0 desired=0;", emptyOwner.Json);
        Assert.DoesNotContain(originalText.TrimEnd('\r', '\n'), emptyOwner.Json);
        Assert.Equal(0, Volatile.Read(ref proofs));
        var update = CoordinatorUpdate.Parse(CliArguments.Parse(["update", "--root", fixture.Root, "--trusted", "--target", "App.csproj"]),
            fixture.Root, fixture.IndexPath, fixture.Uuid);
        var published = await owner.UpdateAsync(update, "publish-proof", deadline.Token);
        Assert.Null(published.ErrorCode);
        var arguments = new[] { "explore", "--root", fixture.Root, "--trusted", "--term", "Service",
            "--freshness", "require-fresh", "--max-milliseconds", "10000" };
        var first = await Run(arguments);
        Assert.Equal(0, first.Exit);
        Assert.Contains("Freshness=verified-current", first.Json);
        Assert.Contains(originalText.TrimEnd('\r', '\n'), first.Json);
        Assert.DoesNotContain("Owner observation ", first.Json);
        Assert.Equal(2, Volatile.Read(ref proofs));
        var observed = await Run([.. arguments, "--owner-status"]);
        Assert.Equal(0, observed.Exit);
        Assert.Contains($"Owner observation state=idle generation={published.Result!.Generation} pin=same revision={published.Revision} desired={published.DesiredRevision};", observed.Json);
        Assert.Contains("Freshness=verified-current", observed.Json);
        Assert.Contains(originalText.TrimEnd('\r', '\n'), observed.Json);
        Assert.Equal(4, Volatile.Read(ref proofs));

        // The second proof can fail after source admission; no body from that reply may escape.
        Volatile.Write(ref failProof, Volatile.Read(ref proofs) + 2);
        var invalidated = await Run(arguments);
        Assert.Equal(3, invalidated.Exit);
        Assert.Contains("Error FRESHNESS_REQUIREMENT_NOT_MET", invalidated.Json);
        Assert.Contains("returned source ranges=0", invalidated.Json);
        Assert.DoesNotContain(originalText.TrimEnd('\r', '\n'), invalidated.Json);
        Assert.Equal(Volatile.Read(ref failProof), Volatile.Read(ref proofs));
        Volatile.Write(ref failProof, 0);

        var changedText = originalText.Replace("ServiceExtra {}", "ServiceExtra{ }", StringComparison.Ordinal);
        Assert.NotEqual(originalText, changedText);
        File.WriteAllText(sourcePath, changedText, new UTF8Encoding(false));
        var dirty = await Run(arguments);
        Assert.Equal(3, dirty.Exit);
        Assert.Contains("Freshness=dirty", dirty.Json);
        Assert.Contains("returned source ranges=0", dirty.Json);
        Assert.DoesNotContain(changedText.TrimEnd('\r', '\n'), dirty.Json);

        var replacement = await owner.UpdateAsync(update, "publish-current", deadline.Token);
        Assert.Null(replacement.ErrorCode);
        Assert.Equal(originalGeneration + 2, replacement.Result!.Generation);
        var currentReply = await Run([.. arguments, "--owner-status"]);
        Assert.Equal(0, currentReply.Exit);
        Assert.Contains("generation=" + replacement.Result.Generation, currentReply.Json);
        Assert.Contains("Freshness=verified-current", currentReply.Json);
        Assert.Contains(changedText.TrimEnd('\r', '\n'), currentReply.Json);
        Assert.Contains($"Owner observation state=idle generation={replacement.Result.Generation} pin=same revision={replacement.Revision} desired={replacement.DesiredRevision};", currentReply.Json);
        Assert.Equal(originalGeneration, pinned.Snapshot().Graph.Generation);
        await owner.DisposeAsync();
        var unavailable = await Run(observationArguments);
        Assert.Equal(0, unavailable.Exit);
        Assert.Contains("Owner observation state=unavailable generation=unknown pin=unknown", unavailable.Json);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("agent")]
    public async Task ExplorePreparationTimeoutReportsBudgetWithoutReturningEvidence(string format)
    {
        using var fixture = new Fixture();
        IndexSnapshot saved;
        using (var reader = IndexReader.Open(fixture.IndexPath, fixture.Uuid)) saved = reader.Snapshot();
        var expanded = saved with { Graph = saved.Graph with { Generation = saved.Graph.Generation + 1,
            SnapshotId = "timeout-preparation", Nodes = [.. saved.Graph.Nodes,
            .. Enumerable.Range(0, 50000).Select(i => new HarnessNode("timeout-node-" + i,
                HarnessNodeKind.SourceFile, "Unrelated", saved.Graph.WorkspaceId, null))] } };
        using (var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true))
            writer.Commit(IndexStage.Create(expanded));

        var reply = await Run("explore", "--root", fixture.Root, "--trusted", "--term", "Service",
            "--format", format, "--max-milliseconds", "10");
        Assert.Equal(4, reply.Exit);
        Assert.Empty(reply.Progress);
        using var document = JsonDocument.Parse(reply.Json);
        Assert.Equal("explore", document.RootElement.GetProperty("command").GetString());
        var error = Assert.Single(document.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("QUERY_TIMEOUT", error.GetProperty("code").GetString());
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains("10 ms computation deadline", message, StringComparison.Ordinal);
        Assert.Contains("saved-index preparation", message, StringComparison.Ordinal);
        Assert.Contains("No source evidence was returned", message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, reply.Json, StringComparison.Ordinal);
        Assert.False(document.RootElement.TryGetProperty("result", out _));
    }

    [Theory]
    [InlineData(null, "QUERY_TIMEOUT")]
    [InlineData("5000", null)]
    [InlineData("100", "PREPARATION_TIMEOUT")]
    public async Task ExplorePreparationBudgetIsOptInAndStartsExplorationAfterTheReader(string? preparation, string? errorCode)
    {
        using var fixture = new Fixture();
        using var writer = new PhaseCallbackWriter("reader.open", () => Thread.Sleep(1100));
        using var metrics = new SharpDeps.Analysis.Core.Diagnostics.PhaseMetrics.Run("preparation-deadline", "explore", writer);
        string[] arguments = ["explore", "--root", fixture.Root, "--term", "Service", "--trusted", "--format", "agent"];
        if (preparation is not null) arguments = [.. arguments, "--prepare-max-milliseconds", preparation];
        var reply = await Run(arguments);
        if (errorCode is null)
        {
            Assert.True(writer.Invoked);
            Assert.Equal(0, reply.Exit);
            Assert.Contains("Service", reply.Json, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(4, reply.Exit);
            Assert.Equal(errorCode, Code(reply.Json));
            Assert.Contains("saved-index preparation", reply.Json, StringComparison.Ordinal);
            Assert.Contains(errorCode == "PREPARATION_TIMEOUT" ? "100 ms preparation deadline" : "1000 ms computation deadline", reply.Json, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(reply.Json);
            Assert.False(document.RootElement.TryGetProperty("result", out _));
        }
        Assert.Empty(reply.Progress);
    }

    [Fact]
    public async Task ExplorePreparationBudgetDoesNotExtendExplorationDeadline()
    {
        using var fixture = new Fixture();
        using var writer = new PhaseCallbackWriter("explore.execute", () => Thread.Sleep(1100));
        using var metrics = new SharpDeps.Analysis.Core.Diagnostics.PhaseMetrics.Run("exploration-deadline", "explore", writer);
        var reply = await Run("explore", "--root", fixture.Root, "--term", "Service", "--trusted",
            "--prepare-max-milliseconds", "5000", "--format", "agent");
        Assert.True(writer.Invoked);
        Assert.Equal(4, reply.Exit);
        Assert.Equal("QUERY_TIMEOUT", Code(reply.Json));
        Assert.Contains("1000 ms computation deadline", reply.Json, StringComparison.Ordinal);
        Assert.Contains("exploration preparation or execution", reply.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("PREPARATION_TIMEOUT", reply.Json, StringComparison.Ordinal);
        Assert.Empty(reply.Progress);
        using var document = JsonDocument.Parse(reply.Json);
        Assert.False(document.RootElement.TryGetProperty("result", out _));
    }

    [Theory]
    [InlineData("reader.open")]
    [InlineData("explore.execute")]
    public async Task ExplorePreparationBudgetPreservesCallerCancellationAcrossBothPhases(string phase)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        using var writer = new PhaseCallbackWriter(phase, cancellation.Cancel);
        using var metrics = new SharpDeps.Analysis.Core.Diagnostics.PhaseMetrics.Run("preparation-cancellation", "explore", writer);
        var output = new StringWriter();
        var progress = new StringWriter();
        var exit = await CliApplication.RunAsync(["explore", "--root", fixture.Root, "--term", "Service", "--trusted",
            "--prepare-max-milliseconds", "5000"], output, progress, cancellation.Token);
        Assert.True(writer.Invoked);
        Assert.Equal(130, exit);
        Assert.Equal("CANCELLED", Code(output.ToString()));
        Assert.DoesNotContain("PREPARATION_TIMEOUT", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(progress.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.False(document.RootElement.TryGetProperty("result", out _));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("30001")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("Infinity")]
    [InlineData("1e3")]
    [InlineData("+10")]
    public async Task ExploreRejectsInvalidPreparationBudgetsBeforeWorkspaceAccess(string value)
    {
        using var fixture = new Fixture(seed: false);
        var reply = await Run("explore", "--root", fixture.Root, "--term", "Service", "--prepare-max-milliseconds", value);
        Assert.Equal(2, reply.Exit);
        Assert.Equal("ARGUMENT_INVALID", Code(reply.Json));
        Assert.Empty(reply.Progress);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
    }

    [Fact]
    public void ExplorePreparationBudgetUsesFiniteBoundsAndIsExploreOnly()
    {
        Assert.Null(CliArguments.Parse(["explore", "Service"]).ExplorePreparationMilliseconds());
        foreach (var value in new[] { 10, 30000 })
            Assert.Equal(value, CliArguments.Parse(["explore", "Service", "--prepare-max-milliseconds", value.ToString(System.Globalization.CultureInfo.InvariantCulture)]).ExplorePreparationMilliseconds());
        var zeroExploration = CliArguments.Parse(["explore", "Service", "--prepare-max-milliseconds", "5000", "--max-milliseconds", "0"]);
        Assert.Equal(5000, zeroExploration.ExplorePreparationMilliseconds());
        Assert.Equal(0, zeroExploration.ExplorationRequest().Budget!.MaxMilliseconds);
        Assert.Equal("ARGUMENT_UNKNOWN", Assert.Throws<CliFailure>(() => CliArguments.Parse(["search", "--term", "Service", "--prepare-max-milliseconds", "5000"])).Code);
        Assert.Equal("ARGUMENT_DUPLICATE", Assert.Throws<CliFailure>(() => CliArguments.Parse(["explore", "Service", "--prepare-max-milliseconds", "100", "--prepare-max-milliseconds", "200"])).Code);
        Assert.Equal("ARGUMENT_REQUIRED", Assert.Throws<CliFailure>(() => CliArguments.Parse(["explore", "Service", "--prepare-max-milliseconds"])).Code);
    }

    private sealed class PhaseCallbackWriter(string phase, Action callback) : StreamWriter(Stream.Null)
    {
        public bool Invoked { get; private set; }
        public override void WriteLine(string? value)
        {
            if (!Invoked && value?.Contains("\"phase\":\"" + phase + "\"", StringComparison.Ordinal) == true)
            {
                Invoked = true;
                callback();
            }
            base.WriteLine(value);
        }
    }

    [Fact]
    public async Task ZeroQueryBudgetWorksAtTheCliBoundaryAndRejectsSubMinimumPositiveValues()
    {
        using var fixture = new Fixture();
        foreach (var command in new[] { "search", "explore" })
        {
            var arguments = new[] { command, "--root", fixture.Root, "--term", "Service", "--max-milliseconds" };
            var actual = await Run([.. arguments, "0"]);
            Assert.Equal(0, actual.Exit);
            Assert.Equal("ARGUMENT_INVALID", Code((await Run([.. arguments, "1"])).Json));
        }
    }

    [Fact]
    public async Task CancellationAndUnsupportedStateProduceOneJsonWithoutWritingOrLeakingPaths()
    {
        using var fixture = new Fixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var output = new StringWriter();
        var errors = new StringWriter();
        Assert.Equal(130, await CliApplication.RunAsync(["status", "--root", fixture.Root], output, errors, cancelled.Token));
        Assert.Equal("CANCELLED", Code(output.ToString()));
        Assert.Empty(errors.ToString());
        File.WriteAllText(Path.Combine(fixture.Root, ".sharpdeps/workspace.json"), "{\"schemaVersion\":99}");
        var invalid = await Run("status", "--root", fixture.Root);
        Assert.Equal(6, invalid.Exit);
        Assert.Equal("WORKSPACE_STATE_INVALID", Code(invalid.Json));
        Assert.DoesNotContain(fixture.Root, invalid.Json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(invalid.Json);
        Assert.Single(document.RootElement.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task HelpAndDoctorAreMachineReadableAndDoNotProbeTargetSdk()
    {
        var help = await Run("help");
        Assert.Equal(0, help.Exit);
        Assert.Empty(help.Progress);
        Assert.Contains("--prepare-max-milliseconds", help.Json, StringComparison.Ordinal);
        Assert.Contains("finite, no 0", help.Json, StringComparison.Ordinal);
        using var helpDoc = JsonDocument.Parse(help.Json);
        Assert.Contains("index", helpDoc.RootElement.GetProperty("result").GetProperty("commands").EnumerateArray().Select(x => x.GetString()));
        var doctor = await Run("doctor");
        Assert.Equal(0, doctor.Exit);
        using var info = JsonDocument.Parse(doctor.Json);
        Assert.Equal("not-run", info.RootElement.GetProperty("result").GetProperty("sdkProbe").GetString());
    }

    [Fact]
    public async Task ActualIpcCoalescesPendingClientsAndCancellingOneCannotCancelSharedPublication()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        var publications = 0;
        var discarded = 0;
        var adopted = 0;
        var remoteAdmissions = 0;
        using var pinned = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var original = pinned.Snapshot();
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        await using var owner = new CliCoordinator(writer, async (_, token) =>
        {
            var count = Interlocked.Increment(ref builds);
            if (count == 1)
            {
                firstBuild.TrySetResult();
                await releaseBuild.Task.WaitAsync(token);
            }
            else
            {
                secondBuild.TrySetResult();
                await releaseSecond.Task.WaitAsync(token);
            }
            using var latest = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
            var previous = latest.Snapshot();
            return new(previous with
            {
                Graph = previous.Graph with
                {
                    Generation = previous.Graph.Generation + 1,
                    SnapshotId = Guid.NewGuid().ToString("N")
                }
            }, () => Interlocked.Increment(ref publications))
            {
                AdoptCommittedState = () => Interlocked.Increment(ref adopted),
                DiscardPreparedState = () => Interlocked.Increment(ref discarded)
            };
        });
        owner.RequestAccepted = id =>
        {
            if ((id is "cancel-client" or "surviving-client") && Interlocked.Increment(ref remoteAdmissions) == 2)
                admitted.TrySetResult();
        };
        var arguments = new[] { "update", "--root", fixture.Root, "--index", fixture.IndexPath, "--trusted", "--target", "App.csproj" };
        var update = CoordinatorUpdate.Parse(CliArguments.Parse(arguments), fixture.Root, fixture.IndexPath, fixture.Uuid);
        var initial = owner.UpdateAsync(update, "initial", deadline.Token);
        await firstBuild.Task.WaitAsync(deadline.Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var canceledClient = CoordinatorProtocol.TrySendAsync(owner.Owner, Request("cancel-client"), cancel.Token);
        var survivingClient = CoordinatorProtocol.TrySendAsync(owner.Owner, Request("surviving-client"), deadline.Token);
        await admitted.Task.WaitAsync(deadline.Token);
        Assert.Equal(2, owner.Status("status").DesiredRevision);
        releaseBuild.TrySetResult();
        await secondBuild.Task.WaitAsync(deadline.Token);
        Assert.Equal("INDEX_BUSY", Assert.Throws<IndexStoreException>(() =>
            IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true)).Code);
        var shortOwner = FinishShortOwnerAsync();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledClient);
        releaseSecond.TrySetResult();
        var survivor = Assert.IsType<CoordinatorReply>(await survivingClient);
        Assert.Null(survivor.ErrorCode);
        Assert.Equal(original.Graph.Generation + 1, survivor.Result!.Generation);
        Assert.Equal(survivor.Result.Generation, (await shortOwner).Result!.Generation);
        Assert.Equal(2, builds); // superseded extraction + one coalesced fresh extraction
        Assert.Equal(1, publications);
        Assert.Equal(1, Volatile.Read(ref adopted));
        Assert.Equal(2, Volatile.Read(ref discarded));
        Assert.Equal(original.Graph.Generation, pinned.Snapshot().Graph.Generation);
        Assert.Contains(pinned.Search("Service"), hit => hit.NodeId == fixture.Symbol("Service"));
        using var current = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        Assert.Equal(survivor.Result.Generation, current.Snapshot().Graph.Generation);
        Assert.Contains(current.Search("Service"), hit => hit.NodeId == fixture.Symbol("Service"));
        CoordinatorRequest Request(string id) => new(CoordinatorProtocol.Version, owner.Owner.Instance, fixture.Uuid,
            id, "update", fixture.Root, fixture.IndexPath, true, arguments);

        async Task<CoordinatorReply> FinishShortOwnerAsync()
        {
            var reply = await initial;
            await owner.DrainAsync();
            await owner.DisposeAsync();
            return reply;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCliDeadlineAndCallerCancellationDoNotCancelSharedOwner(bool cancelCaller)
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var building = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        await using var owner = new CliCoordinator(writer, async (_, token) =>
        {
            building.TrySetResult(token);
            await release.Task.WaitAsync(token);
            using var current = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
            var previous = current.Snapshot();
            return new(previous with
            {
                Graph = previous.Graph with
                {
                    Generation = previous.Graph.Generation + 1,
                    SnapshotId = Guid.NewGuid().ToString("N")
                }
            }, () => { });
        });
        owner.RequestAccepted = id =>
        {
            if (id == "deadline-survivor")
                admitted.TrySetResult();
        };
        var arguments = new[] { "update", "--root", fixture.Root, "--index", fixture.IndexPath,
            "--trusted", "--target", "App.csproj" };
        var output = new StringWriter();
        var expired = CliApplication.RunAsync([.. arguments, "--request-id", "deadline-client", "--timeout-seconds",
            cancelCaller ? "3600" : "1"], output, TextWriter.Null, caller.Token);
        var sharedToken = await building.Task.WaitAsync(deadline.Token);
        var survivor = Run([.. arguments, "--request-id", "deadline-survivor", "--timeout-seconds", "1800"]);
        await admitted.Task.WaitAsync(deadline.Token);
        if (cancelCaller)
            caller.Cancel();
        Assert.Equal(cancelCaller ? 130 : 4, await expired.WaitAsync(deadline.Token));
        Assert.Equal(cancelCaller ? "CANCELLED" : "TIMEOUT", Code(output.ToString()));
        Assert.False(sharedToken.IsCancellationRequested);
        Assert.False(survivor.IsCompleted);
        Assert.Equal("INDEX_BUSY", Assert.Throws<IndexStoreException>(() =>
            IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true)).Code);
        release.TrySetResult();
        var completed = await survivor.WaitAsync(deadline.Token);
        Assert.Equal(0, completed.Exit);
        using var reply = JsonDocument.Parse(completed.Json);
        Assert.Equal(2, reply.RootElement.GetProperty("result").GetProperty("generation").GetInt64());
        Assert.False(sharedToken.IsCancellationRequested);
        await owner.DrainAsync().WaitAsync(deadline.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task ActualCliShortOwnerDrainsSharedPublicationAfterItsDeadlineOrCallerCancellation(bool cancelCaller)
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var survivorCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var mutexName = "sharpdeps-drain-" + Guid.NewGuid().ToString("N");
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Named mutex ownership must stay on this dedicated thread across the asynchronous test.
        var held = Task.Factory.StartNew(() =>
        {
            using var mutex = new Mutex(true, mutexName, out var created);
            try
            {
                Assert.True(created);
                acquired.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
            finally
            {
                if (created)
                    mutex.ReleaseMutex();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        using var progress = new OwnerStartGateWriter();
        Task<int>? shortOwner = null;
        Task<int>? survivor = null;
        try
        {
            await acquired.Task.WaitAsync(deadline.Token);
            var (sdkRoot, _) = SemanticBaselineFixture();
            Assert.True(SharpDeps.Analysis.Roslyn.SemanticEnvironment.TryRegister(sdkRoot, out var reason), reason);
            var sdk = SharpDeps.Analysis.Roslyn.SemanticEnvironment.Describe(fixture.Root).SdkVersion;
            Assert.NotNull(sdk);
            File.WriteAllText(Path.Combine(fixture.Root, "global.json"), JsonSerializer.Serialize(new
            { sdk = new { version = sdk, rollForward = "disable" } }));
            var target = Path.Combine(fixture.Root, "App.csproj");
            File.WriteAllText(target, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableNETAnalyzers>false</EnableNETAnalyzers>
                  </PropertyGroup>
                  <UsingTask TaskName="HoldSharedPublication" TaskFactory="RoslynCodeTaskFactory"
                             AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
                    <ParameterGroup>
                      <MutexName ParameterType="System.String" Required="true" />
                      <ReadyFile ParameterType="System.String" Required="true" />
                    </ParameterGroup>
                    <Task>
                      <Code Type="Fragment" Language="cs"><![CDATA[
                        using (var mutex = new System.Threading.Mutex(false, MutexName))
                        {
                            System.IO.File.WriteAllText(ReadyFile, "waiting");
                            if (!mutex.WaitOne(System.TimeSpan.FromSeconds(90)))
                                throw new System.TimeoutException("Shared publication gate was not released.");
                            mutex.ReleaseMutex();
                        }
                      ]]></Code>
                    </Task>
                  </UsingTask>
                  <Target Name="HoldSharedPublicationBeforeCompile" BeforeTargets="CoreCompile"
                          Condition="'$(DesignTimeBuild)' == 'true'">
                    <HoldSharedPublication MutexName="{{mutexName}}"
                        ReadyFile="$(MSBuildProjectDirectory)/.sharpdeps/drain.ready" />
                  </Target>
                </Project>
                """);
            var config = Path.Combine(fixture.Root, "restore.config");
            File.WriteAllText(config, "<configuration><packageSources><clear /></packageSources></configuration>");
            await OracleProcess.ExecuteAsync(fixture.Root, ["restore", target, "--configfile", config, "--verbosity", "quiet"],
                deadline.Token, packageRoot: Path.Combine(fixture.Root, "packages"));
            using var pinned = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
            var original = pinned.Snapshot();
            Assert.Null(CoordinatorProtocol.Discover(fixture.Root, fixture.IndexPath, fixture.Uuid));
            var arguments = new[] { "update", "--root", fixture.Root, "--index", fixture.IndexPath,
                "--trusted", "--target", target };
            var output = new StringWriter();
            shortOwner = CliApplication.RunAsync([.. arguments, "--request-id", "short-owner", "--timeout-seconds",
                cancelCaller ? "3600" : "30"], output, progress, caller.Token);
            await progress.Started.Task.WaitAsync(deadline.Token);
            var owner = Assert.IsType<CoordinatorOwner>(CoordinatorProtocol.Discover(fixture.Root, fixture.IndexPath, fixture.Uuid));
            Assert.Equal(Environment.ProcessId, owner.ProcessId);
            var survivorOutput = new StringWriter();
            survivor = CliApplication.RunAsync([.. arguments, "--request-id", "drain-survivor", "--timeout-seconds", "1800"],
                survivorOutput, TextWriter.Null, survivorCancellation.Token);
            CoordinatorReply status;
            // Admit the survivor before cold SDK/workspace startup can consume the owner's deadline.
            do
            {
                status = Assert.IsType<CoordinatorReply>(await StatusAsync(deadline.Token));
                if (status.DesiredRevision != 1 || status.State != "updating")
                    await Task.Delay(20, deadline.Token);
            } while (status.DesiredRevision != 1 || status.State != "updating");
            progress.Resume.TrySetResult();
            do
            {
                status = Assert.IsType<CoordinatorReply>(await StatusAsync(deadline.Token));
                if (status.DesiredRevision < 2)
                    await Task.Delay(20, deadline.Token);
            } while (status.DesiredRevision < 2);
            while (!File.Exists(Path.Combine(fixture.Root, ".sharpdeps", "drain.ready")))
            {
                Assert.False(shortOwner.IsCompleted, output.ToString());
                Assert.False(survivor.IsCompleted, survivorOutput.ToString());
                await Task.Delay(20, deadline.Token);
            }
            Assert.False(shortOwner.IsCompleted);
            Assert.Equal(owner.Instance, status.Instance);
            Assert.Equal(0, status.Revision);
            Assert.Equal(2, status.DesiredRevision);
            Assert.Equal("updating", status.State);
            Assert.Null(status.ErrorCode);
            if (cancelCaller)
                caller.Cancel();
            // Observe status admission, not socket connectivity: Unix shares a listener across admitted sessions.
            while (true)
            {
                using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                probeDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    var probe = await StatusAsync(probeDeadline.Token);
                    if (probe is null)
                    {
                        break;
                    }
                    Assert.Equal(owner.Instance, probe.Instance);
                    Assert.Equal(0, probe.Revision);
                    Assert.Equal(2, probe.DesiredRevision);
                    Assert.Equal("updating", probe.State);
                    Assert.Null(probe.ErrorCode);
                }
                catch (OperationCanceledException) when (probeDeadline.IsCancellationRequested && !deadline.IsCancellationRequested)
                {
                    break;
                }
                catch (CliFailure failure) when (failure.Code == "COORDINATOR_RESULT_UNKNOWN")
                {
                    // A connection raced admission shutdown; retry without treating transport failure as proof.
                }
                await Task.Delay(20, deadline.Token);
            }
            Assert.False(shortOwner.IsCompleted);
            Assert.False(survivor.IsCompleted);
            Assert.Empty(output.ToString());
            Assert.Contains("extraction started", progress.ToString(), StringComparison.Ordinal);
            Assert.Equal(owner.Instance, CoordinatorProtocol.Discover(fixture.Root, fixture.IndexPath, fixture.Uuid)!.Instance);
            Assert.Equal("INDEX_BUSY", Assert.Throws<IndexStoreException>(() =>
                IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true)).Code);
            using (var unchanged = IndexReader.Open(fixture.IndexPath, fixture.Uuid))
                Assert.Equal(original.Graph.SnapshotId, unchanged.Snapshot().Graph.SnapshotId);
            release.TrySetResult();
            await held.WaitAsync(deadline.Token);
            var survivorExit = await survivor.WaitAsync(deadline.Token);
            Assert.True(survivorExit == 0, $"Exit={survivorExit}; {survivorOutput}");
            Assert.Equal(cancelCaller ? 130 : 4, await shortOwner.WaitAsync(deadline.Token));
            Assert.Equal(cancelCaller ? "CANCELLED" : "TIMEOUT", Code(output.ToString()));
            using var reply = JsonDocument.Parse(survivorOutput.ToString());
            var result = reply.RootElement.GetProperty("result");
            Assert.Equal(owner.Instance, result.GetProperty("ownerInstance").GetGuid());
            Assert.Equal(2, result.GetProperty("revision").GetInt64());
            Assert.Equal(2, result.GetProperty("desiredRevision").GetInt64());
            Assert.Equal(original.Graph.Generation + 1, result.GetProperty("generation").GetInt64());
            using var current = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
            var published = current.Snapshot().Graph;
            Assert.Equal(result.GetProperty("snapshotId").GetString(), published.SnapshotId);
            Assert.Equal(original.Graph.Generation + 1, published.Generation);
            var service = Assert.Single(published.Nodes, node => node.Kind == HarnessNodeKind.Type && node.Name == "Service");
            Assert.Equal(HarnessIdentity.LogicalSymbolId(fixture.Uuid,
                HarnessIdentity.ProjectId(fixture.Uuid, "App.csproj"), "type", "doc:T:Service"), service.Id);
            Assert.Contains(current.Search("Service"), hit => hit.NodeId == service.Id);
            Assert.Equal(original.Graph.SnapshotId, pinned.Snapshot().Graph.SnapshotId);
            Assert.Contains(pinned.Search("Service"), hit => hit.NodeId == fixture.Symbol("Service"));
            Assert.Equal(owner.Instance, CoordinatorProtocol.Discover(fixture.Root, fixture.IndexPath, fixture.Uuid)!.Instance);
            Assert.Null(await StatusAsync(deadline.Token));
            using var available = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
            Assert.Equal(fixture.Uuid, available.WorkspaceUuid);
            Task<CoordinatorReply?> StatusAsync(CancellationToken token) => CoordinatorProtocol.TrySendAsync(owner, new(CoordinatorProtocol.Version,
                owner.Instance, fixture.Uuid, "drain-status", "status", fixture.Root, fixture.IndexPath, false, []), token);
        }
        finally
        {
            progress.Resume.TrySetResult();
            release.TrySetResult();
            caller.Cancel();
            survivorCancellation.Cancel();
            await Task.WhenAll(new Task?[] { held, shortOwner, survivor }.OfType<Task>()).WaitAsync(TimeSpan.FromMinutes(2));
        }
    }

    [Fact]
    public async Task PreparedStateIsDiscardedOnRollbackAndAdoptionFailureCannotUndoCommit()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "App.csproj"), "<Project />");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var pinned = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var original = pinned.Snapshot();
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        var reject = true;
        var adopted = 0;
        var discarded = 0;
        await using var owner = new CliCoordinator(writer, (_, _) => Task.FromResult(new CliIndexBuild(
            original with
            {
                Graph = original.Graph with
                {
                    Generation = original.Graph.Generation + 1,
                    SnapshotId = Guid.NewGuid().ToString("N")
                }
            },
            () =>
            {
                if (reject)
                    throw new CliFailure("TEST_PUBLICATION_REJECTED", 3);
            })
        {
            AdoptCommittedState = () =>
            {
                Interlocked.Increment(ref adopted);
                throw new InvalidOperationException("Optional state adoption failed.");
            },
            DiscardPreparedState = () =>
            {
                Interlocked.Increment(ref discarded);
                throw new InvalidOperationException("Optional state cleanup failed.");
            }
        }));
        var update = CoordinatorUpdate.Parse(CliArguments.Parse(["update", "--root", fixture.Root,
            "--trusted", "--target", "App.csproj"]), fixture.Root, fixture.IndexPath, fixture.Uuid);
        var failed = await owner.UpdateAsync(update, "reject-prepared", deadline.Token);
        Assert.Equal("TEST_PUBLICATION_REJECTED", failed.ErrorCode);
        await WaitUntilAsync(() => Volatile.Read(ref discarded) == 1, deadline.Token);
        Assert.Equal(0, Volatile.Read(ref adopted));
        using (var unchanged = IndexReader.Open(fixture.IndexPath, fixture.Uuid))
            Assert.Equal(original.Graph.Generation, unchanged.Snapshot().Graph.Generation);
        reject = false;
        var committed = await owner.UpdateAsync(update, "commit-despite-cache", deadline.Token);
        Assert.Null(committed.ErrorCode);
        Assert.Equal(original.Graph.Generation + 1, committed.Result!.Generation);
        await WaitUntilAsync(() => Volatile.Read(ref discarded) == 2, deadline.Token);
        Assert.Equal(1, Volatile.Read(ref adopted));
        using var current = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        Assert.Equal(committed.Result.SnapshotId, current.Snapshot().Graph.SnapshotId);
        Assert.Equal(original.Graph.Generation, pinned.Snapshot().Graph.Generation);
        Assert.Equal(pinned.Search("Service").Select(h => h.NodeId), current.Search("Service").Select(h => h.NodeId));
    }

    [Fact]
    public async Task IpcRejectsUnsupportedRequestsAndExclusiveIndexCannotJoinItsOwner()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        await using var owner = new CliCoordinator(writer, (_, _) => throw new InvalidOperationException("Must not extract"));
        var denied = await CoordinatorProtocol.TrySendAsync(owner.Owner, new(CoordinatorProtocol.Version, owner.Owner.Instance,
            fixture.Uuid, "denied", "update", fixture.Root, fixture.IndexPath, false, []), deadline.Token);
        Assert.Equal("WORKSPACE_UNTRUSTED", denied!.ErrorCode);
        var mismatch = await CoordinatorProtocol.TrySendAsync(owner.Owner, new(99, owner.Owner.Instance,
            fixture.Uuid, "mismatch", "status", fixture.Root, fixture.IndexPath, false, []), deadline.Token);
        Assert.Equal("COORDINATOR_PROTOCOL_MISMATCH", mismatch!.ErrorCode);
        foreach (var command in new[] { "index", "update" })
        {
            var arguments = new[] { command, "--root", fixture.Root, "--index", fixture.IndexPath,
                "--trusted", "--target", "App.csproj", "--timeout-seconds", "0" };
            var remote = await CoordinatorProtocol.TrySendAsync(owner.Owner, new(CoordinatorProtocol.Version,
                owner.Owner.Instance, fixture.Uuid, "exclusive", "update", fixture.Root, fixture.IndexPath, true,
                arguments), deadline.Token);
            Assert.Equal("ARGUMENT_UNSUPPORTED", remote!.ErrorCode);
            var exclusive = await Run(arguments);
            Assert.Equal(6, exclusive.Exit);
            Assert.Equal("INDEX_BUSY", Code(exclusive.Json));
        }
        Assert.Equal(0, owner.Status("status").DesiredRevision);
        Assert.Equal("INDEX_BUSY", Assert.Throws<IndexStoreException>(() =>
            IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true)).Code);
        Assert.Throws<CliFailure>(() => CoordinatorProtocol.Decode<CoordinatorRequest>(
            Encoding.UTF8.GetBytes("{\"protocol\":1,\"protocol\":99}")));
        Assert.Throws<CliFailure>(() => CoordinatorProtocol.Decode<CoordinatorRequest>([0xff, 0xfe]));
        using var tooLarge = new MemoryStream([0xff, 0xff, 0xff, 0x7f]);
        await Assert.ThrowsAsync<CliFailure>(() => CoordinatorProtocol.ReadAsync<CoordinatorRequest>(tooLarge, deadline.Token));
        using var reader = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        Assert.Equal(1, reader.Snapshot().Graph.Generation);
    }

    [Fact]
    public void SdkUnavailableAtPublicationRequiresOwnerRestartAndKeepsSavedGeneration()
    {
        using var fixture = new Fixture();
        var sdk = CliSdkSelection.Capture(fixture.Root);
        using var pinned = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var previous = pinned.Snapshot();
        var next = previous with { Graph = previous.Graph with { Generation = previous.Graph.Generation + 1,
            SnapshotId = Guid.NewGuid().ToString("N") } };
        var stage = IndexStage.Create(next);
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        var buildGuardCalled = false;
        var guard = CoordinatorExecutor.PublicationGuard(sdk, fixture.Root, () => buildGuardCalled = true);
        // Change SDK selection after staging, before the real transaction's publication callback.
        File.WriteAllText(Path.Combine(fixture.Root, "global.json"),
            "{\"sdk\":{\"version\":\"99.0.999\",\"rollForward\":\"disable\"}}");
        Assert.Equal("CLI_SDK_UNAVAILABLE", Assert.Throws<CliFailure>(() => CliSdkSelection.Capture(fixture.Root)).Code);
        var failure = Assert.Throws<CliFailure>(() => writer.Commit(stage, verifySelection: guard));
        Assert.Equal("COORDINATOR_SDK_RESTART_REQUIRED", failure.Code);
        Assert.Equal(6, failure.ExitCode);
        Assert.False(buildGuardCalled);
        Assert.Equal(previous.Graph.Generation, pinned.Snapshot().Graph.Generation);
        using var current = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        Assert.Equal(previous.Graph.Generation, current.Snapshot().Graph.Generation);
        Assert.Equal(previous.Graph.SnapshotId, current.Snapshot().Graph.SnapshotId);
        Assert.Equal(previous.Graph.Nodes.Select(node => node.Id), current.Snapshot().Graph.Nodes.Select(node => node.Id));
        Assert.Equal(pinned.Search("Service").Select(hit => hit.NodeId), current.Search("Service").Select(hit => hit.NodeId));
    }

    [Fact]
    public async Task ConnectionBudgetDoesNotStopOwnerOrCancelExistingSessions()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        await using var owner = new CliCoordinator(writer, (_, _) => throw new InvalidOperationException("Must not extract"));
        var connections = new List<NamedPipeClientStream>();
        try
        {
            for (var i = 0; i < 32; i++)
            {
                var pipe = new NamedPipeClientStream(".", owner.Owner.Endpoint, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                connections.Add(pipe);
                await pipe.ConnectAsync(1000, deadline.Token);
            }
            var request = new CoordinatorRequest(CoordinatorProtocol.Version, owner.Owner.Instance, fixture.Uuid,
                "within-cap", "status", fixture.Root, fixture.IndexPath, false, []);
            await CoordinatorProtocol.WriteAsync(connections[0], request, deadline.Token);
            var reply = await CoordinatorProtocol.ReadAsync<CoordinatorReply>(connections[0], deadline.Token);
            Assert.Equal("idle", reply.State);
            Assert.Null(reply.ErrorCode);
            Assert.False(owner.Stopped.IsCompleted);
            Assert.Equal("INDEX_BUSY", Assert.Throws<IndexStoreException>(() =>
                IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true)).Code);
        }
        finally { foreach (var connection in connections) connection.Dispose(); }
    }

    [Fact]
    public async Task NativeWatchUsesTheExistingLeaseAndPeriodicFullRecoversLostNotifications()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        File.WriteAllText(Path.Combine(fixture.Root, "App.csproj"), "<Project />");
        var markdown = Path.Combine(fixture.Root, "guide.markdown");
        File.WriteAllText(markdown, "# Before\n");
        File.WriteAllText(Path.Combine(fixture.Root, "view.mdx"), "# Static MDX text\n");
        using var pinned = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var original = pinned.Snapshot();
        // A custom index with an input-like suffix must never become a Markdown input or an event loop.
        var customIndex = Path.Combine(fixture.Root, "state", "index.mdx");
        using var writer = IndexWriter.Open(fixture.Root, customIndex, fixture.Uuid, true);
        writer.Commit(IndexStage.Create(original));
        var neighbor = customIndex + ".owner.json.notes.md";
        File.WriteAllText(neighbor, "# User source\n");
        Assert.False(CliIndexer.IsIndexOutput(neighbor, customIndex));
        Assert.True(CliIndexer.IsIndexOutput(customIndex + ".owner.json." + Guid.NewGuid().ToString("N") + ".tmp", customIndex));
        var beforeOwner = await CliIndexer.CaptureWatchInputsAsync(fixture.Root, customIndex, deadline.Token);
        var recoveryInput = Path.Combine(fixture.Root, "additional.dat");
        string? recoveredSnapshot = null;
        var incrementalBuilds = 0;
        await using var owner = new CliCoordinator(writer, (update, token) =>
        {
            if (!update.Full) Interlocked.Increment(ref incrementalBuilds);
            using var reader = IndexReader.Open(customIndex, fixture.Uuid);
            var prior = reader.Snapshot();
            var snapshot = prior with { Graph = prior.Graph with {
                Generation = prior.Graph.Generation + 1, SnapshotId = Guid.NewGuid().ToString("N") } };
            if (update.Full && File.Exists(recoveryInput)
                && File.ReadAllBytes(recoveryInput).SequenceEqual(new byte[] { 1, 0, 2, 0 }))
                Volatile.Write(ref recoveredSnapshot, snapshot.Graph.SnapshotId);
            return Task.FromResult(new CliIndexBuild(snapshot, () => { }));
        });
        var update = CoordinatorUpdate.Parse(CliArguments.Parse(["update", "--root", fixture.Root, "--index", customIndex,
            "--trusted", "--target", "App.csproj"]), fixture.Root, customIndex, fixture.Uuid);
        // Keep the periodic timer out of the native-event/self-output assertions.
        owner.StartWatch(update, TimeSpan.FromHours(1));
        await WaitUntilAsync(() => owner.Status("initial").Result?.Generation >= 2, deadline.Token);
        Assert.Equal("INDEX_BUSY", Assert.Throws<IndexStoreException>(() =>
            IndexWriter.Open(fixture.Root, customIndex, fixture.Uuid, true)).Code);
        Assert.Equal(beforeOwner, await CliIndexer.CaptureWatchInputsAsync(fixture.Root, customIndex, deadline.Token));
        // Probe excluded outputs before editing an input: delayed duplicate save hints are not self-output loops.
        var generation = owner.Status("initial-committed").Result!.Generation;
        var temporary = customIndex + ".owner.json." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, "{}");
        File.Delete(temporary);
        await Task.Delay(200, deadline.Token);
        Assert.Equal(generation, owner.Status("no-self-loop").Result!.Generation);
        // Prove the real native notification path by observing a non-full extraction, rather than timing a sleep.
        File.WriteAllText(markdown, "# Native edit\n");
        await WaitUntilAsync(() => Volatile.Read(ref incrementalBuilds) != 0
            && owner.Status("native").State == "idle", deadline.Token);
        generation = owner.Status("saved").Result!.Generation;
        // Restart only the watch on the same owner/lease, then observe a subsequent periodic extraction.
        await owner.StopWatchAsync();
        owner.StartWatch(update, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => owner.Status("resync-started").Result?.Generation > generation
            && owner.Status("resync-started").State == "idle", deadline.Token);
        owner.Watch!.SetNotificationsEnabled(false);
        File.WriteAllBytes(recoveryInput, [1, 0, 2, 0]);
        IndexReader? recoveredReader = null;
        await WaitUntilAsync(() =>
        {
            var expected = Volatile.Read(ref recoveredSnapshot);
            if (expected is null || owner.Status("periodic").Result?.SnapshotId != expected) return false;
            var committed = IndexReader.Open(customIndex, fixture.Uuid);
            if (committed.Snapshot().Graph.SnapshotId != expected) { committed.Dispose(); return false; }
            recoveredReader = committed; // Pin this observed full commit before the next periodic stage.
            return true;
        }, deadline.Token);
        // Lost events use full extraction even when the selected Markdown manifest alone would be unchanged.
        using var current = recoveredReader!;
        Assert.Equal(original.Graph.Nodes.Select(node => node.Id), current.Snapshot().Graph.Nodes.Select(node => node.Id));
        Assert.Equal(pinned.Search("Service").Select(hit => hit.NodeId), current.Search("Service").Select(hit => hit.NodeId));
        Assert.Equal(1, pinned.Snapshot().Graph.Generation);
        await owner.StopWatchAsync();
        using var stoppedReader = IndexReader.Open(customIndex, fixture.Uuid);
        var stoppedGeneration = stoppedReader.Snapshot().Graph.Generation;
        File.WriteAllText(markdown, "# After stop\n");
        await Task.Delay(200, deadline.Token);
        using var afterStop = IndexReader.Open(customIndex, fixture.Uuid);
        Assert.Equal(stoppedGeneration, afterStop.Snapshot().Graph.Generation);
        var saved = await Run("search", "--root", fixture.Root, "--index", customIndex, "--term", "Service");
        Assert.Equal(0, saved.Exit);
        Assert.Contains("unverified", saved.Json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WatchHashGuardRollsBackLostEventAndStoppedStageCannotPublish(bool measured)
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        File.WriteAllText(Path.Combine(fixture.Root, "App.csproj"), "<Project />");
        var markdown = Path.Combine(fixture.Root, "guide.md");
        File.WriteAllText(markdown, "# Before\n");
        using var pinned = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var original = pinned.Snapshot();
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var owner = new CliCoordinator(writer, async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(original with { Graph = original.Graph with { Generation = 2, SnapshotId = "watch-staged" } }, () => { });
        });
        var directory = Path.Combine(fixture.Root, ".sharpdeps", "watch-metrics");
        var update = CoordinatorUpdate.Parse(CliArguments.Parse(["update", "--root", fixture.Root,
            "--trusted", "--target", "App.csproj"]), fixture.Root, fixture.IndexPath, fixture.Uuid);
        if (measured)
            update = update with { Options = update.Options with { MetricsDirectory = directory } };
        owner.StartWatch(update, TimeSpan.FromSeconds(30));
        await entered.Task.WaitAsync(deadline.Token);
        owner.Watch!.SetNotificationsEnabled(false);
        File.WriteAllText(markdown, "# Changed without notification\n");
        release.TrySetResult();
        await WaitUntilAsync(() => owner.Status("guard").ErrorCode == "WATCH_INPUT_CHANGED", deadline.Token);
        using (var unchanged = IndexReader.Open(fixture.IndexPath, fixture.Uuid))
        {
            Assert.Equal(original.Graph.Generation, unchanged.Snapshot().Graph.Generation);
            Assert.Equal(original.Graph.SnapshotId, unchanged.Snapshot().Graph.SnapshotId);
            Assert.Equal(pinned.Search("Service").Select(hit => hit.NodeId), unchanged.Search("Service").Select(hit => hit.NodeId));
        }
        await owner.StopWatchAsync();
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.StartWatch(update, TimeSpan.FromSeconds(30));
        await entered.Task.WaitAsync(deadline.Token);
        await owner.StopWatchAsync(); // cancels only the watch waiter, invalidating its eventual publication
        release.TrySetResult();
        await owner.DrainAsync();
        await owner.DisposeAsync();
        using var reacquired = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        using var final = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        Assert.Equal(original.Graph.Generation, final.Snapshot().Graph.Generation);
        Assert.Equal(original.Graph.Nodes.Select(node => node.Id), final.Snapshot().Graph.Nodes.Select(node => node.Id));
        Assert.Equal(pinned.Search("Service").Select(hit => hit.NodeId), final.Search("Service").Select(hit => hit.NodeId));
        if (measured)
        {
            var records = Directory.GetFiles(directory, "*.jsonl").Select(file => File.ReadAllText(file)).ToArray();
            Assert.All(records, text => Assert.DoesNotContain(fixture.Root, text, StringComparison.Ordinal));
            var cycles = records.Select(text => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line).GetProperty("payload")).ToArray())
                .Where(events => events[0].GetProperty("operation").GetString() == "watch-cycle").ToArray();
            Assert.Equal(2, cycles.Length);
            Assert.Contains(cycles, events => events[^1].GetProperty("outcome").GetString() == "failed");
            Assert.Contains(cycles, events => events[^1].GetProperty("outcome").GetString() == "cancelled");
            Assert.All(cycles, events => Assert.Contains(events, value => value.GetProperty("type").GetString() == "phase"
                && value.GetProperty("phase").GetString() == "watch.input-scan"));
            Assert.Contains(cycles.SelectMany(events => events), value => value.GetProperty("type").GetString() == "phase"
                && value.GetProperty("phase").GetString() == "watch.idle-wait");
        }
        else
            Assert.False(Directory.Exists(directory));
        Assert.False(SharpDeps.Analysis.Core.Diagnostics.PhaseMetrics.Enabled);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GeneratedDomainVariantsSurviveIndexingAndTheStoreRoundTrip()
    {
        var (fixtureRoot, _) = SemanticBaselineFixture();
        var root = Path.Combine(Path.GetTempPath(), "sharpdeps-generated-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyFixture(fixtureRoot, root);
            var target = Path.Combine(root, "SemanticBaseline.sln");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await RestoreAndBuildAsync(root, target, deadline.Token);

            var workspace = Guid.NewGuid();
            var outputDirectory = Path.Combine(root, ".sharpdeps", "generated-test-" + Guid.NewGuid().ToString("N"));
            var indexPath = Path.Combine(outputDirectory, "index.sqlite");
            using var writer = IndexWriter.Open(root, indexPath, workspace, true);
            var snapshot = await CliIndexer.BuildAsync(new(root, target, indexPath, workspace), null, deadline.Token);
            var generated = snapshot.Files.Where(file => file.Kind == IndexFileKinds.GeneratedSource).ToArray();
            Assert.Equal(2, generated.Length);
            Assert.Equal(2, generated.Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(2, snapshot.StoredGeneratedBodies.Count);
            Assert.All(generated, file =>
            {
                Assert.StartsWith("generated/Domain", file.RelativePath);
                Assert.False(Path.IsPathRooted(file.RelativePath));
                var body = Assert.Single(snapshot.StoredGeneratedBodies, body => body.SourceId == file.SourceId);
                Assert.Contains("class OrderFactory", body.Body, StringComparison.Ordinal);
            });

            writer.Commit(IndexStage.Create(snapshot), deadline.Token);
            using var reader = IndexReader.Open(indexPath, workspace);
            var saved = reader.Snapshot();
            Assert.Equal(generated.OrderBy(file => file.SourceId, StringComparer.Ordinal),
                saved.Files.Where(file => file.Kind == IndexFileKinds.GeneratedSource).OrderBy(file => file.SourceId, StringComparer.Ordinal));
            Assert.Equal(snapshot.StoredGeneratedBodies.OrderBy(body => body.SourceId, StringComparer.Ordinal),
                saved.StoredGeneratedBodies.OrderBy(body => body.SourceId, StringComparer.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OverBudgetGeneratedDocumentFailsTheIndexAndKeepsThePreviousSnapshot()
    {
        var (fixtureRoot, _) = SemanticBaselineFixture();
        var root = Path.Combine(Path.GetTempPath(), "sharpdeps-overbudget-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Work on a copy: the marker below changes what the generator emits, and
            // no other test may observe that state.
            CopyFixture(fixtureRoot, root);
            var domainProject = Path.Combine(root, "src", "Domain", "Domain.csproj");
            File.WriteAllText(domainProject, File.ReadAllText(domainProject).Replace("</Project>",
                "  <ItemGroup>\n    <AdditionalFiles Include=\"*.overbudget\" />\n  </ItemGroup>\n</Project>",
                StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(root, "src", "Generator", "GeneratedOrderFactory.cs"), OverBudgetGeneratorSource);

            var target = Path.Combine(root, "SemanticBaseline.sln");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await RestoreAndBuildAsync(root, target, deadline.Token);

            var workspace = Guid.NewGuid();
            var indexPath = Path.Combine(root, ".sharpdeps", "index.sqlite");
            using var writer = IndexWriter.Open(root, indexPath, workspace, true);
            var options = new CliIndexOptions(root, target, indexPath, workspace);
            var first = await CliIndexer.BuildAsync(options, null, deadline.Token);
            Assert.Equal(2, first.Files.Count(file => file.Kind == IndexFileKinds.GeneratedSource));
            writer.Commit(IndexStage.Create(first), deadline.Token);

            // The marker makes the generator emit a document over the retained-content
            // budget, so the build must fail before publishing anything.
            var marker = Path.Combine(root, "src", "Domain", "boundary.overbudget");
            File.WriteAllText(marker, "on");
            CliIndexException failure;
            try
            {
                failure = await Assert.ThrowsAsync<CliIndexException>(
                    () => CliIndexer.BuildAsync(options, first, deadline.Token));
            }
            finally
            {
                File.Delete(marker);
            }

            Assert.Equal("CLI_GENERATED_CONTENT_UNAVAILABLE", failure.Code);
            Assert.Contains("OverBudget.g.cs", failure.Message, StringComparison.Ordinal);

            using var reader = IndexReader.Open(indexPath, workspace);
            var saved = reader.Snapshot();
            Assert.Equal(1, saved.Graph.Generation);
            Assert.Equal(2, saved.Files.Count(file => file.Kind == IndexFileKinds.GeneratedSource));
            Assert.Equal(2, saved.StoredGeneratedBodies.Count);
            Assert.DoesNotContain(saved.Files,
                file => file.RelativePath.Contains("OverBudget", StringComparison.Ordinal));
        }
        finally
        {
            // The in-process build host keeps the copied analyzer assembly loaded
            // until the test process exits, so cleanup is best effort.
            TryDeleteDirectory(root);
        }
    }

    private const string OverBudgetGeneratorSource = """
        using System;
        using System.IO;
        using System.Linq;
        using System.Text;
        using Microsoft.CodeAnalysis;
        using Microsoft.CodeAnalysis.Text;

        namespace SampleGeneration;

        [Generator]
        public sealed class GeneratedOrderFactory : IIncrementalGenerator
        {
            public void Initialize(IncrementalGeneratorInitializationContext context)
            {
                context.RegisterPostInitializationOutput(static production =>
                {
                    const string source =
                        "namespace Generated;\n\n" +
                        "public static class OrderFactory\n" +
                        "{\n" +
                        "    public static Domain.Order Create(string id) => new() { Id = id };\n" +
                        "}\n";
                    production.AddSource("OrderFactory.g.cs", SourceText.From(source, Encoding.UTF8));
                });

                var additional = context.AdditionalTextsProvider.Collect();
                context.RegisterSourceOutput(additional, static (production, files) =>
                {
                    if (!files.Any(file => Path.GetFileName(file.Path)
                            .Equals("boundary.overbudget", StringComparison.OrdinalIgnoreCase)))
                    {
                        return;
                    }

                    production.AddSource("OverBudget.g.cs", SourceText.From(
                        "// " + new string('-', 1_100_000) + "\npublic static class OverBudget { }\n",
                        Encoding.UTF8));
                });
            }
        }
        """;

    private static (string Root, string Target) SemanticBaselineFixture()
    {
        var repository = Environment.GetEnvironmentVariable("SHARPDEPS_TEST_REPOSITORY_ROOT");
        if (string.IsNullOrWhiteSpace(repository))
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
                directory = directory.Parent;
            repository = directory?.FullName ?? throw new InvalidOperationException("Repository root could not be located from the test output directory.");
        }

        var root = Path.Combine(repository, "tests", "fixtures", "semantic-baseline");
        return (root, Path.Combine(root, "SemanticBaseline.sln"));
    }

    private static void CopyFixture(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (name is "bin" or "obj" or ".sharpdeps") continue;
            CopyFixture(directory, Path.Combine(destination, name));
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private static async Task RestoreAndBuildAsync(string root, string target, CancellationToken token)
    {
        foreach (var command in new[] { "restore", "build" })
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            foreach (var argument in new[] { command, target, "--verbosity", "quiet" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Fixture " + command + " did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(token); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.True(process.ExitCode == 0, await output + await errors);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A loaded analyzer assembly can keep a file open; the OS temp directory
            // owns the remainder.
        }
    }

    private static async Task WaitUntilAsync(Func<bool> ready, CancellationToken token)
    {
        while (!ready()) await Task.Delay(20, token);
    }

    private static string Code(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("errors")[0].GetProperty("code").GetString()!;
    }
    private static async Task<(int Exit, string Json, string Progress)> Run(params string[] arguments)
    {
        var output = new StringWriter(); var progress = new StringWriter();
        var exit = await CliApplication.RunAsync(arguments, output, progress);
        return (exit, output.ToString(), progress.ToString());
    }

    private sealed class OwnerStartGateWriter : StringWriter
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task WriteLineAsync(string? value)
        {
            await base.WriteLineAsync(value);
            Started.TrySetResult();
            await Resume.Task;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Base = OperatingSystem.IsMacOS()
            ? "/private/tmp/sharpdeps-sd207-fixtures" : Path.Combine(Path.GetTempPath(), "sharpdeps-sd207-fixtures");
        private const string Text = "public class Service {} public class ServiceExtra {}\n";
        private readonly Guid uuid = Guid.NewGuid();
        public Guid Uuid => uuid;
        public string Root { get; } = Path.Combine(Base, Guid.NewGuid().ToString("N"));
        public string IndexPath => Path.Combine(Root, ".sharpdeps/index.sqlite");
        private string Project => HarnessIdentity.ProjectId(uuid, "App.csproj");
        private string Variant => HarnessIdentity.VariantId(uuid, Project, "net10.0", "Debug");
        public string Symbol(string name) => HarnessIdentity.LogicalSymbolId(uuid, Project, "type", "T:" + name);
        public Fixture(bool seed = true)
        {
            Directory.CreateDirectory(Root);
            if (!seed) return;
            Directory.CreateDirectory(Path.Combine(Root, ".sharpdeps"));
            File.WriteAllText(Path.Combine(Root, ".sharpdeps/workspace.json"), JsonSerializer.Serialize(new {
                schemaVersion = 1, workspaceUuid = uuid, cursorKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
            var bytes = new UTF8Encoding(false).GetBytes(Text);
            File.WriteAllBytes(Path.Combine(Root, "Service.cs"), bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var location = new HarnessLocation("src", "v1", hash, new(0, Text.Length));
            var nodes = new[] { new HarnessNode(Symbol("Service"), HarnessNodeKind.Type, "Service", Project, location),
                new HarnessNode(Symbol("ServiceExtra"), HarnessNodeKind.Type, "ServiceExtra", Project, location) };
            var workspace = HarnessIdentity.WorkspaceId(uuid);
            var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, 1, HarnessGraphContract.IdentityVersion, workspace, "cli-fixture", 1,
                HarnessCoverage.Partial, [new(Variant, Project, "net10.0", "Debug", null, null, null)],
                [new(workspace, HarnessNodeKind.Workspace, "Fixture", null, null), new(Project, HarnessNodeKind.Project, "App", workspace, null), .. nodes],
                nodes.Select(n => new HarnessSymbolOccurrence(HarnessIdentity.SymbolOccurrenceId(uuid, n.Id, Variant), n.Id, Variant, location, [location])).ToArray(),
                [], [], null);
            var snapshot = new IndexSnapshot(graph, [new("src", "Service.cs", "code", hash, bytes.Length, Text.Length)], [], [],
                nodes.Select(n => new IndexSearchText(n.Id, n.Name, "", "", "", "Service.cs")).ToArray());
            using var writer = IndexWriter.Open(Root, IndexPath, uuid, true);
            writer.Commit(IndexStage.Create(snapshot));
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Base) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped run root.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
