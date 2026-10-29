namespace SharpDeps.Query;

using SharpDeps.Analysis.Contracts.Harness;

public enum ExploreFormat
{
    /// <summary>Includes saved identities, hashes, spans, and provenance.</summary>
    Audit,
    /// <summary>Compact evidence with exact selectors for follow-up queries.</summary>
    Agent
}

public enum ExploreScopePolicy
{
    /// <summary>Leaves ambiguous compilation scopes unresolved.</summary>
    RequireUnique,
    /// <summary>Uses explicit identities first, then the sole saved Debug variant within one project.</summary>
    PreferDebug
}

public sealed record ExploreRequest(string Query, string? ProjectId = null, string? VariantId = null,
    string? PathPrefix = null, QueryFreshnessPolicy Freshness = QueryFreshnessPolicy.AllowStale,
    QueryBudget? Budget = null, ExploreFormat Format = ExploreFormat.Audit,
    ExploreScopePolicy ScopePolicy = ExploreScopePolicy.RequireUnique, string? PathTo = null);

public sealed record ExploreUnit(string SourceId, string Path, string ContentHash, HarnessRawSpan Span,
    string Text, int FirstLine, IReadOnlyList<string> SymbolIds,
    string Role = "saved-declaration", string BodyAvailability = "unknown", bool CompleteSavedRange = true);

public sealed record ExploreReply(string Text, IReadOnlyList<QueryIssue> Errors,
    IReadOnlyList<ExploreUnit> Units, IReadOnlyList<string> OmissionReasons, int WireChars, int WireBytes)
{
    public bool Succeeded => Errors.Count == 0;
}
