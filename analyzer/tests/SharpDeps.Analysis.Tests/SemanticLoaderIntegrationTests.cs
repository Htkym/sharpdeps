using SharpDeps.Analysis.Roslyn;
using Xunit;

namespace SharpDeps.Analysis.Tests;

/// <summary>
/// Integration tests that load real solutions with MSBuildWorkspace. They need a
/// .NET SDK; without one the loader reports a precondition failure instead of a
/// partial result.
/// </summary>
[Collection("semantic")]
public sealed class SemanticLoaderIntegrationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    static SemanticLoaderIntegrationTests()
    {
        Assert.True(
            SemanticEnvironment.TryRegister(RepositoryRoot, out var reason),
            reason ?? "MSBuild registration failed.");
    }

    [Fact]
    public async Task LoadsMultiTargetedVariantsSeparately()
    {
        var report = await LoadAsync("semantic-baseline/SemanticBaseline.sln");

        var domainVariants = report.Variants
            .Where(variant => variant.ProjectName.StartsWith("Domain", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, domainVariants.Length);
        Assert.Contains(domainVariants, variant => variant.TargetFramework == "net10.0");
        Assert.Contains(domainVariants, variant => variant.TargetFramework == "netstandard2.0");
        Assert.Equal(2, domainVariants.Select(variant => variant.VariantKey).Distinct().Count());
        Assert.All(domainVariants, variant => Assert.Equal("loaded", variant.LoadState));
    }

    [Fact]
    public async Task ResolvesReferenceToTheCompatibleVariant()
    {
        var report = await LoadAsync("semantic-baseline/SemanticBaseline.sln");

        var applicationToDomain = report.References.Single(reference =>
            reference.SourceProjectName == "Application" && reference.TargetProjectName.StartsWith("Domain", StringComparison.Ordinal));
        Assert.Equal("net8.0", applicationToDomain.SourceTargetFramework);
        Assert.Equal("netstandard2.0", applicationToDomain.TargetTargetFramework);
        Assert.Equal("compatible", applicationToDomain.Resolution);

        var infrastructureToDomain = report.References.Single(reference =>
            reference.SourceProjectName == "Infrastructure" && reference.TargetProjectName.StartsWith("Domain", StringComparison.Ordinal));
        Assert.Equal("net10.0", infrastructureToDomain.TargetTargetFramework);
        Assert.Equal("exact", infrastructureToDomain.Resolution);

        Assert.Equal(0, report.Coverage.Unresolved);
    }

    [Fact]
    public async Task EvaluatesConditionalProjectReferencesPerConfiguration()
    {
        var debug = await LoadAsync("semantic-baseline/SemanticBaseline.sln", configuration: "Debug");
        var release = await LoadAsync("semantic-baseline/SemanticBaseline.sln", configuration: "Release");

        Assert.DoesNotContain(
            debug.References,
            reference => reference.SourceProjectName == "Infrastructure" && reference.TargetProjectName.StartsWith("Application", StringComparison.Ordinal));
        Assert.Contains(
            release.References,
            reference => reference.SourceProjectName == "Infrastructure" && reference.TargetProjectName.StartsWith("Application", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UsesTheEvaluatedCompileItems()
    {
        var report = await LoadAsync("semantic-baseline/SemanticBaseline.sln");

        var infrastructure = report.Variants.Single(variant => variant.ProjectName == "Infrastructure");
        Assert.True(
            infrastructure.DocumentCount > 0,
            "The evaluated compile items must include at least one document.");
        Assert.Contains(
            infrastructure.Documents,
            document => document.EndsWith("shared/Shared.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            infrastructure.Documents,
            document => document.EndsWith("Removed.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            infrastructure.Documents,
            document => document.EndsWith("OrderStore.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RecordsTheEnvironmentThatWasUsed()
    {
        var report = await LoadAsync("semantic-baseline/SemanticBaseline.sln");

        Assert.False(string.IsNullOrWhiteSpace(report.Environment.SdkVersion));
        Assert.False(string.IsNullOrWhiteSpace(report.Environment.MsBuildVersion));
        Assert.False(string.IsNullOrWhiteSpace(report.Environment.RoslynVersion));
        Assert.False(string.IsNullOrWhiteSpace(report.Environment.HostRuntimeVersion));
        Assert.EndsWith("global.json", report.Environment.GlobalJsonPath ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("10.0.300", report.Environment.GlobalJsonSdkVersion);
    }

    [Fact]
    public async Task ReportsGeneratedDocumentsWithoutFailing()
    {
        var report = await LoadAsync("semantic-baseline/SemanticBaseline.sln");

        Assert.All(
            report.Variants,
            variant => Assert.Null(variant.GeneratedDocumentError));
        Assert.DoesNotContain(
            report.Limitations,
            limitation => limitation.Code == "semantic.generatedDocumentsUnavailable");
    }

    [Fact]
    public async Task LoadsSlnxAndSingleProjectTargets()
    {
        var slnx = await LoadAsync("semantic-baseline/SemanticBaseline.slnx");
        Assert.Equal(5, slnx.Variants.Count);
        // Direct ProjectReference items only: Application -> Domain,
        // Infrastructure -> Domain, Infrastructure.Tests -> Infrastructure.
        Assert.Equal(3, slnx.References.Count);
        Assert.Equal(0, slnx.Coverage.Unresolved);

        var singleProject = await LoadAsync("semantic-baseline/src/Infrastructure/Infrastructure.csproj");
        Assert.Contains(singleProject.Variants, variant => variant.ProjectName == "Infrastructure");
        // Project-scoped loading pulls in the referenced projects as source too.
        Assert.Contains(
            singleProject.Variants,
            variant => variant.ProjectName.StartsWith("Domain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BaselineFixtureLoadsWithoutCompilationErrors()
    {
        var report = await LoadAsync("semantic-baseline/SemanticBaseline.sln");

        Assert.All(report.Variants, variant => Assert.Equal(0, variant.ErrorDiagnosticCount));
        Assert.Empty(report.Diagnostics);
        Assert.Equal(0, report.Coverage.Unresolved);

        // The transitive-reference note is informational: it describes an adjustment
        // the loader made, not a failure of the target.
        Assert.All(
            report.Limitations,
            limitation => Assert.Equal("semantic.transitiveReferencesAdded", limitation.Code));
    }

    [Fact]
    public async Task AddsTheTransitiveReferencesTheCompilerWouldSee()
    {
        var report = await LoadAsync("semantic-baseline/SemanticBaseline.sln");

        var testProject = report.Variants.Single(variant => variant.ProjectName == "Infrastructure.Tests");
        Assert.True(
            testProject.AddedTransitiveReferences >= 1,
            "The test project only references Infrastructure, so Domain has to be added transitively.");
        Assert.Equal(0, testProject.ErrorDiagnosticCount);
        Assert.Contains(
            report.Limitations,
            limitation => limitation.Code == "semantic.transitiveReferencesAdded");
    }

    [Fact]
    public async Task DoesNotModifyTheAnalyzedSources()
    {
        var fixtureRoot = Path.Combine(TestPaths.RepositoryRoot, "tests", "fixtures", "semantic-baseline");
        var before = SnapshotSourceFiles(fixtureRoot);

        await LoadAsync("semantic-baseline/SemanticBaseline.sln");

        var after = SnapshotSourceFiles(fixtureRoot);
        Assert.Equal(before, after);
    }

    private static Dictionary<string, string> SnapshotSourceFiles(string root)
        => Directory
            .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(
                file => Path.GetRelativePath(root, file),
                file => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))),
                StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task ReportsTheProfileItActuallyUsed()
    {
        var debug = await LoadAsync("semantic-baseline/SemanticBaseline.sln", configuration: "Debug");
        var release = await LoadAsync("semantic-baseline/SemanticBaseline.sln", configuration: "Release");

        Assert.Equal("Debug", debug.Profile.Configuration);
        Assert.Null(debug.Profile.Platform);
        Assert.Matches("^[0-9a-f]{16}$", debug.Profile.ProfileHash);
        Assert.NotEqual(debug.Profile.ProfileHash, release.Profile.ProfileHash);

        var domainVariants = debug.Profile.Variants
            .Where(variant => variant.ProjectName.StartsWith("Domain", StringComparison.Ordinal))
            .Select(variant => variant.TargetFramework)
            .OrderBy(framework => framework, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "net10.0", "netstandard2.0" }, domainVariants);
    }

    [Fact]
    public async Task ReportsAnUnrestoredProjectWithoutThrowing()
    {
        var report = await LoadAsync("semantic-unrestored/SemanticUnrestored.sln");

        var unrestored = report.Variants.Single(variant => variant.ProjectName == "Unrestored");
        Assert.True(unrestored.CompilationObtained);
        Assert.True(
            unrestored.ErrorDiagnosticCount > 0,
            "Missing package references must surface as compilation errors, not as a clean result.");
        Assert.Contains(
            report.Limitations,
            limitation => limitation.Code == "semantic.compilationErrors");
        Assert.Contains(
            report.Limitations,
            limitation => limitation.Code == "semantic.compilationErrors"
                && limitation.Message.Contains("CS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailsCleanlyForAMissingTarget()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => SemanticLoader.LoadAsync(new SemanticLoadOptions("does-not-exist.sln")));
    }

    private static async Task<SemanticProbeReport> LoadAsync(string relativeTarget, string configuration = "Debug")
    {
        var target = Path.Combine(RepositoryRoot, "tests", "fixtures", relativeTarget.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(target), $"Fixture not found: {target}");
        var load = await SemanticLoader.LoadAsync(new SemanticLoadOptions(target, configuration));
        return load.Report;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures", "semantic-baseline")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located from the test output directory.");
    }
}
