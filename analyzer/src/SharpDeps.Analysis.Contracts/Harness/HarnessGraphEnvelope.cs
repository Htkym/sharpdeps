namespace SharpDeps.Analysis.Contracts.Harness;

using System.Text.Json.Serialization;
using System.Text.Json;

public static class HarnessGraphContract
{
    public const string Format = "sharpdeps-harness-graph";
    public const int SchemaVersion = 1;
    public const string IdentityVersion = "sharpdeps-harness/1";

    public static HarnessGraphEnvelope Read(string json)
    {
        try
        {
            var graph = JsonSerializer.Deserialize(json, HarnessGraphJsonContext.Default.HarnessGraphEnvelope)
                ?? throw new JsonException("A harness graph object is required.");
            ValidateHeader(graph);
            return graph;
        }
        catch (ArgumentException error) { throw new JsonException("Invalid harness graph input.", error); }
        catch (OverflowException error) { throw new JsonException("Invalid harness graph range.", error); }
    }

    // Header validation is separate from complete storage/query graph validation in later tasks.
    public static void ValidateHeader(HarnessGraphEnvelope graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.Format != Format || graph.SchemaVersion != SchemaVersion || graph.IdentityVersion != IdentityVersion
            || graph.Generation < 0 || string.IsNullOrWhiteSpace(graph.SnapshotId)
            || graph.WorkspaceId is null || graph.WorkspaceId.Length != 35 || !graph.WorkspaceId.StartsWith("hw_", StringComparison.Ordinal)
            || !Guid.TryParseExact(graph.WorkspaceId[3..], "N", out var workspace) || workspace == Guid.Empty
            || graph.WorkspaceId != "hw_" + workspace.ToString("N") || !Enum.IsDefined(graph.Coverage)
            || graph.Variants is null || graph.Nodes is null || graph.SymbolOccurrences is null
            || graph.Edges is null || graph.LegacyReferences is null)
            throw new JsonException("Unsupported or incomplete harness graph header.");
    }
}

public enum HarnessNodeKind
{
    Workspace, Project, Namespace, Type, Member, ExternalSymbol, SourceFile,
    Document, Section, FrontMatter, CodeFence, LinkTarget, SymbolMention
}
public enum HarnessCertainty { Resolved, Candidate, Unresolved }
public enum HarnessCoverage { CompleteWithinScope, Partial, Failed }

/// <summary>Original UTF-16, zero-based half-open range; null range means unknown.</summary>
public readonly record struct HarnessRawSpan
{
    [JsonConstructor]
    public HarnessRawSpan(int start, int length)
    {
        if (start < 0 || length < 0) throw new ArgumentOutOfRangeException(nameof(start));
        _ = checked(start + length);
        Start = start;
        Length = length;
    }
    public int Start { get; }
    public int Length { get; }
    public int End => checked(Start + Length);
}

public sealed record HarnessLocation(string SourceId, string? SourceVersion, string? ContentHash, HarnessRawSpan? RawSpan);
public sealed record HarnessVariant(string Id, string ProjectId, string TargetFramework, string Configuration,
    string? Platform, string? RuntimeIdentifier, string? AnalysisFingerprint);
public sealed record HarnessNode(string Id, HarnessNodeKind Kind, string Name, string? ParentId, HarnessLocation? Location,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Signature = null);
public sealed record HarnessSymbolOccurrence(string Id, string LogicalSymbolId, string VariantId, HarnessLocation? Location,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HarnessLocation>? Declarations = null);
public sealed record HarnessEdge(string Id, string SourceNodeId, string TargetNodeId,
    string? SourceOccurrenceId, string? TargetOccurrenceId, string? VariantId,
    string Kind, HarnessCertainty Certainty, string Producer, HarnessLocation? Evidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Origin = null);
public sealed record HarnessLegacyReference(int SchemaVersion, string LegacyId, string NodeId, string? OccurrenceId);
public sealed record HarnessMarkdownIdentity(string ComponentVersion, string CanonicalSourceHash,
    string ParserVersion, string ContractVersion, string ProfileId, string OptionsHash);

// A new envelope; AnalysisSnapshot/report-v2 and their serializer/schema stay unchanged.
// Producers retain separate occurrences/variant edges. Candidate evidence is never promoted to Resolved.
public sealed record HarnessGraphDiagnostic(string Code, int Count);

public sealed record HarnessGraphEnvelope(
    string Format, int SchemaVersion, string IdentityVersion, string WorkspaceId, string SnapshotId,
    long Generation, HarnessCoverage Coverage,
    IReadOnlyList<HarnessVariant> Variants, IReadOnlyList<HarnessNode> Nodes,
    IReadOnlyList<HarnessSymbolOccurrence> SymbolOccurrences, IReadOnlyList<HarnessEdge> Edges,
    IReadOnlyList<HarnessLegacyReference> LegacyReferences, HarnessMarkdownIdentity? Markdown,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HarnessGraphDiagnostic>? Diagnostics = null);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(HarnessGraphEnvelope))]
public partial class HarnessGraphJsonContext : JsonSerializerContext;
