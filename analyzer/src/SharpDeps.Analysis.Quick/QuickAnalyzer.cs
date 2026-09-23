namespace SharpDeps.Analysis.Quick;

using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Graph;
using SharpDeps.Analysis.Core.Paths;

/// <summary>
/// SDK-free Quick analysis: reads solution/project files as declared XML and builds
/// project and namespace graphs from syntax only. It never evaluates MSBuild, so
/// every edge is a declared or inferred relationship, not a resolved usage.
/// </summary>
public static class QuickAnalyzer
{
        public static async Task<CodeMapReport> AnalyzeAsync(
        string solutionPath,
        int maxProjects,
        int maxEdges,
        QuickSourceIndexCollector? collector = null)
        {
            var resolvedSolutionPath = Path.GetFullPath(string.IsNullOrWhiteSpace(solutionPath)
                ? throw new InvalidOperationException("A solution path is required.")
                : solutionPath);

            var resolvedExtension = Path.GetExtension(resolvedSolutionPath);
            var isSolutionFile = string.Equals(resolvedExtension, ".sln", StringComparison.OrdinalIgnoreCase);
            var isSlnxFile = string.Equals(resolvedExtension, ".slnx", StringComparison.OrdinalIgnoreCase);
            var isProjectFile = ProjectPaths.SupportedProjectExtensions.Contains(resolvedExtension);

            if (!isSolutionFile && !isSlnxFile && !isProjectFile)
            {
                throw new InvalidOperationException(
                    $"Only .sln, .slnx, .csproj, .fsproj, .vbproj, and .vcxproj are supported. Received: {resolvedSolutionPath}");
            }

            if (!File.Exists(resolvedSolutionPath))
            {
                var missingKind = isProjectFile ? "Project file" : "Solution file";
                throw new FileNotFoundException($"{missingKind} was not found: {resolvedSolutionPath}", resolvedSolutionPath);
            }

            var notes = new List<string>();
            var warnings = new List<string>();

            ParsedSolution parsedSolution;
            if (isSlnxFile)
            {
                parsedSolution = await SolutionDiscovery.ParseSlnxAsync(resolvedSolutionPath);
                notes.Add("The selected .slnx file was parsed through XML project discovery.");
            }
            else if (isSolutionFile)
            {
                parsedSolution = await SolutionDiscovery.ParseSlnAsync(resolvedSolutionPath);
            }
            else
            {
                parsedSolution = await SolutionDiscovery.ParseProjectClosureAsync(resolvedSolutionPath, maxProjects, warnings);
                notes.Add("The selected project file was parsed by following ProjectReference edges without a .sln or .slnx file.");
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
                    loadedProjects.Add(await ProjectLoader.LoadProjectAsync(project, parsedSolution.SolutionDirectoryPath, collector));
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
                project => ProjectPaths.NormalizePathKey(project.FullPath),
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
            var diagram = GraphBuilder.Build(
                selectedGraphNodes,
                visibleEdges,
                projectCycleResult.CycleNodeKeys,
                projectCycleResult.CycleEdgeKeys);

            var namespaceGraph = await NamespaceAnalyzer.AnalyzeAsync(loadedProjects, maxProjects, maxEdges, collector);
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
}