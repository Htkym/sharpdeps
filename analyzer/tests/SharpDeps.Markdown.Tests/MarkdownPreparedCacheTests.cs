namespace SharpDeps.Markdown.Tests;

using System.Text.Json;
using SharpDeps.Analysis.Markdown;
using Syntamark;
using Xunit;

public sealed class MarkdownPreparedCacheTests
{
    private static MarkdownGraphRequest Request(string text = "# Heading\r\nA 😀 body.\r\n")
        => new(text, Guid.Parse("11111111-2222-4333-8444-555555555555"),
            Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"), "scope", "guide.md", "version",
            "snapshot-1", 1);

    [Fact]
    public void CommittedFactsAreReusedAcrossIdentityAndGenerationWithoutChangingProjection()
    {
        var adapter = new LithoSharpMarkdownAdapter();
        var cache = new MarkdownPreparedCache();
        var request = Request();
        var first = cache.BeginUpdate();
        var prepared = first.Prepare(adapter, request, true, out var hit);
        Assert.False(hit);
        Assert.True(first.Adopt());

        var nextRequest = request with { SnapshotId = "snapshot-2", Generation = 2, DocumentUuid = Guid.NewGuid() };
        var next = cache.BeginUpdate();
        var reused = next.Prepare(adapter, nextRequest, true, out hit);
        Assert.True(hit);
        Assert.Same(prepared, reused);
        Assert.Equal(JsonSerializer.Serialize(adapter.Analyze(nextRequest, true)),
            JsonSerializer.Serialize(adapter.Project(nextRequest, reused, true)));
        Assert.Throws<UnauthorizedAccessException>(() => next.Prepare(adapter, nextRequest, false, out _));
        Assert.Throws<OperationCanceledException>(() => next.Prepare(adapter, nextRequest, true, out _, new(true)));
        Assert.Throws<InvalidOperationException>(() => next.Prepare(adapter,
            nextRequest with { ExpectedParserVersion = "wrong" }, true, out _));
    }

    [Fact]
    public void SourceWorkspaceScopeAndEveryOptionArePartOfTheReuseBoundary()
    {
        var adapter = new LithoSharpMarkdownAdapter();
        var request = Request("---\ntitle: Intro\n---\n# Heading\nBody\n");
        var cache = new MarkdownPreparedCache();
        var first = cache.BeginUpdate();
        first.Prepare(adapter, request, true, out _);
        first.Adopt();
        var changes = new[]
        {
            request with { WorkspaceUuid = Guid.NewGuid() },
            request with { ScopeId = "other" },
            request with { SourceId = "renamed.md" },
            request with { SourceVersion = "other" },
            request with { RawText = request.RawText.Replace("Body", "Edit") },
            request with { Options = new MarkdownParseOptions(maxInputUtf16: 1048577) },
            request with { Options = new MarkdownParseOptions(maxOutputItems: 131073) },
            request with { Options = new MarkdownParseOptions(maxNestingDepth: 201) },
            request with { Options = new MarkdownParseOptions(maxScanUnits: 16777217) },
            request with { Options = new MarkdownParseOptions(MarkdownInputKind.Body) },
            request with { Options = new MarkdownParseOptions(MarkdownFrontMatterRecovery.TreatAsBody) }
        };
        foreach (var change in changes)
        {
            var candidate = cache.BeginUpdate();
            candidate.Prepare(adapter, change, true, out var hit);
            Assert.False(hit);
        }
        Assert.Throws<ArgumentException>(() => cache.BeginUpdate().Prepare(adapter,
            request with { Options = new MarkdownParseOptions(profileId: "commonmark/0.31.2") }, true, out _));
    }

    [Fact]
    public void AbandonedAndSupersededCandidatesCannotEvictTheCommittedFacts()
    {
        var adapter = new LithoSharpMarkdownAdapter();
        var request = Request();
        var cache = new MarkdownPreparedCache(maximumEntries: 1);
        var first = cache.BeginUpdate();
        var original = first.Prepare(adapter, request, true, out _);
        first.Adopt();

        var abandoned = cache.BeginUpdate();
        abandoned.Prepare(adapter, request with { RawText = "# Changed" }, true, out _);
        var current = cache.BeginUpdate();
        Assert.Same(original, current.Prepare(adapter, request, true, out var hit));
        Assert.True(hit);
        Assert.True(current.Adopt());
        Assert.False(abandoned.Adopt());

        var replacement = cache.BeginUpdate();
        replacement.Prepare(adapter, request with { SourceId = "other.md" }, true, out _);
        Assert.True(replacement.Adopt());
        var afterEviction = cache.BeginUpdate();
        var rebuilt = afterEviction.Prepare(adapter, request, true, out hit);
        Assert.False(hit);
        Assert.NotSame(original, rebuilt);
        Assert.Equal(JsonSerializer.Serialize(original.Facts), JsonSerializer.Serialize(rebuilt.Facts));

        cache.Clear();
        Assert.False(afterEviction.Adopt());
        cache.BeginUpdate().Prepare(adapter, request, true, out hit);
        Assert.False(hit);
    }

    [Fact]
    public void ResourceBoundsBypassStorageAndCachedPartialFactsStayPartial()
    {
        var adapter = new LithoSharpMarkdownAdapter();
        var request = Request();
        foreach (var cache in new[] { new MarkdownPreparedCache(maximumSourceUtf16: 1),
            new MarkdownPreparedCache(maximumItems: 1) })
        {
            var first = cache.BeginUpdate();
            first.Prepare(adapter, request, true, out _);
            first.Adopt();
            cache.BeginUpdate().Prepare(adapter, request, true, out var hit);
            Assert.False(hit);
        }

        var partialRequest = Request("---\ntitle: broken\n");
        var partialCache = new MarkdownPreparedCache();
        var candidate = partialCache.BeginUpdate();
        var partial = candidate.Prepare(adapter, partialRequest, true, out _);
        Assert.Equal(MarkdownParseStatus.Partial, partial.Facts.Status);
        Assert.True(candidate.Adopt());
        var reused = partialCache.BeginUpdate().Prepare(adapter, partialRequest, true, out var partialHit);
        Assert.True(partialHit);
        Assert.Same(partial, reused);
        Assert.Equal(MarkdownParseStatus.Partial, reused.Facts.Status);
        Assert.Equal(JsonSerializer.Serialize(adapter.Analyze(partialRequest, true)),
            JsonSerializer.Serialize(adapter.Project(partialRequest, reused, true)));
    }
}
