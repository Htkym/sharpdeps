namespace SharpDeps.Analysis.Contracts;

public sealed record FileNamespaceInfo(
    string ProjectName,
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<string> Usings);

public sealed record RawSolutionEntry(string ProjectGuid, string Name, string RelativePath, string TypeGuid)
{
    public bool IsSolutionFolder => string.Equals(TypeGuid, "{2150E333-8FDC-42A3-9474-1A3956D46DE8}", StringComparison.OrdinalIgnoreCase);
}

public sealed record ParsedSolution(
    string SolutionPath,
    string SolutionName,
    string SolutionDirectoryPath,
    IReadOnlyList<SolutionProjectEntry> Projects);

public sealed record SolutionProjectEntry(
    string Id,
    string Name,
    string FullPath,
    string RelativePath,
    string GroupPath);

public sealed record LoadedProject(
    string Name,
    string FullPath,
    string RelativePath,
    string GroupPath,
    string LookupKey,
    string Kind,
    string TargetFramework,
    IReadOnlyList<ProjectReferenceInfo> ProjectReferences,
    IReadOnlyList<string> PackageReferences);

public sealed record ProjectReferenceInfo(
    string IncludePath,
    string FullPath,
    string LookupKey,
    bool IsConditional);

public sealed record CodeMapEdge(
    string SourceKey,
    string TargetKey,
    string SourceName,
    string TargetName,
    int Count);
