namespace SharpDeps.Query;

using System.Text.Json;
using System.Text.Json.Serialization;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Index;

public enum QueryKind { Search, Symbol, Callers, Callees, Impact, Context, Status, Browse }
public enum QueryFreshnessPolicy { AllowStale, RequireFresh, Refresh }
public sealed record QueryBudget(int MaxNodes = 60, int MaxEdges = 120, int MaxDepth = 2,
    int MaxMilliseconds = 1000, int MaxChars = 18000, int MaxBytes = 64000);
public sealed record QueryScope(HarnessNodeKind? Kind = null, string? ProjectId = null,
    string? PathPrefix = null, IReadOnlyList<string>? VariantIds = null);
public sealed record QueryRequest(string RequestId, QueryKind Kind, string? Term = null,
    string? NodeId = null, IReadOnlyList<string>? Ids = null, QueryScope? Scope = null,
    IReadOnlyList<string>? EdgeKinds = null, bool IncludeCandidates = false, bool IncludeDocuments = true,
    bool Dependents = true, QueryFreshnessPolicy Freshness = QueryFreshnessPolicy.AllowStale,
    bool RequireComplete = false, int PageSize = 100, string? Cursor = null, QueryBudget? Budget = null);
public sealed record QueryIssue(string Code, string Message, string? Path = null);
public sealed record QuerySnapshot(string WorkspaceId, string Id, long Generation,
    IReadOnlyList<string> VariantIds, int VariantCount, string Freshness, DateTimeOffset? CheckedAt,
    HarnessCoverage Coverage, IReadOnlyList<string> DirtyPaths, int DirtyPathCount,
    IReadOnlyList<string> Unverified, int UnverifiedCount)
{
    public int GraphSchemaVersion => HarnessGraphContract.SchemaVersion;
    public string IdentityVersion => HarnessGraphContract.IdentityVersion;
    public int IndexSchemaVersion => 1; // The current storage contract accepted by PinnedQueryIndex.
    public string CoordinatorState => "not-connected";
}
public sealed record QuerySnippet(string SourceId, string ContentHash, HarnessRawSpan Span,
    string Text, bool OmittedBefore, bool OmittedAfter);
public sealed record QueryItem(string Id, string Kind, string Label, string Reason,
    HarnessCertainty Certainty, string? Path, HarnessLocation? Evidence,
    IReadOnlyList<string> ViaEdgeIds, HarnessNode? Node = null, HarnessEdge? Edge = null,
    HarnessSymbolOccurrence? Occurrence = null, QuerySnippet? Snippet = null, double? Rank = null,
    string? MatchMethod = null);
public sealed record QueryUsage(QueryBudget Limits, int UsedNodes, int UsedEdges,
    int UsedChars, int UsedBytes, int EstimatedTokens, string TokenCountKind = "estimate");
public sealed record QueryEnvelope(string ApiVersion, string RequestId, QuerySnapshot Snapshot,
    IReadOnlyList<QueryItem> Items, IReadOnlyList<QueryItem> Candidates,
    IReadOnlyList<QueryItem> Unresolved, QueryUsage Budget, bool Truncated,
    IReadOnlyList<string> TruncationReasons, string? NextCursor,
    IReadOnlyList<QueryIssue> Diagnostics, IReadOnlyList<QueryIssue> Errors);
/// <summary>Json is the final budgeted wire payload, including the envelope and diagnostics.</summary>
public sealed record QueryReply(QueryEnvelope Envelope, string Json)
{
    public bool Succeeded => Envelope.Errors.Count == 0;
}

/// <summary>Each instance supplies one committed, immutable snapshot; search must use that same pin.</summary>
public interface IQueryIndex
{
    IndexSnapshot Snapshot { get; }
    IReadOnlyList<IndexSearchHit> Search(string literal, int limit);
}
/// <summary>The caller owns and disposes the underlying IndexReader after the query session.</summary>
public sealed class PinnedQueryIndex : IQueryIndex
{
    private readonly IndexReader reader;
    public PinnedQueryIndex(IndexReader reader) { this.reader = reader; Snapshot = reader.Snapshot(); }
    public IndexSnapshot Snapshot { get; }
    public IReadOnlyList<IndexSearchHit> Search(string literal, int limit) => reader.Search(literal, limit);
}
public static class QueryJson
{
    public static JsonSerializerOptions Options { get; } = MakeOptions();
    private static JsonSerializerOptions MakeOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
