namespace SharpDeps.Analysis.Core.Text;

/// <summary>Escaping for Mermaid labels.</summary>
public static class MermaidText
{
    public static string EscapeLabel(string value)
        => value
            .Replace('\\', '/')
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "'")
            .Replace("#", "&#35;");
}
