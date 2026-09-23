using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace SharpDeps.Analysis.Roslyn;

public sealed record SemanticLoadOptions(
    string TargetPath,
    string Configuration = "Debug",
    string? Platform = null,
    int TimeoutSeconds = 180);

/// <summary>
/// Loads a solution or project with MSBuildWorkspace and reports what was actually
/// read: per-variant TFM, documents, generated documents, and how each
/// ProjectReference resolved to a target variant.
/// </summary>
/// <remarks>
/// Requires <see cref="SemanticEnvironment.TryRegister"/> to have succeeded
/// first. Loading evaluates MSBuild project files and may run build logic, so the
/// caller is responsible for the workspace-trust decision.
/// </remarks>
public static class SemanticLoader
{
    /// <summary>Document paths are evidence, but a report must stay small.</summary>
    private const int MaxDocumentPaths = 100;

    public static async Task<SemanticProbeReport> LoadAsync(
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

        var stopwatch = Stopwatch.StartNew();
        Solution solution;
        try
        {
            solution = await OpenAsync(workspace, targetPath, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            limitations.Add(new ProbeLimitation("semantic.loadFailed", error.Message, null));
            return new SemanticProbeReport(
                SchemaVersion: "semantic-probe-1",
                CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
                Environment: SemanticEnvironment.Describe(Path.GetDirectoryName(targetPath) ?? targetPath),
                TargetPath: targetPath,
                Configuration: options.Configuration,
                Variants: [],
                References: [],
                Diagnostics: diagnostics,
                Limitations: limitations,
                Coverage: new ProbeCoverage(0, 0, 0, 0, 0, 0));
        }

        var variants = new List<ProjectVariantInfo>();
        var references = new List<ReferenceEdgeInfo>();
        var unresolvedReferences = 0;
        var generatedFailures = 0;

        foreach (var project in solution.Projects.OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetFramework = TryGetTargetFramework(project);
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
            var variantKey = ProjectVariantResolver.VariantKey(
                project.FilePath ?? project.Name,
                targetFramework,
                options.Configuration,
                options.Platform);

            var generatedCount = 0;
            string? generatedError = null;
            try
            {
                var generatedDocuments = await project.GetSourceGeneratedDocumentsAsync(cancellationToken);
                generatedCount = generatedDocuments.Count();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                generatedError = error.Message;
                generatedFailures++;
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

            var compilationObtained = false;
            var errorDiagnostics = 0;
            var sampleErrors = new List<string>();
            try
            {
                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation is not null)
                {
                    compilationObtained = true;
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
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                limitations.Add(
                    new ProbeLimitation(
                        "semantic.compilationUnavailable",
                        $"Compilation could not be produced for '{project.Name}': {error.Message}",
                        1));
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
        var analyzed = loaded;

        return new SemanticProbeReport(
            SchemaVersion: "semantic-probe-1",
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            Environment: SemanticEnvironment.Describe(Path.GetDirectoryName(targetPath) ?? targetPath),
            TargetPath: targetPath,
            Configuration: options.Configuration,
            Variants: variants,
            References: references,
            Diagnostics: diagnostics,
            Limitations: limitations,
            Coverage: new ProbeCoverage(
                Discovered: variants.Count,
                Loaded: loaded,
                Analyzed: analyzed,
                Failed: variants.Count - loaded,
                Skipped: 0,
                Unresolved: unresolvedReferences));
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

    private static string? TryGetTargetFramework(Project project)
    {
        // Roslyn does not expose the evaluated TFM on Project. Derive it from data
        // MSBuild gave Roslyn: the TFM-specific preprocessor symbols first, then the
        // output path segment. Return null when neither is available so the caller
        // records an unresolved reference instead of guessing.
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
        var match = System.Text.RegularExpressions.Regex.Match(
            symbol,
            "^NET(?<major>\\d+)_(?<minor>\\d+)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (match.Success)
        {
            return $"net{match.Groups["major"].Value}.{match.Groups["minor"].Value}";
        }

        var standard = System.Text.RegularExpressions.Regex.Match(
            symbol,
            "^NETSTANDARD(?<major>\\d+)_(?<minor>\\d+)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
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
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                value,
                "^(net\\d+\\.\\d+|netstandard\\d+\\.\\d+|netcoreapp\\d+\\.\\d+)$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
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
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    segment,
                    "^(net\\d+\\.\\d+|netstandard\\d+\\.\\d+|netcoreapp\\d+\\.\\d+)$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                return segment;
            }
        }

        return null;
    }
}
