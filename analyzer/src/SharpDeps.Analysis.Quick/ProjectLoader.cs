namespace SharpDeps.Analysis.Quick;

using System.Xml.Linq;
using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Paths;
using SharpDeps.Analysis.Core.Xml;


/// <summary>Reads a project file as declared XML (no evaluation).</summary>
public static class ProjectLoader
{
        public static async Task<LoadedProject> LoadProjectAsync(SolutionProjectEntry project, string solutionDirectoryPath)
        {
            var projectText = await File.ReadAllTextAsync(project.FullPath);
            var document = XDocument.Parse(projectText, LoadOptions.PreserveWhitespace);
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

                    var fullReferencePath = Path.GetFullPath(Path.Combine(projectDirectoryPath, includePath));
                    return new ProjectReferenceInfo(
                        includePath,
                        fullReferencePath,
                        ProjectPaths.NormalizePathKey(fullReferencePath),
                        ProjectXml.HasCondition(element));
                })
                .Where(reference => reference is not null)
                .Cast<ProjectReferenceInfo>()
                .ToArray();
}