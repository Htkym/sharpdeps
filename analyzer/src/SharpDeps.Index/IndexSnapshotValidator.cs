namespace SharpDeps.Index;

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;

/// <summary>Pure storage-boundary validation. It proves manifest/graph consistency, not filesystem freshness or trust.</summary>
public static class IndexSnapshotValidator
{
    public const int MaximumSearchFieldUtf16 = 64 * 1024;
    public const int MaximumSearchTextUtf16 = 16 * 1024 * 1024;
    public const int MaximumCollectionItems = 1_000_000;
    public const long MaximumInputFileBytes = 64L * 1024 * 1024;
    public const long MaximumInputTotalBytes = 256L * 1024 * 1024;
    public const int MaximumInputFileUtf16 = 64 * 1024 * 1024;
    private const int MaximumPathUtf16 = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void Validate(IndexSnapshot snapshot)
    {
        try { ValidateCore(snapshot); }
        catch (IndexStoreException) { throw; }
        catch (Exception error) when (error is ArgumentException or JsonException or OverflowException or InvalidOperationException)
        { throw new IndexStoreException("INDEX_INVALID_SNAPSHOT", "Snapshot data does not satisfy the index contract.", error); }
    }

    public static void ValidateRelativePath(string path)
    {
        Identifier(path, "Relative path", MaximumPathUtf16);
        Require(!path.StartsWith('/') && !path.Contains(':') && !path.Contains('\\')
            && !path.Split('/').Any(s => s is "" or "." or ".."), "Use an owner-normalized workspace-relative path.");
    }

    public static void ValidateHash(string hash)
        => Require(hash is not null && hash.Length == 64 && !hash.AsSpan().ContainsAnyExcept("0123456789abcdef"),
            "A lowercase full SHA256 hash is required.");

