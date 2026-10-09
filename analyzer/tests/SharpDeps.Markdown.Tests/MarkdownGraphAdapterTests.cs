namespace SharpDeps.Markdown.Tests;

using System.Security.Cryptography;
using System.Text;
using Syntamark;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Markdown;
using Xunit;

public sealed class MarkdownGraphAdapterTests
{
    private static readonly Guid Workspace = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid Document = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
    private static MarkdownGraphRequest Request(string raw) => new(raw, Workspace, Document, "scope", "readme.md", "version-a", "snapshot-a", 1);

    [Fact]
    public void KeepsAtomicOriginalEvidenceAndRequiresTextBoundDurableSectionTokens()
    {
        var request = Request("# A &amp; 😀\r\n\r\n[link](target.md)\r\n");
        var adapter = new LithoSharpMarkdownAdapter();
        var projection = adapter.Analyze(request, true);
        var heading = Assert.Single(projection.Facts.Headings);
        var decoded = heading.Text.Text!;
        var mapping = heading.Text.Map(new RawSpan(decoded.IndexOf('&'), 1));
        var fragment = Assert.Single(mapping.RawFragments);
        Assert.Equal("&amp;", request.RawText.Substring(fragment.Start, fragment.Length));
        Assert.Contains(heading.Text.SourceSegments, s => s.Kind == MarkdownSegmentKind.Atomic);
        var document = Assert.Single(projection.Graph.Nodes, n => n.Kind == HarnessNodeKind.Document);
        Assert.Equal(HarnessIdentity.DocumentId(Workspace, Document), document.Id);
        Assert.Equal(request.RawText.Length, document.Location!.RawSpan!.Value.End);
        Assert.Equal("version-a", document.Location.SourceVersion);
        Assert.All(projection.Sections, s => { Assert.False(s.Durable); Assert.StartsWith("hmdf_", s.NodeId); });
        Assert.Equal(HarnessCoverage.Partial, projection.Graph.Coverage);
        Assert.Contains(projection.Graph.Edges.Where(e => e.Kind == "markdown-link"), e => e.Certainty == HarnessCertainty.Candidate);
        var tokens = projection.Facts.Sections.Select((s, i) => (s.LocalKey, Token: "persisted-fixture:" + i))
            .ToDictionary(s => s.LocalKey, s => s.Token, StringComparer.Ordinal);
        var bound = request with { SectionTokens = tokens, SectionIdentityTextHash = projection.Facts.TextHash, SnapshotId = "snapshot-b", Generation = 2 };
        var durable = adapter.Analyze(bound, true);
        Assert.All(durable.Sections, s => { Assert.True(s.Durable); Assert.StartsWith("hsec_", s.NodeId); });
        Assert.Throws<ArgumentException>(() => adapter.Analyze(bound with { SectionIdentityTextHash = new string('0', 64) }, true));
        if (tokens.Count > 1)
            Assert.Throws<ArgumentException>(() => adapter.Analyze(bound with { SectionTokens = tokens.Keys.ToDictionary(k => k, _ => "same-token") }, true));
        var other = adapter.Analyze(request with { DocumentUuid = Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff"), SourceId = "other.md" }, true);
        Assert.Equal(projection.Facts.TextHash, other.Facts.TextHash);
        Assert.NotEqual(document.Id, Assert.Single(other.Graph.Nodes, n => n.Kind == HarnessNodeKind.Document).Id);
        Assert.All(other.Graph.Nodes.Where(n => n.Location is not null), n => Assert.Equal("other.md", n.Location!.SourceId));
    }

    [Fact]
    public void RetainsFrontMatterWhitelistFenceBytesAndLinePositionsWithoutExecution()
    {
        var raw = "\uFEFF---\r\ntitle: x\r\ntags: [日本語]\r\nsharpdeps:\r\n  id: explicit-doc\r\n  symbols: [App.Service]\r\nsecret: ignored\r\n---\r\n# Foo\r\n~~~csharp\r\n😀\r\n~~~\r\n";
        var projection = new LithoSharpMarkdownAdapter().Analyze(Request(raw), true);
        Assert.Equal(MarkdownFrontMatterState.Parsed, projection.Facts.FrontMatter.State);
        Assert.Equal(new[] { "title", "tags", "sharpdeps.id", "sharpdeps.symbols" }, projection.Attributes.Select(a => a.Key));
        Assert.Contains("secret", projection.IgnoredMetadataKeys);
        var doc = Assert.Single(projection.Graph.Nodes, n => n.Kind == HarnessNodeKind.Document);
        Assert.Equal("x", doc.Name);
        Assert.Equal(HarnessIdentity.DocumentId(Workspace, Document), doc.Id);
        var fence = Assert.Single(projection.Fences);
        var slice = raw.Substring(fence.Source.ContentSpan.Start, fence.Source.ContentSpan.Length);
        Assert.Contains("😀", slice);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(slice))).ToLowerInvariant(), fence.RawContentHash);
        var line = projection.Facts.GetLinePosition(fence.Source.OpeningMarkerSpan.Start);
        Assert.Equal(raw[..fence.Source.OpeningMarkerSpan.Start].Count(c => c == '\n') + 1, line.Line);
        Assert.Equal(1, line.Column);
        Assert.Contains(projection.SearchFields, s => s.Kind == "metadata:tags" && s.Content.Text == "日本語");
        Assert.Same(fence.Source.Content, Assert.Single(projection.SearchFields, s => s.Kind == "code-fence").Content);
    }

    [Fact]
    public void AssignsAdjacentSectionsAndRetainsUnresolvedReferenceEvidence()
    {
        var projection = new LithoSharpMarkdownAdapter().Analyze(Request("# One\n[a](a.md)\n# Two\n[b][missing]\n"), true);
        Assert.Equal(2, projection.Links.Count);
        Assert.Equal("One", projection.Sections.Single(s => s.NodeId == projection.Links[0].OwnerNodeId).Heading!.Text.Text);
        Assert.Equal("Two", projection.Sections.Single(s => s.NodeId == projection.Links[1].OwnerNodeId).Heading!.Text.Text);
        Assert.Equal(MarkdownLinkResolution.UnresolvedReference, projection.Links[1].Source.Resolution);
        Assert.Equal(HarnessCertainty.Unresolved, projection.Graph.Edges.Single(e => e.Id == projection.Links[1].EdgeId).Certainty);
        var duplicateTokens = projection.Facts.Sections.ToDictionary(s => s.LocalKey, _ => "duplicate-token");
        Assert.Throws<ArgumentException>(() => new LithoSharpMarkdownAdapter().Analyze(
            Request("# One\n[a](a.md)\n# Two\n[b][missing]\n") with
            { SectionTokens = duplicateTokens, SectionIdentityTextHash = projection.Facts.TextHash }, true));
    }

    [Fact]
    public void PreservesAbsentUnknownAndPartialWithoutTreatingThemAsCompleteGraphs()
    {
        var adapter = new LithoSharpMarkdownAdapter();
        var readme = adapter.Analyze(Request("# README\ntext\n"), true);
        Assert.Equal(MarkdownFrontMatterState.Absent, readme.Facts.FrontMatter.State);
        Assert.DoesNotContain(readme.Graph.Nodes, n => n.Kind == HarnessNodeKind.FrontMatter);
        Assert.DoesNotContain(readme.Facts.Diagnostics, d => d.Severity == MarkdownDiagnosticSeverity.Error);
        var failed = adapter.Analyze(Request("\uD800"), true);
        Assert.Equal(MarkdownParseStatus.Failed, failed.Facts.Status);
        Assert.Equal(HarnessCoverage.Failed, failed.Graph.Coverage);
        Assert.Null(failed.Facts.TextHash);
        var limited = adapter.Analyze(Request("# First\n# Second\n") with { Options = new MarkdownParseOptions(maxInputUtf16: 1) }, true);
        Assert.NotEqual(HarnessCoverage.CompleteWithinScope, limited.Graph.Coverage);
    }

    [Fact]
    public void RejectsUntrustedIndexingWrongExpectedStampAndCancellation()
    {
        var adapter = new LithoSharpMarkdownAdapter();
        Assert.Throws<UnauthorizedAccessException>(() => adapter.Analyze(Request("# A\n"), false));
        Assert.Throws<InvalidOperationException>(() => adapter.Analyze(Request("# A\n") with { ExpectedParserVersion = "1/" + new string('0', 64) }, true));
        Assert.Throws<OperationCanceledException>(() => adapter.Analyze(Request("# A\n"), true, new CancellationToken(true)));
    }
}
