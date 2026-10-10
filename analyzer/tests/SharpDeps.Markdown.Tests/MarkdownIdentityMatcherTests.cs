namespace SharpDeps.Markdown.Tests;

using Syntamark;
using SharpDeps.Analysis.Markdown;
using Xunit;

public sealed class MarkdownIdentityMatcherTests
{
    private static readonly Guid Workspace = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid Document = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
    private static readonly Guid Other = Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff");
    private static readonly string Hash = new('a', 64);
    private static MarkdownDocument Facts(string raw) => new LithoSharpMarkdownAdapter().Analyze(
        new(raw, Workspace, Document, "scope", "readme.md", null, "snapshot", 1), true).Facts;

    [Fact]
    public void RetainsProvenDocumentMovesButNeverInfersCopiesOrDuplicateHashes()
    {
        MarkdownDocumentIdentity[] previous = [new(Document, "a.md", null, Hash)];
        Assert.Equal(Document, Assert.Single(MarkdownIdentityMatcher.MatchDocuments(previous,
            [new("moved.md", null, Hash)])).DocumentUuid);
        var copy = MarkdownIdentityMatcher.MatchDocuments(previous, [new("a.md", null, Hash), new("copy.md", null, Hash)]);
        Assert.Equal(Document, copy[0].DocumentUuid);
        Assert.Null(copy[1].DocumentUuid);
        previous = [new(Document, "a.md", null, Hash), new(Other, "b.md", null, Hash)];
        var ambiguous = MarkdownIdentityMatcher.MatchDocuments(previous, [new("a.md", null, Hash), new("c.md", null, Hash)]);
        Assert.Equal(Document, ambiguous[0].DocumentUuid);
        Assert.Null(ambiguous[1].DocumentUuid);
        Assert.Equal("ambiguous-move", ambiguous[1].Reason);
        Assert.Equal(2, ambiguous[1].Candidates.Count);
        var verified = MarkdownIdentityMatcher.MatchDocuments(previous, [new("a.md", null, Hash), new("c.md", null, Hash, "b.md")]);
        Assert.Equal(Other, verified[1].DocumentUuid);
        var explicitMove = MarkdownIdentityMatcher.MatchDocuments([new(Document, "a.md", "guide", Hash)],
            [new("renamed.md", "guide", new string('b', 64))]);
        Assert.Equal(Document, Assert.Single(explicitMove).DocumentUuid);
        Assert.All(MarkdownIdentityMatcher.MatchDocuments([new(Document, "a.md", "guide", Hash)],
            [new("a.md", "guide", Hash), new("b.md", "guide", Hash)]), match =>
        { Assert.Null(match.DocumentUuid); Assert.Equal("duplicate-explicit-id", match.Reason); });
    }

    [Fact]
    public void RetainsSectionsAcrossInsertionBodyEditAndHeadingRenameWithoutUsingLocalOrdinal()
    {
        var before = Match("# One\nbody one\n# Two\nbody two\n");
        var inserted = Match("# Inserted\nnew\n# One\nbody one\n# Two\nbody two\n", before.Snapshot);
        Assert.Equal(Token(before, "One"), Token(inserted, "One"));
        Assert.Equal(Token(before, "Two"), Token(inserted, "Two"));
        var bodyEdit = Match("# One\nchanged body\n# Two\nbody two\n", before.Snapshot);
        Assert.Equal(Token(before, "One"), Token(bodyEdit, "One"));
        var headingEdit = Match("# Renamed\nbody one\n# Two\nbody two\n", before.Snapshot);
        Assert.Equal(Token(before, "One"), Token(headingEdit, "Renamed"));
        var bothEdit = Match("# Renamed\nchanged body\n# Two\nbody two\n", before.Snapshot);
        Assert.NotEqual(Token(before, "One"), Token(bothEdit, "Renamed"));
        var raw = "# Inserted\nnew\n# One\nbody one\n# Two\nbody two\n";
        var bound = new LithoSharpMarkdownAdapter().Analyze(new(raw, Workspace, Document, "scope", "readme.md", null,
            "next", 2, inserted.SectionTokens, inserted.TextHash), true);
        Assert.All(bound.Sections, s => Assert.True(s.Durable));
    }

