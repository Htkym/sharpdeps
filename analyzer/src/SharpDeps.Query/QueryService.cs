namespace SharpDeps.Query;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Index;

/// <summary>Read-only queries against one committed generation. Instances are not thread-safe.
/// Source access requires a separately trusted workspace; no query updates, restores or executes code.</summary>
public sealed class QueryService
{
    private readonly IQueryIndex index;
    private readonly IndexSnapshot saved;
    private readonly Dictionary<string, HarnessNode> nodes;
    private readonly Dictionary<string, HarnessSymbolOccurrence> occurrences;
    private readonly Dictionary<string, IndexFile> files;
    private readonly Dictionary<string, HarnessEdge[]> outgoing;
    private readonly Dictionary<string, HarnessEdge[]> incoming;
    private readonly QueryWorkspace? workspace;
    private readonly bool trusted;
    private readonly QueryCursor cursors;

    public QueryService(IQueryIndex index, QueryWorkspace? workspace = null, bool isTrusted = false, byte[]? cursorKey = null)
    {
        this.index = index; trusted = isTrusted; this.workspace = isTrusted ? workspace : null;
        // Freeze producer-owned arrays before deriving any adjacency or query output.
        saved = JsonSerializer.Deserialize<IndexSnapshot>(JsonSerializer.Serialize(index.Snapshot, QueryJson.Options), QueryJson.Options)!;
        _ = IndexStage.Create(saved); // Complete storage validation, including references and source spans.
        nodes = saved.Graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        occurrences = saved.Graph.SymbolOccurrences.ToDictionary(o => o.Id, StringComparer.Ordinal);
        files = saved.Files.ToDictionary(f => f.SourceId, StringComparer.Ordinal);
        outgoing = saved.Graph.Edges.GroupBy(e => e.SourceNodeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        incoming = saved.Graph.Edges.GroupBy(e => e.TargetNodeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        cursors = new QueryCursor(cursorKey);
    }

    public QueryReply Execute(QueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        // Clone input collections as well: cursor filters and selection use the same immutable request.
        request = request with { Budget = request.Budget ?? new QueryBudget(), Scope = request.Scope is null ? null :
            request.Scope with { VariantIds = request.Scope.VariantIds?.ToArray() }, Ids = request.Ids?.ToArray(),
            EdgeKinds = request.EdgeKinds?.ToArray() };
        ValidateMinimumBudget(request);
        var work = new Work(request, cancellationToken);
        var freshness = new QueryFreshness("unverified", null, [], ["input hashes, inventory and configuration not checked"]);
        try
        {
            work.Check();
            if (request.Freshness == QueryFreshnessPolicy.Refresh)
                work.Error("UPDATE_REQUIRED", "Request a trusted coordinator update, then open a new query reader.");
            else if (request.Freshness == QueryFreshnessPolicy.RequireFresh)
            {
                if (!trusted) work.Error("WORKSPACE_UNTRUSTED", "Fresh source verification requires workspace trust.");
                else if (workspace is null) work.Error("FRESHNESS_UNVERIFIED", "Supply a complete input inventory and configuration verification.");
                else
                {
                    freshness = workspace.Verify(saved, work.Token);
                    work.Check();
                    if (freshness.State != "verified-current")
                        work.Error("FRESHNESS_REQUIREMENT_NOT_MET", "Update the index and reopen the reader after verifying all inputs.");
                }
            }
            if (work.Errors.Count == 0)
            {
                var summary = Snapshot(freshness, request.Scope);
                var filter = QueryCursor.Fingerprint(request);
                var offset = 0;
                if (request.Cursor is not null)
                {
                    var decoded = cursors.Decode(request.Cursor, summary, filter);
                    offset = decoded.Offset;
                    if (decoded.Error is { } error) work.Error(error, "Restart the query without a cursor.");
                }
                if (work.Errors.Count == 0)
                {
                    Select(work, request.Kind == QueryKind.Browse ? offset : 0);
                    work.Check();
                    if (request.Kind != QueryKind.Browse && offset > work.Items.Count) work.Error("INVALID_CURSOR", "Restart the query without a cursor.");
                    else
                    {
                        var page = work.Items.Skip(request.Kind == QueryKind.Browse ? 0 : offset).Take(request.PageSize).ToList();
                        var hasMore = request.Kind == QueryKind.Browse ? work.BrowseHasMore : offset + page.Count < work.Items.Count;
                        if (hasMore) work.Reasons.Add("PAGE_LIMIT");
                        if (request.Kind == QueryKind.Context && workspace is not null)
                            AddSnippets(work, page, ref freshness);
                        if (request.Freshness == QueryFreshnessPolicy.RequireFresh && work.Errors.Count == 0)
                        {
                            // Selection and snippet I/O can race edits after the first verification.
                            freshness = workspace!.Verify(saved, work.Token);
                            work.Check();
                            if (freshness.State != "verified-current")
                            {
                                page.Clear();
                                work.Error("SOURCE_CHANGED_SINCE_SNAPSHOT", "Inputs changed during the query. Update and retry.");
                            }
                        }
                        summary = Snapshot(freshness, request.Scope);
                        return Finish(work, summary, page, offset, filter, hasMore);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) work.Error("CANCELLED", "The query was cancelled.");
            else { work.Reasons.Add("TIME_LIMIT"); work.Error("QUERY_TIMEOUT", "Retry with a narrower query or a larger time budget."); }
        }
        catch (QuerySourceException error)
        { work.Error(error.Code, "Verify the workspace inputs and retry.", error.RelativePath); }
        catch (IndexStoreException error)
        { work.Error(error.Code, "Reopen or rebuild the saved index through the trusted coordinator."); }
        finally { work.Timer.Dispose(); }
        return Finish(work, Snapshot(freshness, request.Scope), [], 0, "", false, errorOnly: true);
    }

    private QuerySnapshot Snapshot(QueryFreshness freshness, QueryScope? scope)
    {
        var variants = saved.Graph.Variants.Where(v => scope?.VariantIds is null || scope.VariantIds.Contains(v.Id))
            .Select(v => v.Id).OrderBy(v => v, StringComparer.Ordinal).ToArray();
        return new(saved.Graph.WorkspaceId, saved.Graph.SnapshotId, saved.Graph.Generation,
            variants.Take(64).ToArray(), variants.Length, freshness.State, freshness.CheckedAt, saved.Graph.Coverage,
            freshness.DirtyPaths.Take(8).ToArray(), freshness.DirtyPaths.Count,
            freshness.Unverified.Take(8).ToArray(), freshness.Unverified.Count);
    }

    private void Select(Work work, int browseOffset)
    {
        var request = work.Request;
        if (request.Kind == QueryKind.Status) return;
        if (request.Kind == QueryKind.Browse)
        {
            var matched = 0;
            foreach (var node in saved.Graph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
            {
                work.Check();
                if (!BrowseInScope(node, work) || (!request.IncludeDocuments && IsDocument(node.Kind))) continue;
                if (matched++ < browseOffset) continue;
                if (work.Items.Count == request.PageSize || !work.Add(NodeItem(node, "browse", [], request.Scope)))
                { work.BrowseHasMore = true; return; }
            }
            if (browseOffset > matched) work.Error("INVALID_CURSOR", "Restart the query without a cursor.");
            return;
        }
        if (request.Kind == QueryKind.Search)
        {
            foreach (var item in Search(work)) if (!work.Add(item)) break;
            return;
        }
        var seeds = request.Ids ?? (request.NodeId is null ? [] : new[] { request.NodeId });
        if (request.Kind is QueryKind.Context or QueryKind.Impact && seeds.Count == 0)
        {
            var matches = Search(work);
            var maximum = request.Kind == QueryKind.Context ? Math.Min(8, request.Budget!.MaxNodes) : request.Budget!.MaxNodes;
            if (matches.Count > maximum) work.Reasons.Add("NODE_LIMIT");
            seeds = matches.Take(maximum).Select(i => i.Id).ToArray();
        }
        var selected = new List<string>();
        foreach (var id in seeds.Distinct(StringComparer.Ordinal))
        {
            work.Check();
            if (!nodes.TryGetValue(id, out var node)) { work.Error("SYMBOL_NOT_FOUND", "Select an exact node ID from search."); continue; }
            if (!InScope(node, request.Scope)) continue;
            if (!work.Add(NodeItem(node, "selected", [], request.Scope))) break;
            selected.Add(id);
        }
        if (request.Kind == QueryKind.Symbol)
        {
            foreach (var occurrence in saved.Graph.SymbolOccurrences)
            {
                work.Check();
                if (!selected.Contains(occurrence.LogicalSymbolId) || !VariantAllowed(occurrence.VariantId, request.Scope)) continue;
                var selectedDeclarations = (occurrence.Declarations ?? []).Where(l => LocationInScope(l, request.Scope))
                    .Take(request.Budget!.MaxNodes + 1).ToArray();
                if (selectedDeclarations.Length > request.Budget.MaxNodes) work.Reasons.Add("DECLARATION_LIMIT");
                var declarations = selectedDeclarations.Take(request.Budget.MaxNodes).ToArray();
                var location = LocationInScope(occurrence.Location, request.Scope) ? occurrence.Location : declarations.FirstOrDefault();
                if (request.Scope?.PathPrefix is not null && location is null) continue;
                var filtered = occurrence with { Location = location, Declarations = declarations };
                if (!work.Add(new(occurrence.Id, "declaration", nodes[occurrence.LogicalSymbolId].Name,
                    "symbol occurrence in selected variant", HarnessCertainty.Resolved, PathOf(location),
                    location, [], Occurrence: filtered))) break;
            }
            return;
        }
        var reached = Walk(work, selected);
        if (request.Kind == QueryKind.Context)
        {
            foreach (var occurrence in saved.Graph.SymbolOccurrences)
            {
                work.Check();
                if (!reached.Contains(new(occurrence.LogicalSymbolId, occurrence.Id, occurrence.VariantId))) continue;
                var declarations = occurrence.Declarations ?? (occurrence.Location is null ? [] : new[] { occurrence.Location });
                var number = 0;
                foreach (var declaration in declarations)
                {
                    if (!LocationInScope(declaration, request.Scope)) continue;
                    if (!work.Add(new(occurrence.Id + "/" + number++, "declaration", nodes[occurrence.LogicalSymbolId].Name,
                        "declaration in selected variant", HarnessCertainty.Resolved, PathOf(declaration), declaration, [],
                        Occurrence: occurrence with { Location = declaration, Declarations = [declaration] }))) return;
                }
            }
        }
    }

    private List<QueryItem> Search(Work work)
    {
        var term = work.Request.Term!;
        var ranks = index.Search(term, 500).ToDictionary(h => h.NodeId, StringComparer.Ordinal);
        if (ranks.Count == 500) work.Reasons.Add("SEARCH_CANDIDATE_LIMIT");
        var text = saved.SearchText.ToDictionary(t => t.NodeId, StringComparer.Ordinal);
        var comparer = Comparer<(int Priority, double Rank, QueryItem Item)>.Create((a, b) =>
        {
            var compared = a.Priority.CompareTo(b.Priority);
            if (compared == 0) compared = a.Rank.CompareTo(b.Rank);
            return compared == 0 ? StringComparer.Ordinal.Compare(a.Item.Id, b.Item.Id) : compared;
        });
        var results = new SortedSet<(int Priority, double Rank, QueryItem Item)>(comparer);
        var candidateCount = 0;
        foreach (var node in saved.Graph.Nodes)
        {
            work.Check();
            if (!InScope(node, work.Request.Scope)) continue;
            var exactPath = LocationsFor(node, work.Request.Scope).Any(l => LocationInScope(l, work.Request.Scope) && PathOf(l) == term);
            var exactId = node.Id == term;
            var exactName = node.Name.Equals(term, StringComparison.Ordinal) || node.Signature == term;
            ranks.TryGetValue(node.Id, out var hit);
            text.TryGetValue(node.Id, out var stored);
            var substring = node.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (node.Signature?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (stored is not null && new[] { stored.Name, stored.Signature, stored.Heading, stored.Body, stored.Path }
                    .Any(s => s.Contains(term, StringComparison.OrdinalIgnoreCase)));
            if (!exactId && !exactName && !exactPath && hit is null && !substring) continue;
            var reason = exactId ? "exact ID" : exactName ? "exact name/signature" : exactPath ? "exact path" : hit?.MatchMethod ?? "literal-substring";
            var item = NodeItem(node, reason, [], work.Request.Scope) with { Rank = hit?.Rank, MatchMethod = reason };
            results.Add((exactId ? 0 : exactName ? 1 : exactPath ? 2 : hit is not null ? 3 : 4, hit?.Rank ?? 0, item));
            candidateCount++;
            if (results.Count > 500) results.Remove(results.Max);
        }
        if (candidateCount > 500) work.Reasons.Add("SEARCH_CANDIDATE_LIMIT");
        return results.Select(r => r.Item).ToList();
    }

    private readonly record struct WalkPosition(string Id, string? OccurrenceId, string? VariantId);

    private bool BrowseInScope(HarnessNode node, Work work)
    {
        var scope = work.Request.Scope;
        if (node.Kind != HarnessNodeKind.Namespace || scope?.VariantIds is not { Count: > 0 } variants)
            return InScope(node, scope);
        // A namespace has project ownership, but no declaration/path proof for the selected variant.
        if (scope.PathPrefix is not null || (scope.Kind is { } kind && kind != node.Kind)) return false;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = node;
        while (visited.Add(current.Id))
        {
            work.Check();
            if (current.Kind == HarnessNodeKind.Project)
            {
                if (scope.ProjectId is { } project && project != current.Id) return false;
                foreach (var variant in saved.Graph.Variants)
                {
                    work.Check();
                    if (variant.ProjectId == current.Id && variants.Contains(variant.Id)) return true;
                }
                return false;
            }
            if (current.ParentId is not { } parent || !nodes.TryGetValue(parent, out current)) return false;
        }
        return false;
    }

    private HashSet<WalkPosition> Walk(Work work, IReadOnlyList<string> seeds)
    {
        var request = work.Request;
        var visited = new HashSet<WalkPosition>();
        var frontier = new List<(WalkPosition Position, string[] Via)>();
        foreach (var id in seeds)
        {
            var variants = saved.Graph.SymbolOccurrences.Where(o => o.LogicalSymbolId == id).ToArray();
            if (variants.Length == 0)
            {
                var position = new WalkPosition(id, null, null);
                visited.Add(position); frontier.Add((position, []));
            }
            foreach (var occurrence in variants)
            {
                work.Check();
                if (!VariantAllowed(occurrence.VariantId, request.Scope)
                    || (request.Scope?.PathPrefix is not null && !(occurrence.Declarations
                        ?? (occurrence.Location is null ? [] : new[] { occurrence.Location }))
                        .Any(l => LocationInScope(l, request.Scope)))) continue;
                var position = new WalkPosition(id, occurrence.Id, occurrence.VariantId);
                visited.Add(position); frontier.Add((position, []));
            }
        }
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < request.Budget!.MaxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<(WalkPosition Position, string[] Via)>();
            foreach (var current in frontier)
            {
                foreach (var (edge, reverse) in WalkEdges(current.Position.Id, request))
                {
                    work.Check();
                    if (!TryAdvance(current.Position, edge, reverse, request, out var neighbour, out var canExpand)) continue;
                    var node = nodes[neighbour.Id];
                    var via = current.Via.Append(edge.Id).ToArray();
                    // Budget both endpoints before adding the connecting edge, including cycles.
                    if (!work.CanNode(neighbour.Id)) { work.Reasons.Add("NODE_LIMIT"); continue; }
                    if (edgeIds.Add(edge.Id))
                    {
                        if (!work.Add(new(edge.Id, edge.Kind, node.Name, request.Kind == QueryKind.Impact ?
                            "impact review candidate via " + edge.Kind : "related via " + edge.Kind, edge.Certainty,
                            PathOf(edge.Evidence), edge.Evidence, via, Edge: edge))) return visited;
                        var occurrence = neighbour.OccurrenceId is null ? null : occurrences[neighbour.OccurrenceId];
                        var location = occurrence is null ? (IsDocument(node.Kind) ? LocationFor(node, request.Scope) : null)
                            : (occurrence.Declarations ?? (occurrence.Location is null ? [] : new[] { occurrence.Location }))
                                .FirstOrDefault(l => LocationInScope(l, request.Scope));
                        if (!work.Add(new(node.Id, node.Kind.ToString(), node.Name, "related via " + edge.Kind,
                            edge.Certainty, PathOf(location), location, via, Node: node with { Location = location }))) return visited;
                    }
                    // Candidates are leaves. A resolved edge after a candidate hop cannot strengthen that path.
                    if (canExpand && edge.Certainty == HarnessCertainty.Resolved && visited.Add(neighbour)) next.Add((neighbour, via));
                }
            }
            frontier = next;
        }
        if (frontier.Any(f => WalkEdges(f.Position.Id, request).Any(step =>
            TryAdvance(f.Position, step.Edge, step.Reverse, request, out var neighbour, out var canExpand)
            && (!edgeIds.Contains(step.Edge.Id) || (canExpand && step.Edge.Certainty == HarnessCertainty.Resolved && !visited.Contains(neighbour))))))
            work.Reasons.Add("DEPTH_LIMIT");
        return visited;
    }

    private IEnumerable<(HarnessEdge Edge, bool Reverse)> WalkEdges(string id, QueryRequest request)
    {
        var reverse = request.Kind == QueryKind.Callers || (request.Kind == QueryKind.Impact && request.Dependents);
        var edges = ((reverse ? incoming : outgoing).GetValueOrDefault(id) ?? []).Select(e => (Edge: e, Reverse: reverse));
        if (request.Kind == QueryKind.Context)
            edges = edges.Concat((incoming.GetValueOrDefault(id) ?? []).Select(e => (Edge: e, Reverse: true)));
        return edges.OrderBy(step => step.Edge.Id, StringComparer.Ordinal).ThenBy(step => step.Reverse);
    }

    private bool TryAdvance(WalkPosition current, HarnessEdge edge, bool reverse, QueryRequest request,
        out WalkPosition next, out bool canExpand)
    {
        next = default; canExpand = false;
        if (!VariantAllowed(edge.VariantId, request.Scope) || !EdgeAllowed(edge, request)) return false;
        var hereOccurrence = reverse ? edge.TargetOccurrenceId : edge.SourceOccurrenceId;
        var nextOccurrence = reverse ? edge.SourceOccurrenceId : edge.TargetOccurrenceId;
        // Logical-only relationships remain visible as leaves, without binding a known occurrence.
        canExpand = current.OccurrenceId == hereOccurrence;
        if (!canExpand && (current.OccurrenceId is null || hereOccurrence is not null)) return false;
        var endpointVariant = nextOccurrence is null ? null : occurrences[nextOccurrence].VariantId;
        // Occurrence-less document/file hops retain the selected variant; they cannot bridge variants.
        var edgeVariant = edge.VariantId ?? endpointVariant;
        if ((current.OccurrenceId is null || !canExpand) && current.VariantId is not null && edgeVariant is not null
            && current.VariantId != edgeVariant) return false;
        var variant = endpointVariant ?? current.VariantId ?? edge.VariantId;
        if (!VariantAllowed(variant, request.Scope)) return false;
        if (nextOccurrence is not null && request.Scope?.PathPrefix is not null)
        {
            var occurrence = occurrences[nextOccurrence];
            if (!(occurrence.Declarations ?? (occurrence.Location is null ? [] : new[] { occurrence.Location }))
                .Any(l => LocationInScope(l, request.Scope))) return false;
        }
        var id = reverse ? edge.SourceNodeId : edge.TargetNodeId;
        var node = nodes[id];
        if (!InScope(node, request.Scope) || (!request.IncludeDocuments && IsDocument(node.Kind))) return false;
        next = new(id, nextOccurrence, variant);
        return true;
    }
    private static bool EdgeAllowed(HarnessEdge edge, QueryRequest request)
    {
        if (!request.IncludeCandidates && edge.Certainty != HarnessCertainty.Resolved) return false;
        if (request.EdgeKinds is not null) return request.EdgeKinds.Contains(edge.Kind, StringComparer.Ordinal);
        if (request.Kind is QueryKind.Callers or QueryKind.Callees)
            return edge.Kind is "calls" or "constructs" or "dispatch_candidates" or "candidate_relation";
        return true;
    }
    private bool InScope(HarnessNode node, QueryScope? scope)
    {
        if (scope is null) return true;
        if (scope.Kind is { } kind && node.Kind != kind) return false;
        if (scope.PathPrefix is { } prefix)
        {
            if (LocationFor(node, scope) is null) return false;
        }
        if (scope.ProjectId is { } project)
        {
            var current = node;
            while (current.Id != project && current.ParentId is { } parent) current = nodes[parent];
            if (current.Id != project) return false;
        }
        if (scope.VariantIds is { Count: > 0 } variants && !IsDocument(node.Kind))
        {
            if (!saved.Graph.SymbolOccurrences.Any(o => o.LogicalSymbolId == node.Id && variants.Contains(o.VariantId))
                && !saved.Graph.Variants.Any(v => v.ProjectId == node.Id && variants.Contains(v.Id))) return false;
        }
        return true;
    }
    private static bool VariantAllowed(string? variant, QueryScope? scope)
        => variant is null || scope?.VariantIds is null || scope.VariantIds.Contains(variant);
    private static bool IsDocument(HarnessNodeKind kind) => kind is HarnessNodeKind.Document or HarnessNodeKind.Section
        or HarnessNodeKind.FrontMatter or HarnessNodeKind.CodeFence or HarnessNodeKind.LinkTarget or HarnessNodeKind.SymbolMention;
    private string? PathOf(HarnessLocation? location) => location is null ? null : files.GetValueOrDefault(location.SourceId)?.RelativePath;
    private bool LocationInScope(HarnessLocation? location, QueryScope? scope)
    {
        if (scope?.PathPrefix is not { } prefix) return true;
        var path = PathOf(location);
        return path is not null && (path == prefix || path.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.Ordinal));
    }
    private HarnessLocation? LocationFor(HarnessNode node, QueryScope? scope)
    {
        return LocationsFor(node, scope).FirstOrDefault(l => LocationInScope(l, scope));
    }
    private IEnumerable<HarnessLocation> LocationsFor(HarnessNode node, QueryScope? scope)
    {
        // Variant and path must be supported by the same declaration, especially for partial symbols.
        if ((scope?.VariantIds is null || IsDocument(node.Kind)) && node.Location is { } primary) yield return primary;
        foreach (var occurrence in saved.Graph.SymbolOccurrences)
            if (occurrence.LogicalSymbolId == node.Id && VariantAllowed(occurrence.VariantId, scope))
                foreach (var location in occurrence.Declarations ?? (occurrence.Location is null ? [] : new[] { occurrence.Location }))
                    yield return location;
    }
    private QueryItem NodeItem(HarnessNode node, string reason, IReadOnlyList<string> via, QueryScope? scope = null)
    {
        var location = LocationFor(node, scope);
        return new(node.Id, node.Kind.ToString(), node.Name, reason, HarnessCertainty.Resolved,
            PathOf(location), location, via, Node: node with { Location = location });
    }

    private void AddSnippets(Work work, List<QueryItem> page, ref QueryFreshness freshness)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var charsRemaining = work.Request.Budget!.MaxChars / 2;
        var groups = page.Select((item, index) => (item, index)).Where(p => p.item.Evidence?.RawSpan is not null)
            .GroupBy(p => p.item.Evidence!.SourceId, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            work.Check();
            if (!files.TryGetValue(group.Key, out var file)) continue;
            try
            {
                var text = new UTF8Encoding(false, true).GetString(workspace!.Read(file, work.Token));
                var sorted = group.Select(p => (p.index, Location: p.item.Evidence!, Span: p.item.Evidence!.RawSpan!.Value))
                    .OrderBy(p => p.Span.Start).ThenBy(p => p.Span.End).ToArray();
                if (sorted.Any(p => p.Location.ContentHash != file.ContentHash || p.Span.End > text.Length))
                    throw new QuerySourceException("SOURCE_CHANGED_SINCE_SNAPSHOT", file.RelativePath);
                // Sort first so a bridging interval joins every overlap, regardless of item order.
                var unions = new List<(int Owner, HarnessRawSpan Span)>();
                foreach (var current in sorted)
                {
                    work.Check();
                    if (unions.Count > 0 && current.Span.Start <= unions[^1].Span.End)
                    {
                        var previous = unions[^1];
                        unions[^1] = (previous.Owner, new(previous.Span.Start,
                            Math.Max(previous.Span.End, current.Span.End) - previous.Span.Start));
                    }
                    else unions.Add((current.index, current.Span));
                }
                foreach (var (owner, span) in unions)
                {
                    work.Check();
                    var length = Math.Min(span.Length, charsRemaining);
                    if (length > 0 && span.Start + length < text.Length && char.IsHighSurrogate(text[span.Start + length - 1])) length--;
                    if (span.Start < text.Length && char.IsLowSurrogate(text[span.Start])) { work.Reasons.Add("SNIPPET_BOUNDARY"); continue; }
                    var included = new HarnessRawSpan(span.Start, length);
                    if (length != span.Length) work.Reasons.Add("SNIPPET_CHAR_LIMIT");
                    page[owner] = page[owner] with { Snippet = Snippet(file, included, text) };
                    charsRemaining -= length;
                }
            }
            catch (DecoderFallbackException)
            {
                changed.Add(file.RelativePath);
                work.Diagnostics.Add(new("INVALID_SOURCE_ENCODING", "Snippet omitted; verify source encoding.", file.RelativePath));
                if (work.Request.Freshness == QueryFreshnessPolicy.RequireFresh)
                    work.Error("INVALID_SOURCE_ENCODING", "The required source text could not be decoded.", file.RelativePath);
            }
            catch (QuerySourceException error)
            {
                if (changed.Add(file.RelativePath)) work.Diagnostics.Add(new(error.Code, "Snippet omitted; update the index and retry.", file.RelativePath));
                if (work.Request.Freshness == QueryFreshnessPolicy.RequireFresh)
                    work.Error(error.Code, "The required source freshness could not be verified.", file.RelativePath);
            }
        }
        if (changed.Count > 0) freshness = new("dirty", DateTimeOffset.UtcNow, changed.Order(StringComparer.Ordinal).ToArray(),
            ["only requested snippets checked; complete inventory and configuration remain unverified"]);
        if (work.Errors.Count > 0) page.Clear();
        static QuerySnippet Snippet(IndexFile file, HarnessRawSpan span, string text)
            => new(file.SourceId, file.ContentHash, span, text.Substring(span.Start, span.Length), span.Start > 0, span.End < text.Length);
    }

    private QueryReply Finish(Work work, QuerySnapshot snapshot, List<QueryItem> page, int offset, string filter, bool hasMore, bool errorOnly = false)
    {
        if (snapshot.VariantCount > snapshot.VariantIds.Count) work.Reasons.Add("VARIANT_METADATA_LIMIT");
        if (snapshot.DirtyPathCount > snapshot.DirtyPaths.Count || snapshot.UnverifiedCount > snapshot.Unverified.Count)
            work.Reasons.Add("FRESHNESS_METADATA_LIMIT");
        foreach (var diagnostic in (saved.Graph.Diagnostics ?? []).Take(16))
            work.Diagnostics.Add(new(diagnostic.Code.Length <= 64 ? diagnostic.Code : "GRAPH_DIAGNOSTIC", "Saved graph diagnostic count: " + diagnostic.Count));
        if ((saved.Graph.Diagnostics?.Count ?? 0) > 16) work.Reasons.Add("DIAGNOSTIC_LIMIT");
        var budget = work.Request.Budget!;
        var minimalError = false;
        while (true)
        {
            if (!errorOnly) work.Check();
            if (work.Request.RequireComplete && (saved.Graph.Coverage != HarnessCoverage.CompleteWithinScope || work.Reasons.Count > 0))
                work.Error("INCOMPLETE_RESULT", "Retry with complete coverage and sufficient query/output budgets.");
            var selectedRemainder = work.Request.Kind == QueryKind.Browse
                ? page.Count < work.Items.Count : offset + page.Count < work.Items.Count;
            var next = (hasMore || selectedRemainder) && page.Count > 0 && work.Errors.Count == 0
                ? cursors.Encode(snapshot, filter, offset + page.Count) : null;
            var errors = work.Errors.Take(8).ToArray(); var diagnostics = work.Diagnostics.Take(16).ToArray();
            if (work.Errors.Count > errors.Length || work.Diagnostics.Count > diagnostics.Length) work.Reasons.Add("DIAGNOSTIC_LIMIT");
            var envelope = new QueryEnvelope("1", work.Request.RequestId, snapshot,
                page.Where(i => i.Certainty == HarnessCertainty.Resolved).ToArray(),
                page.Where(i => i.Certainty == HarnessCertainty.Candidate).ToArray(),
                page.Where(i => i.Certainty == HarnessCertainty.Unresolved).ToArray(),
                new(budget, page.Where(i => i.Node is not null || i.Occurrence is not null).Select(i => i.Id).Distinct().Count(),
                    page.Count(i => i.Edge is not null), 0, 0, 0), work.Reasons.Count > 0,
                work.Reasons.Order(StringComparer.Ordinal).ToArray(), next, diagnostics, errors);
            // Solve the short size/decimal-digit feedback loop before enforcing the cap.
            string json;
            while (true)
            {
                if (!errorOnly) work.Check();
                json = JsonSerializer.Serialize(envelope, QueryJson.Options);
                var bytes = Encoding.UTF8.GetByteCount(json);
                if (envelope.Budget.UsedChars == json.Length && envelope.Budget.UsedBytes == bytes) break;
                envelope = envelope with { Budget = envelope.Budget with { UsedChars = json.Length, UsedBytes = bytes,
                    EstimatedTokens = (json.Length + 3) / 4 } };
            }
            if (json.Length <= budget.MaxChars && envelope.Budget.UsedBytes <= budget.MaxBytes)
            { if (!errorOnly) work.Check(); return new(envelope, json); }
            work.Reasons.Add("OUTPUT_LIMIT");
            if (page.Count > 0) { page.RemoveRange(page.Count / 2, page.Count - page.Count / 2); continue; }
            // Bound optional metadata too, retaining counts and a structured budget error.
            if (snapshot.DirtyPaths.Count > 0 || snapshot.Unverified.Count > 0 || snapshot.VariantIds.Count > 0)
            { snapshot = snapshot with { DirtyPaths = [], Unverified = [], VariantIds = [] }; continue; }
            work.Diagnostics.Clear(); work.Errors.Clear();
            if (minimalError) throw new ArgumentException("The output budget cannot hold the required error metadata.");
            minimalError = true;
            work.Error("OUTPUT_BUDGET_TOO_SMALL", "Increase the output budget or narrow the query.");
            // ValidateMinimumBudget reserves the real snapshot metadata; retain a finite failure exit.
        }
    }

    private void ValidateMinimumBudget(QueryRequest request)
    {
        var budget = request.Budget!;
        var summary = Snapshot(new("unverified", null, [], []), request.Scope) with { VariantIds = [] };
        var minimal = new QueryEnvelope("1", request.RequestId, summary, [], [], [],
            new(budget, 0, 0, 9999999, 9999999, 9999999), true, ["OUTPUT_LIMIT"], null, [],
            [new("OUTPUT_BUDGET_TOO_SMALL", "Increase the output budget or narrow the query.")]);
        var json = JsonSerializer.Serialize(minimal, QueryJson.Options);
        // Reserve space for all bounded reason codes and a diagnostic/error before optional metadata is dropped.
        if (json.Length + 768 > budget.MaxChars || Encoding.UTF8.GetByteCount(json) + 768 > budget.MaxBytes)
            throw new ArgumentException("The output budget cannot hold this snapshot's minimal error envelope.", nameof(request));
    }

    private static void Validate(QueryRequest r)
    {
        var b = r.Budget ?? new QueryBudget();
        static bool Short(string? s, int max) => s is null || (s.Length <= max && !s.Any(char.IsControl));
        if (string.IsNullOrWhiteSpace(r.RequestId) || !Short(r.RequestId, 64) || !Enum.IsDefined(r.Kind) || !Enum.IsDefined(r.Freshness)
            || r.PageSize is < 1 or > 500 || b.MaxNodes is < 1 or > 500 || b.MaxEdges is < 1 or > 1000
            || b.MaxDepth is < 1 or > 8 || b.MaxMilliseconds is < 10 or > 30000
            || b.MaxChars is < 2048 or > 1000000 || b.MaxBytes is < 2048 or > 4000000
            || !Short(r.Term, 128) || !Short(r.NodeId, 128) || !Short(r.Scope?.ProjectId, 128) || !Short(r.Scope?.PathPrefix, 512)
            || (r.Scope?.Kind is { } kind && !Enum.IsDefined(kind))
            || (r.Ids is { } ids && (ids.Count > 64 || ids.Any(s => !Short(s, 128) || string.IsNullOrWhiteSpace(s))))
            || (r.Scope?.VariantIds is { } variants && (variants.Count is < 1 or > 64 || variants.Any(s => !Short(s, 128))))
            || (r.EdgeKinds is { } edges && (edges.Count is < 1 or > 32 || edges.Any(s => !Short(s, 64))))
            || (r.Kind == QueryKind.Browse && (r.Term is not null || r.NodeId is not null || r.Ids is not null || r.EdgeKinds is not null))
            || (r.Kind == QueryKind.Search && string.IsNullOrWhiteSpace(r.Term))
            || (r.Kind is QueryKind.Symbol or QueryKind.Callers or QueryKind.Callees && r.NodeId is null && r.Ids is not { Count: > 0 })
            || (r.Kind == QueryKind.Impact && r.NodeId is null && r.Ids is not { Count: > 0 } && string.IsNullOrWhiteSpace(r.Term))
            || (r.Kind == QueryKind.Context && r.NodeId is null && r.Ids is not { Count: > 0 } && string.IsNullOrWhiteSpace(r.Term)))
            throw new ArgumentException("Invalid query arguments or budgets.", nameof(r));
    }

    private sealed class Work
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly HashSet<string> nodeIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> itemIds = new(StringComparer.Ordinal);
        private int edgeCount;
        public Work(QueryRequest request, CancellationToken token)
        {
            Request = request; Timer = CancellationTokenSource.CreateLinkedTokenSource(token);
            Timer.CancelAfter(request.Budget!.MaxMilliseconds);
        }
        public QueryRequest Request { get; }
        public CancellationTokenSource Timer { get; }
        public CancellationToken Token => Timer.Token;
        public List<QueryItem> Items { get; } = [];
        public bool BrowseHasMore { get; set; }
        public List<QueryIssue> Diagnostics { get; } = [];
        public List<QueryIssue> Errors { get; } = [];
        public HashSet<string> Reasons { get; } = new(StringComparer.Ordinal);
        public void Check() { Token.ThrowIfCancellationRequested(); if (clock.ElapsedMilliseconds > Request.Budget!.MaxMilliseconds) throw new OperationCanceledException(); }
        public bool CanNode(string id) => nodeIds.Contains(id) || nodeIds.Count < Request.Budget!.MaxNodes;
        public bool Add(QueryItem item)
        {
            Check();
            if (itemIds.Contains(item.Id + "/" + item.Certainty)) return true;
            if ((item.Node is not null || item.Occurrence is not null) && !CanNode(item.Id)) { Reasons.Add("NODE_LIMIT"); return false; }
            if (item.Edge is not null && edgeCount >= Request.Budget!.MaxEdges) { Reasons.Add("EDGE_LIMIT"); return false; }
            if (item.Node is not null || item.Occurrence is not null) nodeIds.Add(item.Id);
            if (item.Edge is not null) edgeCount++;
            itemIds.Add(item.Id + "/" + item.Certainty); Items.Add(item); return true;
        }
        public void Error(string code, string message, string? path = null)
        { if (!Errors.Any(e => e.Code == code && e.Path == path)) Errors.Add(new(code, message, path)); }
    }
}
