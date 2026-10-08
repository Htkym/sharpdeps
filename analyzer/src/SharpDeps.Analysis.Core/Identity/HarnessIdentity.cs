namespace SharpDeps.Analysis.Core.Identity;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SharpDeps.Analysis.Contracts.Harness;

/// <summary>Harness identities are separate from the unchanged report-v2 Identity rules.</summary>
public static class HarnessIdentity
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string WorkspaceId(Guid workspaceUuid)
    {
        if (workspaceUuid == Guid.Empty) throw new ArgumentException("A persisted workspace UUID is required.", nameof(workspaceUuid));
        return "hw_" + workspaceUuid.ToString("N");
    }

    // This is an initial project seed. Verified moves retain the persisted ID/alias in later storage work.
    public static string ProjectId(Guid workspaceUuid, string relativeProjectPath)
        => Hash("hpr", WorkspaceId(workspaceUuid), RelativeProjectPath(relativeProjectPath));

    // Caller supplies the Roslyn-normalized full signature and persisted project scope.
    // File path, line, TFM and analysis fingerprint deliberately are not inputs.
    public static string LogicalSymbolId(Guid workspaceUuid, string projectId, string symbolKind, string canonicalSignature)
        => Hash("hsym", WorkspaceId(workspaceUuid), RequireId(projectId, "hpr"),
            Required(symbolKind), Required(canonicalSignature));

    // Selection identity is separate from the semantic analysis fingerprint.
    public static string VariantId(Guid workspaceUuid, string projectId, string targetFramework,
        string configuration, string? platform = null, string? runtimeIdentifier = null)
        => Hash("hvar", WorkspaceId(workspaceUuid), RequireId(projectId, "hpr"),
            Required(targetFramework).ToLowerInvariant(), Required(configuration).ToLowerInvariant(),
            OptionalSelection(platform), OptionalSelection(runtimeIdentifier));

    public static string SymbolOccurrenceId(Guid workspaceUuid, string logicalSymbolId, string variantId)
        => Hash("hocc", WorkspaceId(workspaceUuid), RequireId(logicalSymbolId, "hsym"), RequireId(variantId, "hvar"));

    // UUID allocation and persistence belong to the trusted storage owner, not this pure helper.
    public static string DocumentId(Guid workspaceUuid, Guid documentUuid)
    {
        if (documentUuid == Guid.Empty) throw new ArgumentException("A durable document UUID is required.", nameof(documentUuid));
        return Hash("hdoc", WorkspaceId(workspaceUuid), documentUuid.ToString("N"));
    }

    public static string SectionId(Guid workspaceUuid, string documentId, string durableSectionToken)
        => Hash("hsec", WorkspaceId(workspaceUuid), RequireId(documentId, "hdoc"), Required(durableSectionToken));

    private static string RelativeProjectPath(string value)
    {
        value = Required(value).Replace('\\', '/');
        if (value.StartsWith('/') || value.Contains(':')) throw new ArgumentException("Use a workspace-relative project seed.");
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToArray();
        if (segments.Length == 0 || segments.Any(s => s == "..")) throw new ArgumentException("Project seed cannot contain parent traversal.");
        return string.Join('/', segments); // Ordinal case is retained; filesystem aliases require explicit validation.
    }

    private static string? OptionalSelection(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string Required(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = value.Trim();
        if (value.Length == 0) throw new ArgumentException("An identity input cannot be empty.");
        return value;
    }

    private static string RequireId(string value, string prefix)
    {
        value = Required(value);
        if (value.Length != prefix.Length + 65 || !value.StartsWith(prefix + "_", StringComparison.Ordinal)
            || value.AsSpan(prefix.Length + 1).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException("Expected a harness " + prefix + " identity.");
        return value;
    }

    private static string Hash(string prefix, params string?[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(HarnessGraphContract.IdentityVersion);
        Append(prefix);
        foreach (var field in fields) Append(field);
        return prefix + "_" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Append(string? field)
        {
            hash.AppendData(new byte[] { field is null ? (byte)0 : (byte)1 });
            if (field is null) return;
            var bytes = StrictUtf8.GetBytes(field);
            var size = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(size, (ulong)bytes.Length);
            hash.AppendData(size);
            hash.AppendData(bytes);
        }
    }
}