    [Fact]
    public void KeepsDuplicateSectionsOnUnchangedRefreshAndMakesEditedDuplicatesAmbiguous()
    {
        const string raw = "# Repeated\nsame\n# Repeated\nsame\n";
        var before = Match(raw);
        var unchanged = Match(raw, before.Snapshot);
        Assert.Equal(before.Snapshot.Sections.Select(s => s.Token), unchanged.Snapshot.Sections.Select(s => s.Token));
        Assert.All(unchanged.Matches, m => Assert.True(m.Retained));
        var changed = Match("# Repeated\nchanged\n# Repeated\nsame\n", before.Snapshot);
        Assert.All(changed.Snapshot.Sections.Where(s => s.HeadingKey == "1:Repeated"), s =>
        {
            var match = Assert.Single(changed.Matches, m => m.LocalKey == s.LocalKey);
            Assert.False(match.Retained);
            Assert.Equal("ambiguous-section", match.Reason);
            Assert.Equal(2, match.Candidates.Count);
        });
    }

    [Fact]
    public void DoesNotTurnResidualDuplicateBodiesOrHeadingsIntoProvenMatches()
    {
        var before = Match("# First\nsame\n# Second\nsame\n");
        var changed = Match("# First\nchanged\n# Renamed\nsame\n", before.Snapshot);
        Assert.Equal(Token(before, "First"), Token(changed, "First"));
        Assert.NotEqual(Token(before, "Second"), Token(changed, "Renamed"));
        before = Match("# Repeated\nbody one\n# Repeated\nbody two\n");
        changed = Match("# Repeated\nbody one\n# Repeated\nchanged\n", before.Snapshot);
        Assert.Equal(before.Snapshot.Sections.First(s => s.HeadingKey == "1:Repeated").Token,
            changed.Snapshot.Sections.First(s => s.HeadingKey == "1:Repeated").Token);
        Assert.False(changed.Matches.Single(m => m.LocalKey == changed.Snapshot.Sections
            .Last(s => s.HeadingKey == "1:Repeated").LocalKey).Retained);
    }

    [Fact]
    public void RejectsWrongSourceSnapshotAndTokensThatWouldCollideAfterIdentityNormalization()
    {
        const string raw = "# One\nbody\n";
        var before = Match(raw);
        Assert.Throws<ArgumentException>(() => MarkdownIdentityMatcher.MatchSections(Workspace, Document,
            raw + "edit", Facts(raw), before.Snapshot, () => "fresh"));
        Assert.Throws<ArgumentException>(() => MarkdownIdentityMatcher.MatchSections(Workspace, Other,
            raw, Facts(raw), before.Snapshot, () => "fresh"));
        Assert.Throws<ArgumentException>(() => Match(raw, before.Snapshot with { Sections = [] }));
        Assert.Throws<ArgumentException>(() => MarkdownIdentityMatcher.MatchSections(Workspace, Document,
            raw, Facts(raw), null, () => " token "));
        var malformed = before.Snapshot with { Sections = before.Snapshot.Sections.Select(s => s with { Token = " token " }).ToArray() };
        Assert.Throws<ArgumentException>(() => Match(raw, malformed));
        Assert.Throws<OperationCanceledException>(() => MarkdownIdentityMatcher.MatchSections(Workspace, Document,
            raw, Facts(raw), null, () => throw new InvalidOperationException("must not allocate"), new CancellationToken(true)));
    }

    private static MarkdownSectionMatchResult Match(string raw, MarkdownSectionIdentitySnapshot? previous = null)
    {
        return MarkdownIdentityMatcher.MatchSections(Workspace, Document, raw, Facts(raw), previous,
            () => "fixture:" + Guid.NewGuid().ToString("N"));
    }
    private static string Token(MarkdownSectionMatchResult result, string heading)
        => result.Snapshot.Sections.Single(s => s.HeadingKey == "1:" + heading).Token;
}
