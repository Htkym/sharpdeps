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
        Assert.Equal(4, slnx.References.Count);
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
        Assert.Empty(report.Limitations);
        Assert.Equal(0, report.Coverage.Unresolved);
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

    private static Task<SemanticProbeReport> LoadAsync(string relativeTarget, string configuration = "Debug")
    {
        var target = Path.Combine(RepositoryRoot, "tests", "fixtures", relativeTarget.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(target), $"Fixture not found: {target}");
        return SemanticLoader.LoadAsync(new SemanticLoadOptions(target, configuration));
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
