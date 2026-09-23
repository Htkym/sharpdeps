namespace SharpDeps.Analysis.Core.Paths;

/// <summary>Path keys and project-file checks shared by the Quick analyzer.</summary>
public static class ProjectPaths
{
    public static readonly HashSet<string> SupportedProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj",
        ".fsproj",
        ".vbproj",
        ".vcxproj"
    };

    public static bool LooksLikeProjectPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var extension = Path.GetExtension(relativePath);
        return SupportedProjectExtensions.Contains(extension);
    }

    /// <summary>
    /// Case-insensitive on Windows so the same project file is one node regardless
    /// of how a solution file spells the path.
    /// </summary>
    public static string NormalizePathKey(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return OperatingSystem.IsWindows()
            ? fullPath.ToLowerInvariant()
            : fullPath;
    }

    public static bool IsTrue(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
