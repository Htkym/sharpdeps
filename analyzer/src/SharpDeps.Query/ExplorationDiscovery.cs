namespace SharpDeps.Query;

using SharpDeps.Analysis.Core.Diagnostics;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;

public sealed partial class ExplorationService
{
    public static bool HasSavedIdentity(string query) => Run.Terms(query).Any(Run.IsSavedIdentity);

    internal static string AggregateDiscoveryOutcome(bool failed, int returnedRanges, IEnumerable<string> groupOutcomes)
    {
        if (failed)
            return "failed";
        if (returnedRanges > 0)
            return "source-returned";
        var outcomes = groupOutcomes.ToArray();
        if (outcomes.Contains("scope-choices", StringComparer.Ordinal))
            return "scope-choices";
        return outcomes.Contains("budget-limited", StringComparer.Ordinal) ? "budget-limited" : "source-unavailable";
    }

    private sealed partial class Run
    {
        private List<Run>? discoveryGroups;
        private int omittedGroups;
        private Func<string, int, (int Chars, int Bytes)>? measureDiscovery;

        private string Outcome(IReadOnlyList<ExploreUnit> units, IReadOnlyList<Pending> omitted)
        {
            if (Errors.Any(error => error.Code == "SYMBOL_NOT_FOUND"))
                return "no-match";
            if (Errors.Count != 0)
                return "failed";
            if (units.Count > 0)
                return "source-returned";
            if (choices.Count > 0 || symbolChoices.Count > 0)
                return "scope-choices";
            if (!trusted || workspace is null || omitted.Any(item => item.Reason != "budget"))
                return "source-unavailable";
            if (omitted.Any(item => item.Reason == "budget") && trusted && workspace is not null)
                return "budget-limited";
            return "source-unavailable";
        }

        private void PrepareMemberFallback()
        {
            var omittedTypes = pending.Where(item => item.Reason == "budget" && item.Selection.Node.Kind == HarnessNodeKind.Type)
                .Select(item => item.Selection.Node.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var item in selected.Values.Where(item => item.Node.Kind == HarnessNodeKind.Member
                && item.Node.ParentId is { } parent && omittedTypes.Contains(parent)).OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray())
            {
                Check();
                var locations = item.Occurrence?.Declarations ?? (item.Occurrence?.Location is { } occurrenceLocation ? [occurrenceLocation]
                    : item.Node.Location is { } nodeLocation ? new[] { nodeLocation } : []);
                foreach (var location in locations.Where(PathAllowed).Distinct())
                {
                    if (location.RawSpan is not { Length: > 0 } || allCandidates.Any(candidate => candidate.Selection.Identity == item.Identity && candidate.Location == location))
                        continue;
                    var candidate = new Pending(item with { IncludeSource = true }, location, "budget", SourcePriority: 3);
                    pending.Add(candidate);
                    allCandidates.Add(candidate);
                }
            }
        }

