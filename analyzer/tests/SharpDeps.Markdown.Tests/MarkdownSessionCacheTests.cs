namespace SharpDeps.Markdown.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Markdown;
using Syntamark;
using Xunit;

public sealed class MarkdownSessionCacheTests
{
    private const string Original = "Alpha 😀\r\n\r\nBeta Example\r\n\r\nGamma suffix\r\n";
    private static MarkdownGraphRequest Request(string raw = Original)
        => new(raw, Guid.Parse("11111111-2222-4333-8444-555555555555"),
            Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"), "scope", "guide.md", Hash(raw), "snapshot", 1);
    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    [Fact]
    public void CandidateSelectionBindsBothPackagesAndTheLoadedRuntimeToTheDevelopmentPin()
    {
        using var pinFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "markdown-runtime-pin.json")));
        var pin = pinFile.RootElement;
        Assert.True(pin.GetProperty("developmentOnly").GetBoolean());
        Assert.Equal(MarkdownRuntimePin.ComponentVersion, pin.GetProperty("componentVersion").GetString());
        Assert.Equal("c60683ef270e9a1cb451610425621363d0072dcf", pin.GetProperty("sourceCommit").GetString());
        Assert.Equal(MarkdownRuntimePin.CanonicalSourceHash, pin.GetProperty("canonicalSourceHash").GetString());
        Assert.Equal(MarkdownRuntimePin.ParserVersion, pin.GetProperty("parserVersion").GetString());
        var artifacts = pin.GetProperty("artifacts");
        Assert.Equal(MarkdownRuntimePin.ComponentVersion, artifacts.GetProperty("runtime").GetProperty("componentVersion").GetString());
        Assert.Equal(MarkdownRuntimePin.ComponentVersion, artifacts.GetProperty("source").GetProperty("componentVersion").GetString());
        Assert.Equal(MarkdownRuntimePin.RuntimeAssemblySha256, artifacts.GetProperty("runtime").GetProperty("assemblySha256").GetString());
        MarkdownRuntimePin.RequireLoaded(MarkdownRuntimePin.ParserVersion);
    }

    [Fact]
    public void SupportedEditsUndoAndUnsupportedTransitionsMatchColdFactsSyntaxIdentityAndProjection()
    {
        var adapter = new LithoSharpMarkdownAdapter();
        var cache = new MarkdownPreparedCache();
        MarkdownSectionIdentitySnapshot? prior = null;
        var generation = 0;
        foreach (var (raw, delta) in new[]
        {
            (Original, false),
            (Original.Replace("Beta", "Beta longer"), true),
            (Original, true),
            (Original.Replace("😀", "🎉"), true),
            (Original.Replace("Beta", "Beta\r\nsecond"), false),
            (Original, false),
            (Original.Replace("suffix", "short"), true),
            ("\uFEFF# Heading\r\n" + Original, false),
            ("IReadOnlyState<T>", false),
            (Original, false),
            (Original.Replace("Beta", "Replacement"), true)
        })
        {
            var request = Request(raw) with { SnapshotId = "snapshot-" + ++generation, Generation = generation };
            using var candidate = cache.BeginUpdate();
            var prepared = candidate.Prepare(adapter, request, true, out var hit);
            Assert.False(hit);
            Assert.Equal(delta ? 1 : 0, candidate.SessionUpdates);
            Assert.Equal(delta ? 0 : 1, candidate.FullPreparations);
            var cold = adapter.Prepare(request, true);
            Assert.Equal(JsonSerializer.Serialize(cold.Facts), JsonSerializer.Serialize(prepared.Facts));
            Assert.Equal(JsonSerializer.Serialize(cold.SyntaxRoot), JsonSerializer.Serialize(prepared.SyntaxRoot));
            var nextToken = 0;
            var sections = MarkdownIdentityMatcher.MatchSections(request.WorkspaceUuid, request.DocumentUuid, raw,
                prepared.Facts, prior, () => "token-" + generation + "-" + ++nextToken);
            nextToken = 0;
            var coldSections = MarkdownIdentityMatcher.MatchSections(request.WorkspaceUuid, request.DocumentUuid, raw,
                cold.Facts, prior, () => "token-" + generation + "-" + ++nextToken);
            Assert.Equal(JsonSerializer.Serialize(coldSections), JsonSerializer.Serialize(sections));
            var bound = request with { SectionTokens = sections.SectionTokens, SectionIdentityTextHash = sections.TextHash };
            Assert.Equal(JsonSerializer.Serialize(adapter.Project(bound, cold, true)),
                JsonSerializer.Serialize(adapter.Project(bound, prepared, true)));
            Assert.Equal(Hash(raw), prepared.Facts.TextHash);
            foreach (var region in prepared.Facts.TextRegions)
                if (region.Content.Text is { Length: > 0 } decoded)
                {
                    var mapped = region.Content.Map(new RawSpan(0, decoded.Length));
                    Assert.All(mapped.RawFragments, span =>
                    {
                        Assert.InRange(span.End, 0, raw.Length);
                        Assert.Equal(cold.Facts.GetLinePosition(span.Start), prepared.Facts.GetLinePosition(span.Start));
                        Assert.Equal(cold.Facts.GetLinePosition(span.End), prepared.Facts.GetLinePosition(span.End));
                    });
                }
            prior = sections.Snapshot;
            Assert.True(candidate.Adopt());
            using var unchanged = cache.BeginUpdate();
            Assert.Same(prepared, unchanged.Prepare(adapter, request with { Generation = generation + 1 }, true, out hit));
            Assert.True(hit);
            Assert.Equal(0, unchanged.SessionUpdates);
            Assert.Equal(0, unchanged.FullPreparations);
        }
    }

    [Fact]
    public void SessionsMatchAllLineageOptionsAndPermitOpaqueVersionChanges()
    {
        var adapter = new LithoSharpMarkdownAdapter();
        var cache = new MarkdownPreparedCache();
        var request = Request();
        using (var initial = cache.BeginUpdate())
        {
            initial.Prepare(adapter, request, true, out _);
            Assert.True(initial.Adopt());
        }
        var changes = new[]
        {
            request with { WorkspaceUuid = Guid.NewGuid() }, request with { ScopeId = "other" },
            request with { SourceId = "renamed.md" },
            request with { Options = new MarkdownParseOptions(maxInputUtf16: 1048577) },
            request with { Options = new MarkdownParseOptions(maxOutputItems: 131073) },
            request with { Options = new MarkdownParseOptions(maxNestingDepth: 201) },
            request with { Options = new MarkdownParseOptions(maxScanUnits: 16777217) },
            request with { Options = new MarkdownParseOptions(MarkdownInputKind.Body) },
            request with { Options = new MarkdownParseOptions(MarkdownFrontMatterRecovery.TreatAsBody) }
        };
        foreach (var change in changes)
        {
            using var candidate = cache.BeginUpdate();
            var prepared = candidate.Prepare(adapter, change, true, out var hit);
            Assert.False(hit);
            Assert.Equal(0, candidate.SessionUpdates);
            Assert.Equal(1, candidate.FullPreparations);
            Assert.Equal(JsonSerializer.Serialize(adapter.Prepare(change, true).Facts), JsonSerializer.Serialize(prepared.Facts));
        }
        using var version = cache.BeginUpdate();
        var rebound = version.Prepare(adapter, request with { SourceVersion = "opaque-new-version" }, true, out var versionHit);
        Assert.False(versionHit);
        Assert.Equal(1, version.SessionUpdates);
        Assert.Equal("opaque-new-version", rebound.Facts.SourceVersion);
        using var sameVersion = cache.BeginUpdate();
        var rawChanged = request with { RawText = Original.Replace("Beta", "Changed") };
        var changed = sameVersion.Prepare(adapter, rawChanged, true, out var rawHit);
        Assert.False(rawHit);
        Assert.Equal(1, sameVersion.SessionUpdates);
        Assert.Equal(request.SourceVersion, changed.Facts.SourceVersion);
        Assert.Equal(Hash(rawChanged.RawText), changed.Facts.TextHash);
        Assert.Throws<InvalidOperationException>(() => cache.BeginUpdate().Prepare(adapter,
            request with { ExpectedParserVersion = "wrong" }, true, out _));
        Assert.Throws<UnauthorizedAccessException>(() => cache.BeginUpdate().Prepare(adapter, request, false, out _));
    }

    [Fact]
    public void StaleHashVersionAndScopeAreRejectedBeforeApplyingTheSession()
    {
        var adapter = new LithoSharpMarkdownAdapter();
        var request = Request();
        var options = new MarkdownParseOptions();
        var session = MarkdownParser.CreateSession(request.RawText, request.ScopeId, request.SourceId, request.SourceVersion, options);
        var changed = Request(Original.Replace("Beta", "Updated"));
        foreach (var old in new[] { request with { SourceVersion = "stale-version" },
            Request(Original.Replace("Gamma", "Other")) })
            Assert.Throws<InvalidOperationException>(() => adapter.PrepareSession(changed, options,
                adapter.Prepare(old, true), session, out _, default));
        var wrongScope = MarkdownParser.CreateSession(request.RawText, "other", request.SourceId, request.SourceVersion, options);
        Assert.Throws<InvalidOperationException>(() => adapter.PrepareSession(changed, options,
            adapter.Prepare(request, true), wrongScope, out _, default));
        Assert.Equal(request.SourceVersion, session.Document.SourceVersion);
        Assert.Equal(Hash(Original), session.Document.TextHash);
    }

    [Fact]
    public void CanceledAbandonedAndDisposedCandidatesCannotReplaceCommittedSessions()
    {
        var adapter = new LithoSharpMarkdownAdapter();
        var cache = new MarkdownPreparedCache();
        var request = Request();
        MarkdownPreparedInput committed;
        using (var initial = cache.BeginUpdate())
        {
            committed = initial.Prepare(adapter, request, true, out _);
            Assert.True(initial.Adopt());
        }
        using var abandoned = cache.BeginUpdate();
        var replacement = Request(Original.Replace("Beta", "New paragraph"));
        abandoned.Prepare(adapter, replacement, true, out _);
        Assert.Equal(1, abandoned.SessionUpdates);
        using var canceled = cache.BeginUpdate();
        Assert.Throws<OperationCanceledException>(() => canceled.Prepare(adapter, replacement, true, out _, new(true)));
        using var current = cache.BeginUpdate();
        Assert.Same(committed, current.Prepare(adapter, request, true, out var hit));
        Assert.True(hit);
        Assert.True(current.Adopt());
        Assert.False(abandoned.Adopt());
        abandoned.Dispose();
        Assert.Throws<InvalidOperationException>(() => abandoned.Prepare(adapter, request, true, out _));
        Assert.False(abandoned.Adopt());
        current.Dispose();
        using var retained = cache.BeginUpdate();
        Assert.Same(committed, retained.Prepare(adapter, request, true, out hit));
        Assert.True(hit);
        var recovered = retained.Prepare(adapter, replacement, true, out hit);
        Assert.False(hit);
        Assert.Equal(1, retained.SessionUpdates);
        Assert.Equal(JsonSerializer.Serialize(adapter.Prepare(replacement, true).Facts), JsonSerializer.Serialize(recovered.Facts));
    }

    [Fact]
    public void SessionRetentionIsChargedAndEvictionOrRestartUsesTheColdPath()
    {
        var adapter = new LithoSharpMarkdownAdapter();
        var request = Request();
        var cache = new MarkdownPreparedCache(maximumSourceUtf16: Original.Length * 3);
        using (var initial = cache.BeginUpdate())
        {
            initial.Prepare(adapter, request, true, out _);
            Assert.True(initial.Adopt());
        }
        using (var other = cache.BeginUpdate())
        {
            other.Prepare(adapter, request with { SourceId = "other.md" }, true, out _);
            Assert.True(other.Adopt());
        }
        using var evicted = cache.BeginUpdate();
        var rebuilt = evicted.Prepare(adapter, Request(Original.Replace("Beta", "Delta")), true, out var hit);
        Assert.False(hit);
        Assert.Equal(0, evicted.SessionUpdates);
        Assert.Equal(1, evicted.FullPreparations);
        Assert.Equal(JsonSerializer.Serialize(adapter.Prepare(Request(Original.Replace("Beta", "Delta")), true).Facts),
            JsonSerializer.Serialize(rebuilt.Facts));
        cache.Clear();
        Assert.False(evicted.Adopt());
        using var restart = cache.BeginUpdate();
        restart.Prepare(adapter, request, true, out hit);
        Assert.False(hit);
        Assert.Equal(0, restart.SessionUpdates);
        Assert.Equal(1, restart.FullPreparations);
        using var bounded = new MarkdownPreparedCache(maximumSourceUtf16: Original.Length).BeginUpdate();
        bounded.Prepare(adapter, request, true, out _);
        Assert.Equal(1, bounded.FullPreparations);
        Assert.Equal(0, bounded.SessionUpdates);
    }

    [Fact]
    public void AnItemBoundCanRetainPreparedFactsWithoutRetainingTheirSession()
    {
        var adapter = new LithoSharpMarkdownAdapter();
        var request = Request();
        var cold = adapter.Prepare(request, true);
        var facts = cold.Facts;
        var ordinaryItems = facts.Headings.Count + (long)facts.Sections.Count + facts.Links.Count + facts.Fences.Count
            + facts.TextRegions.Count + facts.Diagnostics.Count + cold.Attributes.Count + cold.IgnoredMetadataKeys.Count
            + cold.SyntaxRoot!.DescendantsAndSelf().LongCount();
        var cache = new MarkdownPreparedCache(maximumItems: ordinaryItems);
        using (var first = cache.BeginUpdate())
        {
            first.Prepare(adapter, request, true, out _);
            Assert.True(first.Adopt());
        }
        using var next = cache.BeginUpdate();
        next.Prepare(adapter, request, true, out var hit);
        Assert.True(hit);
        var changed = Request(Original.Replace("Beta", "Changed"));
        var prepared = next.Prepare(adapter, changed, true, out hit);
        Assert.False(hit);
        Assert.Equal(0, next.SessionUpdates);
        Assert.Equal(1, next.FullPreparations);
        Assert.Equal(JsonSerializer.Serialize(adapter.Prepare(changed, true).Facts), JsonSerializer.Serialize(prepared.Facts));
    }

    [Theory]
    [InlineData("\uD800", 1048576, 131072, 16777216)]
    [InlineData(Original, 1, 131072, 16777216)]
    [InlineData("# A\n", 1048576, 5, 16777216)]
    [InlineData(Original, 1048576, 131072, 10)]
    public void FailedAndResourceBoundedFactsStayCanonical(string raw, int input, int output, int scan)
    {
        var adapter = new LithoSharpMarkdownAdapter();
        var request = Request(raw) with { Options = new MarkdownParseOptions(maxInputUtf16: input, maxOutputItems: output, maxScanUnits: scan) };
        using var candidate = new MarkdownPreparedCache().BeginUpdate();
        var prepared = candidate.Prepare(adapter, request, true, out _);
        Assert.Equal(0, candidate.SessionUpdates);
        Assert.Equal(JsonSerializer.Serialize(adapter.Analyze(request, true)),
            JsonSerializer.Serialize(adapter.Project(request, prepared, true)));
        Assert.Equal(JsonSerializer.Serialize(adapter.Prepare(request, true).SyntaxRoot), JsonSerializer.Serialize(prepared.SyntaxRoot));
    }
}
