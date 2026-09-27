using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using SharpDeps.Analysis.Core.Identity;

namespace SharpDeps.Analysis.Roslyn;

public sealed record SemanticLoadOptions(
    string TargetPath,
    string Configuration = "Debug",
    string? Platform = null,
    int TimeoutSeconds = 180,
    IReadOnlyList<ProjectVariantSelection>? ProjectVariants = null);

public sealed record ProjectVariantSelection(string ProjectLogicalId, string TargetFramework);

/// <summary>
/// Result of a semantic load: the probe report, the per-variant compilations the
/// collectors (SD-008/SD-010) analyze, and the "assembly name → defining variant"
/// map the symbol resolver needs. Compilations are keyed by variant key.
/// </summary>
public sealed record SemanticLoadResult(
    SemanticProbeReport Report,
    IReadOnlyDictionary<string, Compilation> Compilations,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> DefiningVariantByAssembly,
    IReadOnlyList<GeneratedSourceDocumentInfo> GeneratedDocuments);

/// <summary>
/// Loads a solution or project with MSBuildWorkspace and reports what was actually
/// read: per-variant TFM, documents, generated documents, compilation state, and how
/// each ProjectReference resolved to a target variant.
/// </summary>
/// <remarks>
/// Requires <see cref="SemanticEnvironment.TryRegister"/> to have succeeded first.
/// Loading evaluates MSBuild project files and runs design-time builds, so it writes
/// to the projects' obj directories and may run build logic; the caller is
/// responsible for the workspace-trust decision.
///
/// The load never writes to project files: transitive project references are added to
/// the in-memory compilations only. (MSBuildWorkspace.TryApplyChanges persists project
/// changes to disk, so it is not used.)
/// </remarks>
public static class SemanticLoader
{
    /// <summary>Document paths are evidence, but a report must stay small.</summary>
    private const int MaxDocumentPaths = 100;

    /// <summary>
    /// Generated content is retained with the analysis result; one document above this
    /// stays in the report as a hash only, and the limitation says so.
    /// </summary>
    private const int MaxGeneratedDocumentChars = 1 << 20;

