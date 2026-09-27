namespace SharpDeps.Analysis.Roslyn;

/// <summary>
/// Loader diagnostics and evaluated project variants used by the semantic report writer.
/// </summary>
public sealed record SemanticProbeReport(
    string SchemaVersion,
    string CreatedAt,
    SemanticEnvironmentInfo Environment,
    string TargetPath,
    string Configuration,
    SemanticProfileInfo Profile,
    IReadOnlyList<ProjectVariantInfo> Variants,
    IReadOnlyList<ReferenceEdgeInfo> References,
    IReadOnlyList<ProbeDiagnostic> Diagnostics,
    IReadOnlyList<ProbeLimitation> Limitations,
    ProbeCoverage Coverage);

/// <summary>
/// The analysis profile the loader actually used. Changing any input produces a new
/// analysis, so the profile is reported alongside the result (never assumed).
/// </summary>
public sealed record SemanticProfileInfo(
    string Configuration,
    string? Platform,
    string ProfileHash,
    IReadOnlyList<SemanticVariantInfo> Variants);

public sealed record SemanticVariantInfo(
    string ProjectName,
    string ProjectPath,
    string? TargetFramework);

public sealed record ProjectVariantInfo(
    string ProjectName,
    string ProjectPath,
    string? TargetFramework,
    string Configuration,
    string VariantKey,
    string LoadState,
    int DocumentCount,
    IReadOnlyList<string> Documents,
    bool DocumentsTruncated,
    int GeneratedDocumentCount,
    string? GeneratedDocumentError,
    int MetadataReferenceCount,
    int AddedTransitiveReferences,
    bool CompilationObtained,
    int ErrorDiagnosticCount,
    string? FailureReason,
    string Kind = "unknown");

public sealed record ReferenceEdgeInfo(
    string SourceVariantKey,
    string SourceProjectName,
    string SourceTargetFramework,
    string TargetProjectName,
    string TargetProjectPath,
    string? TargetVariantKey,
    string? TargetTargetFramework,
    string Resolution,
    string? Note);

public sealed record ProbeDiagnostic(string Kind, string Message);

public sealed record ProbeLimitation(string Code, string Message, int? Count);

public sealed record ProbeCoverage(
    int Discovered,
    int Loaded,
    int Analyzed,
    int Failed,
    int Skipped,
    int Unresolved);
