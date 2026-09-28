// Identity rules for the v2 model (ADR 0002, plan section 5.2).
//
// The TypeScript implementation in src/analyzer/identity.ts is the reference; the
// two must produce the same ids for the same logical inputs. `IdentityTests` and
// `tests/helpers/identityAgreement.test.ts` pin the same golden values on both
// sides, so changing either implementation requires updating both.
//
// Every id is an opaque, prefixed, truncated SHA-256 hash: no absolute paths and no
// symbol names leave the analyzer inside an id.

namespace SharpDeps.Analysis.Core.Identity;

using System.Security.Cryptography;
using System.Text;

public static class Identity
{
    /// <summary>Field separator that cannot appear in a path, a symbol name, or a doc id.</summary>
    private const char UnitSeparator = '\u001f';

    private const int HashLength = 16;

    private static string Hash(string prefix, params string[] parts)
    {
        var input = string.Join(UnitSeparator, parts);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return prefix + "_" + Convert.ToHexString(digest)[..HashLength].ToLowerInvariant();
    }

    public static string NormalizeRelativePath(string relativePath)
        => relativePath.Replace('\\', '/').TrimEnd('/');

    /// <summary>Stable key for a type declaration; documentation id first, structural key second.</summary>
    public static string TypeKey(
        string? documentationId,
        string? namespaceName,
        IReadOnlyList<string>? containingTypes,
        string name,
        int arity = 0)
    {
        if (!string.IsNullOrWhiteSpace(documentationId))
        {
            return "doc:" + documentationId.Trim();
        }

        var chain = new List<string>(containingTypes ?? []);
        chain.Add(name);
        return $"sig:{namespaceName ?? string.Empty}|{string.Join('.', chain)}`{arity}";
    }

    public static string MemberKey(
        string? documentationId,
        string containingTypeKey,
        string name,
        IReadOnlyList<string>? parameterTypes = null)
    {
        if (!string.IsNullOrWhiteSpace(documentationId))
        {
            return "doc:" + documentationId.Trim();
        }

        var parameters = string.Join(",", (parameterTypes ?? []).Select(type => type.Trim()));
        return $"sig:{containingTypeKey}|{name}({parameters})";
    }

    public static string WorkspaceRootId(string rootPath)
        => Hash("wrk", "root", NormalizeRelativePath(rootPath));

    public static string ProjectLogicalId(string rootId, string projectRelativePath)
        => Hash("prj", rootId, NormalizeRelativePath(projectRelativePath));

    public static string ProjectVariantId(
        string logicalId,
        string targetFramework,
        string configuration,
        string? platform)
        => Hash(
            "var",
            logicalId,
            targetFramework.Trim().ToLowerInvariant(),
            configuration.Trim().ToLowerInvariant(),
            (platform ?? string.Empty).Trim().ToLowerInvariant());

    public static string NamespaceId(string variantId, string namespaceName)
        => Hash("ns", variantId, namespaceName.Trim());

    public static string TypeId(string variantId, string key) => Hash("ty", variantId, key);

    public static string MemberId(string variantId, string key) => Hash("mb", variantId, key);

    public static string ExternalTypeId(string assemblyIdentity, string key)
        => Hash("ty", "ext", assemblyIdentity.Trim(), key);

    public static string DocumentId(string rootId, string relativePath, string origin)
        => Hash("doc", rootId, NormalizeRelativePath(relativePath), origin);

    public static string RelationId(string basis, string sourceEntityId, string targetEntityId, string profileHash)
        => Hash("rel", basis, sourceEntityId, targetEntityId, profileHash);

    public static string EvidenceId(string relationId, string kind, string documentId, string spanKey)
        => Hash("ev", relationId, kind, documentId, spanKey);

    public static string CycleGroupId(string scope, string basis, IEnumerable<string> memberIds)
        => Hash("cyc", scope, basis, string.Join(",", memberIds.OrderBy(id => id, StringComparer.Ordinal)));

    public static string DiagnosticId(string code, string? targetId, string? evidenceId)
        => Hash("dg", code, targetId ?? string.Empty, evidenceId ?? string.Empty);

    public static string AnalysisId(string targetId, string mode, string profileHash, string startedAt)
        => Hash("an", targetId, mode, profileHash, startedAt);

    /// <summary>Hash of the inputs that affect analysis results; 16 hex characters, no prefix.</summary>
    public static string ProfileHash(string configuration, string? platform, IEnumerable<(string ProjectLogicalId, string TargetFramework)> variants)
    {
        var parts = new List<string>
        {
            configuration.Trim().ToLowerInvariant(),
            (platform ?? string.Empty).Trim().ToLowerInvariant()
        };
        parts.AddRange(variants
            .Select(variant => $"{variant.ProjectLogicalId}={variant.TargetFramework.Trim().ToLowerInvariant()}")
            .OrderBy(part => part, StringComparer.Ordinal));
        return Hash("prf", [.. parts])[4..];
    }

    /// <summary>DOM ids are derived from the opaque id, never from symbol names.</summary>
    public static string DomId(string prefix, string id) => prefix + "-" + Hash("dom", id)[4..];
}
