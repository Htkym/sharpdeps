using SharpDeps.Analysis.Quick;
using Xunit;

namespace SharpDeps.Analysis.Tests;

/// <summary>
/// Guards the boundary that makes the SDK-free Quick path possible: the Quick
/// analyzer must not pull MSBuild or the Roslyn workspace assemblies, because those
/// require an SDK and would break environments that only have a .NET runtime.
/// </summary>
public sealed class QuickDependencyBoundaryTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.Build",
        "Microsoft.CodeAnalysis.Workspaces",
        "Microsoft.CodeAnalysis.CSharp.Workspaces",
        "Microsoft.CodeAnalysis.Workspaces.MSBuild"
    ];

    [Fact]
    public void QuickAnalyzerDoesNotReferenceMsBuildOrWorkspaces()
    {
        var references = typeof(QuickAnalyzer)
            .Assembly.GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToArray();

        var forbidden = references
            .Where(name => ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(forbidden);
    }

    [Fact]
    public void QuickAnalyzerStillUsesSyntaxOnlyRoslyn()
    {
        var references = typeof(QuickAnalyzer)
            .Assembly.GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.Contains(references, name => name == "Microsoft.CodeAnalysis.CSharp");
        Assert.DoesNotContain(references, name => name == "Microsoft.CodeAnalysis.Workspaces.MSBuild");
    }

    [Fact]
    public void PublishedQuickHostDoesNotShipMsBuildAssemblies()
    {
        var publishDirectory = Environment.GetEnvironmentVariable("SHARPDEPS_QUICK_HOST_OUTPUT")
            ?? Path.Combine(TestPaths.RepositoryRoot, "analyzer", "bin", "quick");
        if (!Directory.Exists(publishDirectory))
        {
            return;
        }

        var msbuildAssemblies = Directory
            .EnumerateFiles(publishDirectory, "Microsoft.Build*.dll", SearchOption.AllDirectories)
            .ToArray();

        Assert.Empty(msbuildAssemblies);
        Assert.True(File.Exists(Path.Combine(publishDirectory, "code-map.dll")));
    }

}
