namespace SharpDeps.Analysis.Quick;

using System.Text;
using System.Xml;
using System.Xml.Linq;
using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Paths;
using SharpDeps.Analysis.Core.Xml;

/// <summary>Reads a project file as declared XML (no evaluation).</summary>
public static class ProjectLoader
{
    public static async Task<LoadedProject> LoadProjectAsync(
        SolutionProjectEntry project,
        string solutionDirectoryPath,
        QuickSourceIndexCollector? collector = null)
    {
        var projectBytes = await File.ReadAllBytesAsync(project.FullPath);
        var projectText = DecodeText(projectBytes);
        var document = XDocument.Parse(
            projectText,
            LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        var root = document.Root ?? throw new InvalidOperationException("Project XML is empty.");

        var projectDirectoryPath = Path.GetDirectoryName(project.FullPath)
            ?? throw new InvalidOperationException($"Could not determine the directory for {project.FullPath}");

        var packageReferences = root
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "PackageReference", StringComparison.OrdinalIgnoreCase))
            .Select(element => ProjectXml.GetAttributeOrChildValue(element, "Include") ?? ProjectXml.GetAttributeOrChildValue(element, "Update"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var projectReferences = ReadProjectReferences(root, projectDirectoryPath);

        if (collector is not null)
        {
            CollectProjectReferences(collector, project, root, projectText, projectBytes, projectDirectoryPath);
        }

        var kind = ProjectKindDetection.DetermineProjectKind(
            project.Name,
            ProjectXml.ReadProjectSdk(root),
            packageReferences,
            ProjectXml.GetPropertyValue(root, "IsTestProject"),
            ProjectXml.GetPropertyValue(root, "OutputType"),
            ProjectXml.GetPropertyValue(root, "UseWPF"),
            ProjectXml.GetPropertyValue(root, "UseWindowsForms"));

        return new LoadedProject(
            project.Name,
            project.FullPath,
            project.RelativePath,
            project.GroupPath,
            ProjectPaths.NormalizePathKey(project.FullPath),
            kind,
            ProjectXml.GetPropertyValue(root, "TargetFramework")
                ?? ProjectXml.GetPrimaryTargetFramework(ProjectXml.GetPropertyValue(root, "TargetFrameworks"))
                ?? "(not specified)",
            projectReferences,
            packageReferences);
    }

    public static ProjectReferenceInfo[] ReadProjectReferences(XElement root, string projectDirectoryPath)
        => root
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "ProjectReference", StringComparison.OrdinalIgnoreCase))
            .Select(element =>
            {
                var includePath = ProjectXml.GetAttributeOrChildValue(element, "Include");
                if (string.IsNullOrWhiteSpace(includePath))
                {
                    return null;
                }

                var fullReferencePath = Path.GetFullPath(Path.Combine(projectDirectoryPath, ProjectPaths.NativeSeparators(includePath)));
                return new ProjectReferenceInfo(
                    includePath,
                    fullReferencePath,
                    ProjectPaths.NormalizePathKey(fullReferencePath),
                    ProjectXml.HasCondition(element));
            })
            .Where(reference => reference is not null)
            .Cast<ProjectReferenceInfo>()
            .ToArray();

    /// <summary>
    /// Records the project file and every declared ProjectReference item with the
    /// position of its XML element. The span covers the element's declaration line:
    /// Quick records where the declaration is, not which attribute was written.
    /// </summary>
    private static void CollectProjectReferences(
        QuickSourceIndexCollector collector,
        SolutionProjectEntry project,
        XElement root,
        string projectText,
        byte[] projectBytes,
        string projectDirectoryPath)
    {
        var document = collector.AddDocument(
            project.FullPath,
            projectBytes,
            QuickSourceIndexCollector.ComputeContentHash(projectBytes));

        foreach (var element in root
                     .Descendants()
                     .Where(element => string.Equals(element.Name.LocalName, "ProjectReference", StringComparison.OrdinalIgnoreCase)))
        {
            var includePath = ProjectXml.GetAttributeOrChildValue(element, "Include");
            if (string.IsNullOrWhiteSpace(includePath))
            {
                continue;
            }

            var fullReferencePath = Path.GetFullPath(Path.Combine(projectDirectoryPath, ProjectPaths.NativeSeparators(includePath)));
            var span = element is IXmlLineInfo lineInfo && lineInfo.HasLineInfo()
                ? XmlLineSpan(projectText, lineInfo.LineNumber, lineInfo.LinePosition)
                : (QuickSpan?)null;

            collector.AddProjectReference(
                new QuickProjectReference(
                    ProjectPaths.NormalizePathKey(project.FullPath),
                    fullReferencePath,
                    includePath,
                    ProjectXml.HasCondition(element),
                    document.Id,
                    span));
        }
    }

    private static string DecodeText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Converts a 1-based XML line/position into a 0-based span on that line.</summary>
    private static QuickSpan XmlLineSpan(string text, int lineNumber, int linePosition)
    {
        var offset = 0;
        var currentLine = 1;
        while (currentLine < lineNumber)
        {
            var newline = text.IndexOf('\n', offset);
            if (newline < 0)
            {
                break;
            }

            offset = newline + 1;
            currentLine++;
        }

        var lineEnd = text.IndexOfAny(['\r', '\n'], offset);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        var startCharacter = Math.Max(0, linePosition - 1);
        var start = Math.Min(offset + startCharacter, lineEnd);
        return new QuickSpan(
            start,
            lineEnd - start,
            lineNumber - 1,
            startCharacter,
            lineNumber - 1,
            lineEnd - offset);
    }
}
