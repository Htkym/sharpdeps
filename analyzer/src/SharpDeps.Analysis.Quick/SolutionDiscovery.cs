namespace SharpDeps.Analysis.Quick;

using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Paths;
using SharpDeps.Analysis.Core.Xml;


/// <summary>Finds the projects of a .sln/.slnx/.csproj target without evaluating MSBuild.</summary>
public static class SolutionDiscovery
{
        private static readonly Regex SlnProjectRegex = new(
            "^Project\\(\"(?<typeGuid>\\{[^\\\"]+\\})\"\\)\\s*=\\s*\"(?<name>[^\"]+)\"\\s*,\\s*\"(?<path>[^\"]+)\"\\s*,\\s*\"(?<projectGuid>\\{[^\\\"]+\\})\"",
            RegexOptions.Compiled);

        private static readonly Regex NestedProjectRegex = new(
            "^\\s*(?<child>\\{[^\\}]+\\})\\s*=\\s*(?<parent>\\{[^\\}]+\\})\\s*$",
            RegexOptions.Compiled);

        public static async Task<ParsedSolution> ParseSlnAsync(string solutionPath)
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
                .Where(entry => !entry.IsSolutionFolder && ProjectPaths.LooksLikeProjectPath(entry.RelativePath))
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

        public static async Task<ParsedSolution> ParseSlnxAsync(string solutionPath)
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
                    var projectPath = ProjectXml.GetAttributeOrChildValue(element, "Path")
                        ?? ProjectXml.GetAttributeOrChildValue(element, "Include")
                        ?? ProjectXml.GetAttributeOrChildValue(element, "FilePath");
                    if (!ProjectPaths.LooksLikeProjectPath(projectPath))
                    {
                        return null;
                    }

                    var resolvedProjectPath = projectPath!;
                    var fullPath = Path.GetFullPath(Path.Combine(solutionDirectoryPath, resolvedProjectPath));
                    var groupPath = BuildSlnxGroupPath(element, solutionDirectoryPath, resolvedProjectPath);
                    return new SolutionProjectEntry(
                        ProjectXml.GetAttributeOrChildValue(element, "Guid")
                            ?? ProjectXml.GetAttributeOrChildValue(element, "Id")
                            ?? fullPath,
                        ProjectXml.GetAttributeOrChildValue(element, "Name")
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

        public static async Task<ParsedSolution> ParseProjectClosureAsync(string projectPath, int maxProjects, List<string> warnings)
        {
            var resolvedProjectPath = Path.GetFullPath(projectPath);
            var rootDirectoryPath = Path.GetDirectoryName(resolvedProjectPath)
                ?? throw new InvalidOperationException($"Could not determine the project directory for {resolvedProjectPath}");
            var rootProjectName = Path.GetFileNameWithoutExtension(resolvedProjectPath);
            var traversalLimit = Math.Max(1, maxProjects);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queued = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            var projectEntries = new Dictionary<string, SolutionProjectEntry>(StringComparer.Ordinal);
            var traversalCapped = false;

            var rootLookupKey = ProjectPaths.NormalizePathKey(resolvedProjectPath);
            pending.Enqueue(resolvedProjectPath);
            queued.Add(rootLookupKey);

            while (pending.Count > 0)
            {
                var currentProjectPath = pending.Dequeue();
                var currentLookupKey = ProjectPaths.NormalizePathKey(currentProjectPath);
                if (!visited.Add(currentLookupKey))
                {
                    continue;
                }

                var projectDirectoryPath = Path.GetDirectoryName(currentProjectPath)
                    ?? throw new InvalidOperationException($"Could not determine the project directory for {currentProjectPath}");

                XElement root;
                try
                {
                    var projectText = await File.ReadAllTextAsync(currentProjectPath);
                    var document = XDocument.Parse(projectText, LoadOptions.PreserveWhitespace);
                    root = document.Root ?? throw new InvalidOperationException("Project XML is empty.");
                }
                catch (Exception error)
                {
                    warnings.Add($"Failed to parse project '{Path.GetFileNameWithoutExtension(currentProjectPath)}': {error.Message}");
                    continue;
                }

                projectEntries[currentLookupKey] = new SolutionProjectEntry(
                    currentProjectPath,
                    Path.GetFileNameWithoutExtension(currentProjectPath),
                    currentProjectPath,
                    Path.GetRelativePath(rootDirectoryPath, currentProjectPath),
                    string.Empty);

                foreach (var projectReference in ProjectLoader.ReadProjectReferences(root, projectDirectoryPath))
                {
                    if (!ProjectPaths.LooksLikeProjectPath(projectReference.FullPath))
                    {
                        continue;
                    }

                    if (!File.Exists(projectReference.FullPath))
                    {
                        warnings.Add($"Referenced project was not found and was skipped: {projectReference.FullPath}");
                        continue;
                    }

                    if (queued.Contains(projectReference.LookupKey))
                    {
                        continue;
                    }

                    if (queued.Count >= traversalLimit)
                    {
                        traversalCapped = true;
                        continue;
                    }

                    if (queued.Add(projectReference.LookupKey))
                    {
                        pending.Enqueue(projectReference.FullPath);
                    }
                }
            }

            if (traversalCapped)
            {
                warnings.Add($"Project-scoped traversal was limited to the first {traversalLimit} project(s). Increase --max-projects to include more.");
            }

            return new ParsedSolution(
                resolvedProjectPath,
                $"{rootProjectName} (project scope)",
                rootDirectoryPath,
                projectEntries.Values
                    .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                    .ToArray());
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
                .Select(element => ProjectXml.GetAttributeOrChildValue(element, "Name"))
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

        private static bool IsLikelySolutionFolderElement(XElement element)
        {
            var localName = element.Name.LocalName;
            if (localName.Contains("Folder", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var path = ProjectXml.GetAttributeOrChildValue(element, "Path")
                ?? ProjectXml.GetAttributeOrChildValue(element, "Include")
                ?? ProjectXml.GetAttributeOrChildValue(element, "FilePath");
            return string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(ProjectXml.GetAttributeOrChildValue(element, "Name"));
        }
}