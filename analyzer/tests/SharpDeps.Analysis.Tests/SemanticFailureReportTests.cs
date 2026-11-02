using SharpDeps.Analysis.Roslyn;
using SharpDeps.Analysis.Roslyn.Symbols;
using Xunit;

namespace SharpDeps.Analysis.Tests;

/// <summary>
/// A failed semantic host run keeps the probe report: the failure is added as a
/// structured diagnostic, and the diagnostics read before the failure are not lost.
/// </summary>
public sealed class SemanticFailureReportTests
{
    [Fact]
    public void UnidentifiedGeneratedDocumentKeepsTheLoadedDiagnosticsInTheReport()
    {
        var registry = new SourceDocumentRegistry("root", "root");
        var error = Assert.Throws<GeneratedDocumentRegistrationException>(() => registry.RegisterGenerated(
            "variant", "Sample", " ", "Generated.g.cs", "C:/work/obj/Generated.g.cs", new string('a', 64), 10));

        var warning = new ProbeDiagnostic("Warning", "workspace warning");
        var limitation = new ProbeLimitation("semantic.compilationErrors", "1 error(s).", 1);
        var loaded = Report([warning], [limitation]);

        var failed = loaded.WithFailure(error.Code, error.Message);

        Assert.Equal(GeneratedDocumentRegistrationException.GeneratorUnidentified, error.Code);
        Assert.Contains(warning, failed.Diagnostics);
        Assert.Contains(limitation, failed.Limitations);
        var added = Assert.Single(failed.Diagnostics, diagnostic => diagnostic.Kind == error.Code);
        Assert.Equal(error.Message, added.Message);
        Assert.Contains(failed.Limitations, entry => entry.Code == error.Code);
        // The loaded report itself is unchanged, so nothing read before the failure is dropped.
        Assert.Single(loaded.Diagnostics);
        Assert.Single(loaded.Limitations);
    }

    private static SemanticProbeReport Report(
        IReadOnlyList<ProbeDiagnostic> diagnostics,
        IReadOnlyList<ProbeLimitation> limitations)
        => new(
            SchemaVersion: "semantic-probe-1",
            CreatedAt: DateTimeOffset.UnixEpoch.ToString("O"),
            Environment: new SemanticEnvironmentInfo(null, null, null, null, null, null, null, null, null, []),
            TargetPath: "Sample.sln",
            Configuration: "Debug",
            Profile: new SemanticProfileInfo("Debug", null, string.Empty, []),
            Variants: [],
            References: [],
            Diagnostics: diagnostics,
            Limitations: limitations,
            Coverage: new ProbeCoverage(1, 1, 1, 0, 0, 0));
}
