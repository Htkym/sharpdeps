#:property PublishAot=false
#:package Microsoft.CodeAnalysis.CSharp@4.14.0

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var options = CliOptions.Parse(args);
var outputPath = options.Require("output");
var solutionPath = options.Require("solution");
var maxProjects = options.GetInt("max-projects", 40);
var maxEdges = options.GetInt("max-edges", 80);

var report = await CodeMapAnalyzer.AnalyzeAsync(solutionPath, maxProjects, maxEdges);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(
    outputPath,
    JsonSerializer.Serialize(report, CodeMapJsonContext.Default.CodeMapReport));

sealed class CodeMapAnalyzer
{
    private const string SolutionFolderTypeGuid = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";
    private const string CycleColor = "#e5484d";

    private static readonly Regex SlnProjectRegex = new(
        "^Project\\(\"(?<typeGuid>\\{[^\\\"]+\\})\"\\)\\s*=\\s*\"(?<name>[^\"]+)\"\\s*,\\s*\"(?<path>[^\"]+)\"\\s*,\\s*\"(?<projectGuid>\\{[^\\\"]+\\})\"",
        RegexOptions.Compiled);

    private static readonly Regex NestedProjectRegex = new(
        "^\\s*(?<child>\\{[^\\}]+\\})\\s*=\\s*(?<parent>\\{[^\\}]+\\})\\s*$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> SupportedProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj",
        ".fsproj",
        ".vbproj",
        ".vcxproj"
    };

    private static readonly string[] TestPackageMarkers =
    [
        "coverlet.collector",
        "microsoft.net.test.sdk",
        "mstest.testframework",
        "mstest.testadapter",
        "nunit",
        "nunit3testadapter",
        "xunit",
        "xunit.runner.visualstudio"
    ];