    private static void ValidateCore(IndexSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        HarnessGraphContract.ValidateHeader(snapshot.Graph);
        var graph = snapshot.Graph;
        Identifier(graph.SnapshotId, "Snapshot ID");
        var workspace = Guid.ParseExact(graph.WorkspaceId[3..], "N");
        Require(graph.WorkspaceId == HarnessIdentity.WorkspaceId(workspace), "Workspace identity is not canonical.");
        var nodes = Unique(graph.Nodes, n => n.Id, "Node");
        var variants = Unique(graph.Variants, v => v.Id, "Variant");
        var occurrences = Unique(graph.SymbolOccurrences, o => o.Id, "Occurrence");
        _ = Unique(graph.Edges, e => e.Id, "Edge");
        var files = Unique(snapshot.Files, f => f.SourceId, "Source file");
        var filesByPath = new Dictionary<string, IndexFile>(StringComparer.OrdinalIgnoreCase);
        long inputBytes = 0;
        foreach (var file in files.Values)
        {
            ValidateRelativePath(file.RelativePath); Identifier(file.Kind, "File kind", 128); ValidateHash(file.ContentHash);
            Require(file.ByteLength is >= 0 and <= MaximumInputFileBytes
                && (file.Utf16Length is null || file.Utf16Length is >= 0 and <= MaximumInputFileUtf16),
                "File lengths must be nonnegative and within the selected-input limits.");
            if (!file.Deleted)
            {
                inputBytes = checked(inputBytes + file.ByteLength);
                Require(inputBytes <= MaximumInputTotalBytes, "Live input files exceed the snapshot byte limit.");
            }
            Require(filesByPath.TryAdd(file.RelativePath, file), "File paths must be unique, including case aliases.");
        }
        Require(nodes.TryGetValue(graph.WorkspaceId, out var root) && root.Kind == HarnessNodeKind.Workspace
            && root.ParentId is null && graph.Nodes.Count(n => n.Kind == HarnessNodeKind.Workspace) == 1,
            "The graph needs exactly its declared workspace root.");
        foreach (var node in nodes.Values)
        {
            Require(Enum.IsDefined(node.Kind), "Unknown node kind.");
            Text(node.Name, "Node name", MaximumSearchFieldUtf16);
            if (node.Signature is not null) Text(node.Signature, "Node signature", MaximumSearchFieldUtf16);
            if (node.ParentId is not null) Require(nodes.ContainsKey(node.ParentId), "Node parent does not exist.");
            switch (node.Kind)
            {
                case HarnessNodeKind.Project:
                    HashId(node.Id, "hpr");
                    Require(node.ParentId == graph.WorkspaceId, "A project must belong to the workspace root.");
                    break;
                case HarnessNodeKind.Namespace or HarnessNodeKind.Type or HarnessNodeKind.Member or HarnessNodeKind.ExternalSymbol:
                    HashId(node.Id, "hsym"); break;
                case HarnessNodeKind.Document: HashId(node.Id, "hdoc"); break;
                case HarnessNodeKind.Section: HashId(node.Id, "hsec"); break;
                // SourceFile and snapshot-local Markdown auxiliary IDs have no Core allocation contract.
                // Their owner-provided IDs still pass the unique, bounded identifier/reference checks.
            }
            Location(node.Location);
        }
        var owners = new Dictionary<string, (string? Project, string? Document)>(StringComparer.Ordinal);
        foreach (var node in nodes.Values) Owner(node.Id);
        foreach (var variant in variants.Values)
        {
            Require(nodes.TryGetValue(variant.ProjectId, out var project) && project.Kind == HarnessNodeKind.Project,
                "Variant project does not exist.");
            Identifier(variant.TargetFramework, "Target framework", 256); Identifier(variant.Configuration, "Configuration", 256);
            OptionalIdentifier(variant.Platform, "Platform", 256); OptionalIdentifier(variant.RuntimeIdentifier, "Runtime identifier", 256);
            if (variant.AnalysisFingerprint is not null) Identifier(variant.AnalysisFingerprint, "Analysis fingerprint", 4096);
            Require(variant.Id == HarnessIdentity.VariantId(workspace, variant.ProjectId, variant.TargetFramework,
                variant.Configuration, variant.Platform, variant.RuntimeIdentifier), "Variant identity does not match its selection.");
        }
        foreach (var occurrence in occurrences.Values)
        {
            Require(nodes.TryGetValue(occurrence.LogicalSymbolId, out var logical) && IsSymbol(logical.Kind),
                "Occurrence logical symbol does not exist.");
            Require(variants.TryGetValue(occurrence.VariantId, out var variant), "Occurrence variant does not exist.");
            Require(occurrence.Id == HarnessIdentity.SymbolOccurrenceId(workspace, occurrence.LogicalSymbolId, occurrence.VariantId),
                "Occurrence identity does not match its logical symbol and variant.");
            if (logical.Kind != HarnessNodeKind.ExternalSymbol)
                Require(Owner(logical.Id).Project == variant.ProjectId, "Occurrence variant belongs to another project.");
            Location(occurrence.Location);
            if (occurrence.Declarations is not null)
            {
                Count(occurrence.Declarations, "Declarations");
                var declarations = new HashSet<HarnessLocation>();
                foreach (var declaration in occurrence.Declarations)
                {
                    Require(declaration is not null && declarations.Add(declaration), "Declaration locations must be non-null and unique.");
                    Location(declaration);
                }
                Require(occurrence.Location is null || declarations.Contains(occurrence.Location),
                    "The primary occurrence location must be one of its declared locations.");
            }
        }
        foreach (var edge in graph.Edges)
        {
            Require(nodes.ContainsKey(edge.SourceNodeId) && nodes.ContainsKey(edge.TargetNodeId), "Edge endpoints do not exist.");
            Require(Enum.IsDefined(edge.Certainty), "Unknown edge certainty.");
            // Kind/producer are extensible strings in harness-v1, unlike the closed node/certainty enums.
            Identifier(edge.Kind, "Edge kind", 128); Identifier(edge.Producer, "Edge producer", 256);
            if (edge.Origin is not null) Identifier(edge.Origin, "Edge origin", 256);
            if (edge.VariantId is not null) Require(variants.ContainsKey(edge.VariantId), "Edge variant does not exist.");
            if (edge.SourceOccurrenceId is not null)
            {
                Require(occurrences.TryGetValue(edge.SourceOccurrenceId, out var source)
                    && source.LogicalSymbolId == edge.SourceNodeId, "Edge source occurrence does not match its logical endpoint.");
                Require(edge.VariantId == source.VariantId, "Edge variant does not match its source occurrence.");
            }
            if (edge.TargetOccurrenceId is not null)
            {
                Require(occurrences.TryGetValue(edge.TargetOccurrenceId, out var target)
                    && target.LogicalSymbolId == edge.TargetNodeId, "Edge target occurrence does not match its logical endpoint.");
                // Cross-project calls legitimately target a different variant. A Markdown selection with no
                // source occurrence, however, must agree with the target occurrence it explicitly selects.
                if (edge.SourceOccurrenceId is null && edge.VariantId is not null)
                    Require(edge.VariantId == target.VariantId, "Selected target occurrence belongs to another variant.");
            }
            Location(edge.Evidence);
        }
        Count(graph.LegacyReferences, "Legacy references");
        var legacy = new HashSet<(int Schema, string Legacy, string Node, string? Occurrence)>();
        foreach (var reference in graph.LegacyReferences)
        {
            Require(reference is not null, "Legacy reference cannot be null.");
            Identifier(reference.LegacyId, "Legacy ID");
            Require(reference.SchemaVersion == 2 && nodes.ContainsKey(reference.NodeId), "Unsupported or dangling legacy reference.");
            Require(legacy.Add((reference.SchemaVersion, reference.LegacyId, reference.NodeId, reference.OccurrenceId)),
                "Legacy references must be unique per occurrence.");
            if (reference.OccurrenceId is not null)
                Require(occurrences.TryGetValue(reference.OccurrenceId, out var occurrence)
                    && occurrence.LogicalSymbolId == reference.NodeId, "Legacy occurrence does not match its logical node.");
        }
        if (graph.Markdown is { } markdown)
        {
            Identifier(markdown.ComponentVersion, "Markdown component version", 256); ValidateHash(markdown.CanonicalSourceHash);
            Identifier(markdown.ParserVersion, "Markdown parser version", 256);
            Identifier(markdown.ContractVersion, "Markdown contract version", 128);
            Identifier(markdown.ProfileId, "Markdown profile", 256); ValidateHash(markdown.OptionsHash);
        }
        if (graph.Diagnostics is not null)
        {
            var diagnostics = Unique(graph.Diagnostics, d => d.Code, "Diagnostic");
            Require(diagnostics.Values.All(d => d.Count >= 0), "Diagnostic counts cannot be negative.");
        }
        Count(snapshot.Documents, "Documents");
        var documentIds = new HashSet<string>(StringComparer.Ordinal);
        var documentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitIds = new HashSet<string>(StringComparer.Ordinal);
        var sectionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in snapshot.Documents)
        {
            Require(document is not null && document.DocumentUuid != Guid.Empty, "A persisted document UUID is required.");
            ValidateRelativePath(document.RelativePath); ValidateHash(document.ContentHash); ValidateHash(document.OptionsHash);
            Identifier(document.ScopeId, "Document scope");
            Identifier(document.ParserVersion, "Document parser version", 256);
            Identifier(document.ContractVersion, "Document contract version", 128);
            Identifier(document.ProfileId, "Document profile", 256);
            if (document.ExplicitId is not null)
            {
                Identifier(document.ExplicitId, "Explicit document ID");
                Require(explicitIds.Add(document.ExplicitId), "Explicit document IDs must be unique.");
            }
            var id = HarnessIdentity.DocumentId(workspace, document.DocumentUuid);
            Require(documentIds.Add(id) && documentPaths.Add(document.RelativePath), "Document UUIDs and paths must be unique.");
            Require(filesByPath.TryGetValue(document.RelativePath, out var file) && !file.Deleted
                && file.ContentHash == document.ContentHash, "Document does not match a live manifest file.");
            Require(nodes.TryGetValue(id, out var node) && node.Kind == HarnessNodeKind.Document
                && node.Location?.SourceId == file.SourceId && node.Location.ContentHash == document.ContentHash,
                "Document identity does not match its graph node/source.");
            if (file.Utf16Length is { } length)
                Require(node.Location.RawSpan == new HarnessRawSpan(0, length), "Document graph span must cover its complete raw source.");
            Require(graph.Markdown is { } stamp && stamp.ParserVersion == document.ParserVersion
                && stamp.ContractVersion == document.ContractVersion && stamp.ProfileId == document.ProfileId
                && stamp.OptionsHash == document.OptionsHash, "Document parser/profile/options stamps do not match the graph.");
            Count(document.Sections, "Document sections");
            var localKeys = new HashSet<string>(StringComparer.Ordinal);
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            foreach (var section in document.Sections)
            {
                Require(section is not null, "Section identity cannot be null.");
                Identifier(section.LocalKey, "Section local key"); Identifier(section.Token, "Section token");
                Require(localKeys.Add(section.LocalKey) && tokens.Add(section.Token), "Section local keys and tokens must be unique per document.");
                if (section.HeadingKey is not null) Text(section.HeadingKey, "Section heading key", MaximumSearchFieldUtf16);
                if (section.BodyHash is not null) ValidateHash(section.BodyHash);
                var sectionId = HarnessIdentity.SectionId(workspace, id, section.Token);
                Require(sectionIds.Add(sectionId) && nodes.TryGetValue(sectionId, out var sectionNode)
                    && sectionNode.Kind == HarnessNodeKind.Section && Owner(sectionId).Document == id
                    && sectionNode.Location?.SourceId == file.SourceId, "Section token does not match its graph document/source.");
            }
        }
        Require(graph.Nodes.Where(n => n.Kind == HarnessNodeKind.Document).All(n => documentIds.Contains(n.Id))
            && graph.Nodes.Where(n => n.Kind == HarnessNodeKind.Section).All(n => sectionIds.Contains(n.Id)),
            "Graph documents and sections must have corresponding persisted identities.");
        Count(snapshot.Aliases, "Aliases");
        var aliases = new HashSet<(string Kind, string Old, string New)>();
        foreach (var alias in snapshot.Aliases)
        {
            Require(alias is not null, "Alias cannot be null.");
            Identifier(alias.Kind, "Alias kind", 128); Identifier(alias.OldId, "Alias old ID"); Identifier(alias.NewId, "Alias new ID");
            Identifier(alias.Reason, "Alias reason", 4096);
            Require(Enum.IsDefined(alias.Certainty) && nodes.ContainsKey(alias.NewId), "Alias certainty or target is invalid.");
            Require(aliases.Add((alias.Kind, alias.OldId, alias.NewId)), "Aliases must be unique.");
        }
        var search = Unique(snapshot.SearchText, s => s.NodeId, "Search node");
        long searchLength = 0;
        foreach (var row in search.Values)
        {
            Require(nodes.ContainsKey(row.NodeId), "Search node does not exist.");
            foreach (var value in new[] { row.Name, row.Signature, row.Heading, row.Body, row.Path })
            {
                Text(value, "Search field", MaximumSearchFieldUtf16);
                searchLength = checked(searchLength + value.Length);
                Require(searchLength <= MaximumSearchTextUtf16, "Selected search text exceeds the snapshot limit.");
            }
            if (row.Path.Length != 0) ValidateRelativePath(row.Path);
        }

