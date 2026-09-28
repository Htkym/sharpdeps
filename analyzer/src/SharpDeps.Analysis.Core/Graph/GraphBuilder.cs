namespace SharpDeps.Analysis.Core.Graph;

using System.Text;
using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Text;

/// <summary>
/// Builds the Mermaid rendering plus the diagram nodes/edges. Node and edge ids
/// are assigned here and are stable for a given input order, so the webview and
/// the export agree on what was clicked.
/// </summary>
public static class GraphBuilder
{
    public const string CycleColor = "#e5484d";

    public static MermaidDiagram Build(
        IReadOnlyList<GraphNode> selectedNodes,
        IReadOnlyList<CodeMapEdge> visibleEdges,
        IReadOnlySet<string> cycleNodeKeys,
        IReadOnlySet<(string Source, string Target)> cycleEdgeKeys)
    {
        var builder = new StringBuilder();
        builder.AppendLine("flowchart LR");

        if (selectedNodes.Count == 0)
        {
            builder.AppendLine("  Empty[\"No nodes found\"]");
            return new MermaidDiagram(builder.ToString().TrimEnd(), [], []);
        }

        var idLookup = new Dictionary<string, string>(StringComparer.Ordinal);
        var diagramNodes = new List<CodeMapDiagramProject>(selectedNodes.Count);
        var cycleNodeIds = new List<string>();
        var groupIndex = 0;

        foreach (var group in selectedNodes
                     .GroupBy(node => string.IsNullOrWhiteSpace(node.GroupPath) ? "(solution root)" : node.GroupPath, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"  subgraph G{groupIndex}[\"{MermaidText.EscapeLabel(group.Key)}\"]");
            foreach (var node in group.OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                var nodeId = $"P{idLookup.Count}";
                idLookup[node.Key] = nodeId;
                var inCycle = cycleNodeKeys.Contains(node.Key);
                if (inCycle)
                {
                    cycleNodeIds.Add(nodeId);
                }

                diagramNodes.Add(new CodeMapDiagramProject(nodeId, node.Key, node.Name, node.Kind, inCycle, node.RepresentativeFile));
                builder.AppendLine($"    {nodeId}[\"{MermaidText.EscapeLabel(node.Name)}\\n{MermaidText.EscapeLabel(node.Kind)}\"]");
            }

            builder.AppendLine("  end");
            groupIndex++;
        }

        if (visibleEdges.Count == 0)
        {
            builder.AppendLine("  EmptyLink[\"No dependencies found\"]");
            AppendCycleStyles(builder, cycleNodeIds, []);
            return new MermaidDiagram(builder.ToString().TrimEnd(), diagramNodes, []);
        }

        var diagramEdges = new List<CodeMapDiagramEdge>(visibleEdges.Count);
        var cycleEdgeIndexes = new List<int>();
        var edgeIndex = 0;
        foreach (var edge in visibleEdges)
        {
            if (!idLookup.TryGetValue(edge.SourceKey, out var sourceId)
                || !idLookup.TryGetValue(edge.TargetKey, out var targetId))
            {
                continue;
            }

            var edgeId = $"E{edgeIndex}";
            var inCycle = cycleEdgeKeys.Contains((edge.SourceKey, edge.TargetKey));
            builder.AppendLine($"  {sourceId} {edgeId}@--> {targetId}");
            if (inCycle)
            {
                cycleEdgeIndexes.Add(edgeIndex);
            }

            diagramEdges.Add(new CodeMapDiagramEdge(
                edgeId,
                edge.SourceKey,
                edge.TargetKey,
                sourceId,
                targetId,
                edge.SourceName,
                edge.TargetName,
                edge.Count,
                inCycle));
            edgeIndex++;
        }

        AppendCycleStyles(builder, cycleNodeIds, cycleEdgeIndexes);
        return new MermaidDiagram(builder.ToString().TrimEnd(), diagramNodes, diagramEdges);
    }

    private static void AppendCycleStyles(
        StringBuilder builder,
        IReadOnlyList<string> cycleNodeIds,
        IReadOnlyList<int> cycleEdgeIndexes)
    {
        foreach (var nodeId in cycleNodeIds)
        {
            builder.AppendLine($"  style {nodeId} stroke:{CycleColor},stroke-width:2px");
        }

        if (cycleEdgeIndexes.Count > 0)
        {
            builder.AppendLine($"  linkStyle {string.Join(",", cycleEdgeIndexes)} stroke:{CycleColor},stroke-width:2px");
        }
    }
}