    public static async Task<CodeMapReport> AnalyzeAsync(string solutionPath, int maxProjects, int maxEdges)
    {
        var resolvedSolutionPath = Path.GetFullPath(string.IsNullOrWhiteSpace(solutionPath)
            ? throw new InvalidOperationException("A solution path is required.")
            : solutionPath);

        if (!File.Exists(resolvedSolutionPath))
        {
            throw new FileNotFoundException($"Solution file was not found: {resolvedSolutionPath}", resolvedSolutionPath);
        }

        if (!resolvedSolutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            && !resolvedSolutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Only .sln and .slnx are supported. Received: {resolvedSolutionPath}");
        }

        var notes = new List<string>();
        var warnings = new List<string>();

        ParsedSolution parsedSolution;
        if (resolvedSolutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            parsedSolution = await ParseSlnxAsync(resolvedSolutionPath);
            notes.Add("The selected .slnx file was parsed through XML project discovery.");
        }
        else
        {
            parsedSolution = await ParseSlnAsync(resolvedSolutionPath);
        }

        if (parsedSolution.Projects.Count == 0)
        {
            return new CodeMapReport(
                parsedSolution.SolutionPath,
                parsedSolution.SolutionName,
                0,
                0,
                0,
                0,
                [],
                [],
                ["No supported project files were found in the selected solution."],
                warnings,
                "flowchart LR\n  Empty[\"No projects found\"]",
                [],
                [],
                [],
                [],
                NamespaceGraph.Empty("No projects were found, so namespace analysis was skipped."));
        }

        var loadedProjects = new List<LoadedProject>();
        foreach (var project in parsedSolution.Projects)
        {
            try
            {
                loadedProjects.Add(await LoadProjectAsync(project, parsedSolution.SolutionDirectoryPath));
            }
            catch (Exception error)
            {
                warnings.Add($"Failed to parse project '{project.Name}': {error.Message}");
            }
        }

        if (loadedProjects.Count == 0)
        {
            return new CodeMapReport(
                parsedSolution.SolutionPath,
                parsedSolution.SolutionName,
                0,
                0,
                0,
                0,
                [],
                [],
                ["No project files could be parsed from the selected solution."],
                warnings,
                "flowchart LR\n  Empty[\"No projects could be parsed\"]",
                [],
                [],
                [],
                [],
                NamespaceGraph.Empty("No projects could be parsed, so namespace analysis was skipped."));
        }

        var projectLookup = loadedProjects.ToDictionary(
            project => NormalizePathKey(project.FullPath),
            project => project,
            StringComparer.Ordinal);

        var edgeAccumulator = new Dictionary<(string SourceKey, string TargetKey), int>();
        var externalDependencyCount = 0;
        var conditionalProjectReferenceCount = 0;

        foreach (var project in loadedProjects)
        {
            foreach (var projectReference in project.ProjectReferences)
            {
                if (projectReference.IsConditional)
                {
                    conditionalProjectReferenceCount++;
                }

                if (!projectLookup.TryGetValue(projectReference.LookupKey, out var targetProject))
                {
                    externalDependencyCount++;
                    continue;
                }

                var edgeKey = (project.LookupKey, targetProject.LookupKey);
                edgeAccumulator[edgeKey] = edgeAccumulator.TryGetValue(edgeKey, out var count) ? count + 1 : 1;
            }
        }

        var nameByKey = loadedProjects.ToDictionary(
            project => project.LookupKey,
            project => project.Name,
            StringComparer.Ordinal);

        var projectEdges = edgeAccumulator
            .Select(entry => new CodeMapEdge(
                entry.Key.SourceKey,
                entry.Key.TargetKey,
                nameByKey[entry.Key.SourceKey],
                nameByKey[entry.Key.TargetKey],
                entry.Value))
            .OrderByDescending(edge => edge.Count)
            .ThenBy(edge => edge.SourceName, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetName, StringComparer.Ordinal)
            .ToArray();

        var outgoingCounts = projectEdges
            .GroupBy(edge => edge.SourceKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(edge => edge.Count), StringComparer.Ordinal);

        var incomingCounts = projectEdges
            .GroupBy(edge => edge.TargetKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(edge => edge.Count), StringComparer.Ordinal);

        var selectedProjects = loadedProjects
            .OrderByDescending(project => outgoingCounts.GetValueOrDefault(project.LookupKey) + incomingCounts.GetValueOrDefault(project.LookupKey))
            .ThenByDescending(project => project.PackageReferences.Count)
            .ThenBy(project => project.Name, StringComparer.Ordinal)
            .Take(Math.Max(1, maxProjects))
            .ToArray();

        var selectedKeys = selectedProjects.Select(project => project.LookupKey).ToHashSet(StringComparer.Ordinal);
        var visibleEdges = projectEdges
            .Where(edge => selectedKeys.Contains(edge.SourceKey) && selectedKeys.Contains(edge.TargetKey))
            .Take(Math.Max(1, maxEdges))
            .ToArray();

        if (selectedProjects.Length < loadedProjects.Count)
        {
            notes.Add($"Diagram truncated to the top {selectedProjects.Length} projects out of {loadedProjects.Count} total.");
        }

        if (visibleEdges.Length < projectEdges.Length)
        {
            notes.Add($"Showing the top {visibleEdges.Length} project reference edges out of {projectEdges.Length} total.");
        }

        var totalPackageReferences = loadedProjects.Sum(project => project.PackageReferences.Count);
        if (totalPackageReferences > 0)
        {
            notes.Add($"PackageReference nodes are summarized only: {totalPackageReferences} package reference(s) across {loadedProjects.Count} project(s).");
        }

        if (externalDependencyCount > 0)
        {
            warnings.Add($"{externalDependencyCount} project reference(s) point outside the selected solution and are summarized as externals.");
        }

        if (conditionalProjectReferenceCount > 0)
        {
            warnings.Add($"{conditionalProjectReferenceCount} conditional ProjectReference item(s) were detected and may vary by configuration.");
        }

        var kindSummary = loadedProjects
            .GroupBy(project => project.Kind, StringComparer.Ordinal)
            .Select(group => new ProjectKindSummary(group.Key, group.Count()))
            .OrderByDescending(summary => summary.Count)
            .ThenBy(summary => summary.Name, StringComparer.Ordinal)
            .ToArray();

        var dependencyHubs = loadedProjects
            .Select(project => new DependencyHubSummary(
                project.Name,
                project.Kind,
                outgoingCounts.GetValueOrDefault(project.LookupKey),
                incomingCounts.GetValueOrDefault(project.LookupKey),
                project.PackageReferences.Count))
            .OrderByDescending(summary => summary.OutgoingDependencies + summary.IncomingDependencies)
            .ThenByDescending(summary => summary.PackageReferences)
            .ThenBy(summary => summary.Name, StringComparer.Ordinal)
            .Take(6)
            .ToArray();

        var codeMapProjects = loadedProjects
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .Select(project => new CodeMapProject(
                project.Name,
                project.RelativePath,
                project.GroupPath,
                project.Kind,
                project.TargetFramework,
                outgoingCounts.GetValueOrDefault(project.LookupKey),
                incomingCounts.GetValueOrDefault(project.LookupKey),
                project.PackageReferences.Count))
            .ToArray();

        var projectCycleResult = CycleDetector.Analyze(
            loadedProjects.Select(project => project.LookupKey),
            projectEdges,
            nameByKey);
        var projectCycles = projectCycleResult.ToCycles("project");

        if (projectCycles.Count > 0)
        {
            warnings.Add(
                $"{projectCycles.Count} circular project-dependency group(s) detected involving {projectCycleResult.CycleNodeKeys.Count} project(s).");
        }

        var selectedGraphNodes = selectedProjects
            .Select(project => new GraphNode(project.LookupKey, project.Name, project.Kind, project.GroupPath))
            .ToArray();
        var diagram = BuildGraph(
            selectedGraphNodes,
            visibleEdges,
            projectCycleResult.CycleNodeKeys,
            projectCycleResult.CycleEdgeKeys);

        var namespaceGraph = await NamespaceAnalyzer.AnalyzeAsync(loadedProjects, maxProjects, maxEdges);
        if (namespaceGraph.Cycles.Count > 0)
        {
            warnings.Add(
                $"{namespaceGraph.Cycles.Count} circular namespace-dependency group(s) detected across {namespaceGraph.NamespaceCount} namespace(s).");
        }

        return new CodeMapReport(
            parsedSolution.SolutionPath,
            parsedSolution.SolutionName,
            loadedProjects.Count,
            projectEdges.Sum(edge => edge.Count),
            totalPackageReferences,
            loadedProjects.Count(project => project.Kind == "test"),
            kindSummary,
            dependencyHubs,
            notes.ToArray(),
            warnings.ToArray(),
            diagram.Mermaid,
            codeMapProjects,
            diagram.Projects,
            diagram.Edges,
            projectCycles,
            namespaceGraph);
    }

