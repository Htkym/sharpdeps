namespace SharpDeps.Analysis.Core.Xml;

using System.Xml.Linq;

/// <summary>
/// Reads MSBuild XML without evaluating conditions or property expansion. Quick
/// analysis uses this on purpose: it never runs build logic, so anything it reads
/// is a declared value, not an evaluated one.
/// </summary>
public static class ProjectXml
{
    public static bool HasCondition(XElement element)
        => element.Attributes().Any(attribute => string.Equals(attribute.Name.LocalName, "Condition", StringComparison.OrdinalIgnoreCase))
           || element.Ancestors().Any(ancestor =>
               ancestor.Attributes().Any(attribute => string.Equals(attribute.Name.LocalName, "Condition", StringComparison.OrdinalIgnoreCase)));

    public static string? GetAttributeOrChildValue(XElement element, string name)
        => element.Attributes()
               .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
               ?.Value
           ?? element.Elements()
               .FirstOrDefault(child => string.Equals(child.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
               ?.Value;

    public static string? GetPropertyValue(XElement root, string propertyName)
        => root
            .Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, propertyName, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?.Trim();

    public static string ReadProjectSdk(XElement root)
        => root.Attribute("Sdk")?.Value
           ?? string.Join(
               ";",
               root.Elements()
                   .Where(element => string.Equals(element.Name.LocalName, "Sdk", StringComparison.OrdinalIgnoreCase))
                   .Select(element => element.Attribute("Name")?.Value)
                   .Where(value => !string.IsNullOrWhiteSpace(value))
                   .Cast<string>())
           ?? string.Empty;

    public static string? GetPrimaryTargetFramework(string? targetFrameworks)
    {
        if (string.IsNullOrWhiteSpace(targetFrameworks))
        {
            return null;
        }

        return targetFrameworks
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }
}
