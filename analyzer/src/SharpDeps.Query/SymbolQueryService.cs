namespace SharpDeps.Query;

using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Index;

public sealed partial class ExplorationService
{
    private static readonly string[] SymbolEdgeKinds = ["calls", "constructs", "reads", "writes", "references_member",
        "overrides", "implements_explicitly", "dispatch_candidates", "candidate_relation"];

    internal static SymbolQueryRequest NormalizeSymbols(SymbolQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Operation) || request.MaxResults is < 1 or > 32
            || request.Direction is not ("incoming" or "outgoing")) throw new ArgumentException("Invalid symbol query.");
        var find = request.Operation == SymbolQueryOperation.FindSymbols;
        if (find ? string.IsNullOrWhiteSpace(request.Query) || request.Selectors is not null
            : request.Query is not null || request.Selectors is not { Count: > 0 and <= 8 }
                || request.Selectors.Any(id => id is null || !IsOccurrenceIdentity(id))
                || request.ProjectId is null || request.VariantId is null)
            throw new ArgumentException("Find requires a query; read/relations require exact occurrence selectors and explicit scope.");
        if (request.Operation != SymbolQueryOperation.Relations && (request.EdgeKinds is not null || request.Direction != "outgoing")
            || request.EdgeKinds is { } kinds && (kinds.Count is < 1 or > 9 || kinds.Any(kind => !SymbolEdgeKinds.Contains(kind, StringComparer.Ordinal))))
            throw new ArgumentException("Unsupported relation filter.");
        request = request with { Budget = request.Budget ?? new QueryBudget(MaxChars: 12000, MaxBytes: 48000),
            Selectors = request.Selectors?.Distinct(StringComparer.Ordinal).ToArray(), EdgeKinds = request.EdgeKinds?.Distinct(StringComparer.Ordinal).ToArray() };
        _ = Normalize(ToExploreRequest(request));
        return request;
    }

    internal static ExploreRequest ToExploreRequest(SymbolQueryRequest request)
        => new(request.Query ?? request.Selectors![0], request.ProjectId, request.VariantId, request.PathPrefix,
            request.Freshness, request.Budget, ExploreFormat.Audit, ExploreScopePolicy.RequireUnique)
        { PrepareMaxMilliseconds = request.PrepareMaxMilliseconds, ResponseMode = ExploreResponseMode.Evidence };

    public SymbolQueryReply ExecuteSymbols(SymbolQueryRequest request, CancellationToken cancellationToken = default,
        CancellationToken deadlineToken = default)
    {
        request = NormalizeSymbols(request);
        var explore = ToExploreRequest(request);
        using var execution = new ExplorationExecutionBudget(explore, cancellationToken, deadlineToken);
        try
        {
            var saved = PrepareSnapshot(explore, execution.Token);
            execution.StartQuery();
            return ExecuteSymbolsPrepared(request, saved, execution);
        }
        catch (OperationCanceledException) { return SymbolsFailure(request, execution.Code); }
        catch (QuerySourceException error) { return SymbolsFailure(request, error.Code); }
        catch (IndexStoreException error) { return SymbolsFailure(request, error.Code); }
    }

    internal SymbolQueryReply ExecuteSymbolsPrepared(SymbolQueryRequest request, ExplorationSnapshot saved,
        ExplorationExecutionBudget execution)
    {
        var run = new Run(saved, index, workspace, trusted, ToExploreRequest(request), execution, execution.CallerToken);
        try { return run.ExecuteSymbols(request); }
        catch (OperationCanceledException) { return SymbolsFailure(request, execution.Code); }
        catch (QuerySourceException error) { return SymbolsFailure(request, error.Code); }
        catch (IndexStoreException error) { return SymbolsFailure(request, error.Code); }
    }

    internal static SymbolQueryReply SymbolsFailure(SymbolQueryRequest request, string code)
    {
        request ??= new(SymbolQueryOperation.FindSymbols, Query: "invalid");
        // Error identifiers, never exception text, enter the wire contract.
        if (code.Length is 0 or > 80 || code.Any(ch => !(ch is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
            code = "INTERNAL_ERROR";
        var budget = request.Budget;
        if (budget is null || budget.MaxChars is < 2048 or > 1000000 || budget.MaxBytes is < 2048 or > 4000000
            || !ExplorationExecutionBudget.Finite(budget.MaxMilliseconds) || budget.MaxNodes is < 1 or > 500
            || budget.MaxEdges is < 1 or > 1000 || budget.MaxDepth is < 1 or > 8)
            budget = new QueryBudget(MaxChars: 12000, MaxBytes: 48000);
        var envelope = new SymbolQueryEnvelope("1", Enum.IsDefined(request.Operation) ? request.Operation : SymbolQueryOperation.FindSymbols, null, [],
            [new(code, "No evidence returned. Preserve scope and finite budgets; inspect the failure code.")], [], false,
            ExplorationExecutionBudget.Finite(request.PrepareMaxMilliseconds) ? request.PrepareMaxMilliseconds : 30000, budget);
        return new(envelope, JsonSerializer.Serialize(envelope, QueryJson.Options));
    }

    private sealed partial class Run
    {
        public SymbolQueryReply ExecuteSymbols(SymbolQueryRequest operation)
        {
            Check();
            if (project is not null && !owner.saved.Graph.Variants.Any(v => v.ProjectId == project)
                || variant is not null && !owner.saved.Graph.Variants.Any(v => v.Id == variant && (project is null || v.ProjectId == project)))
                return SymbolsFailure(operation, "SCOPE_NOT_FOUND");
            if (request.Freshness == QueryFreshnessPolicy.Refresh) return SymbolsFailure(operation, "UPDATE_REQUIRED");
            if (request.Freshness == QueryFreshnessPolicy.RequireFresh) Verify();
            if (Errors.Count != 0) return SymbolsFailure(operation, Errors[0].Code);
            var items = new List<SymbolQueryItem>();
            var omitted = new HashSet<string>(StringComparer.Ordinal);
            if (operation.Operation == SymbolQueryOperation.FindSymbols) FindSymbols(operation, items, omitted);
            else
            {
                foreach (var id in operation.Selectors!)
                {
                    Check();
                    if (items.Count >= Math.Min(operation.MaxResults, operation.Budget!.MaxNodes))
                    { items.Add(new(id, null, project, variant, null, null, "omitted", [], [], ["NODE_LIMIT"])); omitted.Add("NODE_LIMIT"); continue; }
                    if (!owner.occurrences.TryGetValue(id, out var occurrence))
                    { items.Add(new(id, null, project, variant, null, null, "not-found", [], [], ["SYMBOL_NOT_FOUND"])); continue; }
                    var node = owner.nodes[occurrence.LogicalSymbolId];
                    if (occurrence.VariantId != variant || owner.Project(node) != project || !NodeAllowed(node, variant))
                    { items.Add(new(id, null, project, variant, null, null, "outside-scope", [], [], ["SYMBOL_OUTSIDE_SCOPE"])); continue; }
                    var item = SymbolItem(occurrence, node);
                    items.Add(item);
                    if (operation.Operation == SymbolQueryOperation.ReadSymbols) ReadSymbol(operation, items, items.Count - 1, occurrence, omitted);
                    else ReadRelations(operation, items, items.Count - 1, occurrence, omitted);
                }
            }
            if (Errors.Count != 0) return SymbolsFailure(operation, Errors[0].Code);
            if (request.Freshness == QueryFreshnessPolicy.RequireFresh) Verify();
            if (Errors.Count != 0) return SymbolsFailure(operation, Errors[0].Code);
            Check();
            // A last complete wire check includes escaped JSON and transport wrapper overhead.
            while (!SymbolFits(operation, items, omitted))
            {
                omitted.Add("OUTPUT_LIMIT");
                if (items.Count == 0) return SymbolsFailure(operation, "OUTPUT_BUDGET_TOO_SMALL");
                if (operation.Operation == SymbolQueryOperation.FindSymbols) items.RemoveAt(items.Count - 1);
                else
                {
                    var i = items.FindLastIndex(item => item.Declarations.Count != 0 || item.Relations.Count != 0);
                    if (i < 0) return SymbolsFailure(operation, "OUTPUT_BUDGET_TOO_SMALL");
                    items[i] = items[i] with { Outcome = "omitted", Declarations = [], Relations = [], Omissions = ["OUTPUT_LIMIT"] };
                }
            }
            var envelope = SymbolEnvelope(operation, items, omitted);
            ValidateSymbolEnvelope(envelope);
            var json = JsonSerializer.Serialize(envelope, QueryJson.Options);
            Check();
            return new(envelope, json);
        }

        private SymbolQueryEnvelope SymbolEnvelope(SymbolQueryRequest operation, IReadOnlyList<SymbolQueryItem> items, IEnumerable<string> omitted)
            => new("1", operation.Operation, new(owner.saved.Graph.WorkspaceId, owner.saved.Graph.SnapshotId,
                owner.saved.Graph.Generation, owner.saved.Graph.Coverage, freshness.State), items, [],
                omitted.Order(StringComparer.Ordinal).ToArray(), omitted.Any(), operation.PrepareMaxMilliseconds, operation.Budget!);

        private bool SymbolFits(SymbolQueryRequest operation, IReadOnlyList<SymbolQueryItem> items, IEnumerable<string> omitted)
        {
            Check();
            var json = JsonSerializer.Serialize(SymbolEnvelope(operation, items, omitted), QueryJson.Options);
            var wire = JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = json } }, isError = false });
            // Reserve a small fixed space for final omission classification.
            return wire.Length + 256 <= operation.Budget!.MaxChars && Encoding.UTF8.GetByteCount(wire) + 256 <= operation.Budget.MaxBytes;
        }

        private SymbolQueryItem SymbolItem(HarnessSymbolOccurrence occurrence, HarnessNode node)
            => new(occurrence.Id, node.Id, owner.Project(node), occurrence.VariantId, node.Name,
                node.Kind.ToString(), "found", [], [], []);

        private SymbolDeclaration? Declaration(HarnessLocation location)
        {
            if (location.RawSpan is not { Length: > 0 } span || !owner.files.TryGetValue(location.SourceId, out var file)
                || location.ContentHash != file.ContentHash) return null;
            return new(file.SourceId, file.RelativePath, file.ContentHash, span,
                QueryWorkspace.IsStored(file) ? "snapshot" : "workspace", "not-requested");
        }

        private void FindSymbols(SymbolQueryRequest operation, List<SymbolQueryItem> items, HashSet<string> omitted)
        {
            // Reuse Explore lexical matching and scope predicates, but do not expand relations or read source.
            var matches = OrderMatches(Search());
            foreach (var match in matches)
            {
                Check();
                foreach (var occurrence in (owner.bySymbol.GetValueOrDefault(match.Node.Id) ?? []).OrderBy(o => o.Id, StringComparer.Ordinal))
                {
                    Check();
                    if (owner.occurrences.ContainsKey(request.Query.Trim()) && occurrence.Id != request.Query.Trim()) continue;
                    if (variant is not null && occurrence.VariantId != variant || !NodeAllowed(match.Node, occurrence.VariantId)) continue;
                    if (items.Count >= Math.Min(operation.MaxResults, operation.Budget!.MaxNodes))
                    { omitted.Add("RESULT_LIMIT_NARROW_QUERY"); return; }
                    var declarations = (occurrence.Declarations ?? (occurrence.Location is null ? [] : [occurrence.Location]))
                        .Where(PathAllowed).Select(Declaration).OfType<SymbolDeclaration>().Take(33).ToArray();
                    var item = SymbolItem(occurrence, match.Node) with { Declarations = declarations.Take(32).ToArray(),
                        Omissions = declarations.Length > 32 ? ["DECLARATION_LIMIT"] : declarations.Length == 0 ? ["SOURCE_RANGE_UNAVAILABLE"] : [] };
                    if (declarations.Length > 32) omitted.Add("DECLARATION_LIMIT");
                    items.Add(item);
                    if (!SymbolFits(operation, items, omitted))
                    { items.RemoveAt(items.Count - 1); omitted.Add("OUTPUT_LIMIT_NARROW_QUERY"); return; }
                }
            }
            foreach (var reason in reasons) omitted.Add(reason);
        }

        private void ReadSymbol(SymbolQueryRequest operation, List<SymbolQueryItem> items, int position,
            HarnessSymbolOccurrence occurrence, HashSet<string> omitted)
        {
            if (!trusted || workspace is null)
            { items[position] = items[position] with { Outcome = "unavailable", Omissions = [!trusted ? "WORKSPACE_UNTRUSTED" : "SOURCE_OWNER_UNAVAILABLE"] }; return; }
            var declarations = new List<SymbolDeclaration>();
            var omissions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var location in (occurrence.Declarations ?? (occurrence.Location is null ? [] : [occurrence.Location])).Where(PathAllowed).Distinct())
            {
                Check();
                var declaration = Declaration(location);
                if (declaration is null) { omissions.Add("SOURCE_RANGE_UNAVAILABLE"); continue; }
                if (declarations.Count >= 32) { omissions.Add("DECLARATION_LIMIT"); break; }
                if (declaration.Span.Length > operation.Budget!.MaxChars || declaration.Span.Length > operation.Budget.MaxBytes)
                { omissions.Add("SOURCE_BUDGET"); continue; }
                try
                {
                    var unit = ReadUnit(owner.files[location.SourceId], location, declaration.Span, [occurrence.Id], "saved-declaration");
                    declarations.Add(declaration with { Text = unit.Text, FirstLine = unit.FirstLine, CompleteSavedRange = true, Outcome = "read" });
                    items[position] = items[position] with { Declarations = declarations.ToArray() };
                    if (!SymbolFits(operation, items, omitted))
                    { declarations.RemoveAt(declarations.Count - 1); omissions.Add("SOURCE_BUDGET"); }
                }
                catch (QuerySourceException error)
                {
                    omissions.Add(error.Code);
                    freshness = new("dirty", null, [], ["requested source could not be verified"]);
                    if (request.Freshness == QueryFreshnessPolicy.RequireFresh) { Error(error.Code, "Source verification failed."); return; }
                }
                catch (DecoderFallbackException)
                {
                    omissions.Add("INVALID_SOURCE_ENCODING");
                    if (request.Freshness == QueryFreshnessPolicy.RequireFresh) { Error("INVALID_SOURCE_ENCODING", "Source verification failed."); return; }
                }
                items[position] = items[position] with { Declarations = declarations.ToArray() };
            }
            if (declarations.Count == 0 && omissions.Count == 0) omissions.Add("SOURCE_RANGE_UNAVAILABLE");
            items[position] = items[position] with { Outcome = declarations.Count == 0 ? "omitted" : omissions.Count > 0 ? "partial" : "read",
                Declarations = declarations.ToArray(), Omissions = omissions.Order(StringComparer.Ordinal).ToArray() };
            if (omissions.Any(reason => reason is "SOURCE_BUDGET" or "DECLARATION_LIMIT")) omitted.Add("OUTPUT_LIMIT");
        }

        private void ReadRelations(SymbolQueryRequest operation, List<SymbolQueryItem> items, int position,
            HarnessSymbolOccurrence occurrence, HashSet<string> omitted)
        {
            var relations = new List<SymbolRelation>();
            var kinds = operation.EdgeKinds ?? ["calls", "constructs"];
            var incoming = operation.Direction == "incoming";
            var available = Math.Max(0, Math.Min(operation.MaxResults, operation.Budget!.MaxEdges) - items.Sum(i => i.Relations.Count));
            var candidates = new SortedSet<HarnessEdge>(Comparer<HarnessEdge>.Create((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id)));
            foreach (var edge in (incoming ? owner.incoming : owner.outgoing).GetValueOrDefault(occurrence.LogicalSymbolId) ?? [])
            {
                Check();
                if (!kinds.Contains(edge.Kind, StringComparer.Ordinal)) continue;
                var here = incoming ? edge.TargetOccurrenceId : edge.SourceOccurrenceId;
                var hereLogical = incoming ? edge.TargetNodeId : edge.SourceNodeId;
                // A logical-only saved relation is a visible leaf, not a bound occurrence.
                if (here is not null ? here != occurrence.Id : hereLogical != occurrence.LogicalSymbolId) continue;
                if (edge.VariantId is not null && edge.VariantId != variant) continue;
                candidates.Add(edge);
                if (candidates.Count > available + 1) candidates.Remove(candidates.Max!);
            }
            foreach (var edge in candidates)
            {
                Check();
                var otherId = incoming ? edge.SourceNodeId : edge.TargetNodeId;
                var otherSelector = incoming ? edge.SourceOccurrenceId : edge.TargetOccurrenceId;
                var outside = !NodeAllowed(owner.nodes[otherId], variant)
                    || otherSelector is not null && owner.occurrences[otherSelector].VariantId != variant;
                if (items.Sum(i => i.Relations.Count) >= Math.Min(operation.MaxResults, operation.Budget!.MaxEdges))
                { omitted.Add("RELATION_LIMIT_NARROW_QUERY"); break; }
                relations.Add(new(edge.Id, edge.Kind, edge.Certainty, edge.SourceNodeId, edge.TargetNodeId,
                    edge.SourceOccurrenceId, edge.TargetOccurrenceId, edge.VariantId, outside, edge.Evidence,
                    owner.nodes[edge.SourceNodeId].Name, owner.nodes[edge.TargetNodeId].Name,
                    owner.Project(owner.nodes[edge.SourceNodeId]), owner.Project(owner.nodes[edge.TargetNodeId]),
                    edge.SourceOccurrenceId is null ? null : owner.occurrences[edge.SourceOccurrenceId].VariantId,
                    edge.TargetOccurrenceId is null ? null : owner.occurrences[edge.TargetOccurrenceId].VariantId));
                items[position] = items[position] with { Relations = relations.ToArray() };
                if (!SymbolFits(operation, items, omitted))
                { relations.RemoveAt(relations.Count - 1); omitted.Add("OUTPUT_LIMIT_NARROW_QUERY"); break; }
            }
            items[position] = items[position] with { Outcome = "relations", Relations = relations.ToArray(),
                Omissions = omitted.Order(StringComparer.Ordinal).ToArray() };
        }

        private void ValidateSymbolEnvelope(SymbolQueryEnvelope envelope)
        {
            if (!Enum.IsDefined(envelope.Operation) || envelope.Snapshot is null || !Enum.IsDefined(envelope.Snapshot.Coverage))
                throw new IndexStoreException("INVALID_SYMBOL_RESULT", "Invalid typed result.");
            foreach (var item in envelope.Items)
            {
                Check();
                if (!IsOccurrenceIdentity(item.Selector) || item.Outcome is not ("found" or "read" or "partial" or "omitted" or "relations" or "not-found" or "outside-scope" or "unavailable"))
                    throw new IndexStoreException("INVALID_SYMBOL_RESULT", "Invalid typed result.");
                if (item.LogicalId is not null && (!owner.occurrences.TryGetValue(item.Selector, out var occurrence)
                    || occurrence.LogicalSymbolId != item.LogicalId || occurrence.VariantId != item.VariantId
                    || owner.Project(owner.nodes[item.LogicalId]) != item.ProjectId))
                    throw new IndexStoreException("INVALID_SYMBOL_RESULT", "Invalid typed result identity.");
                if (item.Relations.Any(edge => !Enum.IsDefined(edge.Certainty) || !SymbolEdgeKinds.Contains(edge.Kind)
                    || !owner.nodes.ContainsKey(edge.SourceLogicalId) || !owner.nodes.ContainsKey(edge.TargetLogicalId)))
                    throw new IndexStoreException("INVALID_SYMBOL_RESULT", "Invalid typed result relation.");
                if (item.Declarations.Any(d => d.Outcome is not ("read" or "not-requested")
                    || d.Storage is not ("workspace" or "snapshot")
                    || d.CompleteSavedRange != (d.Text is not null) || d.Text is not null && (d.Text.Length != d.Span.Length || d.FirstLine is not > 0)
                    || !owner.files.TryGetValue(d.SourceId, out var file) || file.ContentHash != d.ContentHash || file.RelativePath != d.Path
                    || !owner.occurrences.TryGetValue(item.Selector, out var selectedOccurrence)
                    || !(selectedOccurrence.Declarations ?? (selectedOccurrence.Location is null ? [] : [selectedOccurrence.Location]))
                        .Any(location => location.SourceId == d.SourceId && location.ContentHash == d.ContentHash && location.RawSpan == d.Span)))
                    throw new IndexStoreException("INVALID_SYMBOL_RESULT", "Invalid typed source result.");
                foreach (var edge in item.Relations)
                {
                    Check();
                    if (edge.SourceName != owner.nodes[edge.SourceLogicalId].Name || edge.TargetName != owner.nodes[edge.TargetLogicalId].Name
                        || edge.SourceProjectId != owner.Project(owner.nodes[edge.SourceLogicalId])
                        || edge.TargetProjectId != owner.Project(owner.nodes[edge.TargetLogicalId])
                        || edge.SourceVariantId != (edge.SourceSelector is null ? null : owner.occurrences.GetValueOrDefault(edge.SourceSelector)?.VariantId)
                        || edge.TargetVariantId != (edge.TargetSelector is null ? null : owner.occurrences.GetValueOrDefault(edge.TargetSelector)?.VariantId))
                        throw new IndexStoreException("INVALID_SYMBOL_RESULT", "Invalid typed relation scope.");
                    if (!(owner.outgoing.GetValueOrDefault(edge.SourceLogicalId) ?? []).Any(saved =>
                    {
                        Check();
                        return saved.Id == edge.Id && saved.SourceNodeId == edge.SourceLogicalId
                        && saved.TargetNodeId == edge.TargetLogicalId && saved.SourceOccurrenceId == edge.SourceSelector
                        && saved.TargetOccurrenceId == edge.TargetSelector && saved.VariantId == edge.VariantId
                        && saved.Kind == edge.Kind && saved.Certainty == edge.Certainty && saved.Evidence == edge.Evidence;
                    }))
                        throw new IndexStoreException("INVALID_SYMBOL_RESULT", "Invalid typed relation reference.");
                }
            }
        }
    }
}
