using SharpDeps.Analysis.Roslyn;
using Xunit;

namespace SharpDeps.Analysis.Tests;

public sealed class ProjectVariantResolverTests
{
    [Fact]
    public void PrefersTargetFrameworkOverTargetFrameworks()
    {
        var selection = ProjectVariantResolver.SelectTargetFrameworks("net10.0", "net8.0;netstandard2.0");

        Assert.Equal(["net10.0"], selection.TargetFrameworks);
        Assert.Equal("targetFramework", selection.Source);
    }

    [Fact]
    public void SplitsTargetFrameworksAndKeepsOrder()
    {
        var selection = ProjectVariantResolver.SelectTargetFrameworks(null, "netstandard2.0; net10.0 ");

        Assert.Equal(["netstandard2.0", "net10.0"], selection.TargetFrameworks);
        Assert.Equal("targetFrameworks", selection.Source);
    }

    [Fact]
    public void ReportsMissingTargetFrameworkInsteadOfInventingOne()
    {
        var selection = ProjectVariantResolver.SelectTargetFrameworks(null, null);

        Assert.Empty(selection.TargetFrameworks);
        Assert.Equal("notSpecified", selection.Source);
    }

    [Fact]
    public void VariantKeysNormalizeSeparatorsAndCase()
    {
        var windows = ProjectVariantResolver.VariantKey(@"C:\repo\A\A.csproj", "NET10.0", "Debug", null);
        var unix = ProjectVariantResolver.VariantKey("C:/repo/A/A.csproj", "net10.0", "debug", null);

        Assert.Equal(windows, unix);
    }

    [Fact]
    public void VariantKeysSeparateTfmAndConfiguration()
    {
        var debug = ProjectVariantResolver.VariantKey("C:/repo/A/A.csproj", "net10.0", "Debug", null);
        var release = ProjectVariantResolver.VariantKey("C:/repo/A/A.csproj", "net10.0", "Release", null);
        var otherTfm = ProjectVariantResolver.VariantKey("C:/repo/A/A.csproj", "net8.0", "Debug", null);

        Assert.NotEqual(debug, release);
        Assert.NotEqual(debug, otherTfm);
    }

    [Theory]
    [InlineData("net10.0", "net10.0", "exact")]
    [InlineData("net10.0", "net8.0", "compatible")]
    [InlineData("net8.0", "netstandard2.0", "compatible")]
    [InlineData("netstandard2.0", "netstandard2.0", "exact")]
    public void ResolvesCompatibleTargets(string source, string candidate, string expectedResolution)
    {
        var resolved = ProjectVariantResolver.ResolveReferenceTarget(
            source,
            [candidate],
            out var resolution,
            out var note);

        Assert.Equal(candidate, resolved);
        Assert.Equal(expectedResolution, resolution);
        Assert.Null(note);
    }

    [Theory]
    [InlineData("net8.0", "net10.0")]
    [InlineData("net8.0", "net9.0")]
    [InlineData("net10.0", "net48")]
    [InlineData("netstandard2.0", "net10.0")]
    public void RefusesIncompatibleTargets(string source, string candidate)
    {
        var resolved = ProjectVariantResolver.ResolveReferenceTarget(
            source,
            [candidate],
            out var resolution,
            out var note);

        Assert.Null(resolved);
        Assert.Equal("unresolved", resolution);
        Assert.NotNull(note);
    }

    [Fact]
    public void ReportsAmbiguousCompatibleChoicesInsteadOfSilentlyPicking()
    {
        var resolved = ProjectVariantResolver.ResolveReferenceTarget(
            "net10.0",
            ["netstandard2.0", "netstandard2.1"],
            out var resolution,
            out var note);

        Assert.Equal("netstandard2.1", resolved);
        Assert.Equal("compatibleFirst", resolution);
        Assert.NotNull(note);
        Assert.Contains("Several compatible TFMs", note, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsMissingCandidates()
    {
        var resolved = ProjectVariantResolver.ResolveReferenceTarget("net10.0", [], out var resolution, out var note);

        Assert.Null(resolved);
        Assert.Equal("unresolved", resolution);
        Assert.Contains("no target framework", note, StringComparison.OrdinalIgnoreCase);
    }
}