    private static async Task<ParsedSolution> ParseSlnAsync(string solutionPath)
    {
        var solutionDirectoryPath = Path.GetDirectoryName(solutionPath)
            ?? throw new InvalidOperationException($"Could not determine the solution directory for {solutionPath}");
        var lines = await File.ReadAllLinesAsync(solutionPath);

        var rawEntries = new Dictionary<string, RawSolutionEntry>(StringComparer.OrdinalIgnoreCase);
        var nestedParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var insideNestedProjects = false;

        foreach (var line in lines)
        {
            if (insideNestedProjects)
            {
                if (line.TrimStart().StartsWith("EndGlobalSection", StringComparison.Ordinal))
                {
                    insideNestedProjects = false;
                    continue;
                }

                var nestedMatch = NestedProjectRegex.Match(line);
                if (nestedMatch.Success)
                {
                    nestedParents[nestedMatch.Groups["child"].Value] = nestedMatch.Groups["parent"].Value;
                }

                continue;
            }

            if (line.TrimStart().StartsWith("GlobalSection(NestedProjects)", StringComparison.Ordinal))
            {
                insideNestedProjects = true;
                continue;
            }

            var projectMatch = SlnProjectRegex.Match(line);
            if (!projectMatch.Success)
            {
                continue;
            }

            var typeGuid = projectMatch.Groups["typeGuid"].Value;
            var name = projectMatch.Groups["name"].Value;
            var relativePath = projectMatch.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar);
            var projectGuid = projectMatch.Groups["projectGuid"].Value;

            rawEntries[projectGuid] = new RawSolutionEntry(projectGuid, name, relativePath, typeGuid);
        }

        var projects = rawEntries
            .Values
            .Where(entry => !entry.IsSolutionFolder && LooksLikeProjectPath(entry.RelativePath))
            .Select(entry =>
            {
                var fullPath = Path.GetFullPath(Path.Combine(solutionDirectoryPath, entry.RelativePath));
                var groupPath = BuildGroupPath(entry, rawEntries, nestedParents, solutionDirectoryPath);
                return new SolutionProjectEntry(
                    entry.ProjectGuid,
                    entry.Name,
                    fullPath,
                    Path.GetRelativePath(solutionDirectoryPath, fullPath),
                    groupPath);
            })
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();

