namespace SharpDeps.Cli.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
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
        var config = Path.Combine(fixture.Root, "NuGet.Config");
        File.WriteAllText(config, "<configuration><packageSources><clear /></packageSources></configuration>");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { WorkingDirectory = fixture.Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "restore", fixture.Options.Target, "--configfile", config, "--verbosity", "quiet" })
            start.ArgumentList.Add(argument);
        using (var restore = Process.Start(start) ?? throw new InvalidOperationException("Offline fixture restore did not start."))
        {
            var stdout = restore.StandardOutput.ReadToEndAsync();
            var stderr = restore.StandardError.ReadToEndAsync();
            try { await restore.WaitForExitAsync(deadline.Token); }
            finally
            {
                if (!restore.HasExited) { restore.Kill(entireProcessTree: true); await restore.WaitForExitAsync(); }
            }
            Assert.True(restore.ExitCode == 0, await stdout + await stderr);
        }
        using var writer = IndexWriter.Open(fixture.Root, fixture.Options.IndexPath, fixture.Options.WorkspaceUuid, true);
        // Exact original first-index failure input, before testing a complete-to-Partial update.
        File.WriteAllText(fixture.MarkdownPath, "IReadOnlyState<T>");
        var initial = await CliIndexer.BuildForPublicationAsync(fixture.Options, null, deadline.Token);
        Assert.Equal(1, initial.Snapshot.Graph.Generation);
        Assert.Equal(HarnessCoverage.Partial, initial.Snapshot.Graph.Coverage);
        Assert.Single(initial.Snapshot.Documents);
        writer.Commit(IndexStage.Create(initial.Snapshot), deadline.Token, initial.VerifySelection);
        File.WriteAllText(fixture.MarkdownPath, "`IReadOnlyState<T>`");
        var complete = await CliIndexer.BuildForPublicationAsync(fixture.Options, initial.Snapshot, deadline.Token);
        Assert.Single(complete.Snapshot.Documents);
        writer.Commit(IndexStage.Create(complete.Snapshot), deadline.Token, complete.VerifySelection);
        using var pinned = IndexReader.Open(fixture.Options.IndexPath, fixture.Options.WorkspaceUuid);
        var original = JsonSerializer.Serialize(pinned.Snapshot(), QueryJson.Options);
        File.WriteAllText(fixture.MarkdownPath, "IReadOnlyState<T>");
        var partial = await CliIndexer.BuildForPublicationAsync(fixture.Options, complete.Snapshot, deadline.Token);
        Assert.Equal(complete.Snapshot.Graph.Generation + 1, partial.Snapshot.Graph.Generation);
        Assert.Equal(HarnessCoverage.Partial, partial.Snapshot.Graph.Coverage);
        Assert.Contains(partial.Snapshot.Graph.Diagnostics!, diagnostic => diagnostic.Code == "markdown.parser-diagnostic.LMD006");
        Assert.Equal(Assert.Single(complete.Snapshot.Documents).DocumentUuid, Assert.Single(partial.Snapshot.Documents).DocumentUuid);
        Assert.NotEmpty(Assert.Single(partial.Snapshot.Documents).Sections);
        IndexSnapshotValidator.Validate(partial.Snapshot);
        writer.Commit(IndexStage.Create(partial.Snapshot), deadline.Token, partial.VerifySelection);
        Assert.Equal(original, JsonSerializer.Serialize(pinned.Snapshot(), QueryJson.Options));
        using var current = IndexReader.Open(fixture.Options.IndexPath, fixture.Options.WorkspaceUuid);
        Assert.Equal(partial.Snapshot.Graph.Generation, current.Snapshot().Graph.Generation);
        Assert.Contains(current.Snapshot().Files, file => file.Kind == "markdown" && file.RelativePath == "README.md"
            && file.ContentHash == Assert.Single(partial.Snapshot.Documents).ContentHash);
        var query = new QueryService(new PinnedQueryIndex(current));
        Assert.False(query.Execute(new("complete", QueryKind.Status, RequireComplete: true)).Succeeded);

        // Saved readers order search rows by node ID; compare the complete saved representation on both sides.
        var committed = JsonSerializer.Serialize(current.Snapshot(), QueryJson.Options);
        var unchanged = await CliIndexer.BuildForPublicationAsync(fixture.Options, partial.Snapshot, deadline.Token);
        Assert.Equal(Assert.Single(partial.Snapshot.Documents).Sections, Assert.Single(unchanged.Snapshot.Documents).Sections);
        File.WriteAllBytes(fixture.MarkdownPath, [0xff]);
        await Assert.ThrowsAsync<CliIndexException>(() => CliIndexer.BuildForPublicationAsync(fixture.Options, partial.Snapshot, deadline.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CliIndexer.BuildForPublicationAsync(
            fixture.Options, partial.Snapshot, new CancellationToken(true)));
        using var retained = IndexReader.Open(fixture.Options.IndexPath, fixture.Options.WorkspaceUuid);
        Assert.Equal(committed, JsonSerializer.Serialize(retained.Snapshot(), QueryJson.Options));
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
