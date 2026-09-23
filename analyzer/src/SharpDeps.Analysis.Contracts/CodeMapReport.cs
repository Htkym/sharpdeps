// Legacy v1 Quick report contract. The v2 analysis model is added in SD-006; this
// shape is kept so the extension and its snapshot test keep working meanwhile.
namespace SharpDeps.Analysis.Contracts;

using System.Text.Json.Serialization;

public sealed record CodeMapReport(
    string SolutionPath,
    string SolutionName,
    int ProjectCount,
    int TotalDependencies,
    int TotalPackageReferences,
    int TestProjectCount,
    IReadOnlyList<ProjectKindSummary> ProjectKinds,
    IReadOnlyList<DependencyHubSummary> DependencyHubs,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Warnings,
    string Mermaid,
    IReadOnlyList<CodeMapProject> Projects,
    IReadOnlyList<CodeMapDiagramProject> DiagramProjects,
    IReadOnlyList<CodeMapDiagramEdge> DiagramEdges,
    IReadOnlyList<DependencyCycle> ProjectCycles,
    NamespaceGraph Namespaces);

public sealed record ProjectKindSummary(string Name, int Count);

public sealed record DependencyHubSummary(
    string Name,
    string Kind,
    int OutgoingDependencies,
    int IncomingDependencies,
    int PackageReferences);

public sealed record CodeMapDiagramProject(
    string NodeId,
    string LookupKey,
    string Name,
    string Kind,
    bool InCycle,
    string? RepresentativeFile = null);

public sealed record CodeMapDiagramEdge(
    string EdgeId,
    string SourceKey,
    string TargetKey,
    string SourceNodeId,
    string TargetNodeId,
    string SourceName,
    string TargetName,
    int Count,
    bool InCycle);

public sealed record CodeMapProject(
    string Name,
    string RelativePath,
    string GroupPath,
    string Kind,
    string TargetFramework,
    int OutgoingDependencies,
    int IncomingDependencies,
    int PackageReferences);

public sealed record MermaidDiagram(
    string Mermaid,
    IReadOnlyList<CodeMapDiagramProject> Projects,
    IReadOnlyList<CodeMapDiagramEdge> Edges);

public sealed record GraphNode(string Key, string Name, string Kind, string GroupPath, string? RepresentativeFile = null);

public sealed record DependencyCycle(
    string Scope,
    IReadOnlyList<string> Nodes,
    int Length);

public sealed record NamespaceGraph(
    int NamespaceCount,
    int DependencyCount,
    string Mermaid,
    IReadOnlyList<CodeMapDiagramProject> DiagramNodes,
    IReadOnlyList<CodeMapDiagramEdge> DiagramEdges,
    IReadOnlyList<DependencyCycle> Cycles,
    IReadOnlyList<string> Notes)
{
    public static NamespaceGraph Empty(string note) => new(
        0,
        0,
        "flowchart LR\n  Empty[\"No namespace data\"]",
        [],
        [],
        [],
        string.IsNullOrWhiteSpace(note) ? [] : [note]);
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CodeMapReport))]
public partial class CodeMapJsonContext : JsonSerializerContext;
