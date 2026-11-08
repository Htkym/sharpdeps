namespace SharpDeps.Cli.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Core.Diagnostics;
using SharpDeps.Analysis.Markdown;
using SharpDeps.Cli;
using SharpDeps.Index;
using SharpDeps.Query;
using Syntamark;
using Xunit;

[CollectionDefinition("markdown-index", DisableParallelization = true)]
public sealed class MarkdownIndexCollection { }

[Collection("markdown-index")]
public sealed class CliMarkdownInputTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealIndexingPublishesTheOriginalBareGenericInputAndPreservesThePinnedGeneration()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await fixture.Restore(deadline.Token);
        using var writer = IndexWriter.Open(fixture.Root, fixture.Options.IndexPath, fixture.Options.WorkspaceUuid, true);
        var executor = new CoordinatorExecutor(writer);
        Task<CliIndexBuild> Build(bool full = false) => executor.BuildAsync(new(fixture.Options, full), deadline.Token);
        // Exact original first-index failure input, before testing a complete-to-Partial update.
        File.WriteAllText(fixture.MarkdownPath, "IReadOnlyState<T>");
        var initial = await Build(full: true);
        Assert.Equal(1, initial.MarkdownPrepareCalls);
        Assert.Equal(0, initial.MarkdownPreparedCacheHits);
        Assert.Equal(1, initial.Snapshot.Graph.Generation);
        Assert.Equal(HarnessCoverage.Partial, initial.Snapshot.Graph.Coverage);
        Assert.Single(initial.Snapshot.Documents);
        writer.Commit(IndexStage.Create(initial.Snapshot), deadline.Token, initial.VerifySelection);
        initial.AdoptCommittedState!();
        File.WriteAllText(fixture.MarkdownPath, "`IReadOnlyState<T>`");
        var complete = await Build();
        Assert.Equal(1, complete.MarkdownPrepareCalls);
        Assert.Equal(0, complete.MarkdownPreparedCacheHits);
        Assert.Single(complete.Snapshot.Documents);
        writer.Commit(IndexStage.Create(complete.Snapshot), deadline.Token, complete.VerifySelection);
        complete.AdoptCommittedState!();
        using var pinned = IndexReader.Open(fixture.Options.IndexPath, fixture.Options.WorkspaceUuid);
        var original = JsonSerializer.Serialize(pinned.Snapshot(), QueryJson.Options);
        File.WriteAllText(fixture.MarkdownPath, "IReadOnlyState<T>");
        var partial = await Build();
        Assert.Equal(1, partial.MarkdownPrepareCalls);
        Assert.Equal(0, partial.MarkdownPreparedCacheHits);
        Assert.Equal(complete.Snapshot.Graph.Generation + 1, partial.Snapshot.Graph.Generation);
        Assert.Equal(HarnessCoverage.Partial, partial.Snapshot.Graph.Coverage);
        Assert.Contains(partial.Snapshot.Graph.Diagnostics!, diagnostic => diagnostic.Code == "markdown.parser-diagnostic.LMD006");
        Assert.Equal(Assert.Single(complete.Snapshot.Documents).DocumentUuid, Assert.Single(partial.Snapshot.Documents).DocumentUuid);
        Assert.NotEmpty(Assert.Single(partial.Snapshot.Documents).Sections);
        IndexSnapshotValidator.Validate(partial.Snapshot);
        writer.Commit(IndexStage.Create(partial.Snapshot), deadline.Token, partial.VerifySelection);
        partial.AdoptCommittedState!();
        Assert.Equal(original, JsonSerializer.Serialize(pinned.Snapshot(), QueryJson.Options));
        using var current = IndexReader.Open(fixture.Options.IndexPath, fixture.Options.WorkspaceUuid);
        Assert.Equal(partial.Snapshot.Graph.Generation, current.Snapshot().Graph.Generation);
        Assert.Contains(current.Snapshot().Files, file => file.Kind == "markdown" && file.RelativePath == "README.md"
            && file.ContentHash == Assert.Single(partial.Snapshot.Documents).ContentHash);
        var query = new QueryService(new PinnedQueryIndex(current));
        Assert.False(query.Execute(new("complete", QueryKind.Status, RequireComplete: true)).Succeeded);

        // Saved readers order search rows by node ID; compare the complete saved representation on both sides.
        var committed = JsonSerializer.Serialize(current.Snapshot(), QueryJson.Options);
        var unchanged = await Build();
        Assert.Equal(0, unchanged.MarkdownPrepareCalls);
        Assert.Equal(1, unchanged.MarkdownPreparedCacheHits);
        Assert.Equal(Assert.Single(partial.Snapshot.Documents).Sections, Assert.Single(unchanged.Snapshot.Documents).Sections);
        const string duplicateId = "---\nsharpdeps:\n  id: duplicate\n---\n# Duplicate\n";
        File.WriteAllText(fixture.MarkdownPath, duplicateId);
        File.WriteAllText(Path.Combine(fixture.Root, "duplicate.md"), duplicateId);
        var duplicate = await Assert.ThrowsAsync<CliIndexException>(() => CliIndexer.BuildForPublicationAsync(
            fixture.Options, partial.Snapshot, deadline.Token));
        Assert.Equal("CLI_MARKDOWN_ID_AMBIGUOUS", duplicate.Code);
        File.Delete(Path.Combine(fixture.Root, "duplicate.md"));
        CliIndexBuild? ownerBuild = null;
        var rejectPublication = false;
        await using (var owner = new CliCoordinator(writer, async (update, token) =>
        {
            ownerBuild = await executor.BuildAsync(update, token);
            return rejectPublication ? ownerBuild with
            {
                VerifySelection = () => throw new CliFailure("TEST_PUBLICATION_REJECTED", 4)
            } : ownerBuild;
        }))
        {
            var update = new CoordinatorUpdate(fixture.Options, false);
            // The coordinator must not adopt a parsed replacement whose transaction is rejected.
            File.WriteAllText(fixture.MarkdownPath, "# Replacement\n[Example](Example.cs)\nChanged paragraph.\n");
            rejectPublication = true;
            var rejected = await owner.UpdateAsync(update, "reject-prepared-candidate", deadline.Token);
            Assert.Equal("TEST_PUBLICATION_REJECTED", rejected.ErrorCode);
            Assert.Equal(1, ownerBuild!.MarkdownPrepareCalls);
            Assert.Equal(0, ownerBuild.MarkdownPreparedCacheHits);
            using (var afterRejection = IndexReader.Open(fixture.Options.IndexPath, fixture.Options.WorkspaceUuid))
                Assert.Equal(committed, JsonSerializer.Serialize(afterRejection.Snapshot(), QueryJson.Options));

            File.WriteAllText(fixture.MarkdownPath, "IReadOnlyState<T>");
            rejectPublication = false;
            var cached = await owner.UpdateAsync(update, "retain-committed-prepared", deadline.Token);
            Assert.Null(cached.ErrorCode);
            Assert.Equal(0, ownerBuild!.MarkdownPrepareCalls);
            Assert.Equal(1, ownerBuild.MarkdownPreparedCacheHits);
            // A successful publication must adopt its new facts before the next owner update.
            File.WriteAllText(fixture.MarkdownPath, "# Replacement\n[Example](Example.cs)\nChanged paragraph.\n");
            Assert.Null((await owner.UpdateAsync(update, "adopt-new-prepared", deadline.Token)).ErrorCode);
            Assert.Equal(1, ownerBuild!.MarkdownPrepareCalls);
            Assert.Null((await owner.UpdateAsync(update, "reuse-new-prepared", deadline.Token)).ErrorCode);
            Assert.Equal(0, ownerBuild!.MarkdownPrepareCalls);
            Assert.Equal(1, ownerBuild.MarkdownPreparedCacheHits);
            Assert.Equal(original, JsonSerializer.Serialize(pinned.Snapshot(), QueryJson.Options));

            var beforeCatalogEdit = ownerBuild.Snapshot;
            var mention = Assert.Single(beforeCatalogEdit.Graph.Nodes, node =>
                node.Kind == HarnessNodeKind.SymbolMention && node.Name == "Example.cs");
            var oldTargets = beforeCatalogEdit.Graph.Edges.Where(edge => edge.SourceNodeId == mention.Id
                && edge.Kind == "documents_symbol").Select(edge => edge.TargetNodeId).ToHashSet(StringComparer.Ordinal);
            Assert.NotEmpty(oldTargets);
            File.WriteAllText(Path.Combine(fixture.Root, "Example.cs"), "public sealed class Renamed { }");
            Assert.Null((await owner.UpdateAsync(update, "rebind-cached-mentions", deadline.Token)).ErrorCode);
            Assert.Equal(0, ownerBuild!.MarkdownPrepareCalls);
            Assert.Equal(1, ownerBuild.MarkdownPreparedCacheHits);
            Assert.DoesNotContain(ownerBuild.Snapshot.Graph.Edges, edge => edge.SourceNodeId == mention.Id
                && edge.Kind == "documents_symbol" && oldTargets.Contains(edge.TargetNodeId));
            Assert.Contains(ownerBuild.Snapshot.Graph.Edges, edge => edge.SourceNodeId == mention.Id
                && edge.Kind == "documents_symbol");
            await AssertColdEquivalent(beforeCatalogEdit, ownerBuild.Snapshot);

            var beforeRename = ownerBuild.Snapshot;
            var documentUuid = Assert.Single(beforeRename.Documents).DocumentUuid;
            File.Move(fixture.MarkdownPath, Path.Combine(fixture.Root, "renamed.md"));
            Assert.Null((await owner.UpdateAsync(update, "rename-prepared-source", deadline.Token)).ErrorCode);
            Assert.Equal(1, ownerBuild!.MarkdownPrepareCalls);
            Assert.Equal(0, ownerBuild.MarkdownPreparedCacheHits);
            var renamed = Assert.Single(ownerBuild.Snapshot.Documents);
            Assert.Equal("renamed.md", renamed.RelativePath);
            Assert.Equal(documentUuid, renamed.DocumentUuid);
            await AssertColdEquivalent(beforeRename, ownerBuild.Snapshot);
        }
        using (var latest = IndexReader.Open(fixture.Options.IndexPath, fixture.Options.WorkspaceUuid))
            committed = JsonSerializer.Serialize(latest.Snapshot(), QueryJson.Options);
        File.WriteAllBytes(fixture.MarkdownPath, [0xff]);
        await Assert.ThrowsAsync<CliIndexException>(() => CliIndexer.BuildForPublicationAsync(fixture.Options, partial.Snapshot, deadline.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CliIndexer.BuildForPublicationAsync(
            fixture.Options, partial.Snapshot, new CancellationToken(true)));
        using var retained = IndexReader.Open(fixture.Options.IndexPath, fixture.Options.WorkspaceUuid);
        Assert.Equal(committed, JsonSerializer.Serialize(retained.Snapshot(), QueryJson.Options));

        async Task AssertColdEquivalent(IndexSnapshot previous, IndexSnapshot cached)
        {
            // Snapshot-local Markdown IDs include this run identity; bind the same input as the process oracle.
            var allocations = 0;
            Guid AllocateSnapshot() => ++allocations == 1 ? Guid.ParseExact(cached.Graph.SnapshotId, "N")
                : throw new InvalidOperationException("The oracle unexpectedly allocated a new durable identity.");
            var cold = await CliIndexer.BuildForPublicationAsync(fixture.Options, previous, deadline.Token, null, AllocateSnapshot);
            var differences = FullBuildOracle.Differences(new(cold.Snapshot, [], [], Environment.ProcessId),
                new(cached, [], [], Environment.ProcessId));
            Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences.Take(20)));
        }
    }

    [Fact]
    public void OriginalBareGenericFactsAreAdmittedWithoutChangingTheirPartialStatus()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.MarkdownPath, "IReadOnlyState<T>");
        var partial = fixture.Facts();
        CliIndexer.RequireIndexableMarkdownFacts(partial, "README.md");
        Assert.Equal(MarkdownParseStatus.Partial, partial.Status);
        Assert.Equal(MarkdownCoverageState.Complete, partial.Coverage.RawPositions);
        Assert.Equal(MarkdownCoverageState.Partial, partial.Coverage.DecodedMapping);
        Assert.Contains(partial.Diagnostics, diagnostic => diagnostic.Id == "LMD006");
        Assert.False(File.Exists(fixture.Options.IndexPath));
        File.WriteAllText(fixture.MarkdownPath, "`IReadOnlyState<T>`");
        CliIndexer.RequireIndexableMarkdownFacts(fixture.Facts(), "README.md");
        Assert.Equal(MarkdownParseStatus.Complete, fixture.Facts().Status);
    }

    [Fact]
    public async Task IncompleteUpdateRetainsTheCompleteDocumentAndReturnsActionableIpcError()
    {
        using var fixture = new Fixture();
        var previous = fixture.CompleteSnapshot();
        using var writer = IndexWriter.Open(fixture.Root, fixture.Options.IndexPath, fixture.Options.WorkspaceUuid, true);
        writer.Commit(IndexStage.Create(previous));
        using var pinned = IndexReader.Open(fixture.Options.IndexPath, fixture.Options.WorkspaceUuid);
        var original = pinned.Snapshot();
        File.WriteAllText(fixture.MarkdownPath, "---\na: [\n---\n# A\nbody\n");
        await using var owner = new CliCoordinator(writer, (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            CliIndexer.RequireIndexableMarkdownFacts(fixture.Facts(), "README.md");
            throw new InvalidOperationException("Incomplete facts must fail before publication.");
        });
        using var output = new StringWriter();
        using var stderr = new StringWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exit = await CliApplication.RunAsync(["update", "--root", fixture.Root, "--target", "App.csproj",
            "--trusted", "--quiet"], output, stderr, deadline.Token);
        Assert.Equal(4, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var error = Assert.Single(json.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("CLI_MARKDOWN_FACTS_INCOMPLETE", error.GetProperty("code").GetString());
        Assert.Contains("no new generation", error.GetProperty("message").GetString());
        Assert.DoesNotContain(fixture.Root, output.ToString());
        Assert.Empty(stderr.ToString());
        Assert.Same(original, pinned.Snapshot());
        using var current = IndexReader.Open(fixture.Options.IndexPath, fixture.Options.WorkspaceUuid);
        Assert.Equal(previous.Graph.SnapshotId, current.Snapshot().Graph.SnapshotId);
        Assert.Equal(previous.Graph.Generation, current.Snapshot().Graph.Generation);
        Assert.Equal(JsonSerializer.Serialize(original, QueryJson.Options), JsonSerializer.Serialize(current.Snapshot(), QueryJson.Options));
        var query = new QueryService(new PinnedQueryIndex(current));
        var saved = query.Execute(new("complete", QueryKind.Status, RequireComplete: true));
        Assert.True(saved.Succeeded);
        Assert.Equal("unverified", saved.Envelope.Snapshot.Freshness);
    }

#if SHARPDEPS_MARKDOWN_SESSION_CANDIDATE
    [Fact]
    [Trait("Category", "Integration")]
    public async Task OwnerSessionsCacheCatalogRefreshAndPublicationRejectionMatchTheSavedColdOracle()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await fixture.Restore(deadline.Token);
        const string raw = "Alpha 😀\r\n\r\nExample Beta\r\n\r\nGamma suffix\r\n";
        File.WriteAllText(fixture.MarkdownPath, raw);
        using var writer = IndexWriter.Open(fixture.Root, fixture.Options.IndexPath, fixture.Options.WorkspaceUuid, true);
        var oraclePath = Path.Combine(fixture.Root, ".sharpdeps", "oracle.sqlite");
        using var oracleWriter = IndexWriter.Open(fixture.Root, oraclePath, fixture.Options.WorkspaceUuid, true);
        var executor = new CoordinatorExecutor(writer);
        var initial = await Build("full", full: true);
        Commit(initial);
        using var pinned = IndexReader.Open(fixture.Options.IndexPath, fixture.Options.WorkspaceUuid);
        var pinnedJson = JsonSerializer.Serialize(pinned.Snapshot(), QueryJson.Options);
        var documentId = Assert.Single(initial.Snapshot.Documents).DocumentUuid;
        var sectionToken = Assert.Single(Assert.Single(initial.Snapshot.Documents).Sections).Token;

        var editedRaw = raw.Replace("Beta", "Beta longer");
        File.WriteAllText(fixture.MarkdownPath, editedRaw);
        var edited = await Build("session-delta");
        Assert.Equal(documentId, Assert.Single(edited.Snapshot.Documents).DocumentUuid);
        Assert.Equal(sectionToken, Assert.Single(Assert.Single(edited.Snapshot.Documents).Sections).Token);
        var mention = Assert.Single(edited.Snapshot.Graph.Nodes, node => node.Kind == HarnessNodeKind.SymbolMention && node.Name == "Example");
        Assert.Equal(new HarnessRawSpan(editedRaw.IndexOf("Example", StringComparison.Ordinal), "Example".Length), mention.Location!.RawSpan);
        var oldTargets = edited.Snapshot.Graph.Edges.Where(edge => edge.SourceNodeId == mention.Id && edge.Kind == "mentions_symbol")
            .Select(edge => edge.TargetNodeId).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(oldTargets);
        Commit(edited);
        await AssertColdEquivalent(initial.Snapshot, edited.Snapshot);

        File.WriteAllText(Path.Combine(fixture.Root, "Example.cs"), "public sealed class Renamed { }");
        var catalogOnly = await Build("cache");
        Assert.Equal(0, catalogOnly.MarkdownPrepareCalls);
        Assert.Equal(1, catalogOnly.MarkdownPreparedCacheHits);
        Assert.DoesNotContain(catalogOnly.Snapshot.Graph.Edges, edge => edge.SourceNodeId == mention.Id
            && edge.Kind == "mentions_symbol" && oldTargets.Contains(edge.TargetNodeId));
        Commit(catalogOnly);
        await AssertColdEquivalent(edited.Snapshot, catalogOnly.Snapshot);

        File.WriteAllText(fixture.MarkdownPath, "\uFEFF# Heading\r\n[Example](Example.cs)\r\n" + editedRaw);
        var unsupported = await Build("full");
        Commit(unsupported);
        await AssertColdEquivalent(catalogOnly.Snapshot, unsupported.Snapshot);
        File.WriteAllText(fixture.MarkdownPath, editedRaw);
        var undo = await Build("full");
        Commit(undo);
        await AssertColdEquivalent(unsupported.Snapshot, undo.Snapshot);

        File.WriteAllText(fixture.MarkdownPath, editedRaw.Replace("longer", "rejected publication"));
        var rejected = await Build("session-delta");
        Assert.Throws<CliFailure>(() => writer.Commit(IndexStage.Create(rejected.Snapshot), deadline.Token,
            () => throw new CliFailure("TEST_PUBLICATION_REJECTED", 4)));
        using (var afterRejection = IndexReader.Open(fixture.Options.IndexPath, fixture.Options.WorkspaceUuid))
            Assert.Equal(undo.Snapshot.Graph.SnapshotId, afterRejection.Snapshot().Graph.SnapshotId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.BuildAsync(new(fixture.Options, false), new(true)));
        File.WriteAllText(fixture.MarkdownPath, editedRaw);
        var retained = await Build("cache");
        Commit(retained);
        await AssertColdEquivalent(undo.Snapshot, retained.Snapshot);
        Assert.Equal(pinnedJson, JsonSerializer.Serialize(pinned.Snapshot(), QueryJson.Options));

        var restarted = new CoordinatorExecutor(writer);
        var rebuilt = await restarted.BuildAsync(new(fixture.Options, false), deadline.Token);
        Assert.Equal(1, rebuilt.MarkdownPrepareCalls);
        Assert.Equal(0, rebuilt.MarkdownPreparedCacheHits);
        Commit(rebuilt);
        await AssertColdEquivalent(retained.Snapshot, rebuilt.Snapshot);

        async Task<CliIndexBuild> Build(string mode, bool full = false)
        {
            var directory = Path.Combine(fixture.Root, ".sharpdeps", "metrics");
            string runId;
            CliIndexBuild build;
            using (var metrics = PhaseMetrics.Start(directory, "markdown-owner") ?? throw new InvalidOperationException("Metrics did not start."))
            {
                runId = metrics.RunId;
                build = await executor.BuildAsync(new(fixture.Options, full), deadline.Token);
                metrics.Complete();
            }
            var events = File.ReadAllLines(Path.Combine(directory, runId + ".jsonl"))
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line).GetProperty("payload")).ToArray();
            Assert.Contains(events, item => item.GetProperty("type").GetString() == "decision"
                && item.GetProperty("stage").GetString() == "markdown.prepare.mode" && item.GetProperty("reason").GetString() == mode);
            if (mode == "cache")
                Assert.DoesNotContain(events, item => item.GetProperty("type").GetString() == "count"
                    && item.GetProperty("name").GetString()!.StartsWith("markdown.session.", StringComparison.Ordinal));
            Assert.DoesNotContain(build.Snapshot.Graph.Diagnostics!, diagnostic => diagnostic.Code.StartsWith("markdown.session.", StringComparison.Ordinal)
                || diagnostic.Code == "markdown.prepare.mode");
            return build;
        }

        void Commit(CliIndexBuild build)
        {
            writer.Commit(IndexStage.Create(build.Snapshot), deadline.Token, build.VerifySelection);
            build.AdoptCommittedState!();
        }

        async Task AssertColdEquivalent(IndexSnapshot previous, IndexSnapshot cached)
        {
            var previousTokens = previous.Documents.SelectMany(document => document.Sections.Select(section => section.Token))
                .ToHashSet(StringComparer.Ordinal);
            var allocations = new Queue<Guid>(new[] { Guid.ParseExact(cached.Graph.SnapshotId, "N") }
                .Concat(cached.Documents.SelectMany(document => document.Sections).Where(section => !previousTokens.Contains(section.Token))
                    .Select(section => Guid.ParseExact(section.Token, "N"))));
            var cold = await CliIndexer.BuildForPublicationAsync(fixture.Options, previous, deadline.Token, null, () => allocations.Dequeue());
            Assert.Empty(allocations);
            oracleWriter.Commit(IndexStage.Create(cold.Snapshot), deadline.Token, cold.VerifySelection);
            using var coldReader = IndexReader.Open(oraclePath, fixture.Options.WorkspaceUuid);
            using var actualReader = IndexReader.Open(fixture.Options.IndexPath, fixture.Options.WorkspaceUuid);
            var differences = FullBuildOracle.Differences(Observe(coldReader), Observe(actualReader));
            Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences.Take(20)));
        }

        static OracleObservation Observe(IndexReader reader)
            => new(reader.Snapshot(), new[] { "Example", "longer", "suffix", "Alpha", "Renamed" }
                .ToDictionary(term => term, term => reader.Search(term, 500).ToArray(), StringComparer.Ordinal), [], Environment.ProcessId);
    }