        void Location(HarnessLocation? location)
        {
            if (location is null) return;
            Identifier(location.SourceId, "Location source ID");
            Require(files.TryGetValue(location.SourceId, out var file) && !file.Deleted, "Location source is not a live manifest file.");
            Require(location.ContentHash == file.ContentHash, "Location content hash differs from its manifest source.");
            if (location.SourceVersion is not null) Text(location.SourceVersion, "Source version", 4096);
            if (location.RawSpan is { } span)
                Require(span.Start >= 0 && span.Length >= 0 && (long)span.Start + span.Length <= int.MaxValue
                    && (file.Utf16Length is null || (long)span.Start + span.Length <= file.Utf16Length.Value),
                    "Location range exceeds the original UTF-16 source.");
        }

        (string? Project, string? Document) Owner(string nodeId)
        {
            var pending = new List<HarnessNode>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cursor = nodeId;
            (string? Project, string? Document) owner = default;
            while (!owners.TryGetValue(cursor, out owner))
            {
                Require(seen.Add(cursor) && pending.Count < 4096, "Node parents contain a cycle or exceed the depth limit.");
                var node = nodes[cursor]; pending.Add(node);
                if (node.ParentId is null) { owner = default; break; }
                cursor = node.ParentId;
            }
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var node = pending[i];
                if (node.Kind == HarnessNodeKind.Project) owner.Project = node.Id;
                if (node.Kind == HarnessNodeKind.Document) owner.Document = node.Id;
                owners.Add(node.Id, owner);
            }
            return owners[nodeId];
        }
    }

    private static bool IsSymbol(HarnessNodeKind kind)
        => kind is HarnessNodeKind.Namespace or HarnessNodeKind.Type or HarnessNodeKind.Member or HarnessNodeKind.ExternalSymbol;

    private static Dictionary<string, T> Unique<T>(IReadOnlyList<T> values, Func<T, string> key, string label) where T : class
    {
        Count(values, label);
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            Require(value is not null, label + " cannot be null.");
            var id = key(value); Identifier(id, label + " ID");
            Require(result.TryAdd(id, value), label + " IDs must be unique.");
        }
        return result;
    }

    private static void Count<T>(IReadOnlyList<T> values, string label)
        => Require(values is not null && values.Count <= MaximumCollectionItems, label + " must be present and bounded.");

    private static void HashId(string id, string prefix)
        => Require(id.Length == prefix.Length + 65 && id.StartsWith(prefix + "_", StringComparison.Ordinal)
            && !id.AsSpan(prefix.Length + 1).ContainsAnyExcept("0123456789abcdef"), "Non-canonical harness identity.");

    private static void OptionalIdentifier(string? value, string label, int limit)
    {
        if (value is not null) Identifier(value, label, limit);
    }

    private static void Identifier(string value, string label, int limit = 2048)
    {
        Text(value, label, limit);
        Require(!string.IsNullOrWhiteSpace(value) && value == value.Trim() && !value.Any(char.IsControl),
            label + " must be nonempty and owner-normalized.");
    }

    private static void Text(string value, string label, int limit)
    {
        Require(value is not null && value.Length <= limit && !value.Contains('\0'), label + " must be present and bounded without NUL.");
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException error)
        { throw new IndexStoreException("INDEX_INVALID_SNAPSHOT", label + " contains invalid UTF-16.", error); }
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new IndexStoreException("INDEX_INVALID_SNAPSHOT", message);
    }
}
