// v2 analysis model (report v2) for the analyzers. The normative definition is
// schemas/report-v2.schema.json; these records mirror it and are serialized with
// the camelCase naming policy so the TypeScript validator accepts them as-is.
//
// The property order of the JSON source generator follows the declaration order
// below; the shape (not the order) is what the contract test checks.

namespace SharpDeps.Analysis.Contracts;

using System.Text.Json.Serialization;

public sealed record TargetDescriptor(string Kind, string RootId, string RelativePath);

public sealed record ProjectVariant(
    string VariantId,
    string ProjectLogicalId,
    string TargetFramework,
    string Configuration,
    string? Platform,
    string TargetFrameworkSource);

public sealed record AnalysisProfile(
    string Configuration,
    string? Platform,
    IReadOnlyList<ProjectVariant> ProjectVariants,
    string ProfileHash);

public sealed record AnalysisCapabilities(
    bool TypeGraph,
    bool Evidence,
    bool GeneratedDocuments,
    bool CycleWitness,
    bool Search);

public sealed record AnalysisCoverage(
    int Discovered,
    int Loaded,
    int Analyzed,
    int Failed,
    int Skipped,
    int Unresolved);

public sealed record Limitation(string Code, string Message, string? Scope = null, int? Count = null);

public sealed record AnalysisProject(
    string Id,
    string VariantId,
    string Name,
    string RelativePath,
    string GroupPath,
    string Kind,
    string TargetFramework,
    string? Configuration,
    string? Platform,
    IReadOnlyList<string> PackageReferences,
    string LoadState,
    IReadOnlyList<Limitation> Limitations);

public sealed record AnalysisNamespace(
    string Id,
    string ProjectVariantId,
    string Name,
    int TypeCount,
    string? RepresentativeDocumentId);

public sealed record AnalysisType(
    string Id,
    string ProjectVariantId,
    string? NamespaceId,
    string Name,
    string FullName,
    string? DocumentationId,
    string Kind,
    string Accessibility,
    bool IsPartial,
    int DeclarationCount,
    int? MemberCount,
    bool? IsExternal,
    bool? IsGenerated = null);

public sealed record AnalysisRelation(
    string Id,
    string SourceEntityId,
    string TargetEntityId,
    string Basis,
    IReadOnlyList<string> Kinds,
    int EvidenceCount,
    int DistinctSourceMemberCount,
    int DistinctSourceDocumentCount,
    int GeneratedEvidenceCount,
    int PublicSurfaceEvidenceCount,
    string? Confidence,
    int? AmbiguousCandidates);

public sealed record CycleWitness(IReadOnlyList<string> MemberIds, IReadOnlyList<string> RelationIds);

public sealed record CycleGroup(
    string Id,
    string Scope,
    string Basis,
    IReadOnlyList<string> MemberIds,
    IReadOnlyList<string> InternalRelationIds,
    CycleWitness? Witness,
    bool? Truncated);

public sealed record AnalysisDiagnostic(
    string Id,
    string Severity,
    string Code,
    string Message,
    string? TargetId,
    string? EvidenceId,
    string? AnalysisId);

public sealed record EvidenceIndexEntry(string RelationId, long StartByte, int Count);

public sealed record EvidenceIndex(
    string Format,
    string FileName,
    long ByteLength,
    IReadOnlyList<EvidenceIndexEntry> Relations);

public sealed record DeclarationIndexEntry(string TypeId, long StartByte, int Count);

/// <summary>
/// Where a type's declarations are in the declarations file. Editor-driven lookups
/// (SD-019) resolve a cursor position to a type and a type back to a declaration.
/// </summary>
public sealed record DeclarationIndex(
    string Format,
    string FileName,
    long ByteLength,
    IReadOnlyList<DeclarationIndexEntry> Types);

/// <summary>One declaration of a type, in declaration order within its type.</summary>
public sealed record DeclarationRecord(
    string TypeId,
    string ProjectVariantId,
    string DocumentId,
    string RelativePath,
    PhysicalSpan Span,
    int DeclarationIndex,
    bool IsPartial);

public sealed record SourceDocument(
    string Id,
    string RelativePath,
    string Origin,
    string ContentHash,
    long? ByteLength,
    string? MappedFromDocumentId);

public sealed record AnalysisSnapshot(
    int SchemaVersion,
    string AnalyzerVersion,
    string AnalysisId,
    string CreatedAt,
    TargetDescriptor Target,
    string Mode,
    AnalysisProfile Profile,
    AnalysisCapabilities Capabilities,
    string Completeness,
    AnalysisCoverage Coverage,
    IReadOnlyList<AnalysisProject> Projects,
    IReadOnlyList<AnalysisNamespace> Namespaces,
    IReadOnlyList<AnalysisType> Types,
    IReadOnlyList<AnalysisRelation> Relations,
    IReadOnlyList<CycleGroup> CycleGroups,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics,
    EvidenceIndex? EvidenceIndex,
    DeclarationIndex? DeclarationIndex,
    IReadOnlyList<SourceDocument> SourceManifest,
    IReadOnlyList<Limitation> Limitations);

public sealed record PhysicalSpan(
    int Start,
    int Length,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter);

public sealed record MappedLocation(string RelativePath, int Line, int Character);

/// <summary>One line of the evidence NDJSON file.</summary>
public sealed record EvidenceRecord(
    string Id,
    string RelationId,
    string SourceEntityId,
    string TargetEntityId,
    string? SourceTypeId,
    string? SourceMemberId,
    string? TargetTypeId,
    string? TargetMemberId,
    string Kind,
    string Origin,
    string DocumentId,
    PhysicalSpan? PhysicalSpan,
    MappedLocation? MappedLocation,
    string SourceContentHash,
    string Confidence,
    bool PublicSurface,
    string? Snippet);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CodeMapReport))]
[JsonSerializable(typeof(AnalysisSnapshot))]
public partial class CodeMapJsonContext : JsonSerializerContext;

/// <summary>Compact (one line per record) serialization for the evidence file.</summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EvidenceRecord))]
[JsonSerializable(typeof(DeclarationRecord))]
public partial class EvidenceJsonContext : JsonSerializerContext;
