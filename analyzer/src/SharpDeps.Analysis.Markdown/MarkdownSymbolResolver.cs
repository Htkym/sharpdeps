namespace SharpDeps.Analysis.Markdown;

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Syntamark;
using SharpDeps.Analysis.Contracts.Harness;

/// <summary>The trusted owner supplies aliases from actual nodes/occurrences in Graph; no files are opened.</summary>
public sealed record MarkdownSymbolCandidate(string NodeId, string FullName, string ShortName,
    string? DocumentationId = null, string? Project = null, string? Namespace = null, string? VariantId = null,
    string? SourceId = null);
public sealed record MarkdownSymbolCatalog(HarnessGraphEnvelope Graph, IReadOnlyList<MarkdownSymbolCandidate> Symbols,
    IReadOnlyDictionary<string, string>? CodeFileAliases = null);
public sealed record MarkdownSymbolScope(string? Project = null, string? Namespace = null, string? VariantId = null);
public enum MarkdownMentionOrigin { ExplicitMetadata, CodeFileLink, CodeFence, BodyText }

public sealed record MarkdownSymbolMention(string Id, string OwnerNodeId, string Token, string Selector,
    string Relation, MarkdownMentionOrigin Origin, MarkdownSymbolScope Scope, HarnessLocation Location,
    IReadOnlyList<HarnessRawSpan> RawFragments, MarkdownMappingPrecision Precision, string? Snippet,
    HarnessCertainty Certainty, string? TargetNodeId, IReadOnlyList<string> CandidateNodeIds, string? Reason);

/// <summary>Complete must mean old/new selectors, targets and scopes are all known. Otherwise every mention is revisited.</summary>
public sealed record MarkdownSymbolChanges(IReadOnlyList<string>? Tokens = null,
    IReadOnlyList<string>? TargetNodeIds = null, IReadOnlyList<string>? ScopeKeys = null, bool Complete = false);

