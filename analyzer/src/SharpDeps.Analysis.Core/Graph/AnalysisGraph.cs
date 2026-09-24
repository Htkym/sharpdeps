// Aggregated analysis graph (SD-012).
//
// Evidence is collected at the finest granularity (types). This graph turns it into
// relations at a requested granularity, keeping the distinction the plan requires:
//   - relation count: distinct source → target pairs (per basis)
//   - occurrence count: evidence records behind a relation
//
// A relation whose two ends live inside the same parent is not an edge of that parent
// graph (no self-edges from aggregation). Cycles are computed per basis so different
// bases are never mixed.

namespace SharpDeps.Analysis.Core.Graph;

using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Identity;

/// <summary>Granularity of an aggregated graph.</summary>
public enum GraphGranularity
{
    Type,
    Namespace,
    Project
}

/// <summary>
/// One piece of evidence at the finest granularity, with the classification the
/// aggregation needs. Produced by the declaration and operation collectors.
/// </summary>
public sealed record GraphEvidence(
    string Basis,
    string SourceEntityId,
    string TargetEntityId,
    string Kind,
    string? SourceMemberId,
    string DocumentId,
    bool PublicSurface,
    bool Generated,
    string Confidence,
    string? SourceNamespaceId,
    string? TargetNamespaceId,
    string? SourceProjectId,
    string? TargetProjectId,
    bool TargetIsExternal)
{
    /// <summary>A type that depends on itself must not be reported as a cycle.</summary>
    public bool IsSelfReference => SourceEntityId == TargetEntityId;

    public bool SameNamespace
        => SourceNamespaceId is not null && SourceNamespaceId == TargetNamespaceId;

    public bool SameProject
        => SourceProjectId is not null && SourceProjectId == TargetProjectId;
}

/// <summary>An aggregated relation: one source → target pair for one basis.</summary>
public sealed record GraphRelation(
    string Basis,
    string SourceEntityId,
    string TargetEntityId,
    IReadOnlyList<string> Kinds,
    int EvidenceCount,
    int DistinctSourceMemberCount,
    int DistinctSourceDocumentCount,
    int GeneratedEvidenceCount,
    int PublicSurfaceEvidenceCount,
    string Confidence)
{
    public string Key => $"{Basis}\u001f{SourceEntityId}\u001f{TargetEntityId}";
}

public sealed record AnalysisGraph(
    GraphGranularity Granularity,
    IReadOnlyList<GraphRelation> Relations,
    IReadOnlyList<GraphEvidence> Evidence)
{
    public int NodeCount => Relations
        .SelectMany(relation => new[] { relation.SourceEntityId, relation.TargetEntityId })
        .Distinct(StringComparer.Ordinal)
        .Count();

    /// <summary>Distinct source → target pairs, the plan's "relation count".</summary>
    public int RelationCount => Relations.Count;

    /// <summary>Normalized evidence records, the plan's "occurrence count".</summary>
    public int OccurrenceCount => Evidence.Count;
}

public static class AnalysisGraphBuilder
{
    /// <summary>
    /// Aggregates evidence to <paramref name="granularity"/>. <paramref name="parentOf"/>
    /// returns the parent entity id of a type at that granularity (namespace or project
    /// variant); relations inside one parent are dropped instead of becoming self-edges.
    /// </summary>
    public static AnalysisGraph Build(
        IReadOnlyList<GraphEvidence> evidence,
        GraphGranularity granularity,
        Func<string, string?>? parentOf = null)
    {
        var relations = new Dictionary<string, RelationAccumulator>(StringComparer.Ordinal);

        foreach (var record in evidence)
        {
            var source = Map(record.SourceEntityId, granularity, parentOf);
            var target = Map(record.TargetEntityId, granularity, parentOf);
            if (source is null || target is null)
            {
                continue;
            }

            // Type-level self references stay in the model (they are real dependencies
            // that cycle detection excludes); an aggregated self edge would be a
            // meaningless self-loop in the parent graph, so it is not an edge there.
            if (source == target && granularity != GraphGranularity.Type)
            {
                continue;
            }

            var key = $"{record.Basis}\u001f{source}\u001f{target}";
            if (!relations.TryGetValue(key, out var accumulator))
            {
                accumulator = new RelationAccumulator(record.Basis, source, target);
                relations[key] = accumulator;
            }

            accumulator.Add(record);
        }

        return new AnalysisGraph(
            granularity,
            relations.Values
                .OrderBy(relation => relation.Basis, StringComparer.Ordinal)
                .ThenBy(relation => relation.SourceEntityId, StringComparer.Ordinal)
                .ThenBy(relation => relation.TargetEntityId, StringComparer.Ordinal)
                .Select(relation => relation.ToRelation())
                .ToArray(),
            [.. evidence
                .Where(record => record.SourceEntityId != record.TargetEntityId || granularity == GraphGranularity.Type)
                .OrderBy(record => record.Basis, StringComparer.Ordinal)
                .ThenBy(record => record.SourceEntityId, StringComparer.Ordinal)
                .ThenBy(record => record.TargetEntityId, StringComparer.Ordinal)
                .ThenBy(record => record.DocumentId, StringComparer.Ordinal)
                .ThenBy(record => record.Kind, StringComparer.Ordinal)]);
    }

    private static string? Map(string entityId, GraphGranularity granularity, Func<string, string?>? parentOf)
        => granularity switch
        {
            GraphGranularity.Type => entityId,
            _ => parentOf?.Invoke(entityId)
        };

    private sealed class RelationAccumulator(string basis, string source, string target)
    {
        private readonly HashSet<string> _kinds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sourceMembers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _documents = new(StringComparer.Ordinal);
        private int _evidenceCount;
        private int _generatedCount;
        private int _publicSurfaceCount;
        private bool _inferred;

        public string Basis { get; } = basis;

        public string SourceEntityId { get; } = source;

        public string TargetEntityId { get; } = target;

        public void Add(GraphEvidence record)
        {
            _evidenceCount++;
            _kinds.Add(record.Kind);
            if (record.SourceMemberId is not null)
            {
                _sourceMembers.Add(record.SourceMemberId);
            }

            _documents.Add(record.DocumentId);
            if (record.Generated)
            {
                _generatedCount++;
            }

            if (record.PublicSurface)
            {
                _publicSurfaceCount++;
            }

            if (string.Equals(record.Confidence, "inferred", StringComparison.Ordinal))
            {
                _inferred = true;
            }
        }

        public GraphRelation ToRelation() => new(
            Basis,
            SourceEntityId,
            TargetEntityId,
            [.. _kinds.OrderBy(kind => kind, StringComparer.Ordinal)],
            _evidenceCount,
            _sourceMembers.Count,
            Math.Max(1, _documents.Count),
            _generatedCount,
            _publicSurfaceCount,
            _inferred ? "inferred" : "resolved");
    }
}
