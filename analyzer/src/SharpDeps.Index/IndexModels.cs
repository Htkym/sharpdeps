namespace SharpDeps.Index;

using SharpDeps.Analysis.Contracts.Harness;
using System.Text.Json.Serialization;
using System.Text.Json;

/// <summary>The owner supplies a complete, bounded input manifest, including analysis configuration files.</summary>
public sealed record IndexFile(string SourceId, string RelativePath, string Kind, string ContentHash,
    long ByteLength, int? Utf16Length, bool Deleted = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IndexReuseProof? Reuse = null);
/// <summary>Exact catalog values selected by the analysis owner; never inferred from display signatures.</summary>
public sealed record IndexReuseSymbol(string NodeId, string FullName, string ShortName,
    string? DocumentationId, string? Project, string? Namespace, string? VariantId, string? SourceId);
/// <summary>Optional owner proof for a closed semantic input set. Missing proof requires a full rebuild.</summary>
public sealed record IndexReuseProof(string OwnerVersion, string OptionsHash, string InputHash,
    string EnvironmentHash, string DependencyHash, string SdkPath, IReadOnlyList<IndexReuseSymbol> Catalog)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IndexProjectReuse? Projects { get; init; }
}
/// <summary>Optional bounded owner extraction data; old readers ignore it and old proofs require full extraction.</summary>
public sealed record IndexProjectReuse(string Version, string StructureHash, IReadOnlyList<IndexProjectExtraction> Variants);
public sealed record IndexProjectExtraction(string Key, string VariantId, string ProjectId,
    IReadOnlyList<string> SourceIds, IReadOnlyList<string> References, JsonElement Data);
public sealed record IndexSectionIdentity(string LocalKey, string Token, string? HeadingKey, string? BodyHash);
public sealed record IndexDocumentIdentity(Guid DocumentUuid, string RelativePath, string? ExplicitId,
    string ContentHash, string ScopeId, string ParserVersion, string ContractVersion, string ProfileId,
    string OptionsHash, IReadOnlyList<IndexSectionIdentity> Sections);
public sealed record IndexAlias(string Kind, string OldId, string NewId, string Reason, HarnessCertainty Certainty);
/// <summary>Only explicitly selected search text is stored, never unrestricted source blobs.</summary>
public sealed record IndexSearchText(string NodeId, string Name, string Signature, string Heading, string Body, string Path);
/// <summary>
/// The retained body of one source-generated input. A generated document has no file
/// on disk to re-read, so its body is stored next to the IndexFile entry whose hash,
/// byte length and UTF-16 length it must match exactly. This is the single bounded
/// exception to the no-source-blobs rule: bodies are capped per file and in total by
/// the same input limits as physical files. A stored body proves integrity of the
/// index-time snapshot only, never that the generator would still produce it.
/// </summary>
public sealed record IndexGeneratedBody(string SourceId, string Body);
/// <summary>The IndexFile kind whose text lives in a stored generated body, not on disk.</summary>
public static class IndexFileKinds
{
    public const string GeneratedSource = "generatedSource";
}
public sealed record IndexSnapshot(HarnessGraphEnvelope Graph, IReadOnlyList<IndexFile> Files,
    IReadOnlyList<IndexDocumentIdentity> Documents, IReadOnlyList<IndexAlias> Aliases,
    IReadOnlyList<IndexSearchText> SearchText,
    IReadOnlyList<IndexGeneratedBody>? GeneratedBodies = null)
{
    /// <summary>Stored generated bodies; empty for snapshots written before generated support.</summary>
    [JsonIgnore]
    public IReadOnlyList<IndexGeneratedBody> StoredGeneratedBodies => GeneratedBodies ?? [];
}
public sealed record IndexSearchHit(string NodeId, double Rank, string MatchMethod);
public sealed record IndexWriterOwner(int ProcessId, string ProcessStartUtc, string CanonicalRoot, string IndexPath,
    int SchemaVersion);

public sealed class IndexStoreException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