        public void Discover()
        {
            using var metricsScope = PhaseMetrics.Measure("explore.discover");
            var matches = OrderMatches(Search());
            if (Errors.Count != 0)
                return;
            var rankedScopes = new Dictionary<(string Project, string Variant), int>();
            using (PhaseMetrics.Measure("explore.discovery-rank"))
            {
                for (var rank = 0; rank < matches.Count; rank++)
                {
                    Check();
                    var node = matches[rank].Node;
                    if (IsDocument(node))
                        continue;
                    foreach (var occurrence in owner.bySymbol.GetValueOrDefault(node.Id) ?? [])
                    {
                        Check();
                        var scope = owner.saved.Graph.Variants.Single(value => value.Id == occurrence.VariantId);
                        if ((project is null || scope.ProjectId == project) && (variant is null || scope.Id == variant)
                            && owner.Locations(node, scope.Id).Any(PathAllowed))
                            rankedScopes.TryAdd((scope.ProjectId, scope.Id), rank);
                    }
                }
            }
            if (rankedScopes.Count == 0)
            {
                if (Resolve())
                {
                    Expand();
                    if (request.Format == ExploreFormat.Agent)
                        PrepareAgentSummary();
                    ReadUnits();
                }
                if (request.Freshness == QueryFreshnessPolicy.RequireFresh && Errors.Count == 0)
                    Verify();
                return;
            }

            var budget = request.Budget!;
            (string Project, string Variant)[] scopes;
            using (PhaseMetrics.Measure("explore.discovery-select"))
            {
                scopes = rankedScopes.OrderBy(item => item.Value)
                    .ThenBy(item => item.Key.Project, StringComparer.Ordinal).ThenBy(item => item.Key.Variant, StringComparer.Ordinal)
                    .Take(Math.Min(4, Math.Min(budget.MaxNodes, budget.MaxEdges))).Select(item => item.Key).ToArray();
            }
            omittedGroups = rankedScopes.Count - scopes.Length;
            if (omittedGroups > 0)
                reasons.Add("SCOPE_GROUP_LIMIT");
            discoveryGroups = [];
            for (var position = 0; position < scopes.Length; position++)
            {
                Check();
                var scope = scopes[position];
                // Collection quotas sum to the caller's limits; no group gets a new deadline or reader.
                var scoped = request with
                {
                    ProjectId = scope.Project,
                    VariantId = scope.Variant,
                    ScopePolicy = ExploreScopePolicy.RequireUnique,
                    Budget = budget with
                    {
                        MaxNodes = budget.MaxNodes / scopes.Length + (position < budget.MaxNodes % scopes.Length ? 1 : 0),
                        MaxEdges = budget.MaxEdges / scopes.Length + (position < budget.MaxEdges % scopes.Length ? 1 : 0)
                    }
                };
                var group = new Run(owner, index, workspace, trusted, scoped, execution, originalToken)
                { freshness = freshness, ownerObservation = ownerObservation, relationDisplayLimit = 2, omissionDisplayLimit = 2 };
                discoveryGroups.Add(group);
                if (group.Resolve())
                {
                    group.Expand();
                    if (request.Format == ExploreFormat.Agent)
                        group.PrepareAgentSummary();
                    group.PrepareUnits();
                }
            }
            foreach (var group in discoveryGroups)
                group.measureDiscovery = (text, returnedRanges) => WireSize(RenderDiscovery(group, text, returnedRanges));

            var baseline = WireSize(RenderDiscovery());
            var shareChars = Math.Max(0, budget.MaxChars - baseline.Chars) / discoveryGroups.Count;
            var shareBytes = Math.Max(0, budget.MaxBytes - baseline.Bytes) / discoveryGroups.Count;
            foreach (var group in discoveryGroups)
            {
                Check();
                var used = WireSize(RenderDiscovery());
                group.ReadRequested((Math.Min(budget.MaxChars, used.Chars + shareChars), Math.Min(budget.MaxBytes, used.Bytes + shareBytes)));
            }
            // Release unused reservations before admitting any related declaration.
            foreach (var group in discoveryGroups)
                group.ReadRequested();
            foreach (var group in discoveryGroups)
                group.ReadRelated();
            foreach (var group in discoveryGroups)
                foreach (var error in group.Errors)
                    Error(error.Code, error.Message, error.Path);
            if (request.Freshness == QueryFreshnessPolicy.RequireFresh && Errors.Count == 0)
                Verify();
            if (Errors.Count != 0)
                foreach (var group in discoveryGroups)
                    group.Units.Clear();
        }

        private string RenderDiscovery(Run? replacement = null, string? replacementText = null, int? returnedRanges = null)
        {
            var groups = discoveryGroups!;
            var units = groups.Where(group => group != replacement).Sum(group => group.Units.Count)
                + (replacement is null ? 0 : returnedRanges ?? replacement.Units.Count);
            var outcome = AggregateDiscoveryOutcome(Errors.Count != 0, units,
                groups.Select(group => group.Outcome(group.Units, group.pending)));
            var text = new StringBuilder($"SharpDeps discover generation={owner.saved.Graph.Generation} snapshot={Safe(owner.saved.Graph.SnapshotId, 160)} outcome={outcome} groups={groups.Count} omittedGroups={omittedGroups}\n");
            foreach (var error in Errors.Take(8))
                text.AppendLine($"Error {error.Code}: {Safe(error.Message, 240)}");
            if (omittedGroups > 0)
                text.AppendLine("Additional scopes omitted; narrow project/variant or name/path. Groups are independent, not one call chain.");
            foreach (var group in groups)
                text.Append(group == replacement ? replacementText
                    : group.Render(group.Units, group.pending, Math.Min(group.relationDisplayLimit, group.relations.Count), group.omissionDisplayLimit));
            return text.ToString();
        }