#endif

    private sealed class Fixture : IDisposable
    {
        private static readonly string Base = OperatingSystem.IsMacOS()
            ? "/private/tmp/sharpdeps-markdown-fixtures" : Path.Combine(Path.GetTempPath(), "sharpdeps-markdown-fixtures");
        public string Root { get; } = Path.Combine(Base, Guid.NewGuid().ToString("N"));
        public string MarkdownPath => Path.Combine(Root, "README.md");
        public CliIndexOptions Options { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(Root, "Example.cs"), "public sealed class Example { }");
            File.WriteAllText(MarkdownPath, "`IReadOnlyState<T>`");
            var state = CliWorkspaceState.Open(Root, create: true);
            Options = new(Root, Path.Combine(Root, "App.csproj"), Path.Combine(Root, ".sharpdeps", "index.sqlite"), state.WorkspaceUuid);
        }

        public async Task Restore(CancellationToken token)
        {
            var config = Path.Combine(Root, "NuGet.Config");
            File.WriteAllText(config, "<configuration><packageSources><clear /></packageSources></configuration>");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "restore", Options.Target, "--configfile", config, "--verbosity", "quiet" })
                start.ArgumentList.Add(argument);
            using (var restore = Process.Start(start) ?? throw new InvalidOperationException("Offline fixture restore did not start."))
            {
                var stdout = restore.StandardOutput.ReadToEndAsync();
                var stderr = restore.StandardError.ReadToEndAsync();
                try
                {
                    await restore.WaitForExitAsync(token);
                }
                finally
                {
                    if (!restore.HasExited)
                    {
                        restore.Kill(entireProcessTree: true);
                        await restore.WaitForExitAsync();
                    }
                }
                Assert.True(restore.ExitCode == 0, await stdout + await stderr);
            }
        }

        public IndexSnapshot CompleteSnapshot()
        {
            var raw = File.ReadAllText(MarkdownPath);
            var bytes = Encoding.UTF8.GetBytes(raw);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var document = Guid.NewGuid();
            var scope = HarnessIdentity.WorkspaceId(Options.WorkspaceUuid);
            var request = new MarkdownGraphRequest(raw, Options.WorkspaceUuid, document, scope, "readme", hash, "complete-markdown", 1);
            var adapter = new LithoSharpMarkdownAdapter();
            var facts = adapter.Analyze(request, true).Facts;
            Assert.Equal(MarkdownParseStatus.Complete, facts.Status);
            var sections = MarkdownIdentityMatcher.MatchSections(Options.WorkspaceUuid, document, raw, facts, null,
                () => Guid.NewGuid().ToString("N"));
            var projection = adapter.Analyze(request with { SectionTokens = sections.SectionTokens,
                SectionIdentityTextHash = sections.TextHash }, true);
            Assert.Equal(HarnessCoverage.CompleteWithinScope, projection.Graph.Coverage);
            return new(projection.Graph, [new("readme", "README.md", "markdown", hash, bytes.Length, raw.Length)],
                [new(document, "README.md", null, hash, scope, facts.ParserVersion, facts.ContractVersion, facts.ProfileId,
                    facts.OptionsHash, sections.Snapshot.Sections.Select(s =>
                        new IndexSectionIdentity(s.LocalKey, s.Token, s.HeadingKey, s.BodyHash)).ToArray())], [], []);
        }

        public MarkdownDocument Facts() => new LithoSharpMarkdownAdapter().Analyze(
            new(File.ReadAllText(MarkdownPath), Options.WorkspaceUuid, Guid.NewGuid(),
                HarnessIdentity.WorkspaceId(Options.WorkspaceUuid), "readme", null, "facts", 1), true).Facts;

        public void Dispose()
        {
            if (Path.GetDirectoryName(Path.GetFullPath(Root)) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Base)))
                throw new InvalidOperationException("Fixture cleanup escaped the temporary directory.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