        return new ParsedSolution(
            solutionPath,
            Path.GetFileNameWithoutExtension(solutionPath),
            solutionDirectoryPath,
            projects);
    }

    private static async Task<ParsedSolution> ParseSlnxAsync(string solutionPath)
    {
        var solutionDirectoryPath = Path.GetDirectoryName(solutionPath)
            ?? throw new InvalidOperationException($"Could not determine the solution directory for {solutionPath}");
        var sourceText = await File.ReadAllTextAsync(solutionPath);
        var document = XDocument.Parse(sourceText, LoadOptions.PreserveWhitespace);

        var projects = document
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase))
            .Select(element =>
            {
                var projectPath = GetAttributeOrChildValue(element, "Path")
                    ?? GetAttributeOrChildValue(element, "Include")
                    ?? GetAttributeOrChildValue(element, "FilePath");
                if (!LooksLikeProjectPath(projectPath))
                {
                    return null;
                }

                var resolvedProjectPath = projectPath!;
                var fullPath = Path.GetFullPath(Path.Combine(solutionDirectoryPath, resolvedProjectPath));
                var groupPath = BuildSlnxGroupPath(element, solutionDirectoryPath, resolvedProjectPath);
                return new SolutionProjectEntry(
                    GetAttributeOrChildValue(element, "Guid")
                        ?? GetAttributeOrChildValue(element, "Id")
                        ?? fullPath,
                    GetAttributeOrChildValue(element, "Name")
                        ?? Path.GetFileNameWithoutExtension(resolvedProjectPath),
                    fullPath,
                    Path.GetRelativePath(solutionDirectoryPath, fullPath),
                    groupPath);
            })
            .Where(entry => entry is not null)
            .Cast<SolutionProjectEntry>()
            .DistinctBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();

        return new ParsedSolution(
            solutionPath,
            Path.GetFileNameWithoutExtension(solutionPath),
            solutionDirectoryPath,
            projects);
    }

    private static async Task<LoadedProject> LoadProjectAsync(SolutionProjectEntry project, string solutionDirectoryPath)
    {
        var projectText = await File.ReadAllTextAsync(project.FullPath);
        var document = XDocument.Parse(projectText, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException("Project XML is empty.");

        var projectDirectoryPath = Path.GetDirectoryName(project.FullPath)
            ?? throw new InvalidOperationException($"Could not determine the directory for {project.FullPath}");

        var packageReferences = root
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "PackageReference", StringComparison.OrdinalIgnoreCase))
            .Select(element => GetAttributeOrChildValue(element, "Include") ?? GetAttributeOrChildValue(element, "Update"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var projectReferences = root
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "ProjectReference", StringComparison.OrdinalIgnoreCase))
            .Select(element =>
            {
                var includePath = GetAttributeOrChildValue(element, "Include");
                if (string.IsNullOrWhiteSpace(includePath))
                {
                    return null;
                }

                var fullReferencePath = Path.GetFullPath(Path.Combine(projectDirectoryPath, includePath));
                var isConditional = HasCondition(element);
                return new ProjectReferenceInfo(
                    includePath,
                    fullReferencePath,
                    NormalizePathKey(fullReferencePath),
                    isConditional);
            })
            .Where(reference => reference is not null)
            .Cast<ProjectReferenceInfo>()
            .ToArray();

        var kind = DetermineProjectKind(
            project.Name,
            ReadProjectSdk(root),
            packageReferences,
            GetPropertyValue(root, "IsTestProject"),
            GetPropertyValue(root, "OutputType"),
            GetPropertyValue(root, "UseWPF"),
            GetPropertyValue(root, "UseWindowsForms"));

        return new LoadedProject(
            project.Name,
            project.FullPath,
            project.RelativePath,
            project.GroupPath,
            NormalizePathKey(project.FullPath),
            kind,
            GetPropertyValue(root, "TargetFramework")
                ?? GetPrimaryTargetFramework(GetPropertyValue(root, "TargetFrameworks"))
                ?? "(not specified)",
            projectReferences,
            packageReferences);
    }

    private static string BuildGroupPath(
        RawSolutionEntry entry,
        IReadOnlyDictionary<string, RawSolutionEntry> rawEntries,
        IReadOnlyDictionary<string, string> nestedParents,
        string solutionDirectoryPath)
    {
        var segments = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentId = entry.ProjectGuid;

        while (nestedParents.TryGetValue(currentId, out var parentId)
            && rawEntries.TryGetValue(parentId, out var parentEntry)
            && parentEntry.IsSolutionFolder
            && visited.Add(parentId))
        {
            segments.Add(parentEntry.Name);
            currentId = parentId;
        }

        segments.Reverse();
        if (segments.Count > 0)
        {
            return string.Join(Path.DirectorySeparatorChar, segments);
        }

        var directoryPath = Path.GetDirectoryName(entry.RelativePath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return "(solution root)";
        }

        var normalizedDirectory = directoryPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetRelativePath(solutionDirectoryPath, Path.GetFullPath(Path.Combine(solutionDirectoryPath, normalizedDirectory)));
    }

    private static string BuildSlnxGroupPath(XElement projectElement, string solutionDirectoryPath, string projectPath)
    {
        var folderNames = projectElement
            .Ancestors()
            .Where(element => IsLikelySolutionFolderElement(element))
            .Select(element => GetAttributeOrChildValue(element, "Name"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Reverse()
            .ToArray();

        if (folderNames.Length > 0)
        {
            return string.Join(Path.DirectorySeparatorChar, folderNames);
        }

        var directoryPath = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return "(solution root)";
        }

        var normalizedDirectory = directoryPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetRelativePath(solutionDirectoryPath, Path.GetFullPath(Path.Combine(solutionDirectoryPath, normalizedDirectory)));
    }

    internal static MermaidDiagram BuildGraph(
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
            builder.AppendLine($"  subgraph G{groupIndex}[\"{EscapeLabel(group.Key)}\"]");
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
                builder.AppendLine($"    {nodeId}[\"{EscapeLabel(node.Name)}\\n{EscapeLabel(node.Kind)}\"]");
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

    private static string DetermineProjectKind(
        string projectName,
        string sdk,
        IReadOnlyList<string> packageReferences,
        string? isTestProject,
        string? outputType,
        string? useWpf,
        string? useWindowsForms)
    {
        if (IsTrue(isTestProject)
            || packageReferences.Any(package => TestPackageMarkers.Any(marker => package.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            || projectName.Contains(".Tests", StringComparison.OrdinalIgnoreCase)
            || projectName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || projectName.EndsWith("Test", StringComparison.OrdinalIgnoreCase))
        {
            return "test";
        }

        if (sdk.Contains("Web", StringComparison.OrdinalIgnoreCase))
        {
            return "web";
        }

        if (IsTrue(useWpf) || IsTrue(useWindowsForms))
        {
            return "desktop";
        }

        if (string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
        {
            return "app";
        }

        return "library";
    }

    private static bool HasCondition(XElement element)
        => element.Attributes().Any(attribute => string.Equals(attribute.Name.LocalName, "Condition", StringComparison.OrdinalIgnoreCase))
           || element.Ancestors().Any(ancestor =>
               ancestor.Attributes().Any(attribute => string.Equals(attribute.Name.LocalName, "Condition", StringComparison.OrdinalIgnoreCase)));

    private static bool LooksLikeProjectPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var extension = Path.GetExtension(relativePath);
        return SupportedProjectExtensions.Contains(extension);
    }

    private static bool IsLikelySolutionFolderElement(XElement element)
    {
        var localName = element.Name.LocalName;
        if (localName.Contains("Folder", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var path = GetAttributeOrChildValue(element, "Path")
            ?? GetAttributeOrChildValue(element, "Include")
            ?? GetAttributeOrChildValue(element, "FilePath");
        return string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(GetAttributeOrChildValue(element, "Name"));
    }

    private static string? GetAttributeOrChildValue(XElement element, string name)
        => element.Attributes()
               .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
               ?.Value
           ?? element.Elements()
               .FirstOrDefault(child => string.Equals(child.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
               ?.Value;

    private static string? GetPropertyValue(XElement root, string propertyName)
        => root
            .Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, propertyName, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?.Trim();

    private static string ReadProjectSdk(XElement root)
        => root.Attribute("Sdk")?.Value
           ?? string.Join(
               ";",
               root.Elements()
                   .Where(element => string.Equals(element.Name.LocalName, "Sdk", StringComparison.OrdinalIgnoreCase))
                   .Select(element => element.Attribute("Name")?.Value)
                   .Where(value => !string.IsNullOrWhiteSpace(value))
                   .Cast<string>())
           ?? string.Empty;

    private static string? GetPrimaryTargetFramework(string? targetFrameworks)
    {
        if (string.IsNullOrWhiteSpace(targetFrameworks))
        {
            return null;
        }

        return targetFrameworks
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    private static string NormalizePathKey(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return OperatingSystem.IsWindows()
            ? fullPath.ToLowerInvariant()
            : fullPath;
    }

    private static bool IsTrue(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static string EscapeLabel(string value)
        => value
            .Replace('\\', '/')
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "'")
            .Replace("#", "&#35;");
}

static class CycleDetector
{
    public static CycleResult Analyze(
        IEnumerable<string> nodeKeys,
        IReadOnlyList<CodeMapEdge> edges,
        IReadOnlyDictionary<string, string> nameByKey)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var key in nodeKeys)
        {
            if (!adjacency.ContainsKey(key))
            {
                adjacency[key] = [];
            }
        }

        foreach (var edge in edges)
        {
            if (!adjacency.TryGetValue(edge.SourceKey, out var targets))
            {
                targets = [];
                adjacency[edge.SourceKey] = targets;
            }

            if (!adjacency.ContainsKey(edge.TargetKey))
            {
                adjacency[edge.TargetKey] = [];
            }

            targets.Add(edge.TargetKey);
        }

        var components = ComputeStronglyConnectedComponents(adjacency);

        var componentIdByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var nonTrivialComponents = new HashSet<int>();
        for (var componentId = 0; componentId < components.Count; componentId++)
        {
            var members = components[componentId];
            foreach (var member in members)
            {
                componentIdByKey[member] = componentId;
            }

            var isCycle = members.Count > 1
                || (members.Count == 1 && adjacency[members[0]].Contains(members[0]));
            if (isCycle)
            {
                nonTrivialComponents.Add(componentId);
            }
        }

        var cycleNodeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var componentId in nonTrivialComponents)
        {
            foreach (var member in components[componentId])
            {
                cycleNodeKeys.Add(member);
            }
        }

        var cycleEdgeKeys = new HashSet<(string Source, string Target)>();
        foreach (var edge in edges)
        {
            if (componentIdByKey.TryGetValue(edge.SourceKey, out var sourceComponent)
                && componentIdByKey.TryGetValue(edge.TargetKey, out var targetComponent)
                && sourceComponent == targetComponent
                && nonTrivialComponents.Contains(sourceComponent))
            {
                cycleEdgeKeys.Add((edge.SourceKey, edge.TargetKey));
            }
        }

        var componentNames = nonTrivialComponents
            .Select(componentId => (IReadOnlyList<string>)components[componentId]
                .Select(key => nameByKey.TryGetValue(key, out var name) ? name : key)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray())
            .ToArray();

        return new CycleResult(cycleNodeKeys, cycleEdgeKeys, componentNames);
    }

    private static List<List<string>> ComputeStronglyConnectedComponents(
        IReadOnlyDictionary<string, List<string>> adjacency)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var tarjanStack = new Stack<string>();
        var components = new List<List<string>>();
        var nextIndex = 0;

        foreach (var start in adjacency.Keys)
        {
            if (index.ContainsKey(start))
            {
                continue;
            }

            var callStack = new Stack<(string Node, int ChildIndex)>();
            callStack.Push((start, 0));

            while (callStack.Count > 0)
            {
                var (node, childIndex) = callStack.Pop();

                if (childIndex == 0)
                {
                    index[node] = nextIndex;
                    low[node] = nextIndex;
                    nextIndex++;
                    tarjanStack.Push(node);
                    onStack.Add(node);
                }
                else
                {
                    var finishedChild = adjacency[node][childIndex - 1];
                    low[node] = Math.Min(low[node], low[finishedChild]);
                }

                var neighbors = adjacency[node];
                var pushedChild = false;
                for (var i = childIndex; i < neighbors.Count; i++)
                {
                    var next = neighbors[i];
                    if (!index.ContainsKey(next))
                    {
                        callStack.Push((node, i + 1));
                        callStack.Push((next, 0));
                        pushedChild = true;
                        break;
                    }

                    if (onStack.Contains(next))
                    {
                        low[node] = Math.Min(low[node], index[next]);
                    }
                }

                if (pushedChild)
                {
                    continue;
                }

                if (low[node] == index[node])
                {
                    var component = new List<string>();
                    string popped;
                    do
                    {
                        popped = tarjanStack.Pop();
                        onStack.Remove(popped);
                        component.Add(popped);
                    }
                    while (!string.Equals(popped, node, StringComparison.Ordinal));

                    components.Add(component);
                }
            }
        }

        return components;
    }
}

sealed class CycleResult
{
    public CycleResult(
        HashSet<string> cycleNodeKeys,
        HashSet<(string Source, string Target)> cycleEdgeKeys,
        IReadOnlyList<IReadOnlyList<string>> nonTrivialComponentNames)
    {
        CycleNodeKeys = cycleNodeKeys;
        CycleEdgeKeys = cycleEdgeKeys;
        NonTrivialComponentNames = nonTrivialComponentNames;
    }

    public HashSet<string> CycleNodeKeys { get; }

    public HashSet<(string Source, string Target)> CycleEdgeKeys { get; }

    public IReadOnlyList<IReadOnlyList<string>> NonTrivialComponentNames { get; }

    public IReadOnlyList<DependencyCycle> ToCycles(string scope)
        => NonTrivialComponentNames
            .Select(names => new DependencyCycle(scope, names, names.Count))
            .OrderByDescending(cycle => cycle.Length)
            .ThenBy(cycle => cycle.Nodes.Count > 0 ? cycle.Nodes[0] : string.Empty, StringComparer.Ordinal)
            .ToArray();
}

static class NamespaceAnalyzer
{
    private const long MaxSourceFileBytes = 2 * 1024 * 1024;
    private const int MaxSourceFiles = 8000;

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        ".git",
        ".vs",
        "node_modules",
        "packages"
    };

    public static async Task<NamespaceGraph> AnalyzeAsync(
        IReadOnlyList<LoadedProject> projects,
        int maxNodes,
        int maxEdges)
    {
        var csharpProjects = projects
            .Where(project => project.FullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (csharpProjects.Length == 0)
        {
            return NamespaceGraph.Empty("Namespace analysis runs on C# projects only; no .csproj files were found.");
        }

        var knownNamespaces = new HashSet<string>(StringComparer.Ordinal);
        var declarationCountByProject = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var representativeFileByNamespace = new Dictionary<string, string>(StringComparer.Ordinal);
        var kindByProject = new Dictionary<string, string>(StringComparer.Ordinal);
        var globalUsingsByProject = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var fileRecords = new List<FileNamespaceInfo>();

        var parsedFiles = 0;
        var truncatedFiles = false;

        foreach (var project in csharpProjects)
        {
            kindByProject[project.Name] = project.Kind;
            var projectDirectory = Path.GetDirectoryName(project.FullPath);
            if (string.IsNullOrEmpty(projectDirectory) || !Directory.Exists(projectDirectory))
            {
                continue;
            }

            foreach (var file in EnumerateCSharpFiles(projectDirectory))
            {
                if (parsedFiles >= MaxSourceFiles)
                {
                    truncatedFiles = true;
                    break;
                }

                string text;
                try
                {
                    if (new FileInfo(file).Length > MaxSourceFileBytes)
                    {
                        continue;
                    }

                    text = await File.ReadAllTextAsync(file);
                }
                catch
                {
                    continue;
                }

                SyntaxNode root;
                try
                {
                    root = CSharpSyntaxTree.ParseText(text).GetRoot();
                }
                catch
                {
                    continue;
                }

                parsedFiles++;

                var fileNamespaces = root
                    .DescendantNodes()
                    .OfType<BaseNamespaceDeclarationSyntax>()
                    .Select(FullNamespaceName)
                    .Where(name => name.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                var usings = new List<string>();
                foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
                {
                    if (directive.Alias is not null)
                    {
                        continue;
                    }

                    var targetNamespace = ResolveUsingNamespace(directive);
                    if (targetNamespace is null)
                    {
                        continue;
                    }

                    if (directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
                    {
                        if (!globalUsingsByProject.TryGetValue(project.Name, out var projectGlobals))
                        {
                            projectGlobals = new HashSet<string>(StringComparer.Ordinal);
                            globalUsingsByProject[project.Name] = projectGlobals;
                        }

                        projectGlobals.Add(targetNamespace);
                    }
                    else
                    {
                        usings.Add(targetNamespace);
                    }
                }

                foreach (var ns in fileNamespaces)
                {
                    knownNamespaces.Add(ns);
                    representativeFileByNamespace.TryAdd(ns, file);
                    if (!declarationCountByProject.TryGetValue(ns, out var perProject))
                    {
                        perProject = new Dictionary<string, int>(StringComparer.Ordinal);
                        declarationCountByProject[ns] = perProject;
                    }

                    perProject[project.Name] = perProject.GetValueOrDefault(project.Name) + 1;
                }

                if (fileNamespaces.Length > 0)
                {
                    fileRecords.Add(new FileNamespaceInfo(project.Name, fileNamespaces, usings));
                }
            }
        }

        if (knownNamespaces.Count == 0)
        {
            return NamespaceGraph.Empty(
                $"Parsed {parsedFiles} C# file(s), but found no namespace declarations (top-level statements are skipped).");
        }

        var edgeAccumulator = new Dictionary<(string Source, string Target), int>();
        foreach (var record in fileRecords)
        {
            var effectiveUsings = new HashSet<string>(record.Usings, StringComparer.Ordinal);
            if (globalUsingsByProject.TryGetValue(record.ProjectName, out var projectGlobals))
            {
                effectiveUsings.UnionWith(projectGlobals);
            }

            var targets = effectiveUsings.Where(knownNamespaces.Contains).ToArray();
            if (targets.Length == 0)
            {
                continue;
            }

            foreach (var source in record.Namespaces)
            {
                foreach (var target in targets)
                {
                    if (string.Equals(source, target, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var key = (source, target);
                    edgeAccumulator[key] = edgeAccumulator.GetValueOrDefault(key) + 1;
                }
            }
        }

        var nameByKey = knownNamespaces.ToDictionary(ns => ns, ns => ns, StringComparer.Ordinal);

        var allEdges = edgeAccumulator
            .Select(entry => new CodeMapEdge(
                entry.Key.Source,
                entry.Key.Target,
                entry.Key.Source,
                entry.Key.Target,
                entry.Value))
            .OrderByDescending(edge => edge.Count)
            .ThenBy(edge => edge.SourceName, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetName, StringComparer.Ordinal)
            .ToArray();

        var cycleResult = CycleDetector.Analyze(knownNamespaces, allEdges, nameByKey);
        var cycles = cycleResult.ToCycles("namespace");

        var outgoingCounts = allEdges
            .GroupBy(edge => edge.SourceKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(edge => edge.Count), StringComparer.Ordinal);
        var incomingCounts = allEdges
            .GroupBy(edge => edge.TargetKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(edge => edge.Count), StringComparer.Ordinal);

        var graphNodes = knownNamespaces
            .Select(ns => new GraphNode(
                ns,
                ns,
                ResolveKind(ns, declarationCountByProject, kindByProject),
                ResolveGroup(ns, declarationCountByProject),
                representativeFileByNamespace.GetValueOrDefault(ns)))
            .ToArray();

        var selectedNodes = graphNodes
            .OrderByDescending(node => outgoingCounts.GetValueOrDefault(node.Key) + incomingCounts.GetValueOrDefault(node.Key))
            .ThenBy(node => node.Name, StringComparer.Ordinal)
            .Take(Math.Max(1, maxNodes))
            .ToArray();

        var selectedKeys = selectedNodes.Select(node => node.Key).ToHashSet(StringComparer.Ordinal);
        var visibleEdges = allEdges
            .Where(edge => selectedKeys.Contains(edge.SourceKey) && selectedKeys.Contains(edge.TargetKey))
            .Take(Math.Max(1, maxEdges))
            .ToArray();

        var diagram = CodeMapAnalyzer.BuildGraph(
            selectedNodes,
            visibleEdges,
            cycleResult.CycleNodeKeys,
            cycleResult.CycleEdgeKeys);

        var notes = new List<string>
        {
            $"Namespace edges are derived from `using` directives among {knownNamespaces.Count} solution namespace(s) (C# syntax only).",
            $"Parsed {parsedFiles} C# source file(s) across {csharpProjects.Length} C# project(s).",
        };

        if (truncatedFiles)
        {
            notes.Add($"Source scan stopped after {MaxSourceFiles} files; namespace data may be incomplete.");
        }

        if (selectedNodes.Length < graphNodes.Length)
        {
            notes.Add($"Namespace diagram truncated to the top {selectedNodes.Length} of {graphNodes.Length} namespaces.");
        }

        if (visibleEdges.Length < allEdges.Length)
        {
            notes.Add($"Showing the top {visibleEdges.Length} of {allEdges.Length} namespace dependency edges.");
        }

        return new NamespaceGraph(
            knownNamespaces.Count,
            edgeAccumulator.Count,
            diagram.Mermaid,
            diagram.Projects,
            diagram.Edges,
            cycles,
            notes);
    }

    private static string FullNamespaceName(BaseNamespaceDeclarationSyntax declaration)
    {
        var names = new List<string> { declaration.Name.ToString().Trim() };
        foreach (var ancestor in declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>())
        {
            names.Add(ancestor.Name.ToString().Trim());
        }

        names.Reverse();
        return string.Join('.', names.Where(name => name.Length > 0));
    }

    private static string? ResolveUsingNamespace(UsingDirectiveSyntax directive)
    {
        var name = directive.Name?.ToString().Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
        {
            var lastDot = name.LastIndexOf('.');
            return lastDot <= 0 ? null : name[..lastDot];
        }

        return name;
    }

    private static string ResolveGroup(
        string ns,
        IReadOnlyDictionary<string, Dictionary<string, int>> declarationCountByProject)
    {
        if (declarationCountByProject.TryGetValue(ns, out var perProject) && perProject.Count > 0)
        {
            return perProject
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .First()
                .Key;
        }

        return "(unassigned)";
    }

    private static string ResolveKind(
        string ns,
        IReadOnlyDictionary<string, Dictionary<string, int>> declarationCountByProject,
        IReadOnlyDictionary<string, string> kindByProject)
    {
        var project = ResolveGroup(ns, declarationCountByProject);
        return kindByProject.TryGetValue(project, out var kind) ? kind : "namespace";
    }

    private static IEnumerable<string> EnumerateCSharpFiles(string rootDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(rootDirectory);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
            }
            catch
            {
                subdirectories = [];
            }

            foreach (var subdirectory in subdirectories)
            {
                if (IgnoredDirectoryNames.Contains(Path.GetFileName(subdirectory)))
                {
                    continue;
                }

                pending.Push(subdirectory);
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.cs");
            }
            catch
            {
                files = [];
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }
}

sealed record FileNamespaceInfo(
    string ProjectName,
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<string> Usings);

sealed class CliOptions
{
    private readonly Dictionary<string, string> _values;

    private CliOptions(Dictionary<string, string> values) => _values = values;

    public static CliOptions Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Length; index++)
        {
            var token = arguments[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = token[2..];
            var value = index + 1 < arguments.Length && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? arguments[++index]
                : "true";
            values[key] = value;
        }

        return new CliOptions(values);
    }

    public string Require(string key)
        => _values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing required option --{key}");

    public int GetInt(string key, int fallback)
        => _values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : fallback;
}

sealed record RawSolutionEntry(string ProjectGuid, string Name, string RelativePath, string TypeGuid)
{
    public bool IsSolutionFolder => string.Equals(TypeGuid, "{2150E333-8FDC-42A3-9474-1A3956D46DE8}", StringComparison.OrdinalIgnoreCase);
}

sealed record ParsedSolution(
    string SolutionPath,
    string SolutionName,
    string SolutionDirectoryPath,
    IReadOnlyList<SolutionProjectEntry> Projects);

sealed record SolutionProjectEntry(
    string Id,
    string Name,
    string FullPath,
    string RelativePath,
    string GroupPath);

sealed record LoadedProject(
    string Name,
    string FullPath,
    string RelativePath,
    string GroupPath,
    string LookupKey,
    string Kind,
    string TargetFramework,
    IReadOnlyList<ProjectReferenceInfo> ProjectReferences,
    IReadOnlyList<string> PackageReferences);

sealed record ProjectReferenceInfo(
    string IncludePath,
    string FullPath,
    string LookupKey,
    bool IsConditional);

sealed record CodeMapEdge(
    string SourceKey,
    string TargetKey,
    string SourceName,
    string TargetName,
    int Count);

sealed record CodeMapReport(
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

sealed record ProjectKindSummary(string Name, int Count);

sealed record DependencyHubSummary(
    string Name,
    string Kind,
    int OutgoingDependencies,
    int IncomingDependencies,
    int PackageReferences);

sealed record CodeMapDiagramProject(
    string NodeId,
    string LookupKey,
    string Name,
    string Kind,
    bool InCycle,
    string? RepresentativeFile = null);

sealed record CodeMapDiagramEdge(
    string EdgeId,
    string SourceKey,
    string TargetKey,
    string SourceNodeId,
    string TargetNodeId,
    string SourceName,
    string TargetName,
    int Count,
    bool InCycle);

sealed record CodeMapProject(
    string Name,
    string RelativePath,
    string GroupPath,
    string Kind,
    string TargetFramework,
    int OutgoingDependencies,
    int IncomingDependencies,
    int PackageReferences);

sealed record MermaidDiagram(
    string Mermaid,
    IReadOnlyList<CodeMapDiagramProject> Projects,
    IReadOnlyList<CodeMapDiagramEdge> Edges);

sealed record GraphNode(string Key, string Name, string Kind, string GroupPath, string? RepresentativeFile = null);

sealed record DependencyCycle(
    string Scope,
    IReadOnlyList<string> Nodes,
    int Length);

sealed record NamespaceGraph(
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
partial class CodeMapJsonContext : JsonSerializerContext;