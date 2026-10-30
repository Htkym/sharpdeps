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

    public static void Validate(IndexSnapshot snapshot, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try { ValidateCore(snapshot, token); token.ThrowIfCancellationRequested(); }
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

    private static void ValidateCore(IndexSnapshot snapshot, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        HarnessGraphContract.ValidateHeader(snapshot.Graph);
        var graph = snapshot.Graph;
        Identifier(graph.SnapshotId, "Snapshot ID");
        var workspace = Guid.ParseExact(graph.WorkspaceId[3..], "N");
        Require(graph.WorkspaceId == HarnessIdentity.WorkspaceId(workspace), "Workspace identity is not canonical.");
        var nodes = Unique(graph.Nodes, n => n.Id, "Node", token);
        var variants = Unique(graph.Variants, v => v.Id, "Variant", token);
        var occurrences = Unique(graph.SymbolOccurrences, o => o.Id, "Occurrence", token);
        _ = Unique(graph.Edges, e => e.Id, "Edge", token);
        var files = Unique(snapshot.Files, f => f.SourceId, "Source file", token);
        var filesByPath = new Dictionary<string, IndexFile>(StringComparer.OrdinalIgnoreCase);
        long inputBytes = 0;
        foreach (var file in files.Values)
        {
            token.ThrowIfCancellationRequested();
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
            token.ThrowIfCancellationRequested();
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
        foreach (var node in nodes.Values) { token.ThrowIfCancellationRequested(); Owner(node.Id); }
        foreach (var variant in variants.Values)
        {
            token.ThrowIfCancellationRequested();
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
            token.ThrowIfCancellationRequested();
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
                    token.ThrowIfCancellationRequested();
                    Require(declaration is not null && declarations.Add(declaration), "Declaration locations must be non-null and unique.");
                    Location(declaration);
                }
                Require(occurrence.Location is null || declarations.Contains(occurrence.Location),
                    "The primary occurrence location must be one of its declared locations.");
            }
        }
        var catalogOccurrences = files.Values.Any(f => f.Reuse is not null)
            ? Checked(occurrences.Values, token).GroupBy(o => o.LogicalSymbolId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal) : null;
        foreach (var file in files.Values.Where(f => f.Reuse is not null))
        {
            token.ThrowIfCancellationRequested();
            var reuse = file.Reuse!;
            Require(!file.Deleted, "Reuse proof must belong to a live target input.");
            Identifier(reuse.OwnerVersion, "Reuse owner version", 128);
            ValidateHash(reuse.OptionsHash); ValidateHash(reuse.InputHash);
            ValidateHash(reuse.EnvironmentHash); ValidateHash(reuse.DependencyHash);
            Identifier(reuse.SdkPath, "Reuse SDK path", MaximumPathUtf16);
            Count(reuse.Catalog, "Reuse catalog");
            long catalogLength = 0;
            foreach (var symbol in reuse.Catalog)
            {
                token.ThrowIfCancellationRequested();
                Require(symbol is not null, "Reuse catalog symbol cannot be null.");
                Require(nodes.TryGetValue(symbol.NodeId, out var node)
                    && node.Kind is HarnessNodeKind.Type or HarnessNodeKind.Member or HarnessNodeKind.ExternalSymbol,
                    "Reuse catalog must identify a real symbol node.");
                foreach (var value in new[] { symbol.FullName, symbol.ShortName, symbol.DocumentationId,
                    symbol.Project, symbol.Namespace, symbol.VariantId, symbol.SourceId })
                {
                    token.ThrowIfCancellationRequested();
                    if (value is null) continue;
                    Text(value, "Reuse catalog field", MaximumSearchFieldUtf16);
                    catalogLength = checked(catalogLength + value.Length);
                    Require(catalogLength <= MaximumSearchTextUtf16, "Reuse catalog exceeds its text budget.");
                }
                Require(!string.IsNullOrWhiteSpace(symbol.FullName) && !string.IsNullOrWhiteSpace(symbol.ShortName),
                    "Reuse catalog needs exact symbol names.");
                var symbolOccurrences = catalogOccurrences!.GetValueOrDefault(symbol.NodeId) ?? [];
                if (symbol.VariantId is not null)
                    Require(variants.ContainsKey(symbol.VariantId) && symbolOccurrences.Any(o => o.VariantId == symbol.VariantId),
                        "Reuse catalog variant must identify an actual occurrence.");
                if (symbol.SourceId is not null)
                    Require(files.ContainsKey(symbol.SourceId) && (node.Location?.SourceId == symbol.SourceId
                        || symbolOccurrences.Any(o => (symbol.VariantId is null || o.VariantId == symbol.VariantId)
                            && (o.Location?.SourceId == symbol.SourceId || o.Declarations?.Any(d => d.SourceId == symbol.SourceId) == true))),
                        "Reuse catalog source must identify an actual declaration.");
            }
            if (reuse.Projects is { } projects)
            {
                Identifier(projects.Version, "Project reuse version", 128);
                ValidateHash(projects.StructureHash);
                Require(projects.Variants is { Count: > 0 and <= 64 }, "Project reuse needs a bounded variant set.");
                var projectProofs = Unique(projects.Variants, p => p.Key, "Project reuse key", token);
                Require(projectProofs.Values.Select(p => p.VariantId).Distinct(StringComparer.Ordinal).Count() == variants.Count
                    && projectProofs.Count == variants.Count, "Project proof must cover exactly the graph variants.");
                var sources = new HashSet<string>(StringComparer.Ordinal);
                long dataLength = 0;
                foreach (var project in projectProofs.Values)
                {
                    token.ThrowIfCancellationRequested();
                    Require(variants.TryGetValue(project.VariantId, out var variant) && variant.ProjectId == project.ProjectId,
                        "Project proof must bind the actual project variant.");
                    Count(project.SourceIds, "Project reuse sources"); Count(project.References, "Project reuse references");
                    Require(Checked(project.SourceIds, token).Distinct(StringComparer.Ordinal).Count() == project.SourceIds.Count
                        && Checked(project.References, token).Distinct(StringComparer.Ordinal).Count() == project.References.Count,
                        "Project proof source/reference sets must be unique.");
                    foreach (var source in project.SourceIds)
                    {
                        token.ThrowIfCancellationRequested();
                        Require(files.TryGetValue(source, out var input) && input.Kind == "source" && !input.Deleted,
                            "Project proof source must be a live captured compiler input.");
                        sources.Add(source);
                    }
                    foreach (var reference in Checked(project.References, token))
                        Require(reference != project.Key && projectProofs.ContainsKey(reference), "Project reference proof is not closed.");
                    Require(project.Data.ValueKind == JsonValueKind.Object, "Project extraction data must be an object.");
                    dataLength = checked(dataLength + project.Data.GetRawText().Length);
                    Require(dataLength <= MaximumSearchTextUtf16, "Project extraction proof exceeds its bounded text budget.");
                }
                Require(sources.SetEquals(Checked(files.Values, token).Where(f => !f.Deleted && f.Kind == "source").Select(f => f.SourceId)),
                    "Project proof must own every captured compiler input; shared inputs may have several owners.");
            }
        }
        long extensionDisplayLength = 0;
        long httpGetTemplateLength = 0;
        foreach (var edge in graph.Edges)
        {
            token.ThrowIfCancellationRequested();
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
            if (edge.ExtensionBinding is { } binding)
            {
                Require(edge.Kind == "calls" && edge.Certainty == HarnessCertainty.Resolved
                    && edge.Producer == "roslyn-operation" && edge.SourceOccurrenceId is not null
                    && edge.TargetOccurrenceId is not null && edge.Evidence is { RawSpan: not null, ContentHash: not null },
                    "Extension binding metadata needs an occurrence-bound resolved Roslyn call with saved evidence.");
                Require(Enum.IsDefined(binding.State), "Unknown extension binding state.");
                if (binding.State == HarnessExtensionBindingState.Saved)
                {
                    Require(binding.OmissionReason is null, "Saved extension binding metadata cannot have an omission reason.");
                    Identifier(binding.ReceiverTypeDisplay!, "Extension receiver display", HarnessExtensionBinding.MaximumReceiverDisplayUtf16);
                    Identifier(binding.ConstructedMethodDisplay!, "Extension method display", HarnessExtensionBinding.MaximumMethodDisplayUtf16);
                    extensionDisplayLength = checked(extensionDisplayLength + binding.ReceiverTypeDisplay!.Length
                        + binding.ConstructedMethodDisplay!.Length);
                    Require(extensionDisplayLength <= HarnessExtensionBinding.MaximumSnapshotDisplayUtf16,
                        "Extension binding displays exceed the snapshot text budget.");
                }
                else
                    Require(binding.ReceiverTypeDisplay is null && binding.ConstructedMethodDisplay is null
                        && binding.OmissionReason is "unsupported-type" or "metadata-budget" or "semantic-unavailable",
                        "Omitted extension binding metadata needs only a supported omission reason.");
            }
            if (edge.HttpGetDeclaration is { } declaration)
            {
                Require(edge.Kind == "attribute" && edge.Certainty == HarnessCertainty.Resolved
                    && edge.Producer == "roslyn-declaration" && edge.SourceOccurrenceId is not null
                    && nodes[edge.SourceNodeId].Kind == HarnessNodeKind.Member
                    && nodes[edge.TargetNodeId].Kind is HarnessNodeKind.Type or HarnessNodeKind.ExternalSymbol
                    && (edge.TargetOccurrenceId is not null || nodes[edge.TargetNodeId].Kind == HarnessNodeKind.ExternalSymbol)
                    && edge.Evidence is { RawSpan: { Length: > 0 }, ContentHash: not null },
                    "HttpGet metadata needs a resolved Roslyn member attribute with saved name evidence.");
                var evidence = edge.Evidence!;
                var nameSpan = evidence.RawSpan!.Value;
                var source = occurrences[edge.SourceOccurrenceId!];
                Require(DeclaredHere(source.Location) || source.Declarations?.Any(location => DeclaredHere(location)) == true,
                    "HttpGet name evidence must belong to its saved member declaration.");
                Require(Enum.IsDefined(declaration.State), "Unknown HttpGet declaration state.");
                if (declaration.State == HarnessHttpGetDeclarationState.Saved)
                {
                    Require(declaration.OmissionReason is null && declaration.TemplateSpan is not null,
                        "Saved HttpGet metadata needs its complete template and literal span only.");
                    Text(declaration.Template!, "HttpGet template", HarnessHttpGetDeclaration.MaximumTemplateUtf16);
                    Require(!declaration.Template!.Any(char.IsControl), "HttpGet template cannot contain control characters.");
                    var literal = declaration.TemplateSpan!.Value;
                    var end = (long)literal.Start + literal.Length;
                    var file = files[evidence.SourceId];
                    Require(literal.Length is > 0 and <= HarnessHttpGetDeclaration.MaximumLiteralSpanUtf16
                        && literal.Start >= nameSpan.End && end <= int.MaxValue
                        && (file.Utf16Length is null || end <= file.Utf16Length.Value),
                        "HttpGet literal range must follow its attribute name within the original UTF-16 source.");
                    Require(DeclaredHere(source.Location, literal) || source.Declarations?.Any(location => DeclaredHere(location, literal)) == true,
                        "HttpGet literal evidence must belong to its saved member declaration.");
                    httpGetTemplateLength = checked(httpGetTemplateLength + declaration.Template!.Length);
                    Require(httpGetTemplateLength <= HarnessHttpGetDeclaration.MaximumSnapshotTemplateUtf16,
                        "HttpGet templates exceed the snapshot text budget.");
                }
                else
                    Require(declaration.Template is null && declaration.TemplateSpan is null
                        && declaration.OmissionReason is "no-template" or "nonliteral-template" or "metadata-budget" or "semantic-unavailable",
                        "Omitted HttpGet metadata needs only a supported omission reason.");

                bool DeclaredHere(HarnessLocation? location, HarnessRawSpan? literal = null)
                {
                    token.ThrowIfCancellationRequested();
                    return location is { RawSpan: { } range } && location.SourceId == evidence.SourceId
                        && location.SourceVersion == evidence.SourceVersion && location.ContentHash == evidence.ContentHash
                        && range.Start <= nameSpan.Start && range.End >= (literal?.End ?? nameSpan.End);
                }
            }
        }
        Count(graph.LegacyReferences, "Legacy references");
        var legacy = new HashSet<(int Schema, string Legacy, string Node, string? Occurrence)>();
        foreach (var reference in graph.LegacyReferences)
        {
            token.ThrowIfCancellationRequested();
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
            var diagnostics = Unique(graph.Diagnostics, d => d.Code, "Diagnostic", token);
            Require(diagnostics.Values.All(d => d.Count >= 0), "Diagnostic counts cannot be negative.");
        }
        Count(snapshot.Documents, "Documents");
        var documentIds = new HashSet<string>(StringComparer.Ordinal);
        var documentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitIds = new HashSet<string>(StringComparer.Ordinal);
        var sectionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in snapshot.Documents)
        {
            token.ThrowIfCancellationRequested();
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
                token.ThrowIfCancellationRequested();
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
        Require(Checked(graph.Nodes, token).Where(n => n.Kind == HarnessNodeKind.Document).All(n => documentIds.Contains(n.Id))
            && Checked(graph.Nodes, token).Where(n => n.Kind == HarnessNodeKind.Section).All(n => sectionIds.Contains(n.Id)),
            "Graph documents and sections must have corresponding persisted identities.");
        Count(snapshot.Aliases, "Aliases");
        var aliases = new HashSet<(string Kind, string Old, string New)>();
        foreach (var alias in snapshot.Aliases)
        {
            token.ThrowIfCancellationRequested();
            Require(alias is not null, "Alias cannot be null.");
            Identifier(alias.Kind, "Alias kind", 128); Identifier(alias.OldId, "Alias old ID"); Identifier(alias.NewId, "Alias new ID");
            Identifier(alias.Reason, "Alias reason", 4096);
            Require(Enum.IsDefined(alias.Certainty) && nodes.ContainsKey(alias.NewId), "Alias certainty or target is invalid.");
            Require(aliases.Add((alias.Kind, alias.OldId, alias.NewId)), "Aliases must be unique.");
        }
        var search = Unique(snapshot.SearchText, s => s.NodeId, "Search node", token);
        long searchLength = 0;
        foreach (var row in search.Values)
        {
            token.ThrowIfCancellationRequested();
            Require(nodes.ContainsKey(row.NodeId), "Search node does not exist.");
            foreach (var value in new[] { row.Name, row.Signature, row.Heading, row.Body, row.Path })
            {
                token.ThrowIfCancellationRequested();
                Text(value, "Search field", MaximumSearchFieldUtf16);
                searchLength = checked(searchLength + value.Length);
                Require(searchLength <= MaximumSearchTextUtf16, "Selected search text exceeds the snapshot limit.");
            }
            if (row.Path.Length != 0) ValidateRelativePath(row.Path);
        }

        void Location(HarnessLocation? location)
        {
            token.ThrowIfCancellationRequested();
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
                token.ThrowIfCancellationRequested();
                Require(seen.Add(cursor) && pending.Count < 4096, "Node parents contain a cycle or exceed the depth limit.");
                var node = nodes[cursor]; pending.Add(node);
                if (node.ParentId is null) { owner = default; break; }
                cursor = node.ParentId;
            }
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                token.ThrowIfCancellationRequested();
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

    private static Dictionary<string, T> Unique<T>(IReadOnlyList<T> values, Func<T, string> key, string label, CancellationToken token) where T : class
    {
        Count(values, label);
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            Require(value is not null, label + " cannot be null.");
            var id = key(value); Identifier(id, label + " ID");
            Require(result.TryAdd(id, value), label + " IDs must be unique.");
        }
        return result;
    }

    private static IEnumerable<T> Checked<T>(IEnumerable<T> values, CancellationToken token)
    {
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            yield return value;
        }
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
