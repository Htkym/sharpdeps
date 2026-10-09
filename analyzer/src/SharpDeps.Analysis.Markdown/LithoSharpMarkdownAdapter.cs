namespace SharpDeps.Analysis.Markdown;

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Syntamark;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;

/// <summary>Supplied text -> shared immutable facts -> harness graph. No link traversal, rendering or code execution.</summary>
public sealed class LithoSharpMarkdownAdapter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public MarkdownGraphProjection Analyze(MarkdownGraphRequest request, bool isTrusted, CancellationToken cancellationToken = default)
    {
        if (!HarnessTrustPolicy.Allows(HarnessOperation.Index, isTrusted))
            throw new UnauthorizedAccessException("Markdown indexing requires a trusted workspace.");
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SnapshotId);
        if (request.Generation < 0) throw new ArgumentOutOfRangeException(nameof(request.Generation));
        var workspaceId = HarnessIdentity.WorkspaceId(request.WorkspaceUuid);
        var documentId = HarnessIdentity.DocumentId(request.WorkspaceUuid, request.DocumentUuid);
        var options = request.Options ?? new MarkdownParseOptions();
        if (options.ProfileId != MarkdownRuntimePin.ProfileId)
            throw new ArgumentException("Only the pinned Markdown profile is supported.");
        var tokens = request.SectionTokens is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : request.SectionTokens.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        MarkdownRuntimePin.RequireLoaded(request.ExpectedParserVersion);
        var facts = MarkdownParser.Parse(request.RawText, request.ScopeId, request.SourceId, request.SourceVersion, options, cancellationToken);
        MarkdownRuntimePin.RequireFacts(facts);
        if (tokens.Count > 0 && (facts.TextHash is null || request.SectionIdentityTextHash != facts.TextHash))
            throw new ArgumentException("Section identity bindings must match this parsed raw TextHash.");
        var sectionsByKey = facts.Sections.ToDictionary(s => s.LocalKey, StringComparer.Ordinal);
        if (tokens.Keys.Any(k => !sectionsByKey.ContainsKey(k)))
            throw new ArgumentException("Section identity bindings contain a key absent from these facts.");

        var nodes = new List<HarnessNode>();
        var edges = new List<HarnessEdge>();
        var sections = new List<MarkdownSectionBinding>();
        var links = new List<MarkdownLinkBinding>();
        var fences = new List<MarkdownFenceBinding>();
        var search = new List<MarkdownSearchField>();
        var reasons = new List<string>();
        var (attributes, ignored) = Metadata(facts.FrontMatter.Root);
        var title = attributes.FirstOrDefault(a => a.Key == "title" && a.Source.Kind == MarkdownYamlKind.Scalar)?.Source.Scalar.Text
            ?? facts.Headings.FirstOrDefault()?.Text.Text ?? request.SourceId;
        nodes.Add(new(workspaceId, HarnessNodeKind.Workspace, "workspace", null, null));
        nodes.Add(new(documentId, HarnessNodeKind.Document, title, workspaceId, Location(new RawSpan(0, request.RawText.Length))));
        AddEdge(workspaceId, documentId, "contains", new RawSpan(0, request.RawText.Length), HarnessCertainty.Resolved);

        var headingByKey = facts.Headings.ToDictionary(h => h.LocalKey, StringComparer.Ordinal);
        var sectionIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal) { workspaceId, documentId };
        foreach (var section in facts.Sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var durable = tokens.TryGetValue(section.LocalKey, out var token);
            var id = durable ? HarnessIdentity.SectionId(request.WorkspaceUuid, documentId, token!)
                : LocalId("section", section.LocalKey);
            if (!seenIds.Add(id)) throw new ArgumentException("Section bindings map distinct sections to the same identity.");
            sectionIds.Add(section.LocalKey, id);
            var heading = section.HeadingLocalKey is null ? null : headingByKey.GetValueOrDefault(section.HeadingLocalKey);
            sections.Add(new(id, durable, section, heading));
            if (!durable) reasons.Add("section-identity-pending");
            if (heading is not null) search.Add(new(id, "heading", heading.Text));
        }
        var ownerRanges = sections.Where(s => s.Source.SubtreeSpan is { Length: > 0 })
            .OrderBy(s => s.Source.SubtreeSpan!.Value.Start)
            .ThenByDescending(s => s.Source.SubtreeSpan!.Value.Length).ToArray();
        var ownerByKey = sections.ToDictionary(s => s.Source.LocalKey, StringComparer.Ordinal);
        foreach (var binding in sections)
        {
            var parent = binding.Source.ParentLocalKey is null ? documentId
                : sectionIds.GetValueOrDefault(binding.Source.ParentLocalKey);
            if (parent is null) reasons.Add("section-parent-unavailable");
            nodes.Add(new(binding.NodeId, HarnessNodeKind.Section, binding.Heading?.Text.Text ?? "preamble",
                parent, Location(binding.Source.SubtreeSpan)));
            if (parent is not null) AddEdge(parent, binding.NodeId, "contains", binding.Source.SubtreeSpan,
                binding.Source.SubtreeSpan is null ? HarnessCertainty.Unresolved : HarnessCertainty.Resolved);
        }
        if (facts.FrontMatter.State != MarkdownFrontMatterState.Absent)
        {
            var id = LocalId("frontmatter", "0");
            nodes.Add(new(id, HarnessNodeKind.FrontMatter, facts.FrontMatter.State.ToString(), documentId, Location(facts.FrontMatter.YamlSpan)));
            AddEdge(documentId, id, "contains", facts.FrontMatter.YamlSpan,
                facts.FrontMatter.YamlSpan is null ? HarnessCertainty.Unresolved : HarnessCertainty.Resolved);
            foreach (var attribute in attributes)
                AddMetadataSearch(attribute.Key, attribute.Source);
        }
        for (var i = 0; i < facts.Links.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = facts.Links[i];
            var id = LocalId("link-target", i.ToString(CultureInfo.InvariantCulture));
            var owner = Owner(source.RawSpan);
            nodes.Add(new(id, HarnessNodeKind.LinkTarget, source.Target ?? source.ReferenceLabel ?? "unresolved",
                null, Location(source.TargetRawSpan)));
            // ResolvedReference means only Markdown definition resolution, never a resolved file/symbol target.
            var certainty = source.Target is null ? HarnessCertainty.Unresolved : HarnessCertainty.Candidate;
            var edgeId = AddEdge(owner, id, "markdown-link", source.RawSpan, certainty);
            links.Add(new(id, edgeId, owner, source));
            search.Add(new(owner, "link-label", source.Label));
            reasons.Add(source.Target is null ? "reference-target-unresolved" : "link-target-resolution-pending");
        }
        for (var i = 0; i < facts.Fences.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = facts.Fences[i];
            var id = LocalId("code-fence", i.ToString(CultureInfo.InvariantCulture));
            var owner = Owner(source.RawSpan);
            var rawContent = request.RawText.Substring(source.ContentSpan.Start, source.ContentSpan.Length);
            var rawHash = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(rawContent))).ToLowerInvariant();
            nodes.Add(new(id, HarnessNodeKind.CodeFence, source.Info ?? "plain", owner, Location(source.RawSpan)));
            AddEdge(owner, id, "contains", source.RawSpan, HarnessCertainty.Resolved);
            fences.Add(new(id, owner, rawHash, source));
            search.Add(new(id, "code-fence", source.Content));
        }
        foreach (var region in facts.TextRegions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            search.Add(new(region.RawSpan is { } span ? Owner(span) : documentId, region.Kind, region.Content));
        }
        var coverage = facts.Status == MarkdownParseStatus.Failed ? HarnessCoverage.Failed
            : facts.Status != MarkdownParseStatus.Complete || facts.Coverage.RawPositions != MarkdownCoverageState.Complete
                || facts.Coverage.DecodedMapping != MarkdownCoverageState.Complete || reasons.Count > 0
                ? HarnessCoverage.Partial : HarnessCoverage.CompleteWithinScope;
        var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
            HarnessGraphContract.IdentityVersion, workspaceId, request.SnapshotId, request.Generation, coverage,
            Array.Empty<HarnessVariant>(), Freeze(nodes), Array.Empty<HarnessSymbolOccurrence>(), Freeze(edges),
            Array.Empty<HarnessLegacyReference>(), new(MarkdownRuntimePin.ComponentVersion, MarkdownRuntimePin.CanonicalSourceHash,
                facts.ParserVersion, facts.ContractVersion, facts.ProfileId, facts.OptionsHash));
        HarnessGraphContract.ValidateHeader(graph);
        cancellationToken.ThrowIfCancellationRequested();
        return new(graph, facts, Freeze(sections), Freeze(links), Freeze(fences), Freeze(attributes), Freeze(ignored),
            Freeze(search), Freeze(reasons.Distinct(StringComparer.Ordinal)));

        HarnessLocation Location(RawSpan? span) => new(facts.SourceId, facts.SourceVersion, facts.TextHash,
            span is { } known ? new HarnessRawSpan(known.Start, known.Length) : null);
        string Owner(RawSpan span)
        {
            // Binary lookup and parent walk avoid a full section scan for every search/link/fence fact.
            var low = 0; var high = ownerRanges.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (ownerRanges[middle].Source.SubtreeSpan!.Value.Start <= span.Start) low = middle + 1;
                else high = middle;
            }
            var candidate = low > 0 ? ownerRanges[low - 1] : null;
            while (candidate is not null)
            {
                if (candidate.Source.SubtreeSpan is { } scope && scope.Start <= span.Start && scope.End >= span.End)
                    return candidate.NodeId;
                candidate = candidate.Source.ParentLocalKey is { } parentKey ? ownerByKey.GetValueOrDefault(parentKey) : null;
            }
            return documentId;
        }
        string AddEdge(string from, string to, string kind, RawSpan? evidence, HarnessCertainty certainty)
        {
            var id = LocalId("edge", edges.Count.ToString(CultureInfo.InvariantCulture));
            edges.Add(new(id, from, to, null, null, null, kind, certainty, "sharpdeps-markdown/1", Location(evidence)));
            return id;
        }
        string LocalId(string kind, string key) => SnapshotId(documentId, request, facts, kind, key);
        void AddMetadataSearch(string key, MarkdownYamlNode value)
        {
            if (value.Kind == MarkdownYamlKind.Scalar) search.Add(new(documentId, "metadata:" + key, value.Scalar));
            else if (key == "tags" && value.Kind == MarkdownYamlKind.Sequence)
                foreach (var item in value.Children.Where(c => c.Kind == MarkdownYamlKind.Scalar))
                    search.Add(new(documentId, "metadata:tags", item.Scalar));
        }
    }

    private static (List<MarkdownMetadataValue> Values, List<string> Ignored) Metadata(MarkdownYamlNode? root)
    {
        var values = new List<MarkdownMetadataValue>();
        var ignored = new List<string>();
        if (root?.Kind != MarkdownYamlKind.Mapping) return (values, ignored);
        foreach (var (key, value) in Entries(root))
        {
            if (key is "title" or "tags") values.Add(new(key, value));
            else if (key == "sharpdeps" && value.Kind == MarkdownYamlKind.Mapping)
                foreach (var (nested, child) in Entries(value))
                    if (nested is "id" or "symbols") values.Add(new("sharpdeps." + nested, child));
                    else ignored.Add("sharpdeps." + nested);
            else ignored.Add(key);
        }
        return (values, ignored);

        static IEnumerable<(string Key, MarkdownYamlNode Value)> Entries(MarkdownYamlNode mapping)
        {
            for (var i = 0; i + 1 < mapping.Children.Count; i += 2)
                if (mapping.Children[i].Scalar.Text is { } key)
                    yield return (key, mapping.Children[i + 1]);
        }
    }

    // hmdf is snapshot-local occurrence identity, never a durable section ID or parser LocalKey alias.
    private static string SnapshotId(string documentId, MarkdownGraphRequest request, MarkdownDocument facts, string kind, string key)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in new string?[] { "sharpdeps-markdown-projection/1", documentId, request.ScopeId, request.SourceId,
            request.SourceVersion, request.SnapshotId, request.Generation.ToString(CultureInfo.InvariantCulture),
            facts.TextHash, facts.ParserVersion, facts.OptionsHash, kind, key })
        {
            hash.AppendData(new byte[] { value is null ? (byte)0 : (byte)1 });
            if (value is null) continue;
            var bytes = StrictUtf8.GetBytes(value);
            var size = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(size, (ulong)bytes.Length);
            hash.AppendData(size);
            hash.AppendData(bytes);
        }
        return "hmdf_" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
}
