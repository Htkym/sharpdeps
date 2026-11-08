namespace SharpDeps.Cli;

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

    private static bool PlainDirectoryBuildProps(string path, ProofBudget budget)
    {
        CliPaths.RejectLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        budget.Bytes(input.Length);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = IndexSnapshotValidator.MaximumInputFileBytes });
        var project = XDocument.Load(reader).Root;
        if (project?.Name != "Project" || project.HasAttributes)
            return false;
        // shortcut: Only unconditional compiler properties are closed; imports, conditions and items stay cold.
        foreach (var group in project.Elements())
        {
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