        private void ReconcileDiscoveryFreshness()
        {
            var current = Errors.Count != 0 && freshness.State == "verified-current"
                ? new QueryFreshness("unverified", null, [], ["discovery did not complete a current-source check"])
                : freshness;
            foreach (var group in discoveryGroups!)
                if (group.freshness.State != "dirty")
                    group.freshness = current;
        }

        private ExploreReply FinishDiscovery()
        {
            if (Errors.Count != 0)
                foreach (var group in discoveryGroups!)
                    group.Units.Clear();
            while (true)
            {
                try { Check(); }
                catch (OperationCanceledException) { return RequestFailure(DeadlineFailureCode()); }
                ReconcileDiscoveryFreshness();
                var text = RenderDiscovery();
                var wire = WireSize(text);
                if (wire.Chars <= request.Budget!.MaxChars && wire.Bytes <= request.Budget.MaxBytes)
                {
                    var groups = discoveryGroups!;
                    var returned = groups.SelectMany(group => group.Units).ToArray();
                    var omissions = reasons.Concat(groups.SelectMany(group => group.reasons))
                        .Concat(groups.Any(group => group.pending.Count > 0) ? ["OMITTED_UNITS"] : Array.Empty<string>())
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    return new(text, Errors.ToArray(), returned, omissions, wire.Chars, wire.Bytes)
                    {
                        Outcome = AggregateDiscoveryOutcome(Errors.Count != 0, returned.Length,
                        groups.Select(group => group.Outcome(group.Units, group.pending)))
                    };
                }
                reasons.Add("OUTPUT_LIMIT");
                var optional = discoveryGroups!.LastOrDefault(group => group.relationDisplayLimit > 0 || group.omissionDisplayLimit > 0);
                if (optional is not null)
                {
                    if (optional.relationDisplayLimit > 0)
                        optional.relationDisplayLimit--;
                    else
                        optional.omissionDisplayLimit--;
                    continue;
                }
                var populated = discoveryGroups!.LastOrDefault(group => group.Units.Count > 0);
                if (populated is not null)
                {
                    var removed = populated.Units[^1];
                    populated.Units.RemoveAt(populated.Units.Count - 1);
                    foreach (var candidate in populated.allCandidates.Where(item => item.Location.SourceId == removed.SourceId
                        && item.Location.RawSpan is { } span && span.Start >= removed.Span.Start && span.End <= removed.Span.End))
                        if (!populated.pending.Any(item => item.Selection.Identity == candidate.Selection.Identity && item.Location == candidate.Location))
                            populated.pending.Add(candidate with { Reason = "budget", RequiredChars = wire.Chars, RequiredBytes = wire.Bytes });
                    continue;
                }
                Error("OUTPUT_BUDGET_TOO_SMALL", "Narrow project/variant or increase maxChars/maxBytes.");
                const string minimal = "SharpDeps discover outcome=failed\nError OUTPUT_BUDGET_TOO_SMALL: narrow project/variant or increase maxChars/maxBytes. No source returned.\n";
                var size = WireSize(minimal);
                return new(minimal, Errors.ToArray(), [], reasons.Order(StringComparer.Ordinal).ToArray(), size.Chars, size.Bytes) { Outcome = "failed" };
            }
        }

        private static (int Chars, int Bytes) WireSize(string text)
        {
            var wire = JsonSerializer.Serialize(new { content = new[] { new { type = "text", text } }, isError = false });
            return (wire.Length, Encoding.UTF8.GetByteCount(wire));
        }
    }
}
