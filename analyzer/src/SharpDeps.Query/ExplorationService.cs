namespace SharpDeps.Query;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Index;

public sealed class ExplorationService
{
    private readonly IQueryIndex index;
    private readonly QueryWorkspace? workspace;
    private readonly bool trusted;

    public ExplorationService(IQueryIndex index, QueryWorkspace? workspace = null, bool isTrusted = false)
    {
        ArgumentNullException.ThrowIfNull(index);
        this.index = index;
        trusted = isTrusted;
        this.workspace = isTrusted ? workspace : null;
    }

    public ExploreReply Execute(ExploreRequest request, CancellationToken cancellationToken = default,
        CancellationToken deadlineToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with { Budget = request.Budget ?? new QueryBudget(MaxChars: 12000, MaxBytes: 48000) };
        Validate(request);
        var clock = Stopwatch.StartNew();
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineToken);
        timer.CancelAfter(request.Budget!.MaxMilliseconds);
        Run? run = null;
        try
        {
            timer.Token.ThrowIfCancellationRequested();
            var saved = ExplorationSnapshotCache.Get(index.ReadSnapshot(timer.Token), timer.Token);
            timer.Token.ThrowIfCancellationRequested();
            if (request.ProjectId is not null && request.VariantId is not null
                && saved.saved.Graph.Variants.Any(v => v.Id == request.VariantId && v.ProjectId != request.ProjectId))
                throw new ArgumentException("Project and variant scopes disagree.", nameof(request));
            run = new Run(saved, index, workspace, trusted, request, timer, clock, cancellationToken);
            run.Check();
            if (request.Freshness == QueryFreshnessPolicy.Refresh)
                run.Error("UPDATE_REQUIRED", "Request a trusted coordinator update and open a new reader.");
            else if (request.Freshness == QueryFreshnessPolicy.RequireFresh) run.Verify();
            if (run.Errors.Count == 0 && run.Resolve())
            {
                run.Expand();
                if (request.Format == ExploreFormat.Agent) run.PrepareAgentSummary();
                run.ReadUnits();
                if (request.Freshness == QueryFreshnessPolicy.RequireFresh && run.Errors.Count == 0) run.Verify();
            }
            run.Check();
        }
        catch (OperationCanceledException)
        {
            if (run is null) return PreparationFailure(cancellationToken.IsCancellationRequested ? "CANCELLED" : "QUERY_TIMEOUT");
            run.Cancel(cancellationToken.IsCancellationRequested);
        }
        catch (QuerySourceException error)
        {
            if (run is null) return PreparationFailure(error.Code);
            run.Error(error.Code, "Verify source inputs and retry.", error.RelativePath);
        }
        catch (IndexStoreException error)
        {
            if (run is null) return PreparationFailure(error.Code);
            run.Error(error.Code, "Reopen the current index through its trusted owner.");
        }
        if (run!.Errors.Count != 0) run.Units.Clear();
        return run.Finish();
    }

    private static ExploreReply PreparationFailure(string code)
    {
        var text = $"SharpDeps explore\nError {code}: preparation did not complete; no source returned.\n";
        var wire = JsonSerializer.Serialize(new { content = new[] { new { type = "text", text } }, isError = true });
        return new(text, [new(code, "Reopen the index or retry with an appropriate deadline.")], [], [code],
            wire.Length, Encoding.UTF8.GetByteCount(wire));
    }

    private static void Validate(ExploreRequest request)
    {
        static bool Text(string? value, int maximum) => value is null
            || value.Length <= maximum && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
        var budget = request.Budget!;
        if (!Text(request.Query, 1024) || string.IsNullOrWhiteSpace(request.Query)
            || !Text(request.ProjectId, 128) || !Text(request.VariantId, 128) || !Text(request.PathPrefix, 512)
            || !Enum.IsDefined(request.Freshness) || !Enum.IsDefined(request.Format) || !Enum.IsDefined(request.ScopePolicy)
            || budget.MaxNodes is < 1 or > 500
            || budget.MaxEdges is < 1 or > 1000 || budget.MaxDepth is < 1 or > 8
            || budget.MaxMilliseconds is < 10 or > 30000 || budget.MaxChars is < 2048 or > 1000000
            || budget.MaxBytes is < 2048 or > 4000000)
            throw new ArgumentException("Invalid exploration arguments or budgets.", nameof(request));
        if (request.PathPrefix is { } prefix)
        {
            try { IndexSnapshotValidator.ValidateRelativePath(prefix.TrimEnd('/')); }
            catch (IndexStoreException error) { throw new ArgumentException("Invalid exploration path scope.", nameof(request), error); }
        }
    }

    private static bool IsDocument(HarnessNode node) => node.Kind is HarnessNodeKind.Document or HarnessNodeKind.Section
        or HarnessNodeKind.FrontMatter or HarnessNodeKind.CodeFence or HarnessNodeKind.LinkTarget or HarnessNodeKind.SymbolMention;

    private sealed record Selected(HarnessNode Node, HarnessSymbolOccurrence? Occurrence, int Priority,
        string Reason, HarnessCertainty Certainty, bool IncludeSource)
    {
        public string Identity => Occurrence?.Id ?? Node.Id;
    }
    private sealed record Pending(Selected Selection, HarnessLocation Location, string Reason,
        int? RequiredChars = null, int? RequiredBytes = null, int SourcePriority = 4);
    private sealed record Relation(HarnessEdge Edge, Selected From, Selected To, bool OutsideScope);
    private sealed record Match(HarnessNode Node, int Score, bool Exact, bool Qualified, bool PathMatch = false);
    private sealed record AgentDetail(Selected Root, Selected? Member = null, HarnessLocation? Location = null,
        int? SelectedMembers = null, int? CallerSymbols = null, int? CallerOccurrences = null, int? SavedCallEdges = null);
    private sealed record AgentSummary(AgentDetail[] Rows, int Roots, int Selected, int Relations,
        int RelationKinds, int Resolved, int Candidate, int Unresolved);

    private sealed class Run
    {
        private readonly ExplorationSnapshot owner;
        private readonly IQueryIndex index;
        private readonly QueryWorkspace? workspace;
        private readonly bool trusted;
        private readonly ExploreRequest request;
        private readonly CancellationTokenSource timer;
        private readonly CancellationToken originalToken;
        private readonly Stopwatch clock;
        private readonly Dictionary<string, Selected> selected = new(StringComparer.Ordinal);
        private readonly List<Relation> relations = [];
        private readonly List<Selected> roots = [];
        private readonly Dictionary<string, Match> sourceMatches = new(StringComparer.Ordinal);
        private readonly HashSet<string> sourcePaths = new(StringComparer.Ordinal);
        private readonly HashSet<string> bodySelections = new(StringComparer.Ordinal);
        private readonly List<Pending> pending = [];
        private readonly List<Pending> allCandidates = [];
        private readonly HashSet<string> reasons = new(StringComparer.Ordinal);
        private readonly List<(string Project, string Variant)> choices = [];
        private readonly List<(string Project, string Variant)> alternativeScopes = [];
        private readonly List<Match> symbolChoices = [];
        private string? scopeDecision;
        private int excludedScopes;
        private AgentSummary? agentSummary;
        private string? project;
        private string? variant;
        private int relationDisplayLimit = 8;
        private int omissionDisplayLimit = 8;
        private string? cachedSourceId;
        private string? cachedSourceText;
        private bool cancelled;
        private QueryFreshness freshness = new("unverified", null, [], ["complete inventory/configuration not checked"]);
        public List<QueryIssue> Errors { get; } = [];
        public List<ExploreUnit> Units { get; } = [];

        public Run(ExplorationSnapshot owner, IQueryIndex index, QueryWorkspace? workspace, bool trusted,
            ExploreRequest request, CancellationTokenSource timer, Stopwatch clock, CancellationToken token)
        {
            this.owner = owner; this.index = index; this.workspace = workspace; this.trusted = trusted;
            this.request = request; originalToken = token; this.timer = timer; this.clock = clock;
            project = request.ProjectId; variant = request.VariantId;
        }

        public void Check()
        {
            timer.Token.ThrowIfCancellationRequested();
            if (clock.ElapsedMilliseconds > request.Budget!.MaxMilliseconds) throw new OperationCanceledException();
        }
        public void Error(string code, string message, string? path = null)
        {
            if (!Errors.Any(e => e.Code == code && e.Path == path)) Errors.Add(new(code, message, path));
        }
        public void Cancel(bool requested)
        {
            cancelled = true;
            Error(requested ? "CANCELLED" : "QUERY_TIMEOUT",
                requested ? "The request was cancelled." : "Narrow the query or increase its time budget.");
            Units.Clear();
        }
        public void Verify()
        {
            Check();
            if (!trusted) { Error("WORKSPACE_UNTRUSTED", "Current source requires workspace trust."); return; }
            if (workspace is null) { Error("FRESHNESS_UNVERIFIED", "Supply the trusted source owner and configuration proof."); return; }
            freshness = workspace.Verify(owner.saved, timer.Token);
            Check();
            if (freshness.State != "verified-current")
                Error("FRESHNESS_REQUIREMENT_NOT_MET", "Wait for the trusted owner to publish a current generation, then reopen the reader.");
        }

        private bool PathAllowed(HarnessLocation location) => request.PathPrefix is not { } prefix
            || owner.files.TryGetValue(location.SourceId, out var file)
                && (file.RelativePath == prefix || file.RelativePath.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.Ordinal));

        private bool NodeAllowed(HarnessNode node, string? selectedVariant = null)
        {
            if (IsDocument(node) && project is not null && !DocumentScopeProven(node, selectedVariant, project)) return false;
            if (project is not null && !IsDocument(node) && owner.Project(node) != project) return false;
            if (selectedVariant is not null && !IsDocument(node)
                && !(owner.bySymbol.GetValueOrDefault(node.Id) ?? []).Any(o => o.VariantId == selectedVariant)) return false;
            return request.PathPrefix is null || owner.Locations(node, selectedVariant).Any(PathAllowed);
        }

        private HarnessNode DocumentSection(HarnessNode node)
        {
            while (node.Kind is not (HarnessNodeKind.Section or HarnessNodeKind.Document)
                && node.ParentId is { } parent && owner.nodes.TryGetValue(parent, out var ancestor)) node = ancestor;
            return node;
        }

        private bool DocumentScopeProven(HarnessNode node, string? selectedVariant, string selectedProject)
        {
            var section = DocumentSection(node).Id;
            foreach (var edge in owner.saved.Graph.Edges)
            {
                Check();
                if (selectedVariant is not null && edge.VariantId is not null && edge.VariantId != selectedVariant) continue;
                var from = owner.nodes[edge.SourceNodeId]; var to = owner.nodes[edge.TargetNodeId];
                HarnessNode code; string? occurrenceId;
                if (IsDocument(from) && DocumentSection(from).Id == section && !IsDocument(to))
                { code = to; occurrenceId = edge.TargetOccurrenceId; }
                else if (IsDocument(to) && DocumentSection(to).Id == section && !IsDocument(from))
                { code = from; occurrenceId = edge.SourceOccurrenceId; }
                else continue;
                if (owner.Project(code) != selectedProject) continue;
                if (selectedVariant is null) return true;
                if (occurrenceId is not null && owner.occurrences[occurrenceId].VariantId == selectedVariant) return true;
                if (edge.VariantId == selectedVariant) return true;
            }
            return false;
        }

        public bool Resolve()
        {
            Check();
            if (project is not null && !owner.saved.Graph.Variants.Any(v => v.ProjectId == project))
            { Error("SCOPE_NOT_FOUND", "The project is not in this selected index."); return false; }
            if (variant is not null)
            {
                var scope = owner.saved.Graph.Variants.FirstOrDefault(v => v.Id == variant);
                if (scope is null) { Error("SCOPE_NOT_FOUND", "The variant is not in this selected index."); return false; }
                project ??= scope.ProjectId;
            }
            var matches = Search();
            if (Errors.Count != 0) return false;
            if (choices.Count > 1)
            {
                if (request.ScopePolicy == ExploreScopePolicy.PreferDebug) scopeDecision = "ambiguous-explicit-occurrences";
                return false;
            }
            if (matches.Count == 0)
            { Error("SYMBOL_NOT_FOUND", "No match in the selected index; try a qualified name, exact identity or saved file path."); return false; }
            foreach (var match in matches.Where(m => !IsDocument(m.Node)))
                foreach (var occurrence in owner.bySymbol.GetValueOrDefault(match.Node.Id) ?? [])
                {
                    Check();
                    var scope = owner.saved.Graph.Variants.Single(v => v.Id == occurrence.VariantId);
                    if ((project is null || scope.ProjectId == project) && (variant is null || scope.Id == variant)
                        && owner.Locations(match.Node, occurrence.VariantId).Any(PathAllowed))
                        choices.Add((scope.ProjectId, scope.Id));
                }
            var tuples = choices.Distinct().OrderBy(t => t.Project, StringComparer.Ordinal).ThenBy(t => t.Variant, StringComparer.Ordinal).ToArray();
            choices.Clear(); choices.AddRange(tuples);
            if (request.ScopePolicy == ExploreScopePolicy.PreferDebug && choices.Count > 0 && !TryPreferScope())
            {
                if (Errors.Count == 0) reasons.Add("SCOPE_AMBIGUOUS");
                return false;
            }
            if (choices.Count > 1) { reasons.Add("SCOPE_AMBIGUOUS"); return false; }
            if (choices.Count == 1)
            {
                project = choices[0].Project; variant = choices[0].Variant; choices.Clear();
            }
            if (request.ScopePolicy == ExploreScopePolicy.PreferDebug && scopeDecision is null && matches.All(match => IsDocument(match.Node)))
                scopeDecision = "no-code-scope";
            matches = matches.Where(m => NodeAllowed(m.Node, variant)).ToList();
            if (Terms(request.Query).Length == 1 && matches.Where(m => m.Exact).ToArray() is { Length: > 1 } exact)
            {
                var families = exact.Select(m => m.Node.Kind == HarnessNodeKind.Member ? m.Node.ParentId
                    : Regex.Replace(m.Node.Name, "[<`].*$", "", RegexOptions.CultureInvariant)).Distinct(StringComparer.Ordinal).ToArray();
                if (families.Length > 1) { symbolChoices.AddRange(exact); reasons.Add("SYMBOL_AMBIGUOUS"); return false; }
            }
            foreach (var match in matches.Take(request.Budget!.MaxNodes))
            {
                Check();
                var include = match.PathMatch || match.Node.Kind != HarnessNodeKind.Type || !matches.Any(m => m.Node.Kind == HarnessNodeKind.Member
                    && m.Node.ParentId == match.Node.Id && m.Exact);
                sourceMatches[match.Node.Id] = match;
                var item = Select(match.Node, variant, 0, match.Exact ? "requested" : "lexical match", HarnessCertainty.Resolved, include);
                if (item is not null) roots.Add(item);
            }
            if (matches.Count > roots.Count) reasons.Add("NODE_LIMIT");
            return roots.Count != 0;
        }

        private bool TryPreferScope()
        {
            if (request.ScopePolicy != ExploreScopePolicy.PreferDebug) return false;
            var identities = Terms(request.Query).Select(term => owner.occurrences.TryGetValue(term, out var occurrence)
                ? owner.nodes[occurrence.LogicalSymbolId] : owner.nodes.GetValueOrDefault(term))
                .OfType<HarnessNode>().DistinctBy(node => node.Id).ToArray();
            if (identities.Length > 0)
            {
                var count = choices.Count;
                choices.RemoveAll(choice => identities.Any(node => !IdentityAllowsScope(node, choice)));
                excludedScopes = count - choices.Count;
                if (choices.Count == 0)
                {
                    scopeDecision = "incompatible-explicit-identities";
                    Error("SYMBOL_OUTSIDE_SCOPE", "No matching saved scope contains all explicit identities within the supplied path.");
                    return false;
                }
            }
            if (choices.Count == 1)
            {
                project = choices[0].Project; variant = choices[0].Variant; choices.Clear();
                scopeDecision = request.VariantId is not null ? "explicit-variant"
                    : Terms(request.Query).Any(owner.occurrences.ContainsKey) ? "explicit-occurrence"
                    : excludedScopes > 0 ? "selected-by-explicit-identities" : "unique-matching-scope";
                return true;
            }
            if (choices.Any(choice => choice.Project != choices[0].Project))
            { scopeDecision = "ambiguous-multiple-projects"; return false; }
            var candidateIds = choices.Select(choice => choice.Variant).ToHashSet(StringComparer.Ordinal);
            HarnessVariant? preferred = null;
            foreach (var candidate in owner.saved.Graph.Variants)
            {
                Check();
                if (!candidateIds.Contains(candidate.Id) || !candidate.Configuration.Equals("Debug", StringComparison.OrdinalIgnoreCase)) continue;
                if (preferred is not null) { scopeDecision = "ambiguous-multiple-debug-variants"; return false; }
                preferred = candidate;
            }
            if (preferred is null) { scopeDecision = "ambiguous-no-debug-variant"; return false; }
            project = preferred.ProjectId; variant = preferred.Id;
            alternativeScopes.AddRange(choices.Where(choice => choice.Variant != variant));
            choices.Clear();
            scopeDecision = "selected-unique-saved-debug";
            return true;
        }

        private bool IdentityAllowsScope(HarnessNode node, (string Project, string Variant) choice)
        {
            Check();
            if (IsDocument(node))
                return DocumentScopeProven(node, choice.Variant, choice.Project)
                    && (request.PathPrefix is null || owner.Locations(node, choice.Variant).Any(PathAllowed));
            return owner.Project(node) == choice.Project && NodeAllowed(node, choice.Variant);
        }

        private static string ShortName(string name)
        {
            var depth = 0; var start = 0;
            for (var i = 0; i < name.Length; i++)
            {
                if (name[i] is '<' or '[' or '(') depth++;
                else if (name[i] is '>' or ']' or ')') depth--;
                else if (name[i] == '.' && depth == 0) start = i + 1;
            }
            return name[start..];
        }
        private static string[] Terms(string query) => Regex.Matches(query,
                @"[\p{L}\p{N}_@][\p{L}\p{N}_@`./\\:+<>,\[\]]*", RegexOptions.CultureInvariant)
            .Select(m => m.Value.Replace('\\', '/').TrimEnd('.', ',', ':')).Where(t => t.Length != 0)
            .Distinct(StringComparer.Ordinal).Take(33).ToArray();

        private List<Match> Search()
        {
            var query = request.Query.Trim();
            var wholeIdentity = owner.occurrences.ContainsKey(query) || owner.nodes.ContainsKey(query);
            string[] terms = wholeIdentity ? [query] : Terms(query);
            if (!wholeIdentity && !terms.Any(term => owner.occurrences.ContainsKey(term) || owner.nodes.ContainsKey(term) || IsSavedIdentity(term)))
            {
                var literal = owner.saved.Graph.Nodes.Where(n => NodeAllowed(n, variant)
                    && (n.Name == query || owner.Label(n) == query || n.Signature == query)).Select(n => new Match(n, 10000, true, true)).ToList();
                if (literal.Count > 0) return literal;
            }
            if (terms.Length > 32) throw new ArgumentException("Explore accepts at most thirty-two distinct terms.");
            var occurrenceScopes = new HashSet<(string Project, string Variant)>();
            foreach (var term in terms)
            {
                Check();
                if (owner.occurrences.TryGetValue(term, out var occurrence))
                {
                    var scope = owner.saved.Graph.Variants.Single(v => v.Id == occurrence.VariantId);
                    if ((project is not null && scope.ProjectId != project) || (variant is not null && scope.Id != variant))
                    { Error("SYMBOL_OUTSIDE_SCOPE", "An explicitly requested identity is outside the supplied compilation scope."); return []; }
                    if (!NodeAllowed(owner.nodes[occurrence.LogicalSymbolId], occurrence.VariantId))
                    { Error("SYMBOL_OUTSIDE_SCOPE", "An explicitly requested identity is outside the supplied project, variant or path scope."); return []; }
                    occurrenceScopes.Add((scope.ProjectId, scope.Id));
                }
                else if (!owner.nodes.ContainsKey(term) && IsSavedIdentity(term))
                { Error("SYMBOL_NOT_FOUND", "An explicitly requested identity is not in the selected index."); return []; }
            }
            if (occurrenceScopes.Count > 1)
            {
                choices.AddRange(occurrenceScopes.OrderBy(s => s.Project, StringComparer.Ordinal).ThenBy(s => s.Variant, StringComparer.Ordinal));
                reasons.Add("SCOPE_AMBIGUOUS");
                return [];
            }
            if (occurrenceScopes.Count == 1)
            {
                var scope = occurrenceScopes.Single();
                project ??= scope.Project;
                variant = scope.Variant;
            }
            var found = new Dictionary<string, Match>(StringComparer.Ordinal);
            var pathTerms = terms.Where(term => term.Contains('/') || term.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || term.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.Ordinal);
            sourcePaths.UnionWith(pathTerms);
            foreach (var term in terms)
            {
                Check();
                var exactNode = owner.occurrences.TryGetValue(term, out var occurrence)
                    ? owner.nodes[occurrence.LogicalSymbolId] : owner.nodes.GetValueOrDefault(term);
                if (exactNode is not null)
                {
                    if (!NodeAllowed(exactNode, variant))
                    { Error("SYMBOL_OUTSIDE_SCOPE", "An explicitly requested identity is outside the supplied project, variant or path scope."); return []; }
                    found[exactNode.Id] = new(exactNode, 10000, true, true, found.GetValueOrDefault(exactNode.Id)?.PathMatch == true);
                    continue;
                }
                var hits = term.Length <= 128
                    ? index.Search(term, 128, timer.Token).Select(h => h.NodeId).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in owner.saved.Graph.Nodes)
                {
                    Check();
                    if (node.Kind is not (HarnessNodeKind.Type or HarnessNodeKind.Member or HarnessNodeKind.Section or HarnessNodeKind.Document)
                        || !NodeAllowed(node, variant)) continue;
                    var label = owner.Label(node);
                    var simple = ShortName(node.Name);
                    var baseName = Regex.Replace(simple, "[<`].*$", "", RegexOptions.CultureInvariant);
                    var exact = term == node.Name || term == simple || term == baseName || term == label || term == node.Signature;
                    var qualified = term.Contains('.') && term == label || term == node.Signature;
                    var path = pathTerms.Contains(term) && owner.Locations(node, variant).Any(l =>
                        owner.files.TryGetValue(l.SourceId, out var file) && (file.RelativePath == term || Path.GetFileName(file.RelativePath) == term));
                    var score = exact ? 100 : path ? 90 : hits.Contains(node.Id) ? 10 : label.Contains(term, StringComparison.OrdinalIgnoreCase) ? 5 : 0;
                    if (score == 0) continue;
                    var previous = found.GetValueOrDefault(node.Id);
                    if (previous is { Score: >= 10000 })
                    {
                        if (path && !previous.PathMatch) found[node.Id] = previous with { PathMatch = true };
                        continue;
                    }
                    found[node.Id] = new(node, score + (previous?.Score ?? 0), exact || previous?.Exact == true,
                        qualified || previous?.Qualified == true, path || previous?.PathMatch == true);
                }
            }
            var matches = found.Values.ToList();
            var anchors = matches.Where(m => m.Exact && m.Node.Kind == HarnessNodeKind.Type).Select(m => m.Node.Id).ToHashSet(StringComparer.Ordinal);
            if (anchors.Count > 0)
                matches.RemoveAll(m => m.Node.Kind == HarnessNodeKind.Member && !m.Qualified && m.Node.ParentId is { } parent
                    && !anchors.Contains(parent) && matches.Any(other => other.Node.Kind == HarnessNodeKind.Member && other.Node.Name == m.Node.Name
                        && other.Node.ParentId is { } otherParent && anchors.Contains(otherParent)));
            if (matches.Any(m => m.Exact)) matches.RemoveAll(m => !m.Exact && m.Score < 90);
            return matches.OrderByDescending(m => m.Score).ThenBy(m => owner.Label(m.Node), StringComparer.Ordinal)
                .ThenBy(m => m.Node.Id, StringComparer.Ordinal).ToList();
        }

        private static bool IsSavedIdentity(string value) => value.Length == 69
                && (value.StartsWith("hsym_", StringComparison.Ordinal) || value.StartsWith("hocc_", StringComparison.Ordinal)
                    || value.StartsWith("hdoc_", StringComparison.Ordinal) || value.StartsWith("hsec_", StringComparison.Ordinal))
                && !value.AsSpan(5).ContainsAnyExcept("0123456789abcdef")
            || value.Length == 68 && value.StartsWith("hpr_", StringComparison.Ordinal)
                && !value.AsSpan(4).ContainsAnyExcept("0123456789abcdef")
            || value.Length == 35 && value.StartsWith("hw_", StringComparison.Ordinal)
                && !value.AsSpan(3).ContainsAnyExcept("0123456789abcdef");

        private Selected? Select(HarnessNode node, string? selectedVariant, int priority, string reason,
            HarnessCertainty certainty, bool includeSource)
        {
            var occurrence = (owner.bySymbol.GetValueOrDefault(node.Id) ?? []).FirstOrDefault(o => o.VariantId == selectedVariant);
            var item = new Selected(node, occurrence, priority, reason, certainty, includeSource);
            if (selected.TryGetValue(item.Identity, out var previous))
                item = previous with { Priority = Math.Min(previous.Priority, priority), IncludeSource = previous.IncludeSource || includeSource,
                    Reason = !previous.IncludeSource && includeSource ? reason : previous.Reason };
            else if (selected.Count >= request.Budget!.MaxNodes)
            { reasons.Add("NODE_LIMIT"); return null; }
            selected[item.Identity] = item;
            return item;
        }

        public void Expand()
        {
            var queue = new Queue<(Selected Item, int Depth, bool Reverse)>();
            foreach (var root in roots) queue.Enqueue((root, 0, true));
            foreach (var root in roots.Where(root => root.Node.Kind == HarnessNodeKind.Type))
            {
                var members = new SortedSet<HarnessNode>(Comparer<HarnessNode>.Create((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id)));
                foreach (var node in owner.saved.Graph.Nodes)
                {
                    Check();
                    if (node.Kind != HarnessNodeKind.Member || node.ParentId != root.Node.Id || !NodeAllowed(node, variant)) continue;
                    members.Add(node);
                    if (members.Count > request.Budget!.MaxNodes + 1) members.Remove(members.Max!);
                }
                foreach (var member in members)
                {
                    Check();
                    var item = Select(member, variant, 1, "declared member", HarnessCertainty.Resolved, false);
                    if (item is null) break;
                    if (!roots.Any(existing => existing.Identity == item.Identity)) queue.Enqueue((item, 0, true));
                }
            }
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            while (queue.TryDequeue(out var current))
            {
                Check();
                if (!visited.Add(current.Item.Identity)) continue;
                if (visited.Count > request.Budget!.MaxNodes) { reasons.Add("NODE_LIMIT"); break; }
                if (current.Depth >= request.Budget.MaxDepth)
                {
                    if ((owner.outgoing.GetValueOrDefault(current.Item.Node.Id) ?? []).Length > 0) reasons.Add("DEPTH_LIMIT");
                    continue;
                }
                var steps = (owner.outgoing.GetValueOrDefault(current.Item.Node.Id) ?? []).Select(e => (Edge: e, Reverse: false));
                if (current.Reverse) steps = steps.Concat((owner.incoming.GetValueOrDefault(current.Item.Node.Id) ?? []).Select(e => (Edge: e, Reverse: true)));
                foreach (var step in steps.OrderByDescending(s => !s.Reverse && BodyEdge(s.Edge.Kind))
                    .ThenBy(s => s.Edge.Id, StringComparer.Ordinal).ThenBy(s => s.Reverse))
                {
                    Check();
                    var edge = step.Edge;
                    if (edge.VariantId is not null && variant is not null && edge.VariantId != variant) continue;
                    var here = step.Reverse ? edge.TargetOccurrenceId : edge.SourceOccurrenceId;
                    if (current.Item.Occurrence is { } occurrence && here is not null && occurrence.Id != here) continue;
                    var firstVisit = !edgeIds.Contains(edge.Id);
                    if (firstVisit && relations.Count >= request.Budget.MaxEdges) { reasons.Add("EDGE_LIMIT"); continue; }
                    var other = owner.nodes[step.Reverse ? edge.SourceNodeId : edge.TargetNodeId];
                    var otherOccurrenceId = step.Reverse ? edge.SourceOccurrenceId : edge.TargetOccurrenceId;
                    var otherOccurrence = otherOccurrenceId is null ? null : owner.occurrences[otherOccurrenceId];
                    var outside = !NodeAllowed(other, variant) || otherOccurrence is not null && otherOccurrence.VariantId != variant;
                    var next = new Selected(other, otherOccurrence, current.Depth + 1, "via " + edge.Kind, edge.Certainty, false);
                    var savedHere = current.Item with { Occurrence = here is null ? null : owner.occurrences[here] };
                    if (firstVisit)
                    {
                        edgeIds.Add(edge.Id);
                        relations.Add(new(edge, step.Reverse ? next : savedHere, step.Reverse ? savedHere : next, outside));
                    }
                    if (outside) continue;
                    if (IsDocument(other))
                    {
                        var section = DocumentSection(other);
                        if (NodeAllowed(section, variant)) Select(section, null, 1, edge.Kind + " " + edge.Certainty, edge.Certainty, true);
                        continue;
                    }
                    if (step.Reverse || !BodyEdge(edge.Kind) || edge.Certainty != HarnessCertainty.Resolved || otherOccurrence is null || variant is null
                        || here is null || current.Item.Occurrence is null) continue;
                    bodySelections.Add(other.Id);
                    var expansion = Select(other, variant, current.Depth + 1, "via " + edge.Kind, edge.Certainty, true);
                    if (expansion is not null) queue.Enqueue((expansion, current.Depth + 1, false));
                }
            }
        }

        private static bool BodyEdge(string kind) => kind is "calls" or "constructs" or "reads" or "writes" or "references_member";

        public void PrepareAgentSummary()
        {
            var typeRoots = roots.Where(root => root.Node.Kind == HarnessNodeKind.Type && root.Reason == "requested")
                .ToDictionary(root => root.Node.Id, StringComparer.Ordinal);
            var memberRoots = roots.Where(root => root.Node.Kind == HarnessNodeKind.Member && root.Reason == "requested"
                    && root.Occurrence is not null)
                .ToDictionary(root => root.Occurrence!.Id, StringComparer.Ordinal);
            var members = new Dictionary<string, List<Selected>>(StringComparer.Ordinal);
            foreach (var item in selected.Values)
            {
                Check();
                if (item.Node.Kind != HarnessNodeKind.Member || item.Node.ParentId is not { } parent || !typeRoots.ContainsKey(parent)) continue;
                if (!members.TryGetValue(parent, out var group)) members.Add(parent, group = []);
                group.Add(item);
            }
            var callers = new Dictionary<string, List<Relation>>(StringComparer.Ordinal);
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            var resolved = 0; var candidate = 0; var unresolved = 0;
            foreach (var relation in relations)
            {
                Check();
                var edge = relation.Edge;
                kinds.Add(edge.Kind);
                if (edge.Certainty == HarnessCertainty.Resolved) resolved++;
                else if (edge.Certainty == HarnessCertainty.Candidate) candidate++;
                else unresolved++;
                if (edge.Kind is not ("calls" or "constructs") || edge.Certainty != HarnessCertainty.Resolved || relation.OutsideScope
                    || relation.From.Occurrence is not { } source || relation.To.Occurrence is not { } target
                    || !memberRoots.TryGetValue(target.Id, out var root) || root.Node.Id != edge.TargetNodeId
                    || edge.SourceOccurrenceId != source.Id || edge.TargetOccurrenceId != target.Id
                    || source.VariantId != variant || target.VariantId != variant || IsDocument(relation.From.Node)
                    || request.PathPrefix is not null && (edge.Evidence is null || !PathAllowed(edge.Evidence))
                    || !NodeAllowed(relation.From.Node, variant) || !NodeAllowed(relation.To.Node, variant)) continue;
                if (!callers.TryGetValue(root.Identity, out var group)) callers.Add(root.Identity, group = []);
                group.Add(relation);
            }
            var rows = new List<AgentDetail>();
            foreach (var root in roots.OrderBy(root => root.Priority).ThenBy(root => root.Identity, StringComparer.Ordinal))
            {
                Check();
                var children = members.GetValueOrDefault(root.Node.Id) ?? [];
                var incoming = callers.GetValueOrDefault(root.Identity) ?? [];
                var countCallers = root.Node.Kind == HarnessNodeKind.Member && root.Reason == "requested" && root.Occurrence is not null;
                rows.Add(new(root, SelectedMembers: typeRoots.ContainsKey(root.Node.Id) ? children.Count : null,
                    CallerSymbols: countCallers ? incoming.Select(relation => relation.From.Node.Id).Distinct(StringComparer.Ordinal).Count() : null,
                    CallerOccurrences: countCallers ? incoming.Select(relation => relation.From.Occurrence!.Id).Distinct(StringComparer.Ordinal).Count() : null,
                    SavedCallEdges: countCallers ? incoming.Count : null));
                foreach (var member in children.OrderBy(member => member.Node.Signature, StringComparer.Ordinal)
                    .ThenBy(member => member.Identity, StringComparer.Ordinal))
                {
                    Check();
                    rows.Add(new(root, member, AgentMemberLocation(member)));
                }
            }
            agentSummary = new(rows.ToArray(), roots.Count, selected.Count, relations.Count, kinds.Count, resolved, candidate, unresolved);
        }

        private HarnessLocation? AgentMemberLocation(Selected member)
        {
            if (member.Occurrence is not { } occurrence)
                return member.Node.Location is { } saved && PathAllowed(saved) ? saved : null;
            if (occurrence.Location is { } primary && PathAllowed(primary)) return primary;
            if (occurrence.Declarations is not { } declarations) return null;
            for (var index = 0; index < Math.Min(declarations.Count, request.Budget!.MaxNodes); index++)
            {
                Check();
                if (PathAllowed(declarations[index])) return declarations[index];
            }
            return null;
        }

        private int SourcePriority(Selected item, HarnessLocation location)
        {
            if (sourceMatches.TryGetValue(item.Node.Id, out var match))
            {
                var anchored = match.PathMatch && owner.files.TryGetValue(location.SourceId, out var file)
                    && (sourcePaths.Contains(file.RelativePath) || sourcePaths.Contains(Path.GetFileName(file.RelativePath)));
                if (match.Exact) return match.PathMatch && !anchored ? 2 : 0;
                if (anchored) return 1;
            }
            return bodySelections.Contains(item.Node.Id) || !sourceMatches.ContainsKey(item.Node.Id) ? 3 : 4;
        }

        public void ReadUnits()
        {
            foreach (var item in selected.Values.Where(s => s.IncludeSource))
            {
                Check();
                var locations = item.Occurrence?.Declarations ?? (item.Occurrence?.Location is { } occurrenceLocation ? [occurrenceLocation]
                    : item.Node.Location is { } nodeLocation ? new[] { nodeLocation } : []);
                if (locations.Count == 0) reasons.Add("DECLARATION_UNAVAILABLE");
                foreach (var location in locations.Distinct().Where(PathAllowed))
                {
                    Check();
                    pending.Add(new(item, location, "budget", SourcePriority: SourcePriority(item, location)));
                }
            }
            pending.Sort((left, right) =>
            {
                var order = left.SourcePriority.CompareTo(right.SourcePriority);
                if (order == 0) order = left.Selection.Priority.CompareTo(right.Selection.Priority);
                if (order == 0) order = StringComparer.Ordinal.Compare(owner.Label(left.Selection.Node), owner.Label(right.Selection.Node));
                if (order == 0) order = StringComparer.Ordinal.Compare(left.Selection.Identity, right.Selection.Identity);
                if (order == 0) order = StringComparer.Ordinal.Compare(left.Location.SourceId, right.Location.SourceId);
                if (order == 0) order = Nullable.Compare(left.Location.RawSpan?.Start, right.Location.RawSpan?.Start);
                return order != 0 ? order : Nullable.Compare(left.Location.RawSpan?.Length, right.Location.RawSpan?.Length);
            });
            allCandidates.AddRange(pending);
            if (!trusted || workspace is null)
            {
                reasons.Add(trusted ? "SOURCE_OWNER_UNAVAILABLE" : "WORKSPACE_UNTRUSTED");
                for (var i = 0; i < pending.Count; i++) pending[i] = pending[i] with { Reason = "source access unavailable" };
                return;
            }
            var failedFiles = new HashSet<string>(StringComparer.Ordinal);
            var requested = allCandidates.Where(candidate => candidate.SourcePriority <= 2).ToArray();
            var groups = requested.GroupBy(candidate => candidate.Location.SourceId, StringComparer.Ordinal)
                .OrderBy(group => group.Min(candidate => candidate.SourcePriority))
                .ThenBy(group => group.Key, StringComparer.Ordinal).ToArray();
            if (groups.Length > 0)
            {
                var baseline = Size(Render(Units, pending, 0, 0));
                var shareChars = Math.Max(0, request.Budget!.MaxChars - baseline.Chars) / groups.Length;
                var shareBytes = Math.Max(0, request.Budget.MaxBytes - baseline.Bytes) / groups.Length;
                foreach (var group in groups)
                {
                    Check();
                    var used = Size(Render(Units, pending, 0, 0));
                    var ceiling = (Chars: Math.Min(request.Budget.MaxChars, used.Chars + shareChars),
                        Bytes: Math.Min(request.Budget.MaxBytes, used.Bytes + shareBytes));
                    foreach (var candidate in group) ReadCandidate(candidate, failedFiles, ceiling);
                }
                // Release unused reservations only after every requested file has had its turn.
                foreach (var candidate in requested) ReadCandidate(candidate, failedFiles);
            }
            CoalesceOwners(failedFiles, requestedOnly: true);
            foreach (var candidate in allCandidates.Where(candidate => candidate.SourcePriority > 2))
                ReadCandidate(candidate, failedFiles);
            CoalesceOwners(failedFiles);
            if (pending.Count != 0) reasons.Add("OMITTED_UNITS");
        }

        private void ReadCandidate(Pending original, HashSet<string> failedFiles, (int Chars, int Bytes)? ceiling = null)
        {
            Check();
            if (Errors.Count != 0 || failedFiles.Contains(original.Location.SourceId)) return;
            var candidate = pending.FirstOrDefault(item => item.Selection.Identity == original.Selection.Identity
                && item.Location == original.Location && item.Reason == "budget");
            if (candidate is null) return;
            var location = candidate.Location;
            if (location.RawSpan is not { Length: > 0 } span || !owner.files.TryGetValue(location.SourceId, out var file))
            { Replace(candidate, candidate with { Reason = "source range unavailable" }); reasons.Add("SOURCE_RANGE_UNAVAILABLE"); return; }
            if (span.Length > request.Budget!.MaxChars || span.Length > request.Budget.MaxBytes)
            { Replace(candidate, candidate with { RequiredChars = span.Length, RequiredBytes = span.Length }); return; }
            try
            {
                var unit = ReadUnit(file, location, span, [candidate.Selection.Node.Id],
                    IsDocument(candidate.Selection.Node) ? "markdown saved-range" : "saved-declaration");
                var proposed = Merge([.. Units, unit]);
                var remaining = pending.Where(item => item != candidate).ToArray();
                if (AdmitSource(proposed, remaining, out var wire, ceiling))
                { Units.Clear(); Units.AddRange(proposed); pending.Remove(candidate); }
                else Replace(candidate, candidate with { RequiredChars = wire.Chars, RequiredBytes = wire.Bytes });
            }
            catch (DecoderFallbackException) { Failed("INVALID_SOURCE_ENCODING", candidate, failedFiles); }
            catch (QuerySourceException error) { Failed(error.Code, candidate, failedFiles); }
        }

        private ExploreUnit ReadUnit(IndexFile file, HarnessLocation location, HarnessRawSpan span,
            IReadOnlyList<string> symbols, string role)
        {
            if (cachedSourceId != file.SourceId)
            {
                cachedSourceText = null; cachedSourceId = null;
                cachedSourceText = new UTF8Encoding(false, true).GetString(workspace!.Read(file, timer.Token));
                cachedSourceId = file.SourceId;
            }
            var text = cachedSourceText!;
            if (location.ContentHash != file.ContentHash || span.End > text.Length
                || span.Start < text.Length && char.IsLowSurrogate(text[span.Start])
                || span.End > 0 && char.IsHighSurrogate(text[span.End - 1]))
                throw new QuerySourceException("SOURCE_CHANGED_SINCE_SNAPSHOT", file.RelativePath);
            return new(file.SourceId, file.RelativePath, file.ContentHash, span,
                text.Substring(span.Start, span.Length), 1 + text.AsSpan(0, span.Start).Count('\n'), symbols, role);
        }

        private void CoalesceOwners(HashSet<string> failedFiles, bool requestedOnly = false)
        {
            var owners = roots.Select(root => root.Node.Kind == HarnessNodeKind.Type ? root.Node.Id : root.Node.ParentId)
                .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            foreach (var id in owners)
            {
                Check();
                if (Errors.Count != 0) break;
                if (!owner.nodes.TryGetValue(id, out var node) || node.Kind != HarnessNodeKind.Type || !NodeAllowed(node, variant)) continue;
                var occurrence = (owner.bySymbol.GetValueOrDefault(node.Id) ?? []).FirstOrDefault(item => item.VariantId == variant);
                foreach (var location in (occurrence?.Declarations ?? []).Where(PathAllowed).Distinct())
                {
                    Check();
                    if (Errors.Count != 0) break;
                    if (location.RawSpan is not { Length: > 0 and <= 8192 } span
                        || span.Length > request.Budget!.MaxChars || span.Length > request.Budget.MaxBytes
                        || !owner.files.TryGetValue(location.SourceId, out var file) || failedFiles.Contains(file.SourceId)) continue;
                    if (requestedOnly && !allCandidates.Any(item => item.SourcePriority <= 2
                        && (item.Selection.Node.Id == node.Id || item.Selection.Node.ParentId == node.Id)
                        && item.Location.SourceId == file.SourceId && item.Location.ContentHash == location.ContentHash
                        && item.Location.RawSpan is { } requestedSpan && requestedSpan.Start >= span.Start && requestedSpan.End <= span.End)) continue;
                    var contained = Units.Where(unit => unit.SourceId == file.SourceId && unit.ContentHash == location.ContentHash
                        && unit.Span.Start >= span.Start && unit.Span.End <= span.End).ToArray();
                    var coveredPending = pending.Where(item => item.Reason == "budget" && item.Location.SourceId == file.SourceId
                        && item.Location.ContentHash == file.ContentHash && item.Location.RawSpan is { Length: > 0 } part
                        && part.Start >= span.Start && part.End <= span.End).ToHashSet();
                    var whole = coveredPending.Where(item => item.Location.RawSpan == span).ToArray();
                    var explicitWhole = whole.Any(item => item.SourcePriority <= 2);
                    var proper = contained.Where(unit => unit.Span != span).ToArray();
                    var memberPending = coveredPending.Where(item => item.Location.RawSpan != span).ToArray();
                    if (!explicitWhole && proper.Select(unit => unit.Span).Concat(memberPending.Select(item => item.Location.RawSpan!.Value))
                        .Distinct().Take(2).Count() < 2) continue;
                    var candidate = new Pending(new(node, occurrence, 0, "owner context", HarnessCertainty.Resolved, false), location, "budget");
                    try
                    {
                        var fragments = new List<ExploreUnit>(proper);
                        foreach (var item in memberPending.OrderBy(item => item.Selection.Identity, StringComparer.Ordinal))
                        {
                            Check();
                            fragments.Add(ReadUnit(file, item.Location, item.Location.RawSpan!.Value, [item.Selection.Node.Id], "saved-declaration"));
                        }
                        fragments = Merge(fragments);
                        if (!explicitWhole && fragments.Sum(item => (long)item.Span.Length) * 4 < span.Length * 3) continue;
                        var symbols = contained.SelectMany(item => item.SymbolIds)
                            .Concat(coveredPending.Select(item => item.Selection.Node.Id)).Distinct(StringComparer.Ordinal).ToArray();
                        var unit = ReadUnit(file, location, span, symbols, explicitWhole ? "saved-declaration" : "saved-owner-declaration");
                        var proposed = Merge([.. Units.Where(item => !contained.Contains(item)), unit]);
                        var remaining = pending.Where(item => !coveredPending.Contains(item)).ToArray();
                        if (!AdmitSource(proposed, remaining, out _)) continue;
                        Units.Clear(); Units.AddRange(proposed);
                        pending.Clear(); pending.AddRange(remaining);
                    }
                    catch (DecoderFallbackException) { Failed("INVALID_SOURCE_ENCODING", candidate, failedFiles); }
                    catch (QuerySourceException error) { Failed(error.Code, candidate, failedFiles); }
                }
            }
        }

        private bool AdmitSource(IReadOnlyList<ExploreUnit> units, IReadOnlyList<Pending> omitted, out (int Chars, int Bytes) wire,
            (int Chars, int Bytes)? ceiling = null)
        {
            var relationCount = Math.Min(relationDisplayLimit, relations.Count);
            var detailCount = omissionDisplayLimit;
            while (true)
            {
                Check();
                wire = Size(Render(units, omitted, relationCount, detailCount));
                if (wire.Chars <= (ceiling?.Chars ?? request.Budget!.MaxChars)
                    && wire.Bytes <= (ceiling?.Bytes ?? request.Budget!.MaxBytes))
                {
                    relationDisplayLimit = relationCount;
                    omissionDisplayLimit = detailCount;
                    return true;
                }
                if (relationCount > 0) relationCount--;
                else if (detailCount > 0) detailCount--;
                else return false;
            }
        }

        private void Replace(Pending old, Pending replacement) { var i = pending.IndexOf(old); if (i >= 0) pending[i] = replacement; }
        private void Failed(string code, Pending candidate, HashSet<string> failedFiles)
        {
            failedFiles.Add(candidate.Location.SourceId);
            pending.RemoveAll(p => p.Location.SourceId == candidate.Location.SourceId);
            pending.AddRange(allCandidates.Where(p => p.Location.SourceId == candidate.Location.SourceId).Select(p => p with { Reason = code }));
            Units.RemoveAll(u => u.SourceId == candidate.Location.SourceId);
            reasons.Add(code);
            freshness = new("dirty", DateTimeOffset.UtcNow,
                freshness.DirtyPaths.Append(owner.files[candidate.Location.SourceId].RelativePath).Distinct(StringComparer.Ordinal).ToArray(),
                ["only requested source units checked; complete semantic freshness remains unverified"]);
            if (request.Freshness == QueryFreshnessPolicy.RequireFresh)
                Error(code, "Source changed or is unavailable; update and reopen the reader.", owner.files[candidate.Location.SourceId].RelativePath);
        }

        private static List<ExploreUnit> Merge(IEnumerable<ExploreUnit> units)
        {
            var result = new List<ExploreUnit>();
            foreach (var group in units.GroupBy(u => u.SourceId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
                foreach (var unit in group.OrderBy(u => u.Span.Start).ThenBy(u => u.Span.End))
                {
                    if (result.LastOrDefault() is { } prior && prior.SourceId == unit.SourceId && unit.Span.Start <= prior.Span.End)
                    {
                        var tail = unit.Span.End > prior.Span.End ? unit.Text[(prior.Span.End - unit.Span.Start)..] : "";
                        result[^1] = prior with { Span = new(prior.Span.Start, Math.Max(prior.Span.End, unit.Span.End) - prior.Span.Start),
                            Text = prior.Text + tail, SymbolIds = prior.SymbolIds.Concat(unit.SymbolIds).Distinct(StringComparer.Ordinal).ToArray(),
                            Role = prior.Role == unit.Role ? prior.Role : "saved-range" };
                    }
                    else result.Add(unit);
                }
            return result;
        }

        public ExploreReply Finish()
        {
            var relationCount = Math.Min(relationDisplayLimit, relations.Count);
            var detailCount = omissionDisplayLimit;
            while (true)
            {
                if (Errors.Count == 0 && !cancelled)
                    try { Check(); } catch (OperationCanceledException) { Cancel(originalToken.IsCancellationRequested); }
                if (pending.Count > 0) reasons.Add("OMITTED_UNITS");
                var text = Render(Units, pending, relationCount, detailCount);
                var wire = Size(text);
                if (wire.Chars <= request.Budget!.MaxChars && wire.Bytes <= request.Budget.MaxBytes)
                    return new(text, Errors.ToArray(), Units.ToArray(), reasons.Order(StringComparer.Ordinal).ToArray(), wire.Chars, wire.Bytes);
                reasons.Add("OUTPUT_LIMIT");
                if (relationCount > 0) { relationCount--; continue; }
                if (detailCount > 0) { detailCount--; continue; }
                if (Units.Count > 0)
                {
                    var removed = Units.OrderByDescending(unit => allCandidates.Where(candidate => unit.SourceId == candidate.Location.SourceId
                            && unit.SymbolIds.Contains(candidate.Selection.Node.Id) && candidate.Location.RawSpan is { } span
                            && span.Start >= unit.Span.Start && span.End <= unit.Span.End)
                        .Select(candidate => candidate.SourcePriority).DefaultIfEmpty(4).Min())
                        .ThenByDescending(u => u.SourceId, StringComparer.Ordinal).ThenByDescending(u => u.Span.Start).First();
                    Units.Remove(removed);
                    foreach (var candidate in allCandidates.Where(p => p.Location.SourceId == removed.SourceId
                        && p.Location.RawSpan is { } span && span.Start >= removed.Span.Start && span.End <= removed.Span.End))
                        if (!pending.Any(p => p.Selection.Identity == candidate.Selection.Identity && p.Location == candidate.Location))
                            pending.Add(candidate with { Reason = "budget", RequiredChars = wire.Chars, RequiredBytes = wire.Bytes });
                    continue;
                }
                Error("OUTPUT_BUDGET_TOO_SMALL", "Increase the response budget.");
                const string minimal = "SharpDeps explore\nError OUTPUT_BUDGET_TOO_SMALL: increase maxChars/maxBytes. No source returned.\n";
                var size = Size(minimal);
                return new(minimal, Errors.ToArray(), [], reasons.Order(StringComparer.Ordinal).ToArray(), size.Chars, size.Bytes);
            }
        }

        private (int Chars, int Bytes) Size(string text)
        {
            // This bounds one text-content result with conservative escaping, not a client-controlled JSON-RPC ID.
            var wire = JsonSerializer.Serialize(new { content = new[] { new { type = "text", text } }, isError = Errors.Count != 0 });
            return (wire.Length, Encoding.UTF8.GetByteCount(wire));
        }

        private string Render(IReadOnlyList<ExploreUnit> units, IReadOnlyList<Pending> omitted, int relationCount, int detailCount)
            => request.Format == ExploreFormat.Agent ? RenderAgent(units, omitted, relationCount, detailCount)
                : RenderAudit(units, omitted, relationCount, detailCount);

        private string RenderAudit(IReadOnlyList<ExploreUnit> units, IReadOnlyList<Pending> omitted, int relationCount, int detailCount)
        {
            var text = new StringBuilder();
            void Line(FormattableString value) => text.AppendLine(value.ToString(CultureInfo.InvariantCulture));
            var graph = owner.saved.Graph;
            Line($"SharpDeps explore generation={graph.Generation} snapshot={Safe(graph.SnapshotId, 160)} coverage={graph.Coverage}");
            Line($"Freshness={freshness.State}; returned source ranges={units.Count}, hash-verified when returned.");
            text.AppendLine("Source defaults: completeSavedRange=true, body=unknown, role=saved-declaration; completeness is the saved span only. Owner context does not select additional semantic targets or prove inactive text active.");
            if (project is not null || variant is not null)
            {
                var scope = owner.saved.Graph.Variants.FirstOrDefault(v => v.Id == variant);
                Line($"Scope project={project ?? "unselected"} variant={variant ?? "unselected"} {Safe(scope?.TargetFramework, 80)} {Safe(scope?.Configuration, 80)} path={request.PathPrefix ?? "all saved paths"}");
            }
            AppendScopeDecision(text, detailCount);
            text.AppendLine("Coverage is the selected index, not proof of runtime consumers or absent unindexed code/tests.");
            foreach (var diagnostic in (graph.Diagnostics ?? []).Take(8)) Line($"Diagnostic {Safe(diagnostic.Code, 80)}={diagnostic.Count}");
            if ((graph.Diagnostics?.Count ?? 0) > 8) Line($"Diagnostics omitted={graph.Diagnostics!.Count - 8}");
            foreach (var error in Errors.Take(8)) Line($"Error {Safe(error.Code, 80)}: {Safe(error.Message, 240)} {Safe(error.Path, 160)}");
            if (freshness.DirtyPaths.Count > 0) Line($"Dirty sources={freshness.DirtyPaths.Count}: {string.Join(", ", freshness.DirtyPaths.Take(4).Select(p => Safe(p, 160)))}");
            if (freshness.Unverified.Count > 0) Line($"Unverified: {string.Join("; ", freshness.Unverified.Take(4).Select(p => Safe(p, 160)))}");
            if (choices.Count > 0)
            {
                Line($"Choose project/variant ({choices.Count} matching scopes; no code scope was inferred):");
                foreach (var choice in choices.Take(detailCount))
                {
                    var scope = graph.Variants.Single(v => v.Id == choice.Variant);
                    Line($"{Safe(owner.nodes[choice.Project].Name, 100)} project={choice.Project} variant={choice.Variant} {Safe(scope.TargetFramework, 80)} {Safe(scope.Configuration, 80)}{ScopeChoiceDimensions(scope)}");
                }
                if (choices.Count > detailCount) Line($"Scope choices omitted={choices.Count - detailCount}; supply project/variant or a narrower query.");
            }
            if (symbolChoices.Count > 0)
            {
                Line($"Ambiguous symbols={symbolChoices.Count}; use exact identity or qualified owner:");
                foreach (var choice in symbolChoices.Take(detailCount))
                    Line($"{Safe(owner.Label(choice.Node), 160)} id={choice.Node.Id} signature={Safe(choice.Node.Signature, 240)}");
                if (symbolChoices.Count > detailCount) Line($"Symbol choices omitted={symbolChoices.Count - detailCount}.");
            }
            var shownRelations = relations.Take(relationCount).ToArray();
            var shownOmissions = omitted.Take(detailCount).ToArray();
            var identities = new Dictionary<string, Selected>(StringComparer.Ordinal);
            foreach (var item in roots) identities.TryAdd(item.Identity, item);
            foreach (var unit in units)
                foreach (var item in selected.Values.Where(s => unit.SymbolIds.Contains(s.Node.Id))
                    .OrderBy(s => s.Priority).ThenBy(s => s.Identity, StringComparer.Ordinal))
                    identities.TryAdd(item.Identity, item);
            foreach (var relation in shownRelations)
            { identities.TryAdd(relation.From.Identity, relation.From); identities.TryAdd(relation.To.Identity, relation.To); }
            foreach (var omission in shownOmissions) identities.TryAdd(omission.Selection.Identity, omission.Selection);
            var aliases = identities.Values.OrderBy(s => s.Priority).ThenBy(s => s.Identity, StringComparer.Ordinal)
                .Select((s, i) => (s.Identity, Alias: "@" + (i + 1).ToString(CultureInfo.InvariantCulture))).ToDictionary(p => p.Identity, p => p.Alias, StringComparer.Ordinal);
            if (identities.Count > 0) text.AppendLine("Symbols (id once; source defaults: selection=requested certainty=Resolved; declarations retain signatures):");
            foreach (var item in identities.Values.OrderBy(s => aliases[s.Identity], StringComparer.Ordinal))
            {
                var returned = units.Any(u => u.SymbolIds.Contains(item.Node.Id));
                var details = returned ? " source" + (item.Reason == "requested" ? "" : " selection=" + Safe(item.Reason, 100))
                    + (item.Certainty == HarnessCertainty.Resolved ? "" : " certainty=" + item.Certainty)
                    : " signature=" + Safe(item.Node.Signature, 240);
                Line($"{aliases[item.Identity]} {Safe(owner.Label(item.Node), 160)} id={item.Identity}{details}");
            }
            var sourceIds = units.Select(u => u.SourceId).Concat(shownOmissions.Select(o => o.Location.SourceId))
                .Concat(shownRelations.Select(r => r.Edge.Evidence?.SourceId).OfType<string>())
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var sourceAliases = sourceIds.Select((id, i) => (id, Alias: "F" + (i + 1).ToString(CultureInfo.InvariantCulture)))
                .ToDictionary(p => p.id, p => p.Alias, StringComparer.Ordinal);
            foreach (var sourceId in sourceIds)
                if (owner.files.TryGetValue(sourceId, out var file))
                    Line($"{sourceAliases[sourceId]} {file.RelativePath} source={sourceId} saved-sha256={file.ContentHash}");
            var proofs = shownRelations.Select(r => (r.Edge.Producer, r.Edge.Origin)).Distinct()
                .Select((key, i) => (key, Alias: "P" + (i + 1).ToString(CultureInfo.InvariantCulture))).ToDictionary(p => p.key, p => p.Alias);
            if (shownRelations.Length > 0) text.AppendLine("Saved relation pointers (source only in returned ranges; certainty is semantic evidence, not runtime dispatch):");
            foreach (var proof in proofs)
                Line($"{proof.Value} producer={Safe(proof.Key.Producer, 64)} origin={Safe(proof.Key.Origin, 80)}");
            foreach (var relation in shownRelations)
            {
                var evidence = relation.Edge.Evidence;
                var source = evidence is null ? "none" : sourceAliases[evidence.SourceId];
                Line($"{aliases[relation.From.Identity]} --{Safe(relation.Edge.Kind, 64)}/{relation.Edge.Certainty}--> {aliases[relation.To.Identity]} proof={proofs[(relation.Edge.Producer, relation.Edge.Origin)]} at={source} span={evidence?.RawSpan?.Start}:{evidence?.RawSpan?.Length}{(relation.OutsideScope ? " outside-selected-scope; not expanded" : "")}");
            }
            if (relations.Count > shownRelations.Length) Line($"Relations omitted={relations.Count - shownRelations.Length}; narrow to an exact symbol for more evidence.");
            foreach (var unit in units)
            {
                var symbols = identities.Values.Where(s => unit.SymbolIds.Contains(s.Node.Id))
                    .OrderBy(s => s.Priority).ThenBy(s => s.Identity, StringComparer.Ordinal).Select(s => aliases[s.Identity]);
                Line($"Source {sourceAliases[unit.SourceId]} {string.Join(",", symbols)} span={unit.Span.Start}:{unit.Span.Length}{(unit.Role == "saved-declaration" ? "" : " role=" + unit.Role)}");
                var lines = unit.Text.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i == lines.Length - 1 && lines[i].Length == 0) break;
                    text.Append((unit.FirstLine + i).ToString(CultureInfo.InvariantCulture)).Append('\t').Append(lines[i]).Append('\n');
                }
            }
            if (omitted.Count > 0) Line($"Omitted saved units={omitted.Count}; no omitted body is claimed complete. Retry exact id with larger budget or read the exact source span; sourceChars=span length:");
            foreach (var omission in shownOmissions)
                Line($"{aliases[omission.Selection.Identity]} {sourceAliases[omission.Location.SourceId]} span={omission.Location.RawSpan?.Start}:{omission.Location.RawSpan?.Length} reason={Safe(omission.Reason, 100)} retryWireCharsAtLeast={omission.RequiredChars} retryWireBytesAtLeast={omission.RequiredBytes}");
            if (omitted.Count > shownOmissions.Length) Line($"Omission details not shown={omitted.Count - shownOmissions.Length}; narrow the query to requested identities.");
            var status = (omitted.Count > 0 ? reasons.Append("OMITTED_UNITS") : reasons).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (status.Length > 0) Line($"Limits/status: {string.Join(",", status)}");
            return text.ToString();
        }

        private string RenderAgent(IReadOnlyList<ExploreUnit> units, IReadOnlyList<Pending> omitted, int relationCount, int detailCount)
        {
            var text = new StringBuilder();
            void Line(FormattableString value) => text.AppendLine(value.ToString(CultureInfo.InvariantCulture));
            var graph = owner.saved.Graph;
            Line($"SharpDeps explore generation={graph.Generation} snapshot={Safe(graph.SnapshotId, 160)} coverage={graph.Coverage} Freshness={freshness.State}");
            text.AppendLine("Source defaults: completeSavedRange=true, body=unknown, role=saved-declaration; hash-verified saved spans only. Owner context adds no semantic targets or proof that inactive text is active.");
            text.AppendLine("Coverage: selected index; runtime consumers and unindexed code/tests remain unproven.");
            var scope = graph.Variants.FirstOrDefault(value => value.Id == variant);
            Line($"Scope project={project ?? "unselected"} variant={variant ?? "unselected"} {Safe(scope?.TargetFramework, 80)} {Safe(scope?.Configuration, 80)} path={request.PathPrefix ?? "all saved paths"}");
            AppendScopeDecision(text, detailCount);
            foreach (var diagnostic in (graph.Diagnostics ?? []).Take(8)) Line($"Diagnostic {Safe(diagnostic.Code, 80)}={diagnostic.Count}");
            if ((graph.Diagnostics?.Count ?? 0) > 8) Line($"Diagnostics omitted={graph.Diagnostics!.Count - 8}");
            foreach (var error in Errors.Take(8)) Line($"Error {Safe(error.Code, 80)}: {Safe(error.Message, 240)} {Safe(error.Path, 160)}");
            if (freshness.DirtyPaths.Count > 0) Line($"Dirty sources={freshness.DirtyPaths.Count}: {string.Join(", ", freshness.DirtyPaths.Take(4).Select(path => Safe(path, 160)))}");
            if (freshness.Unverified.Count > 0) Line($"Unverified: {string.Join("; ", freshness.Unverified.Take(4).Select(value => Safe(value, 160)))}");
            if (choices.Count > 0)
            {
                Line($"Choose project/variant ({choices.Count} matching scopes; no code scope was inferred):");
                foreach (var choice in choices.Take(detailCount))
                {
                    var choiceScope = graph.Variants.Single(value => value.Id == choice.Variant);
                    Line($"{Safe(owner.nodes[choice.Project].Name, 100)} project={choice.Project} variant={choice.Variant} {Safe(choiceScope.TargetFramework, 80)} {Safe(choiceScope.Configuration, 80)}{ScopeChoiceDimensions(choiceScope)}");
                }
                if (choices.Count > detailCount) Line($"Scope choices omitted={choices.Count - detailCount}; supply project/variant or a narrower query.");
            }
            if (symbolChoices.Count > 0)
            {
                Line($"Ambiguous symbols={symbolChoices.Count}; re-query an exact selector with its scope:");
                foreach (var choice in symbolChoices.Take(detailCount))
                    Line($"{Safe(owner.Label(choice.Node), 160)} selector={choice.Node.Id} signature={Safe(choice.Node.Signature, 240)}");
                if (symbolChoices.Count > detailCount) Line($"Symbol choices omitted={symbolChoices.Count - detailCount}.");
            }
            var shownRelations = relations.Take(relationCount).ToArray();
            var shownOmissions = omitted.Take(detailCount).ToArray();
            AgentDetail[] summaryRows = detailCount > 0 && agentSummary is not null ? agentSummary.Rows.Take(detailCount - 1).ToArray() : [];
            var identities = new Dictionary<string, Selected>(StringComparer.Ordinal);
            foreach (var item in roots) identities.TryAdd(item.Identity, item);
            foreach (var unit in units)
                foreach (var item in selected.Values.Where(selection => unit.SymbolIds.Contains(selection.Node.Id))
                    .OrderBy(selection => selection.Priority).ThenBy(selection => selection.Identity, StringComparer.Ordinal))
                    identities.TryAdd(item.Identity, item);
            foreach (var relation in shownRelations)
            { identities.TryAdd(relation.From.Identity, relation.From); identities.TryAdd(relation.To.Identity, relation.To); }
            foreach (var omission in shownOmissions) identities.TryAdd(omission.Selection.Identity, omission.Selection);
            foreach (var row in summaryRows)
                if (row.Member is { } member) identities.TryAdd(member.Identity, member);
            var aliases = identities.Values.OrderBy(selection => selection.Priority).ThenBy(selection => selection.Identity, StringComparer.Ordinal)
                .Select((selection, index) => (selection.Identity, Alias: "@" + (index + 1).ToString(CultureInfo.InvariantCulture)))
                .ToDictionary(pair => pair.Identity, pair => pair.Alias, StringComparer.Ordinal);
            var selectors = roots.Select(item => item.Identity).Concat(shownOmissions.Select(item => item.Selection.Identity)).ToHashSet(StringComparer.Ordinal);
            foreach (var row in summaryRows)
                if (row.Member is { } member) selectors.Add(member.Identity);
            if (identities.Count > 0) text.AppendLine("Symbols (@n is reply-local; selection=requested certainty=Resolved unless shown):");
            foreach (var item in identities.Values.OrderBy(selection => aliases[selection.Identity], StringComparer.Ordinal))
            {
                var details = selectors.Contains(item.Identity) ? " selector=" + item.Identity : "";
                details += units.Any(unit => unit.SymbolIds.Contains(item.Node.Id)) ? " source" : " signature=" + Safe(item.Node.Signature, 240);
                if (item.Reason != "requested") details += " selection=" + Safe(item.Reason, 100);
                if (item.Certainty != HarnessCertainty.Resolved) details += " certainty=" + item.Certainty;
                Line($"{aliases[item.Identity]} {Safe(owner.Label(item.Node), 160)}{details}");
            }
            var sourceIds = units.Select(unit => unit.SourceId).Concat(shownOmissions.Select(item => item.Location.SourceId))
                .Concat(shownRelations.Select(relation => relation.Edge.Evidence?.SourceId).OfType<string>())
                .Concat(summaryRows.Select(row => row.Location?.SourceId).OfType<string>())
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var sourceAliases = sourceIds.Select((id, index) => (id, Alias: "F" + (index + 1).ToString(CultureInfo.InvariantCulture)))
                .ToDictionary(pair => pair.id, pair => pair.Alias, StringComparer.Ordinal);
            foreach (var sourceId in sourceIds)
                if (owner.files.TryGetValue(sourceId, out var file)) Line($"{sourceAliases[sourceId]} {file.RelativePath}");
            AppendAgentSummary(text, summaryRows, units, omitted, aliases, sourceAliases, detailCount);
            if (shownRelations.Length > 0) text.AppendLine("Saved relations (certainty is semantic evidence, not runtime dispatch):");
            foreach (var relation in shownRelations)
            {
                var evidence = relation.Edge.Evidence;
                var at = evidence is null ? "unknown" : sourceAliases[evidence.SourceId] + ":" + VerifiedLine(evidence, units);
                Line($"{aliases[relation.From.Identity]} -{Safe(relation.Edge.Kind, 64)}/{relation.Edge.Certainty}-> {aliases[relation.To.Identity]} at={at}{(relation.OutsideScope ? " outside-selected-scope; not expanded" : "")}");
            }
            if (relations.Count > shownRelations.Length) Line($"Relations omitted={relations.Count - shownRelations.Length}; narrow to an exact selector for more evidence.");
            foreach (var unit in units)
            {
                var symbols = identities.Values.Where(selection => unit.SymbolIds.Contains(selection.Node.Id))
                    .OrderBy(selection => selection.Priority).ThenBy(selection => selection.Identity, StringComparer.Ordinal).Select(selection => aliases[selection.Identity]);
                var lastLine = unit.FirstLine + unit.Text.AsSpan().Count('\n') - (unit.Text.EndsWith('\n') ? 1 : 0);
                Line($"Source {sourceAliases[unit.SourceId]}:{unit.FirstLine}-{lastLine} {string.Join(",", symbols)}{(unit.Role == "saved-declaration" ? "" : " role=" + unit.Role)}");
                var lines = unit.Text.Split('\n');
                for (var index = 0; index < lines.Length; index++)
                {
                    if (index == lines.Length - 1 && lines[index].Length == 0) break;
                    text.Append((unit.FirstLine + index).ToString(CultureInfo.InvariantCulture)).Append('\t').Append(lines[index]).Append('\n');
                }
            }
            if (omitted.Count > 0) Line($"Omitted saved units={omitted.Count}; no omitted body is claimed complete. Retry its exact selector with larger maxChars/maxBytes, or read the saved source range in audit.");
            foreach (var omission in shownOmissions)
                Line($"{aliases[omission.Selection.Identity]} {sourceAliases[omission.Location.SourceId]}:{VerifiedLine(omission.Location, units)} reason={Safe(omission.Reason, 100)} retryCharsAtLeast={omission.RequiredChars} retryBytesAtLeast={omission.RequiredBytes}");
            if (omitted.Count > shownOmissions.Length) Line($"Omission details not shown={omitted.Count - shownOmissions.Length}; narrow to requested selectors.");
            var status = (omitted.Count > 0 ? reasons.Append("OMITTED_UNITS") : reasons).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (status.Length > 0) Line($"Limits/status: {string.Join(",", status)}");
            return text.ToString();
        }

        private void AppendAgentSummary(StringBuilder text, IReadOnlyList<AgentDetail> rows, IReadOnlyList<ExploreUnit> units,
            IReadOnlyList<Pending> omitted, IReadOnlyDictionary<string, string> aliases, IReadOnlyDictionary<string, string> sourceAliases, int detailCount)
        {
            if (detailCount == 0 || agentSummary is not { } summary) return;
            void Line(FormattableString value) => text.AppendLine(value.ToString(CultureInfo.InvariantCulture));
            var incomplete = owner.saved.Graph.Coverage != HarnessCoverage.CompleteWithinScope || reasons.Count > 0 || Errors.Count > 0;
            Line($"Retained overview roots={summary.Roots} selected={summary.Selected} relations={summary.Relations} relationKinds={summary.RelationKinds} resolved={summary.Resolved} candidate={summary.Candidate} unresolved={summary.Unresolved} admittedRanges={units.Count} rowsShown={rows.Count}/{summary.Rows.Length} incomplete={incomplete.ToString().ToLowerInvariant()}; callers are resolved bound in-scope saved evidence, not runtime totals");
            foreach (var row in rows)
            {
                if (row.Member is not { } member)
                {
                    var counts = row.CallerSymbols is { } symbols
                        ? FormattableString.Invariant($" callerSymbols={symbols} callerOccurrences={row.CallerOccurrences} savedCallEdges={row.SavedCallEdges}")
                        : row.SelectedMembers is { } count ? FormattableString.Invariant($" selectedMembers={count}") : "";
                    Line($"Retained root {aliases[row.Root.Identity]} kind={row.Root.Node.Kind}{counts}");
                    continue;
                }
                var location = row.Location;
                var covering = location?.RawSpan is { } span ? units.FirstOrDefault(unit => unit.SourceId == location.SourceId
                    && unit.ContentHash == location.ContentHash && span.Start >= unit.Span.Start && span.End <= unit.Span.End) : null;
                var covered = covering is not null;
                var admitted = covering?.SymbolIds.Contains(member.Node.Id) == true;
                var missing = omitted.Any(item => item.Selection.Identity == member.Identity);
                var status = covered ? (admitted ? "returned" : "saved-range-covered") + (missing ? "+omitted" : "")
                    : missing ? "omitted" : member.IncludeSource ? "not-returned" : "not-requested";
                var at = location is not null ? sourceAliases[location.SourceId] + ":" + VerifiedLine(location, units) : "unknown";
                Line($"Member {aliases[row.Root.Identity]} -> {aliases[member.Identity]} signature={Safe(member.Node.Signature, 240)} variant={member.Occurrence?.VariantId ?? "unknown"} at={at} source={status}");
            }
        }

        private string ScopeChoiceDimensions(HarnessVariant scope)
            => request.ScopePolicy == ExploreScopePolicy.PreferDebug
                ? $" platform={Safe(scope.Platform ?? "unspecified", 80)} rid={Safe(scope.RuntimeIdentifier ?? "unspecified", 80)}" : "";

        private void AppendScopeDecision(StringBuilder text, int detailCount)
        {
            if (scopeDecision is null) return;
            void Line(FormattableString value) => text.AppendLine(value.ToString(CultureInfo.InvariantCulture));
            var selectedScope = owner.saved.Graph.Variants.FirstOrDefault(value => value.Id == variant);
            var platform = selectedScope is null ? "unknown" : selectedScope.Platform ?? "unspecified";
            var rid = selectedScope is null ? "unknown" : selectedScope.RuntimeIdentifier ?? "unspecified";
            Line($"Scope policy=prefer-debug decision={scopeDecision} excludedByIdentity={excludedScopes} platform={Safe(platform, 80)} rid={Safe(rid, 80)}");
            if (alternativeScopes.Count == 0) return;
            Line($"Alternative scopes={alternativeScopes.Count}; supply variantId to switch:");
            foreach (var choice in alternativeScopes.Take(detailCount))
            {
                var scope = owner.saved.Graph.Variants.Single(value => value.Id == choice.Variant);
                Line($"project={choice.Project} variant={choice.Variant} {Safe(scope.TargetFramework, 80)} {Safe(scope.Configuration, 80)}{ScopeChoiceDimensions(scope)}");
            }
            if (alternativeScopes.Count > detailCount) Line($"Alternative scopes omitted={alternativeScopes.Count - detailCount}.");
        }

        private static string VerifiedLine(HarnessLocation location, IReadOnlyList<ExploreUnit> units)
        {
            if (location.RawSpan is not { } span) return "unknown";
            foreach (var unit in units)
                if (unit.SourceId == location.SourceId && unit.ContentHash == location.ContentHash
                    && span.Start >= unit.Span.Start && span.End <= unit.Span.End)
                    return (unit.FirstLine + unit.Text.AsSpan(0, span.Start - unit.Span.Start).Count('\n')).ToString(CultureInfo.InvariantCulture);
            return "unknown";
        }

        private static string Safe(string? value, int maximum)
        {
            if (value is null) return "";
            value = string.Concat(value.Select(c => char.IsControl(c) ? ' ' : c));
            if (value.Length <= maximum) return value;
            var length = maximum;
            if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
            return value[..length] + "…[display shortened]";
        }
    }
}
