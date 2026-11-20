namespace SharpDeps.Query;

using SharpDeps.Analysis.Contracts.Harness;

public enum SymbolQueryOperation { FindSymbols, ReadSymbols, Relations }

/// <summary>Small, same-scope operations. No implicit update, pagination or recursive expansion.</summary>
public sealed record SymbolQueryRequest(SymbolQueryOperation Operation, string? Query = null,
    IReadOnlyList<string>? Selectors = null, string? ProjectId = null, string? VariantId = null,
    string? PathPrefix = null, QueryFreshnessPolicy Freshness = QueryFreshnessPolicy.AllowStale, QueryBudget? Budget = null)
{
    public int PrepareMaxMilliseconds { get; init; } = 30000;
    public int MaxResults { get; init; } = 16;
    public string Direction { get; init; } = "outgoing";
    public IReadOnlyList<string>? EdgeKinds { get; init; }
}

public sealed record SymbolQuerySnapshot(string WorkspaceId, string Id, long Generation,
    HarnessCoverage Coverage, string Freshness);
public sealed record SymbolDeclaration(string SourceId, string Path, string ContentHash, HarnessRawSpan Span,
    string Storage, string Outcome, string? Text = null, int? FirstLine = null, bool CompleteSavedRange = false);
public sealed record SymbolRelation(string Id, string Kind, HarnessCertainty Certainty,
    string SourceLogicalId, string TargetLogicalId, string? SourceSelector, string? TargetSelector,
    string? VariantId, bool OutsideScope, HarnessLocation? Evidence,
    string SourceName, string TargetName, string? SourceProjectId, string? TargetProjectId,
    string? SourceVariantId, string? TargetVariantId);
public sealed record SymbolQueryItem(string Selector, string? LogicalId, string? ProjectId, string? VariantId,
    string? Name, string? Kind, string Outcome, IReadOnlyList<SymbolDeclaration> Declarations,
    IReadOnlyList<SymbolRelation> Relations, IReadOnlyList<string> Omissions);
public sealed record SymbolQueryEnvelope(string ApiVersion, SymbolQueryOperation Operation,
    SymbolQuerySnapshot? Snapshot, IReadOnlyList<SymbolQueryItem> Items, IReadOnlyList<QueryIssue> Errors,
    IReadOnlyList<string> Omissions, bool Truncated, int PrepareMaxMilliseconds, QueryBudget Limits);
/// <summary>Json is the canonical, bounded payload. Hosts must not duplicate it as structured content.</summary>
public sealed record SymbolQueryReply(SymbolQueryEnvelope Envelope, string Json)
{
    public bool Succeeded => Envelope.Errors.Count == 0;
}