    /// <summary>
    /// Compiler errors the user can act on. Restore and language-version problems are
    /// turned into named limitations instead of a bare error count.
    /// </summary>
    private static readonly Dictionary<string, string> ErrorHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CS0246"] = "resolve",
        ["CS0234"] = "resolve",
        ["CS1069"] = "resolve",
        ["CS1705"] = "resolve",
        ["CS8630"] = "language",
        ["CS8400"] = "language",
        ["CS9058"] = "language"
    };

    public static async Task<SemanticLoadResult> LoadAsync(
        SemanticLoadOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!Microsoft.Build.Locator.MSBuildLocator.IsRegistered)
        {
            throw new InvalidOperationException(
                "MSBuild is not registered. Call SemanticEnvironment.TryRegister before loading.");
        }

        var targetPath = Path.GetFullPath(options.TargetPath);
        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException($"Analysis target was not found: {targetPath}", targetPath);
        }

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = options.Configuration
        };
        if (!string.IsNullOrWhiteSpace(options.Platform))
        {
            properties["Platform"] = options.Platform;
        }

        var diagnostics = new List<ProbeDiagnostic>();
        var limitations = new List<ProbeLimitation>();

        using var workspace = MSBuildWorkspace.Create(properties);
        workspace.RegisterWorkspaceFailedHandler(args =>
            diagnostics.Add(new ProbeDiagnostic(args.Diagnostic.Kind.ToString(), args.Diagnostic.Message)));

        Solution solution;
        try
        {
            solution = await OpenAsync(workspace, targetPath, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            limitations.Add(new ProbeLimitation("semantic.loadFailed", error.Message, null));
            return new SemanticLoadResult(
                new SemanticProbeReport(
                    SchemaVersion: "semantic-probe-1",
                    CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
                    Environment: SemanticEnvironment.Describe(Path.GetDirectoryName(targetPath) ?? targetPath),
                    TargetPath: targetPath,
                    Configuration: options.Configuration,
                    Profile: new SemanticProfileInfo(options.Configuration, options.Platform, string.Empty, []),
                    Variants: [],
                    References: [],
                    Diagnostics: diagnostics,
                    Limitations: limitations,
                    Coverage: new ProbeCoverage(0, 0, 0, 0, 0, 0)),
                new Dictionary<string, Compilation>(),
                new Dictionary<string, IReadOnlyDictionary<string, string>>(),
                []);
        }

        if (options.ProjectVariants is { Count: > 0 })
        {
            var root = Path.GetDirectoryName(targetPath)!;
            var rootId = Identity.WorkspaceRootId(root);
            var requested = options.ProjectVariants.ToDictionary(item => item.ProjectLogicalId, item => item.TargetFramework);
            string LogicalId(Project project) => Identity.ProjectLogicalId(rootId, Path.GetRelativePath(root, project.FilePath!));
            var csharp = solution.Projects.Where(project => project.Language == LanguageNames.CSharp && project.FilePath is not null).ToArray();
            foreach (var request in requested)
            {
                if (!csharp.Any(project => LogicalId(project) == request.Key && TryGetTargetFramework(project) == request.Value))
                    throw new InvalidOperationException($"The requested project/TFM is not available: {request.Key} / {request.Value}.");
            }
            var selected = csharp.Where(project => !requested.TryGetValue(LogicalId(project), out var tfm)
                || TryGetTargetFramework(project) == tfm).Select(project => project.Id).ToHashSet();
            foreach (var project in csharp.Where(project => selected.Contains(project.Id)))
            {
                if (project.ProjectReferences.Any(reference => !selected.Contains(reference.ProjectId)
                    && csharp.Any(candidate => candidate.Id == reference.ProjectId)))
                    throw new InvalidOperationException($"The selected TFMs conflict with evaluated ProjectReferences in '{project.Name}'. Choose compatible TFMs or Automatic.");
            }
            foreach (var project in csharp.Where(project => !selected.Contains(project.Id))) solution = solution.RemoveProject(project.Id);
        }

        var addedReferences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var compilations = await BuildCompilationsAsync(
            solution,
            options,
            addedReferences,
            limitations,
            cancellationToken);

        var variants = new List<ProjectVariantInfo>();
        var references = new List<ReferenceEdgeInfo>();
        var unresolvedReferences = 0;
        var errorHints = new List<(string Project, string Hint, string Id, string Message)>();
        var loadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var generatedDocuments = new List<GeneratedSourceDocumentInfo>();

        foreach (var project in solution.Projects.OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetFramework = TryGetTargetFramework(project);
            var variantKey = VariantKeyFor(project, options);

            if (project.FilePath is not null)
            {
                loadedPaths.Add(Path.GetFullPath(project.FilePath));
            }

            if (targetFramework is not null
                && TryReadNameSuffix(project.Name, out var nameTargetFramework)
                && !string.Equals(nameTargetFramework, targetFramework, StringComparison.OrdinalIgnoreCase))
            {
                limitations.Add(
                    new ProbeLimitation(
                        "semantic.variantNameMismatch",
                        $"The workspace name '{project.Name}' suggests TFM {nameTargetFramework}, "
                        + $"but the evaluated symbols say {targetFramework}. The variant is reported as unresolved data.",
                        1));
            }

            var generatedCount = 0;
            string? generatedError = null;
            try
            {
                var projectDocuments = await project.GetSourceGeneratedDocumentsAsync(cancellationToken);
                foreach (var generated in projectDocuments.OrderBy(
                             entry => entry.FilePath,
                             StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    generatedDocuments.Add(
                        await DescribeGeneratedDocumentAsync(
                            generated,
                            variantKey,
                            project.Name,
                            limitations,
                            cancellationToken));
                    generatedCount++;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                generatedError = error.Message;
                limitations.Add(
                    new ProbeLimitation(
                        "semantic.generatedDocumentsUnavailable",
                        $"Source-generated documents could not be read for '{project.Name}': {error.Message}",
                        1));
            }

            var documentPaths = new List<string>();
            foreach (var document in project.Documents.OrderBy(entry => entry.FilePath, StringComparer.Ordinal))
            {
                if (documentPaths.Count >= MaxDocumentPaths)
                {
                    break;
                }

                documentPaths.Add(ToProjectRelativePath(project, document.FilePath));
            }

            var compilationObtained = compilations.TryGetValue(variantKey, out var compilation);
            var errorDiagnostics = 0;
            var sampleErrors = new List<string>();
            if (compilation is not null)
            {
                foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
                {
                    if (diagnostic.Severity != DiagnosticSeverity.Error)
                    {
                        continue;
                    }

                    errorDiagnostics++;
                    if (sampleErrors.Count < 3)
                    {
                        sampleErrors.Add(diagnostic.Id + ": " + diagnostic.GetMessage());
                    }

                    if (ErrorHints.TryGetValue(diagnostic.Id, out var hint))
                    {
                        errorHints.Add((project.Name, hint, diagnostic.Id, diagnostic.GetMessage()));
                    }
                }
            }

            if (errorDiagnostics > 0)
            {
                limitations.Add(
                    new ProbeLimitation(
                        "semantic.compilationErrors",
                        $"'{project.Name}' ({targetFramework ?? "unknown TFM"}) has {errorDiagnostics} error diagnostic(s): "
                        + string.Join(" | ", sampleErrors),
                        errorDiagnostics));
            }

            variants.Add(
                new ProjectVariantInfo(
                    ProjectName: project.Name,
                    ProjectPath: project.FilePath ?? string.Empty,
                    TargetFramework: targetFramework,
                    Configuration: options.Configuration,
                    VariantKey: variantKey,
                    LoadState: "loaded",
                    DocumentCount: project.DocumentIds.Count,
                    Documents: documentPaths,
                    DocumentsTruncated: project.DocumentIds.Count > documentPaths.Count,
                    GeneratedDocumentCount: generatedCount,
                    GeneratedDocumentError: generatedError,
                    MetadataReferenceCount: project.MetadataReferences.Count,
                    AddedTransitiveReferences: addedReferences.GetValueOrDefault(variantKey),
                    CompilationObtained: compilationObtained,
                    ErrorDiagnosticCount: errorDiagnostics,
                    FailureReason: null));

            foreach (var reference in project.ProjectReferences)
            {
                var target = solution.GetProject(reference.ProjectId);
                if (target is null)
                {
                    unresolvedReferences++;
                    continue;
                }

                var targetFrameworkValue = TryGetTargetFramework(target);
                string resolution;
                string? note;
                if (targetFramework is null || targetFrameworkValue is null)
                {
                    resolution = "unresolved";
                    note = "A TFM is missing on the source or target project.";
                    unresolvedReferences++;
                }
                else
                {
                    ProjectVariantResolver.ResolveReferenceTarget(
                        targetFramework,
                        [targetFrameworkValue],
                        out resolution,
                        out note);
                    if (resolution == "unresolved")
                    {
                        unresolvedReferences++;
                    }
                }

                references.Add(
                    new ReferenceEdgeInfo(
                        SourceVariantKey: variantKey,
                        SourceProjectName: project.Name,
                        SourceTargetFramework: targetFramework ?? "(not specified)",
                        TargetProjectName: target.Name,
                        TargetProjectPath: target.FilePath ?? string.Empty,
                        TargetVariantKey: ProjectVariantResolver.VariantKey(
                            target.FilePath ?? target.Name,
                            targetFrameworkValue,
                            options.Configuration,
                            options.Platform),
                        TargetTargetFramework: targetFrameworkValue,
                        Resolution: resolution,
                        Note: note));
            }
        }

        // Transitive references live only in the compilations: report them as edges so
        // the model can distinguish them from declared ProjectReference items.
        foreach (var (variantKey, count) in addedReferences)
        {
            if (count > 0)
            {
                limitations.Add(new ProbeLimitation(
                    "semantic.transitiveReferencesAdded",
                    "MSBuildWorkspace exposes direct ProjectReferences only; the transitively referenced projects the "
                    + "compiler would see were added to the in-memory compilations (project files are not modified).",
                    count));
            }
        }

        // Projects the workspace could not load at all are reported as failed instead of
        // silently disappearing from the model.
        foreach (var (path, message) in CollectFailedProjects(diagnostics))
        {
            if (loadedPaths.Contains(path))
            {
                for (var i = 0; i < variants.Count; i++)
                    if (string.Equals(Path.GetFullPath(variants[i].ProjectPath), path, StringComparison.OrdinalIgnoreCase))
                        variants[i] = variants[i] with { LoadState = "failed", FailureReason = message };
                continue;
            }

            variants.Add(
                new ProjectVariantInfo(
                    ProjectName: Path.GetFileNameWithoutExtension(path),
                    ProjectPath: path,
                    TargetFramework: null,
                    Configuration: options.Configuration,
                    VariantKey: ProjectVariantResolver.VariantKey(path, null, options.Configuration, options.Platform),
                    LoadState: "failed",
                    DocumentCount: 0,
                    Documents: [],
                    DocumentsTruncated: false,
                    GeneratedDocumentCount: 0,
                    GeneratedDocumentError: null,
                    MetadataReferenceCount: 0,
                    AddedTransitiveReferences: 0,
                    CompilationObtained: false,
                    ErrorDiagnosticCount: 0,
                    FailureReason: message));
        }

        AddActionableDiagnostics(errorHints, limitations);

        if (solution.Projects.Any(project => project.Language != LanguageNames.CSharp))
        {
            var otherLanguages = solution.Projects
                .Where(project => project.Language != LanguageNames.CSharp)
                .Select(project => project.Language)
                .Distinct()
                .OrderBy(language => language, StringComparer.Ordinal)
                .ToArray();
            limitations.Add(
                new ProbeLimitation(
                    "semantic.nonCSharpProjects",
                    $"Non-C# projects are out of the semantic scope: {string.Join(", ", otherLanguages)}.",
                    solution.Projects.Count(project => project.Language != LanguageNames.CSharp)));
        }

        if (diagnostics.Count > 0)
        {
            limitations.Add(
                new ProbeLimitation(
                    "semantic.workspaceDiagnostics",
                    "The workspace reported failures while loading; see diagnostics for details.",
                    diagnostics.Count));
        }

        var loaded = variants.Count(variant => variant.LoadState == "loaded");
        var failed = variants.Count(variant => variant.LoadState == "failed");

        var report = new SemanticProbeReport(
            SchemaVersion: "semantic-probe-1",
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            Environment: SemanticEnvironment.Describe(Path.GetDirectoryName(targetPath) ?? targetPath),
            TargetPath: targetPath,
            Configuration: options.Configuration,
            Profile: BuildProfile(solution, options),
            Variants: variants,
            References: references,
            Diagnostics: diagnostics,
            Limitations: limitations,
            Coverage: new ProbeCoverage(
                Discovered: loaded + failed,
                Loaded: loaded,
                Analyzed: loaded,
                Failed: failed,
                Skipped: 0,
                Unresolved: unresolvedReferences));

        var referenceMap = Symbols.SemanticReferenceMap.Build(
            solution,
            project => VariantKeyFor(project, options));

        return new SemanticLoadResult(report, compilations, referenceMap, generatedDocuments);
    }

    /// <summary>
    /// Reads one generated document while the workspace is alive. The content is kept
    /// with the analysis result; a document above the retention budget is reported by
    /// hash only, so the caller can still show that it existed.
    /// </summary>
    private static async Task<GeneratedSourceDocumentInfo> DescribeGeneratedDocumentAsync(
        Document document,
        string variantKey,
        string projectName,
        List<ProbeLimitation> limitations,
        CancellationToken cancellationToken)
    {
        var hintName = string.IsNullOrWhiteSpace(document.Name)
            ? Path.GetFileName(document.FilePath ?? string.Empty)
            : document.Name;

        try
        {
            var text = await document.GetTextAsync(cancellationToken);
            var content = text.ToString();
            var byteLength = System.Text.Encoding.UTF8.GetByteCount(content);
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            var truncated = content.Length > MaxGeneratedDocumentChars;
            if (truncated)
            {
                limitations.Add(new ProbeLimitation(
                    "semantic.generatedDocumentTooLarge",
                    $"The generated document '{hintName}' in '{projectName}' exceeds the retained-content budget; "
                    + "the analysis keeps its hash only.",
                    1));
            }

            return new GeneratedSourceDocumentInfo(
                variantKey,
                projectName,
                hintName,
                document.FilePath,
                hash,
                byteLength,
                truncated ? null : content,
                truncated);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            limitations.Add(new ProbeLimitation(
                "semantic.generatedDocumentContentUnavailable",
                $"The content of the generated document '{hintName}' in '{projectName}' could not be read: "
                + error.Message,
                1));
            return new GeneratedSourceDocumentInfo(
                variantKey,
                projectName,
                hintName,
                document.FilePath,
                "unavailable",
                0,
                null,
                false);
        }
    }

    /// <summary>
    /// Builds one compilation per project variant, adding the transitively referenced
    /// projects as compilation references. Nothing is written to the project files:
    /// the additions are in-memory only.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, Compilation>> BuildCompilationsAsync(
        Solution solution,
        SemanticLoadOptions options,
        Dictionary<string, int> addedReferences,
        List<ProbeLimitation> limitations,
        CancellationToken cancellationToken)
    {
        var cache = new Dictionary<ProjectId, Compilation>();
        var visiting = new HashSet<ProjectId>();
        var reportedCycles = new HashSet<string>(StringComparer.Ordinal);
        var directReferenceIds = solution.Projects.ToDictionary(
            project => project.Id,
            project => project.ProjectReferences.Select(reference => reference.ProjectId).ToHashSet());

        async Task<Compilation?> BuildAsync(ProjectId projectId)
        {
            if (cache.TryGetValue(projectId, out var cached))
            {
                return cached;
            }

            var project = solution.GetProject(projectId);
            if (project is null || project.Language != LanguageNames.CSharp)
            {
                return null;
            }

            if (!visiting.Add(projectId))
            {
                return null;
            }

            try
            {
                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation is null)
                {
                    return null;
                }

                var direct = directReferenceIds.GetValueOrDefault(projectId, []);
                var additions = new List<MetadataReference>();
                foreach (var targetId in TransitiveClosure(solution, direct))
                {
                    if (direct.Contains(targetId))
                    {
                        continue;
                    }

                    if (visiting.Contains(targetId))
                    {
                        var key = $"{project.Name}->{solution.GetProject(targetId)?.Name}";
                        if (reportedCycles.Add(key))
                        {
                            limitations.Add(new ProbeLimitation(
                                "semantic.projectReferenceCycle",
                                $"A project reference cycle involving {project.Name} was detected while resolving transitive "
                                + "references; the cyclic reference is not added to the compilation.",
                                1));
                        }

                        continue;
                    }

                    var targetCompilation = await BuildAsync(targetId);
                    if (targetCompilation is null)
                    {
                        continue;
                    }

                    additions.Add(targetCompilation.ToMetadataReference());
                }

                if (additions.Count > 0)
                {
                    compilation = compilation.AddReferences(additions);
                    var variantKey = VariantKeyFor(project, options);
                    addedReferences[variantKey] = additions.Count;
                }

                cache[projectId] = compilation;
                return compilation;
            }
            finally
            {
                visiting.Remove(projectId);
            }
        }

        var result = new Dictionary<string, Compilation>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects.Where(entry => entry.Language == LanguageNames.CSharp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await BuildAsync(project.Id);
            if (compilation is null)
            {
                continue;
            }

            result[VariantKeyFor(project, options)] = compilation;
        }

        return result;
    }

    /// <summary>Breadth-first closure of the referenced projects, excluding the roots.</summary>
    private static IEnumerable<ProjectId> TransitiveClosure(Solution solution, IReadOnlySet<ProjectId> roots)
    {
        var queue = new Queue<ProjectId>(roots);
        var seen = new HashSet<ProjectId>(roots);
        while (queue.Count > 0)
        {
            var current = solution.GetProject(queue.Dequeue());
            if (current is null)
            {
                continue;
            }

            foreach (var reference in current.ProjectReferences)
            {
                if (!seen.Add(reference.ProjectId))
                {
                    continue;
                }

                queue.Enqueue(reference.ProjectId);
                yield return reference.ProjectId;
            }
        }
    }

    /// <summary>Maps diagnostic messages back to the project files they mention.</summary>
    private static IReadOnlyList<(string Path, string Message)> CollectFailedProjects(
        IReadOnlyList<ProbeDiagnostic> diagnostics)
    {
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pattern = new Regex(
            "[A-Za-z]:\\\\[^\"']+?\\.(?:csproj|vbproj|fsproj|vcxproj)",
            RegexOptions.CultureInvariant);

        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Kind != "Failure") continue;
            foreach (Match match in pattern.Matches(diagnostic.Message))
            {
                if (!failures.ContainsKey(match.Value))
                {
                    failures[match.Value] = diagnostic.Message;
                }
            }
        }

        return failures
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => (entry.Key, entry.Value))
            .ToArray();
    }

    /// <summary>Turns raw compiler errors into named, actionable limitations.</summary>
    private static void AddActionableDiagnostics(
        IReadOnlyList<(string Project, string Hint, string Id, string Message)> hints,
        List<ProbeLimitation> limitations)
    {
        foreach (var group in hints.GroupBy(hint => hint.Hint, StringComparer.Ordinal))
        {
            var projects = group
                .Select(entry => entry.Project)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            var ids = group
                .Select(entry => entry.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            var sample = group.First().Message;

            var (code, message) = group.Key switch
            {
                "resolve" => (
                    "semantic.referencesUnresolved",
                    $"Referenced packages or projects could not be resolved ({string.Join(", ", ids)}) for "
                    + $"{string.Join(", ", projects)}. Run `dotnet restore` for the target and analyze again; "
                    + "SharpDeps never restores automatically. Example: " + sample),
                "language" => (
                    "semantic.languageVersionUnsupported",
                    $"The projects use a C# feature the loaded compiler options reject ({string.Join(", ", ids)}) for "
                    + $"{string.Join(", ", projects)}. Check LangVersion and the SDK used for the analysis. Example: " + sample),
                _ => ("semantic.compilationHint", "Compilation diagnostics require attention. Example: " + sample)
            };

            limitations.Add(new ProbeLimitation(code, message, group.Count()));
        }
    }

    private static SemanticProfileInfo BuildProfile(Solution solution, SemanticLoadOptions options)
    {
        var variants = solution.Projects
            .Where(project => project.Language == LanguageNames.CSharp)
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .Select(project => new SemanticVariantInfo(
                project.Name,
                project.FilePath ?? string.Empty,
                TryGetTargetFramework(project)))
            .ToArray();

        // The hash covers the inputs that change the analysis: configuration, platform,
        // and the per-project TFM selection.
        var profileHash = Identity.ProfileHash(
            options.Configuration,
            options.Platform,
            variants.Select(variant => (
                ProjectLogicalId: ProjectVariantResolver.VariantKey(
                    variant.ProjectPath,
                    variant.TargetFramework,
                    options.Configuration,
                    options.Platform),
                TargetFramework: variant.TargetFramework ?? string.Empty)));

        return new SemanticProfileInfo(options.Configuration, options.Platform, profileHash, variants);
    }

    /// <summary>Variant key of a loaded project: path + TFM + configuration + platform.</summary>
    private static string VariantKeyFor(Project project, SemanticLoadOptions options)
        => ProjectVariantResolver.VariantKey(
            project.FilePath ?? project.Name,
            TryGetTargetFramework(project),
            options.Configuration,
            options.Platform);

    private static string ToProjectRelativePath(Project project, string? documentPath)
    {
        if (string.IsNullOrEmpty(documentPath))
        {
            return string.Empty;
        }

        var projectDirectory = Path.GetDirectoryName(project.FilePath ?? string.Empty);
        if (string.IsNullOrEmpty(projectDirectory))
        {
            return documentPath;
        }

        var relative = Path.GetRelativePath(projectDirectory, documentPath);
        return relative.Replace('\\', '/');
    }

    /// <summary>
    /// Roslyn does not expose the evaluated TFM on Project. Derive it from data
    /// MSBuild gave Roslyn: the TFM-specific preprocessor symbols first, then the
    /// output path segment. Return null when neither is available so the caller
    /// records an unresolved reference instead of guessing.
    /// </summary>
    private static string? TryGetTargetFramework(Project project)
    {
        // Keep platform and platform-version suffixes; NET10_0 alone cannot
        // distinguish net10.0 from net10.0-windows.
        var outputFramework = TargetFrameworkFromPath(project.OutputFilePath);
        if (outputFramework is not null)
        {
            return outputFramework;
        }
        if (TryReadNameSuffix(project.Name, out var namedFramework))
        {
            return namedFramework;
        }
        var symbols = (project.ParseOptions as Microsoft.CodeAnalysis.CSharp.CSharpParseOptions)
            ?.PreprocessorSymbolNames;
        if (symbols is not null)
        {
            foreach (var symbol in symbols.OrderBy(value => value, StringComparer.Ordinal))
            {
                var fromSymbol = TargetFrameworkFromSymbol(symbol);
                if (fromSymbol is not null)
                {
                    return fromSymbol;
                }
            }
        }

        return TargetFrameworkFromPath(project.OutputFilePath);
    }

    private static string? TargetFrameworkFromSymbol(string symbol)
    {
        var match = Regex.Match(
            symbol,
            "^NET(?<major>\\d+)_(?<minor>\\d+)$",
            RegexOptions.CultureInvariant);
        if (match.Success)
        {
            return $"net{match.Groups["major"].Value}.{match.Groups["minor"].Value}";
        }

        var standard = Regex.Match(
            symbol,
            "^NETSTANDARD(?<major>\\d+)_(?<minor>\\d+)$",
            RegexOptions.CultureInvariant);
        if (standard.Success)
        {
            return $"netstandard{standard.Groups["major"].Value}.{standard.Groups["minor"].Value}";
        }

        return null;
    }

    /// <summary>
    /// MSBuildWorkspace disambiguates multi-targeted projects by appending the TFM
    /// to the project name ("Domain(net10.0)"). It is only used as a cross-check
    /// against the evaluated symbols, never as the primary source.
    /// </summary>
    private static bool TryReadNameSuffix(string projectName, out string targetFramework)
    {
        targetFramework = string.Empty;
        var open = projectName.LastIndexOf('(');
        if (open < 0 || !projectName.EndsWith(')'))
        {
            return false;
        }

        var value = projectName[(open + 1)..^1];
        if (!Regex.IsMatch(
                value,
                "^(net\\d+\\.\\d+|netstandard\\d+\\.\\d+|netcoreapp\\d+\\.\\d+)(?:-[a-zA-Z0-9.]+)?$",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        targetFramework = value;
        return true;
    }

    private static string? TargetFrameworkFromPath(string? outputFilePath)
    {
        if (string.IsNullOrWhiteSpace(outputFilePath))
        {
            return null;
        }

        var segments = outputFilePath.Replace('\\', '/').Split('/');
        for (var index = segments.Length - 1; index >= 0; index--)
        {
            var segment = segments[index];
            if (Regex.IsMatch(
                    segment,
                    "^(net\\d+\\.\\d+|netstandard\\d+\\.\\d+|netcoreapp\\d+\\.\\d+)(?:-[a-zA-Z0-9.]+)?$",
                    RegexOptions.CultureInvariant))
            {
                return segment;
            }
        }

        return null;
    }

    /// <summary>
    /// Opens the target with the matching workspace entry point. A single project
    /// file is opened as a project; solutions (including .slnx) as a solution.
    /// </summary>
    private static Task<Solution> OpenAsync(
        MSBuildWorkspace workspace,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(targetPath);
        if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".vbproj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".fsproj", StringComparison.OrdinalIgnoreCase))
        {
            return OpenProjectAsync(workspace, targetPath, cancellationToken);
        }

        return workspace.OpenSolutionAsync(targetPath, cancellationToken: cancellationToken);
    }

    private static async Task<Solution> OpenProjectAsync(
        MSBuildWorkspace workspace,
        string projectPath,
        CancellationToken cancellationToken)
    {
        var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
        return project.Solution;
    }
}