public sealed class MarkdownSymbolResolution
{
    internal MarkdownSymbolResolution(string workspaceId, string documentId, HarnessLocation documentLocation, string snapshotId, long generation,
        HarnessCoverage catalogCoverage,
        IReadOnlyList<MarkdownSymbolMention> mentions, IReadOnlyList<string> reasons)
    {
        WorkspaceId = workspaceId; DocumentId = documentId; DocumentLocation = documentLocation;
        SnapshotId = snapshotId; Generation = generation;
        CatalogCoverage = catalogCoverage;
        Mentions = mentions; Reasons = reasons;
        TokenIndex = Index(mentions.SelectMany(m => new[] { m.Token,
            m.Token.StartsWith("global::", StringComparison.Ordinal) ? m.Token[8..] : m.Token }.Select(t => (t, m.Id))));
        TargetIndex = Index(mentions.SelectMany(m => m.CandidateNodeIds.Select(t => (t, m.Id))));
        ScopeIndex = Index(mentions.Select(m => (MarkdownSymbolResolver.ScopeKey(m.Scope), m.Id)));
    }
    public string WorkspaceId { get; }
    public string DocumentId { get; }
    public HarnessLocation DocumentLocation { get; }
    public string SnapshotId { get; }
    public long Generation { get; }
    public HarnessCoverage CatalogCoverage { get; }
    public IReadOnlyList<MarkdownSymbolMention> Mentions { get; }
    public IReadOnlyList<string> Reasons { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TokenIndex { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TargetIndex { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ScopeIndex { get; }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Index(IEnumerable<(string Key, string Id)> entries)
        => new ReadOnlyDictionary<string, IReadOnlyList<string>>(entries.GroupBy(e => e.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)Array.AsReadOnly(g.Select(e => e.Id)
                .Distinct(StringComparer.Ordinal).ToArray()), StringComparer.Ordinal));
}

/// <summary>Pure mention binding over shared parser facts. A code example never becomes a calls/conforms edge.</summary>
public static class MarkdownSymbolResolver
{
    private const string CatalogIncomplete = "catalog-incomplete";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    // Lexes names in already decoded shared projections; it does not parse Markdown or C#.
    private static readonly Regex Names = new(@"(?:global::)?[\p{L}_][\p{L}\p{Nd}_]*(?:\.[\p{L}_][\p{L}\p{Nd}_]*)*",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static MarkdownSymbolResolution Resolve(MarkdownGraphProjection projection, string rawText,
        MarkdownSymbolCatalog catalog, MarkdownSymbolScope? scope = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(rawText);
        cancellationToken.ThrowIfCancellationRequested();
        HarnessGraphContract.ValidateHeader(projection.Graph);
        var document = projection.Graph.Nodes.Single(n => n.Kind == HarnessNodeKind.Document);
        var facts = projection.Facts;
        MarkdownRuntimePin.RequireFacts(facts);
        if (projection.Graph.Markdown is not { } identity || identity.ParserVersion != facts.ParserVersion
            || identity.ContractVersion != facts.ContractVersion || identity.ProfileId != facts.ProfileId
            || identity.OptionsHash != facts.OptionsHash)
            throw new ArgumentException("Graph parser/profile/options identity must match the shared facts.");
        var hash = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(rawText))).ToLowerInvariant();
        if (facts.TextHash != hash || document.Location is not { } location || location.SourceId != facts.SourceId
            || location.SourceVersion != facts.SourceVersion || location.ContentHash != hash
            || location.RawSpan != new HarnessRawSpan(0, rawText.Length))
            throw new ArgumentException("Raw source and graph binding must match the parser source/version/TextHash.");
        ValidateCatalog(catalog, projection.Graph.WorkspaceId);
        scope ??= new MarkdownSymbolScope();
        var mentions = new List<MarkdownSymbolMention>();
        var reasons = new List<string>();
        foreach (var attribute in projection.Attributes.Where(a => a.Key == "sharpdeps.symbols"))
            AddMetadata(attribute.Source);
        // Definitions provide targets for ReferenceUse facts; they are not a second document mention.
        foreach (var link in projection.Links.Where(l => !l.Source.Image && l.Source.Target is not null
            && l.Source.Kind != MarkdownLinkKind.ReferenceDefinition))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fragments = link.Source.TargetRawSpan is { } span ? new[] { new HarnessRawSpan(span.Start, span.Length) }
                : Array.Empty<HarnessRawSpan>();
            AddEvidence(link.Source.Target!, "sourceId", link.OwnerNodeId, MarkdownMentionOrigin.CodeFileLink,
                scope, fragments, fragments.Length == 0 ? MarkdownMappingPrecision.Unknown : MarkdownMappingPrecision.CoveringTokens);
        }
        foreach (var fence in projection.Fences)
            AddText(fence.Source.Content, fence.Source.ContentSpan, fence.NodeId, MarkdownMentionOrigin.CodeFence);
        foreach (var region in facts.TextRegions.Where(r => r.Kind is "paragraph" or "tableCell"))
            AddText(region.Content, region.RawSpan, null, MarkdownMentionOrigin.BodyText);
        foreach (var heading in facts.Headings)
            AddText(heading.Text, heading.RawSpan, null, MarkdownMentionOrigin.BodyText);
        cancellationToken.ThrowIfCancellationRequested();
        if (mentions.Any(m => m.Origin == MarkdownMentionOrigin.BodyText))
            reasons.Add("inline-code-kind-unavailable: shared DTO merges inline code into body text; body bindings remain heuristic");
        if (facts.FrontMatter.State is MarkdownFrontMatterState.Invalid or MarkdownFrontMatterState.Unterminated or MarkdownFrontMatterState.Unknown)
            reasons.Add("front-matter-not-usable: no explicit symbol mapping was inferred from invalid/unknown YAML");
        if (catalog.Graph.Coverage != HarnessCoverage.CompleteWithinScope) reasons.Add(CatalogIncomplete);
        var result = Result(projection.Graph, document.Id, mentions, reasons, catalog.Graph.Coverage);
        cancellationToken.ThrowIfCancellationRequested();
        return result;

        void AddMetadata(MarkdownYamlNode node)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Kind == MarkdownYamlKind.Sequence)
            {
                foreach (var child in node.Children) AddMetadata(child);
                return;
            }
            if (node.Kind == MarkdownYamlKind.Scalar)
            {
                if (!string.IsNullOrWhiteSpace(node.Scalar.Text))
                    Add(node.Scalar.Text!, "auto", node.Scalar, new RawSpan(0, node.Scalar.Text!.Length),
                        node.RawSpan, document.Id, MarkdownMentionOrigin.ExplicitMetadata, scope);
                return;
            }
            var values = new Dictionary<string, MarkdownYamlNode>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < node.Children.Count; i += 2)
                if (node.Children[i].Scalar.Text is { } key && !values.TryAdd(key, node.Children[i + 1]))
                    throw new ArgumentException("Shared YAML facts must not contain duplicate mapping keys.");
            // Explicit IDs never fall back to a matching name after deletion/rename.
            var selector = new[] { "symbolId", "documentationId", "fullName", "name" }.FirstOrDefault(values.ContainsKey);
            if (selector is null || values[selector].Scalar.Text is not { Length: > 0 } token)
            {
                reasons.Add("unsupported-symbol-mapping: a scalar symbolId/documentationId/fullName/name selector is required");
                return;
            }
            var source = values[selector];
            if (new[] { "project", "namespace", "variant" }.Any(key => values.TryGetValue(key, out var value)
                && (value.Kind != MarkdownYamlKind.Scalar || string.IsNullOrWhiteSpace(value.Scalar.Text))))
            {
                reasons.Add("unsupported-symbol-scope: scope filters must be non-empty scalars");
                return;
            }
            var selectedScope = new MarkdownSymbolScope(Value("project") ?? scope.Project,
                Value("namespace") ?? scope.Namespace, Value("variant") ?? scope.VariantId);
            Add(token, selector, source.Scalar, new RawSpan(0, token.Length), source.RawSpan,
                document.Id, MarkdownMentionOrigin.ExplicitMetadata, selectedScope);
            string? Value(string key) => values.TryGetValue(key, out var value) ? value.Scalar.Text : null;
        }

        void AddText(MarkdownTextProjection text, RawSpan? region, string? owner, MarkdownMentionOrigin origin)
        {
            if (text.Text is not { } decoded) return;
            foreach (Match match in Names.Matches(decoded))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Add(match.Value, "auto", text, new RawSpan(match.Index, match.Length), region, owner, origin, scope);
            }
        }

        void Add(string token, string selector, MarkdownTextProjection text, RawSpan decodedSpan,
            RawSpan? region, string? owner, MarkdownMentionOrigin origin, MarkdownSymbolScope selectedScope)
        {
            var mapped = text.Map(decodedSpan);
            var fragments = mapped.RawFragments.Select(s => new HarnessRawSpan(s.Start, s.Length)).ToArray();
            HarnessRawSpan? covering = fragments.Length == 0 ? null : new(fragments.Min(s => s.Start),
                fragments.Max(s => s.End) - fragments.Min(s => s.Start));
            owner ??= projection.Sections.Where(s => s.Source.SubtreeSpan is { } span
                    && (covering?.Start ?? region?.Start) is { } start && start >= span.Start && start < span.End)
                .OrderBy(s => s.Source.SubtreeSpan!.Value.Length).Select(s => s.NodeId).FirstOrDefault() ?? document.Id;
            AddEvidence(token, selector, owner, origin, selectedScope, fragments, mapped.Precision);
        }

        void AddEvidence(string token, string selector, string owner, MarkdownMentionOrigin origin,
            MarkdownSymbolScope selectedScope, HarnessRawSpan[] fragments, MarkdownMappingPrecision precision)
        {
            if (fragments.Any(s => s.End > rawText.Length)) throw new ArgumentException("Parser mapping is outside raw source.");
            HarnessRawSpan? covering = fragments.Length == 0 ? null : new(fragments.Min(s => s.Start),
                fragments.Max(s => s.End) - fragments.Min(s => s.Start));
            var snippet = fragments.Length == 0 ? null : string.Concat(fragments.Select(s => rawText.Substring(s.Start, s.Length)));
            var mention = new MarkdownSymbolMention(LocalId(document.Id, hash, "mention", mentions.Count.ToString()),
                owner, token, selector, origin is MarkdownMentionOrigin.ExplicitMetadata or MarkdownMentionOrigin.CodeFileLink
                    ? "documents_symbol" : "mentions_symbol",
                origin, selectedScope, new(facts.SourceId, facts.SourceVersion, hash, covering),
                Array.AsReadOnly(fragments), precision, snippet, HarnessCertainty.Unresolved, null, Array.Empty<string>(), null);
            mentions.Add(Bind(mention, catalog));
        }
    }

    /// <summary>No parse or I/O occurs. Unknown change metadata safely rebinds every confirmed/candidate/unresolved mention.</summary>
    public static MarkdownSymbolResolution ReResolve(MarkdownSymbolResolution previous, MarkdownSymbolCatalog catalog,
        MarkdownSymbolChanges? changes = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCatalog(catalog, previous.WorkspaceId);
        HashSet<string>? affected = null;
        // Coverage applies to every mention, including confirmed ones outside the selected token changes.
        if (previous.CatalogCoverage == catalog.Graph.Coverage
            && changes is { Complete: true, Tokens: not null, TargetNodeIds: not null, ScopeKeys: not null })
        {
            affected = new(StringComparer.Ordinal);
            Include(previous.TokenIndex, changes.Tokens);
            Include(previous.TargetIndex, changes.TargetNodeIds);
            Include(previous.ScopeIndex, changes.ScopeKeys);
        }
        var mentions = previous.Mentions.Select(m =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return affected is null || affected.Contains(m.Id) ? Bind(m, catalog) : m;
        }).ToArray();
        var reasons = previous.Reasons.Where(r => r != CatalogIncomplete).ToList();
        if (catalog.Graph.Coverage != HarnessCoverage.CompleteWithinScope) reasons.Add(CatalogIncomplete);
        var result = new MarkdownSymbolResolution(previous.WorkspaceId, previous.DocumentId, previous.DocumentLocation,
            previous.SnapshotId, previous.Generation, catalog.Graph.Coverage,
            Array.AsReadOnly(mentions), Array.AsReadOnly(reasons.ToArray()));
        cancellationToken.ThrowIfCancellationRequested();
        return result;

        void Include(IReadOnlyDictionary<string, IReadOnlyList<string>> index, IReadOnlyList<string> keys)
        {
            foreach (var key in keys) if (index.TryGetValue(key, out var ids)) affected!.UnionWith(ids);
        }
    }

    /// <summary>Append evidence to this source generation. The owner first merges referenced nodes/variants/occurrences into graph.</summary>
    public static HarnessGraphEnvelope ProjectGraph(HarnessGraphEnvelope graph, MarkdownSymbolResolution resolution)
    {
        HarnessGraphContract.ValidateHeader(graph);
        ArgumentNullException.ThrowIfNull(resolution);
        if (graph.WorkspaceId != resolution.WorkspaceId || graph.SnapshotId != resolution.SnapshotId
            || graph.Generation != resolution.Generation || !graph.Nodes.Any(n => n.Id == resolution.DocumentId
                && n.Location == resolution.DocumentLocation))
            throw new ArgumentException("Mention evidence must be projected into its original workspace/source generation.");
        var existing = graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        if (resolution.Mentions.Any(m => !existing.Contains(m.OwnerNodeId)
            || m.CandidateNodeIds.Any(id => !existing.Contains(id) || (m.Scope.VariantId is { } variant
                && (!graph.Variants.Any(v => v.Id == variant) || !graph.SymbolOccurrences.Any(o =>
                    o.LogicalSymbolId == id && o.VariantId == variant))))))
            throw new ArgumentException("The owner must merge all mention owners and selected symbol nodes/variants/occurrences before projection.");
        var oldIds = resolution.Mentions.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var nodes = graph.Nodes.Where(n => !oldIds.Contains(n.Id)).Concat(resolution.Mentions.Select(m =>
            new HarnessNode(m.Id, HarnessNodeKind.SymbolMention, m.Token, m.OwnerNodeId, m.Location))).ToArray();
        var edges = graph.Edges.Where(e => !oldIds.Contains(e.SourceNodeId)).Concat(resolution.Mentions.SelectMany(m =>
            m.CandidateNodeIds.Select(target => new HarnessEdge(LocalId(m.Id, target, m.Relation), m.Id, target,
                null, m.Scope.VariantId is null ? null : graph.SymbolOccurrences.First(o =>
                    o.LogicalSymbolId == target && o.VariantId == m.Scope.VariantId).Id,
                m.Scope.VariantId, m.Relation, m.Certainty, "markdown-symbol-resolver", m.Location,
                m.Origin.ToString())))).ToArray();
        return graph with { Nodes = Array.AsReadOnly(nodes), Edges = Array.AsReadOnly(edges),
            Coverage = graph.Coverage == HarnessCoverage.Failed ? graph.Coverage
                : resolution.Reasons.Count != 0 || resolution.Mentions.Any(m => m.Certainty != HarnessCertainty.Resolved)
                    ? HarnessCoverage.Partial : graph.Coverage };
    }

    // Length prefixes preserve null/empty and prevent scope-key collisions; this is an index key, not a persistent ID.
    public static string ScopeKey(MarkdownSymbolScope scope)
        => string.Concat(new[] { scope.Project, scope.Namespace, scope.VariantId }.Select(s => s is null ? "-:" : s.Length + ":" + s));

    private static MarkdownSymbolMention Bind(MarkdownSymbolMention mention, MarkdownSymbolCatalog catalog)
    {
        var globallyQualified = mention.Token.StartsWith("global::", StringComparison.Ordinal);
        var token = globallyQualified ? mention.Token[8..] : mention.Token;
        var candidates = catalog.Symbols.Where(s => MatchesScope(s, mention.Scope));
        candidates = mention.Selector switch
        {
            "symbolId" => candidates.Where(s => s.NodeId == token),
            "documentationId" => candidates.Where(s => s.DocumentationId == token),
            "fullName" => candidates.Where(s => s.FullName == token),
            "name" => candidates.Where(s => s.ShortName == token),
            "sourceId" => candidates.Where(s => catalog.CodeFileAliases is { } aliases
                && aliases.TryGetValue(token, out var sourceId) && s.SourceId == sourceId),
            _ when mention.Selector == "auto" && globallyQualified => candidates.Where(s => s.FullName == token),
            _ when token.StartsWith("hsym_", StringComparison.Ordinal) => candidates.Where(s => s.NodeId == token),
            _ when token.Length > 2 && token[1] == ':' => candidates.Where(s => s.DocumentationId == token),
            _ => candidates.Where(s => s.FullName == token || s.ShortName == token),
        };
        // Filter occurrences before deduplicating their logical identity; two TFMs are not two symbols.
        var ids = candidates.Select(s => s.NodeId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var incompleteCatalog = catalog.Graph.Coverage != HarnessCoverage.CompleteWithinScope;
        var uncertain = incompleteCatalog || mention.Origin == MarkdownMentionOrigin.BodyText
            || mention.Precision is MarkdownMappingPrecision.Unknown or MarkdownMappingPrecision.Partial
            || mention.RawFragments.Count == 0;
        var certainty = ids.Length == 0 ? HarnessCertainty.Unresolved
            : ids.Length == 1 && !uncertain ? HarnessCertainty.Resolved : HarnessCertainty.Candidate;
        return mention with { Certainty = certainty, TargetNodeId = certainty == HarnessCertainty.Resolved ? ids[0] : null,
            CandidateNodeIds = Array.AsReadOnly(ids), Reason = incompleteCatalog ? CatalogIncomplete : ids.Length == 0
                ? mention.Selector == "sourceId" ? "code-file-alias-or-symbol-not-provided" : "no-matching-symbol"
                : ids.Length > 1 ? "ambiguous-symbol" : uncertain ? "heuristic-or-unknown-source-mapping" : null };
    }

    private static bool MatchesScope(MarkdownSymbolCandidate symbol, MarkdownSymbolScope scope)
        => (scope.Project is null || symbol.Project == scope.Project)
            && (scope.VariantId is null || symbol.VariantId == scope.VariantId)
            && (scope.Namespace is null || symbol.Namespace == scope.Namespace
                || symbol.Namespace?.StartsWith(scope.Namespace + ".", StringComparison.Ordinal) == true);

    private static void ValidateCatalog(MarkdownSymbolCatalog catalog, string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(catalog.Symbols);
        HarnessGraphContract.ValidateHeader(catalog.Graph);
        if (catalog.Graph.WorkspaceId != workspaceId) throw new ArgumentException("Symbol catalog belongs to another workspace.");
        var nodes = catalog.Graph.Nodes.Where(n => n.Kind is HarnessNodeKind.Type or HarnessNodeKind.Member or HarnessNodeKind.ExternalSymbol)
            .Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var symbol in catalog.Symbols)
        {
            if (symbol.NodeId is null || symbol.NodeId.Length != 69 || !symbol.NodeId.StartsWith("hsym_", StringComparison.Ordinal)
                || symbol.NodeId.AsSpan(5).ContainsAnyExcept("0123456789abcdef") || !nodes.Contains(symbol.NodeId)
                || string.IsNullOrWhiteSpace(symbol.FullName) || string.IsNullOrWhiteSpace(symbol.ShortName))
                throw new ArgumentException("Catalog entries must identify real harness symbol nodes and explicit names.");
            if (symbol.VariantId is not null && (!catalog.Graph.Variants.Any(v => v.Id == symbol.VariantId)
                || !catalog.Graph.SymbolOccurrences.Any(o => o.LogicalSymbolId == symbol.NodeId && o.VariantId == symbol.VariantId)))
                throw new ArgumentException("Catalog variant must identify an actual symbol occurrence.");
            if (symbol.SourceId is not null && !catalog.Graph.Nodes.Any(n => n.Id == symbol.NodeId && n.Location?.SourceId == symbol.SourceId)
                && !catalog.Graph.SymbolOccurrences.Any(o => o.LogicalSymbolId == symbol.NodeId
                    && (symbol.VariantId is null || symbol.VariantId == o.VariantId)
                    && (o.Location?.SourceId == symbol.SourceId || o.Declarations?.Any(d => d.SourceId == symbol.SourceId) == true)))
                throw new ArgumentException("Catalog source must identify an actual symbol declaration.");
        }
        if (catalog.CodeFileAliases is not null && catalog.CodeFileAliases.Any(a => string.IsNullOrWhiteSpace(a.Key)
            || string.IsNullOrWhiteSpace(a.Value) || (!catalog.Symbols.Any(s => s.SourceId == a.Value)
                && !catalog.Graph.Nodes.Any(n => n.Kind == HarnessNodeKind.SourceFile && n.Location?.SourceId == a.Value))))
            throw new ArgumentException("Code-file aliases must map exact shared targets to catalogued declaration source IDs.");
    }

    private static MarkdownSymbolResolution Result(HarnessGraphEnvelope graph, string documentId,
        List<MarkdownSymbolMention> mentions, IReadOnlyList<string> reasons, HarnessCoverage catalogCoverage)
        => new(graph.WorkspaceId, documentId, graph.Nodes.Single(n => n.Id == documentId).Location!, graph.SnapshotId, graph.Generation,
            catalogCoverage, Array.AsReadOnly(mentions.ToArray()), Array.AsReadOnly(reasons.ToArray()));

    private static string LocalId(params string[] parts)
        => "hmdf_" + Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(
            string.Concat(parts.Select(p => p.Length + ":" + p))))).ToLowerInvariant();
}
