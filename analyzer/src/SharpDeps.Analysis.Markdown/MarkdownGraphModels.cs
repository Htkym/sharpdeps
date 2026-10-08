namespace SharpDeps.Analysis.Markdown;

using LithoSharp.Markdown;
using SharpDeps.Analysis.Contracts.Harness;

/// <summary>IDs/section tokens are supplied by the trusted owner; this adapter never allocates persistent storage.</summary>
public sealed record MarkdownGraphRequest(
    string RawText, Guid WorkspaceUuid, Guid DocumentUuid, string ScopeId, string SourceId,
    string? SourceVersion, string SnapshotId, long Generation,
    IReadOnlyDictionary<string, string>? SectionTokens = null, string? SectionIdentityTextHash = null,
    string ExpectedParserVersion = MarkdownRuntimePin.ParserVersion, MarkdownParseOptions? Options = null);

public sealed record MarkdownSectionBinding(string NodeId, bool Durable, MarkdownSection Source, MarkdownHeading? Heading);
public sealed record MarkdownLinkBinding(string NodeId, string EdgeId, string OwnerNodeId, MarkdownLink Source);
public sealed record MarkdownFenceBinding(string NodeId, string OwnerNodeId, string RawContentHash, MarkdownFence Source);
public sealed record MarkdownMetadataValue(string Key, MarkdownYamlNode Source);
public sealed record MarkdownSearchField(string NodeId, string Kind, MarkdownTextProjection Content);

/// <summary>Source DTOs retain the original spans, segment kinds, fragments, coverage and diagnostics.</summary>
public sealed record MarkdownGraphProjection(
    HarnessGraphEnvelope Graph, MarkdownDocument Facts,
    IReadOnlyList<MarkdownSectionBinding> Sections, IReadOnlyList<MarkdownLinkBinding> Links,
    IReadOnlyList<MarkdownFenceBinding> Fences, IReadOnlyList<MarkdownMetadataValue> Attributes,
    IReadOnlyList<string> IgnoredMetadataKeys, IReadOnlyList<MarkdownSearchField> SearchFields,
    IReadOnlyList<string> ProjectionReasons);
