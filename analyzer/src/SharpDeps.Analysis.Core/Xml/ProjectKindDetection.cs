namespace SharpDeps.Analysis.Core.Xml;

using System.Xml.Linq;
using SharpDeps.Analysis.Core.Paths;

/// <summary>Classifies a project from its name, SDK, packages, and properties.</summary>
public static class ProjectKindDetection
{
    public static string FromProjectXml(string projectName, XElement root, string? evaluatedOutputType = null)
    {
        var packages = root.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => ProjectXml.GetAttributeOrChildValue(element, "Include")
                ?? ProjectXml.GetAttributeOrChildValue(element, "Update") ?? string.Empty)
            .ToArray();
        return DetermineProjectKind(
            projectName,
            ProjectXml.ReadProjectSdk(root),
            packages,
            ProjectXml.GetPropertyValue(root, "IsTestProject"),
            evaluatedOutputType ?? ProjectXml.GetPropertyValue(root, "OutputType"),
            ProjectXml.GetPropertyValue(root, "UseWPF"),
            ProjectXml.GetPropertyValue(root, "UseWindowsForms"));
    }

    private static readonly string[] TestPackageMarkers =
    [
        "coverlet.collector",
        "microsoft.net.test.sdk",
        "mstest.testframework",
        "mstest.testadapter",
        "nunit",
        "nunit3testadapter",
        "xunit",
        "xunit.runner.visualstudio"
    ];

    public static string DetermineProjectKind(
        string projectName,
        string sdk,
        IReadOnlyList<string> packageReferences,
        string? isTestProject,
        string? outputType,
        string? useWpf,
        string? useWindowsForms)
    {
        if (ProjectPaths.IsTrue(isTestProject)
            || packageReferences.Any(package => TestPackageMarkers.Any(marker => package.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            || projectName.Contains(".Tests", StringComparison.OrdinalIgnoreCase)
            || projectName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || projectName.EndsWith("Test", StringComparison.OrdinalIgnoreCase))
        {
            return "test";
        }

        if (sdk.Contains("Web", StringComparison.OrdinalIgnoreCase))
        {
            return "web";
        }

        if (ProjectPaths.IsTrue(useWpf) || ProjectPaths.IsTrue(useWindowsForms))
        {
            return "desktop";
        }

        if (string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
        {
            return "app";
        }

        return "library";
    }
}
