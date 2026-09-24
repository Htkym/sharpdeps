// Cycle groups and witnesses over an aggregated graph (SD-012).
//
// Strongly connected components are computed per basis: relations from different
// bases (declared, inferred, resolved) are never mixed into one component. A witness
// is a real directed cycle found by walking the edges that exist inside the
// component, so every witness edge is present in the graph. Sorting component
// members and joining them with arrows is never presented as a path.

namespace SharpDeps.Analysis.Core.Graph;

using SharpDeps.Analysis.Contracts;

public sealed record GraphCycle(
    string Basis,
    IReadOnlyList<string> MemberIds,
    IReadOnlyList<GraphRelation> WitnessEdges)
{
    /// <summary>True when the component has a verified cycle path.</summary>
    public bool HasWitness => WitnessEdges.Count > 0;
}

public static class GraphCycles
{
    public static IReadOnlyList<GraphCycle> Find(AnalysisGraph graph)
    {
        var cycles = new List<GraphCycle>();

        foreach (var basisGroup in graph.Relations
                     .GroupBy(relation => relation.Basis, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            // Self references are real dependencies but not architectural cycles.
            var relations = basisGroup
                .Where(relation => relation.SourceEntityId != relation.TargetEntityId)
                .ToArray();
            if (relations.Length == 0)
            {
                continue;
            }

            var nodeKeys = relations
                .SelectMany(relation => new[] { relation.SourceEntityId, relation.TargetEntityId })
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var nameByKey = nodeKeys.ToDictionary(key => key, key => key, StringComparer.Ordinal);
            var edges = relations
                .Select(relation => new CodeMapEdge(
                    relation.SourceEntityId,
                    relation.TargetEntityId,
                    relation.SourceEntityId,
                    relation.TargetEntityId,
                    1))
                .ToArray();

            var result = CycleDetector.Analyze(nodeKeys, edges, nameByKey);
            foreach (var members in result.ToCycles("type").Select(cycle => cycle.Nodes))
            {
                cycles.Add(new GraphCycle(
                    basisGroup.Key,
                    members,
                    FindWitness(relations, members)));
            }
        }

        return cycles;
    }

    /// <summary>
    /// Finds one cycle through the component's members using only edges that exist.
    /// The component is strongly connected, so a path from a start node back to itself
    /// always exists; the search is bounded by the path set and therefore terminates.
    /// </summary>
    private static IReadOnlyList<GraphRelation> FindWitness(
        IReadOnlyList<GraphRelation> relations,
        IReadOnlyList<string> members)
    {
        var memberSet = members.ToHashSet(StringComparer.Ordinal);
        var adjacency = members.ToDictionary(
            member => member,
            _ => new List<GraphRelation>(),
            StringComparer.Ordinal);
        foreach (var relation in relations)
        {
            if (memberSet.Contains(relation.SourceEntityId) && memberSet.Contains(relation.TargetEntityId))
            {
                adjacency[relation.SourceEntityId].Add(relation);
            }
        }

        foreach (var start in members.OrderBy(member => member, StringComparer.Ordinal))
        {
            var path = new List<GraphRelation>();
            var onPath = new HashSet<string>(StringComparer.Ordinal) { start };
            if (TryExtend(start, start, adjacency, path, onPath))
            {
                return path;
            }
        }

        return [];
    }

    private static bool TryExtend(
        string start,
        string current,
        IReadOnlyDictionary<string, List<GraphRelation>> adjacency,
        List<GraphRelation> path,
        HashSet<string> onPath)
    {
        foreach (var relation in adjacency[current]
                     .OrderBy(entry => entry.TargetEntityId, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Basis, StringComparer.Ordinal))
        {
            if (string.Equals(relation.TargetEntityId, start, StringComparison.Ordinal))
            {
                path.Add(relation);
                return true;
            }

            if (!onPath.Add(relation.TargetEntityId))
            {
                continue;
            }

            path.Add(relation);
            if (TryExtend(start, relation.TargetEntityId, adjacency, path, onPath))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
            onPath.Remove(relation.TargetEntityId);
        }

        return false;
    }
}
