namespace SharpDeps.Analysis.Quick;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Graph;

public static class NamespaceAnalyzer
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
        int maxEdges,
        QuickSourceIndexCollector? collector = null)
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
                collector?.AddSkip(project.FullPath, "projectDirectoryMissing");
                continue;
            }

            foreach (var file in EnumerateCSharpFiles(projectDirectory))
            {
                if (parsedFiles >= MaxSourceFiles)
                {
                    truncatedFiles = true;
                    collector?.AddSkip(file, "sourceFileCapReached");
                    break;
                }

                byte[] bytes;
                string text;
                try
                {
                    if (new FileInfo(file).Length > MaxSourceFileBytes)
                    {
                        collector?.AddSkip(file, "sourceFileTooLarge");
                        continue;
                    }

                    bytes = await File.ReadAllBytesAsync(file);
                    text = DecodeSource(bytes);
                }
                catch
                {
                    collector?.AddSkip(file, "sourceFileUnreadable");
                    continue;
                }

                SyntaxNode root;
                try
                {
                    root = CSharpSyntaxTree.ParseText(text).GetRoot();
                }
                catch
                {
                    collector?.AddSkip(file, "sourceFileUnparsable");
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

                if (collector is not null)
                {
                    CollectIndexData(collector, project.Name, file, bytes, root, fileNamespaces);
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

        var diagram = GraphBuilder.Build(
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

    /// <summary>Decodes source bytes, honouring a byte order mark.</summary>
    private static string DecodeSource(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Records the document, its namespace declarations, and its using directives
    /// (with positions) for the v2 model. Runs only when a collector is attached, so
    /// the v1 path is untouched.
    /// </summary>
    private static void CollectIndexData(
        QuickSourceIndexCollector collector,
        string projectName,
        string fullPath,
        byte[] bytes,
        SyntaxNode root,
        IReadOnlyList<string> fileNamespaces)
    {
        var document = collector.AddDocument(
            fullPath,
            bytes,
            QuickSourceIndexCollector.ComputeContentHash(bytes));

        foreach (var declaration in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            var name = FullNamespaceName(declaration);
            if (name.Length == 0)
            {
                continue;
            }

            collector.AddDeclaration(
                new QuickNamespaceDeclaration(projectName, document.Id, name, ToQuickSpan(declaration.GetLocation())));
        }

        var usings = new List<QuickUsing>();
        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (directive.Alias is not null)
            {
                continue;
            }

            var target = ResolveUsingNamespace(directive);
            if (target is null)
            {
                continue;
            }

            var span = ToQuickSpan(directive.GetLocation());
            if (directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
            {
                collector.AddGlobalUsing(new QuickGlobalUsing(projectName, target, document.Id, span));
                continue;
            }

            usings.Add(new QuickUsing(target, span));
        }

        collector.AddFileUsage(new QuickFileUsage(projectName, document.Id, fileNamespaces, usings));
    }

    private static QuickSpan ToQuickSpan(Location location)
    {
        var span = location.GetLineSpan();
        return new QuickSpan(
            location.SourceSpan.Start,
            location.SourceSpan.Length,
            span.StartLinePosition.Line,
            span.StartLinePosition.Character,
            span.EndLinePosition.Line,
            span.EndLinePosition.Character);
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
