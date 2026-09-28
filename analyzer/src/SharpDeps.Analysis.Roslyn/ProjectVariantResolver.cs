namespace SharpDeps.Analysis.Roslyn;

/// <summary>
/// Pure target-framework selection and reference resolution rules.
/// </summary>
/// <remarks>
/// The loader must never apply one TFM to a whole solution, and it must not join
/// variants by project name alone. These rules decide which variants exist and
/// which target variant a ProjectReference resolves to; anything ambiguous stays
/// unresolved and is reported instead of guessed.
/// </remarks>
public static class ProjectVariantResolver
{
    public sealed record TargetFrameworkSelection(
        IReadOnlyList<string> TargetFrameworks,
        string Source);

    /// <summary>Reads TargetFramework / TargetFrameworks without inventing a value.</summary>
    public static TargetFrameworkSelection SelectTargetFrameworks(
        string? targetFramework,
        string? targetFrameworks)
    {
        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            return new TargetFrameworkSelection([targetFramework.Trim()], "targetFramework");
        }

        if (!string.IsNullOrWhiteSpace(targetFrameworks))
        {
            var parsed = targetFrameworks
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (parsed.Length > 0)
            {
                return new TargetFrameworkSelection(parsed, "targetFrameworks");
            }
        }

        return new TargetFrameworkSelection([], "notSpecified");
    }

    public static string VariantKey(string projectPath, string? targetFramework, string configuration, string? platform)
        => string.Join(
            '|',
            NormalizePath(projectPath),
            (targetFramework ?? "(not specified)").ToLowerInvariant(),
            configuration.ToLowerInvariant(),
            (platform ?? string.Empty).ToLowerInvariant());

    /// <summary>
    /// Resolves which target variant a reference from <paramref name="sourceTfm"/>
    /// should bind to. Returns null when nothing is compatible: an unresolved
    /// reference is recorded, never silently bound to the first candidate.
    /// </summary>
    public static string? ResolveReferenceTarget(
        string sourceTfm,
        IReadOnlyList<string> candidateTfms,
        out string resolution,
        out string? note)
    {
        resolution = "unresolved";
        note = null;

        if (candidateTfms.Count == 0)
        {
            note = "The referenced project declares no target framework.";
            return null;
        }

        var exact = candidateTfms.FirstOrDefault(
            candidate => string.Equals(candidate, sourceTfm, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            resolution = "exact";
            return exact;
        }

        var requested = Parse(sourceTfm);
        var compatible = candidateTfms
            .Select(candidate => (candidate, rank: CompatibilityRank(requested, Parse(candidate))))
            .Where(entry => entry.rank >= 0)
            .OrderBy(entry => entry.rank)
            .ThenBy(entry => entry.candidate, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (compatible.Length == 0)
        {
            note = $"No candidate TFM is compatible with {sourceTfm} ({string.Join(", ", candidateTfms)}).";
            return null;
        }

        resolution = compatible.Length > 1 ? "compatibleFirst" : "compatible";
        if (compatible.Length > 1)
        {
            note = $"Several compatible TFMs exist ({string.Join(", ", compatible.Select(entry => entry.candidate))}); "
                + $"the first by rank ({compatible[0].candidate}) was used.";
        }

        return compatible[0].candidate;
    }

    private static (string Family, int Major, int Minor) Parse(string targetFramework)
    {
        var value = targetFramework.Trim().ToLowerInvariant();
        var dash = value.IndexOf('-');
        if (dash > 0)
        {
            value = value[..dash];
        }

        string family;
        string version;
        if (value.StartsWith("netstandard", StringComparison.Ordinal))
        {
            family = "netstandard";
            version = value["netstandard".Length..];
        }
        else if (value.StartsWith("netcoreapp", StringComparison.Ordinal))
        {
            family = "netcoreapp";
            version = value["netcoreapp".Length..];
        }
        else if (value.StartsWith("net", StringComparison.Ordinal))
        {
            version = value["net".Length..];
            // "net8.0" is modern .NET; "net48" is .NET Framework 4.8.
            family = version.Contains('.') ? "net" : "netframework";
        }
        else
        {
            return ("unknown", 0, 0);
        }

        var parts = version.Split('.');
        var major = parts.Length > 0 && int.TryParse(parts[0], out var parsedMajor) ? parsedMajor : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var parsedMinor) ? parsedMinor : 0;

        if (family == "netframework" && major >= 10)
        {
            // "net48" means 4.8: the digits pack major and minor together.
            minor = major % 10;
            major /= 10;
        }

        return (family, major, minor);
    }

    /// <summary>
    /// Lower is better. -1 means incompatible. A .NET project can consume
    /// netstandard and older .NET, but never .NET Framework or a newer .NET.
    /// </summary>
    private static int CompatibilityRank(
        (string Family, int Major, int Minor) requested,
        (string Family, int Major, int Minor) candidate)
    {
        if (requested.Family == candidate.Family)
        {
            if (candidate.Major > requested.Major
                || (candidate.Major == requested.Major && candidate.Minor > requested.Minor))
            {
                return -1;
            }

            return (requested.Major - candidate.Major) * 10 + (requested.Minor - candidate.Minor);
        }

        if (requested.Family == "net" && candidate.Family == "netstandard")
        {
            // netstandard2.0/2.1 are consumable from .NET 5+. MSBuild prefers the
            // highest compatible netstandard, so a higher version ranks better.
            return 100 - (candidate.Major * 10 + candidate.Minor);
        }

        if (requested.Family == "netcoreapp" && candidate.Family == "netstandard")
        {
            return 100 - (candidate.Major * 10 + candidate.Minor);
        }

        return -1;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimEnd('/');
}
