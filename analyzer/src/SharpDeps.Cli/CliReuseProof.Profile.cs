namespace SharpDeps.Cli;

using SharpDeps.Analysis.Core.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using SharpDeps.Index;

internal static partial class CliReuseProof
{
    private static readonly HashSet<string> DirectoryBuildProperties = new(StringComparer.Ordinal)
    {
        "LangVersion", "Nullable", "ImplicitUsings", "AllowUnsafeBlocks", "DefineConstants",
        "Optimize", "CheckForOverflowUnderflow", "WarningLevel", "NoWarn", "WarningsAsErrors",
        "WarningsNotAsErrors", "TreatWarningsAsErrors", "GenerateAssemblyInfo", "GenerateGlobalUsings",
        "Deterministic", "DebugType", "DebugSymbols"
    };

    private static bool PlainDirectoryBuildProps(string path, string root, ProofBudget budget,
        HashSet<string>? importedProps)
    {
        CliPaths.RejectLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var readMetrics = PhaseMetrics.ObserveRead(PhaseMetrics.ReadCategory.Workspace, input);
        budget.Bytes(input.Length);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = IndexSnapshotValidator.MaximumInputFileBytes });
        var project = XDocument.Load(reader).Root;
        if (project?.Name != "Project" || project.HasAttributes)
            return false;
        // shortcut: Only one literal import level and unconditional compiler properties are closed.
        foreach (var group in project.Elements())
        {
            if (group.Name == "Import" && importedProps is not null)
            {
                var include = (string?)group.Attribute("Project");
                if (group.Attributes().Count() != 1 || group.HasElements || !string.IsNullOrWhiteSpace(group.Value)
                    || string.IsNullOrEmpty(include) || Path.IsPathRooted(include)
                    || include.Any(c => !char.IsLetterOrDigit(c) && c is not ('.' or '-' or '_' or '/' or '\\'))
                    || (!OperatingSystem.IsWindows() && include.Contains('\\'))
                    || include.Split('/', '\\').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.')))
                    return false;
                var imported = CliPaths.InRoot(root, Path.GetFullPath(include, Path.GetDirectoryName(path)!));
                if (!imported.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || ExcludedProject(root, imported)
                    || !File.Exists(imported) || !PlainDirectoryBuildProps(imported, root, budget, null))
                    return false;
                importedProps.Add(imported);
                continue;
            }
            if (group.Name != "PropertyGroup" || group.HasAttributes)
                return false;
            foreach (var property in group.Elements())
                if (property.Name.Namespace != XNamespace.None || !DirectoryBuildProperties.Contains(property.Name.LocalName)
                    || property.HasAttributes || property.HasElements || !Static(property.Value))
                    return false;
        }
        CliPaths.RejectLinks(path);
        return true;
    }
}
