// Adjacency index, depth-limited exploration, and display projection (SD-012).
//
// The index is derived from the analysis result and is read-only. Exploration is a
// breadth-first walk over forward (dependencies) or reverse (dependents) adjacency
// with a visited set, so cycles cannot loop forever and the depth limit is a hard
// stop. The projection applies a display budget without changing the analysis: totals
// always describe the full result, and each export says what was omitted.

namespace SharpDeps.Analysis.Core.Graph;

public sealed record GraphNodeSummary(
    string EntityId,
    int OutgoingRelationCount,
    int IncomingRelationCount,
    int OutgoingOccurrenceCount,
    int IncomingOccurrenceCount);

public sealed record GraphExplorationResult(
    string OriginEntityId,
    int Depth,
    IReadOnlyList<GraphRelation> Relations,
    IReadOnlyList<GraphNodeSummary> Nodes,
    bool Truncated,
    int CutoffCount);

public sealed record GraphBudget(int MaxNodes, int MaxEdges);

public sealed record GraphProjection(
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<GraphRelation> Relations,
    int TotalNodeCount,
    int TotalEdgeCount,
    bool Truncated,
    IReadOnlyList<string> PrioritizedIds)
{
    public int OmittedNodeCount => Math.Max(0, TotalNodeCount - NodeIds.Count);

    public int OmittedEdgeCount => Math.Max(0, TotalEdgeCount - Relations.Count);
}

public sealed class GraphIndex
{
    private readonly Dictionary<string, List<GraphRelation>> _outgoing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<GraphRelation>> _incoming = new(StringComparer.Ordinal);

    private GraphIndex(AnalysisGraph graph)
    {
        Graph = graph;

        foreach (var relation in graph.Relations)
        {
            Add(_outgoing, relation.SourceEntityId, relation);
            Add(_incoming, relation.TargetEntityId, relation);
            _outgoing.TryAdd(relation.TargetEntityId, []);
            _incoming.TryAdd(relation.SourceEntityId, []);
        }

        Nodes = _outgoing.Keys
            .Union(_incoming.Keys, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(Summarize)
            .ToArray();
    }

    public AnalysisGraph Graph { get; }

    public IReadOnlyList<GraphNodeSummary> Nodes { get; }

    public static GraphIndex Build(AnalysisGraph graph) => new(graph);

    public IReadOnlyList<GraphRelation> Outgoing(string entityId)
        => _outgoing.TryGetValue(entityId, out var relations) ? relations : [];

    public IReadOnlyList<GraphRelation> Incoming(string entityId)
        => _incoming.TryGetValue(entityId, out var relations) ? relations : [];

    /// <summary>
    /// Walks dependencies (or dependents) up to <paramref name="depth"/> levels (1..3).
    /// A visited set makes cycles terminate; the origin is never re-reported.
    /// </summary>
    public GraphExplorationResult Explore(string entityId, int depth, bool dependents = false, int maxNodes = 500)
    {
        var boundedDepth = Math.Clamp(depth, 1, 3);
        var relations = new List<GraphRelation>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { entityId };
        var frontier = new List<string> { entityId };
        var truncated = false;
        var cutoff = 0;

        for (var level = 0; level < boundedDepth && frontier.Count > 0; level++)
        {
            var next = new List<string>();
            foreach (var current in frontier)
            {
                foreach (var relation in dependents ? Incoming(current) : Outgoing(current))
                {
                    var neighbour = dependents ? relation.SourceEntityId : relation.TargetEntityId;
                    var isNew = visited.Add(neighbour);
                    if (isNew && visited.Count > maxNodes)
                    {
                        // Over budget: the relation is not part of the bounded view.
                        truncated = true;
                        cutoff++;
                        visited.Remove(neighbour);
                        continue;
                    }

                    if (isNew)
                    {
                        next.Add(neighbour);
                    }

                    relations.Add(relation);
                }
            }

            frontier = next;
        }

        var nodeIds = relations
            .SelectMany(relation => new[] { relation.SourceEntityId, relation.TargetEntityId })
            .Where(id => !string.Equals(id, entityId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        return new GraphExplorationResult(
            entityId,
            boundedDepth,
            relations
                .DistinctBy(relation => relation.Key, StringComparer.Ordinal)
                .OrderBy(relation => relation.SourceEntityId, StringComparer.Ordinal)
                .ThenBy(relation => relation.TargetEntityId, StringComparer.Ordinal)
                .ToArray(),
            nodeIds.Select(Summarize).ToArray(),
            truncated,
            cutoff);
    }

    /// <summary>
    /// Applies a display budget. Entities in <paramref name="priorityIds"/> (for example
    /// the current selection or a cycle under inspection) are always kept first.
    /// </summary>
    public GraphProjection Project(GraphBudget budget, IReadOnlyList<string>? priorityIds = null)
    {
        var priority = (priorityIds ?? [])
            .Where(id => _outgoing.ContainsKey(id) || _incoming.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var maxNodes = Math.Max(1, budget.MaxNodes);
        var selected = new List<string>();
        var selectedSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in priority)
        {
            if (selected.Count >= maxNodes)
            {
                break;
            }

            if (selectedSet.Add(id))
            {
                selected.Add(id);
            }
        }

        var remaining = Nodes
            .Where(node => !selectedSet.Contains(node.EntityId))
            .OrderByDescending(node => node.OutgoingRelationCount + node.IncomingRelationCount)
            .ThenByDescending(node => node.OutgoingOccurrenceCount + node.IncomingOccurrenceCount)
            .ThenBy(node => node.EntityId, StringComparer.Ordinal)
            .ToArray();

        foreach (var node in remaining)
        {
            if (selected.Count >= maxNodes)
            {
                break;
            }

            if (selectedSet.Add(node.EntityId))
            {
                selected.Add(node.EntityId);
            }
        }

        var relations = Graph.Relations
            .Where(relation => selectedSet.Contains(relation.SourceEntityId) && selectedSet.Contains(relation.TargetEntityId))
            .OrderByDescending(relation => relation.EvidenceCount)
            .ThenBy(relation => relation.Basis, StringComparer.Ordinal)
            .ThenBy(relation => relation.SourceEntityId, StringComparer.Ordinal)
            .ThenBy(relation => relation.TargetEntityId, StringComparer.Ordinal)
            .Take(Math.Max(1, budget.MaxEdges))
            .ToArray();

        return new GraphProjection(
            selected,
            relations,
            TotalNodeCount: Nodes.Count,
            TotalEdgeCount: Graph.Relations.Count,
            Truncated: selected.Count < Nodes.Count || relations.Length < Graph.Relations.Count,
            PrioritizedIds: priority);
    }

    private static void Add(Dictionary<string, List<GraphRelation>> map, string key, GraphRelation relation)
    {
        if (!map.TryGetValue(key, out var relations))
        {
            relations = [];
            map[key] = relations;
        }

        relations.Add(relation);
    }

    private GraphNodeSummary Summarize(string entityId)
    {
        var outgoing = Outgoing(entityId);
        var incoming = Incoming(entityId);
        return new GraphNodeSummary(
            entityId,
            outgoing.Count,
            incoming.Count,
            outgoing.Sum(relation => relation.EvidenceCount),
            incoming.Sum(relation => relation.EvidenceCount));
    }
}
