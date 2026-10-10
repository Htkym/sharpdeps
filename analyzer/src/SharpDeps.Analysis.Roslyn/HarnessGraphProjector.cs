namespace SharpDeps.Analysis.Roslyn;

using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Roslyn.Evidence;
using SharpDeps.Analysis.Roslyn.Symbols;

/// <summary>Persisted project scope comes from the trusted owner; Key is the loader's variant key.</summary>
public sealed record HarnessProjectVariant(string Key, string ProjectId, string Name,
    string TargetFramework, string Configuration, string? Platform = null,
    string? RuntimeIdentifier = null, string? AnalysisFingerprint = null);

/// <summary>Pure projection of the existing Roslyn collection; no file reads, UUID allocation or host activation.</summary>
public static class HarnessGraphProjector
{
    public static HarnessGraphEnvelope Project(Guid workspaceUuid, string snapshotId, long generation,
        HarnessCoverage coverage, IReadOnlyList<HarnessProjectVariant> projects,
        SymbolIndex index, IReadOnlyList<CollectedEvidence> evidence, OperationCollectionStats stats,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generation < 0 || string.IsNullOrWhiteSpace(snapshotId) || !Enum.IsDefined(coverage))
            throw new ArgumentException("A valid snapshot, generation and loader coverage are required.");
        if (new[] { stats.BodiesScanned, stats.UnresolvedOperations, stats.DynamicReferences, stats.CandidateOnlySymbols }.Any(n => n < 0))
            throw new ArgumentException("Collection counts cannot be negative.", nameof(stats));

        var workspaceId = HarnessIdentity.WorkspaceId(workspaceUuid);
        var nodes = new Dictionary<string, HarnessNode>(StringComparer.Ordinal)
        {
            [workspaceId] = new(workspaceId, HarnessNodeKind.Workspace, "workspace", null, null)
        };
        var occurrences = new Dictionary<string, HarnessSymbolOccurrence>(StringComparer.Ordinal);
        var variants = new Dictionary<string, HarnessVariant>(StringComparer.Ordinal);
        var projectByKey = projects.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var documents = index.Documents.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var typesById = index.Types.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var symbols = new Dictionary<string, HarnessSymbolOccurrence>(StringComparer.Ordinal);
        var legacy = new HashSet<HarnessLegacyReference>();
        var edges = new Dictionary<string, HarnessEdge>(StringComparer.Ordinal);
        var diagnostics = new Dictionary<string, int>(StringComparer.Ordinal);
        Count("roslyn.unresolvedReferences", stats.UnresolvedOperations);
        Count("roslyn.dynamicReferences", stats.DynamicReferences);
        Count("roslyn.candidateSymbols", stats.CandidateOnlySymbols);

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(project.Key) || string.IsNullOrWhiteSpace(project.Name))
                throw new ArgumentException("A loader key and project name are required.", nameof(projects));
            var id = HarnessIdentity.VariantId(workspaceUuid, project.ProjectId, project.TargetFramework,
                project.Configuration, project.Platform, project.RuntimeIdentifier);
            if (variants.Values.Any(v => v.Id == id))
                throw new ArgumentException("Different loader keys cannot represent the same variant.", nameof(projects));
            variants.Add(project.Key, new(id, project.ProjectId, project.TargetFramework, project.Configuration,
                project.Platform, project.RuntimeIdentifier, project.AnalysisFingerprint));
            var node = new HarnessNode(project.ProjectId, HarnessNodeKind.Project, project.Name, workspaceId, null);
            if (nodes.TryGetValue(node.Id, out var old) && old != node)
                throw new ArgumentException("Project metadata must agree across variants.", nameof(projects));
            nodes[node.Id] = node;
        }

        foreach (var ns in index.Namespaces)
        {
            var project = ProjectFor(ns.ProjectVariantId);
            var id = HarnessIdentity.LogicalSymbolId(workspaceUuid, project.ProjectId, "namespace", ns.Name);
            AddSymbol(ns.Id, id, HarnessNodeKind.Namespace, ns.Name, project.ProjectId, ns.Name,
                ns.ProjectVariantId, []);
        }
        foreach (var type in index.Types)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = ProjectFor(type.ProjectVariantId);
            var id = HarnessIdentity.LogicalSymbolId(workspaceUuid, project.ProjectId, "type", type.SymbolKey);
            AddSymbol(type.Id, id, HarnessNodeKind.Type, type.FullName, project.ProjectId, type.SymbolKey,
                type.ProjectVariantId, type.Declarations);
        }
        // Containing types/namespaces have all been registered, including nested types.
        foreach (var type in index.Types)
        {
            var occurrence = symbols[type.Id];
            var parentKey = type.ContainingTypeId ?? type.NamespaceId;
            if (parentKey is not null && symbols.TryGetValue(parentKey, out var parent))
                nodes[occurrence.LogicalSymbolId] = nodes[occurrence.LogicalSymbolId] with { ParentId = parent.LogicalSymbolId };
        }
        foreach (var group in index.Members.GroupBy(m => m.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var member = group.First();
            if (!symbols.TryGetValue(member.TypeId, out var owner))
                throw new ArgumentException("A member's declaring type is missing.", nameof(index));
            var type = typesById[member.TypeId];
            var signature = string.Join("|", type.SymbolKey, member.Kind,
                member.HarnessSignature ?? member.DocumentationId ?? member.Name,
                member.Arity.ToString(System.Globalization.CultureInfo.InvariantCulture), member.Signature);
            if (group.Any(m => m.ProjectVariantId != member.ProjectVariantId || m.TypeId != member.TypeId
                || m.DocumentationId != member.DocumentationId || m.Signature != member.Signature || m.Kind != member.Kind))
                throw new ArgumentException("A legacy member ID collides with a different declaration.", nameof(index));
            var id = HarnessIdentity.LogicalSymbolId(workspaceUuid, ProjectFor(member.ProjectVariantId).ProjectId,
                member.Kind, signature);
            AddSymbol(member.Id, id, HarnessNodeKind.Member, member.Name, owner.LogicalSymbolId, signature,
                member.ProjectVariantId, group.SelectMany(m => m.HarnessDeclarations ?? m.Declarations).Distinct().ToArray());
        }

        foreach (var entry in evidence.SelectMany(ExpandDeclarationOwners))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = entry.Evidence;
            var sourceKey = entry.CanonicalSourceMemberId ?? record.SourceMemberId ?? record.SourceEntityId;
            if (!symbols.TryGetValue(sourceKey, out var source))
            {
                Count("roslyn.missingOwner", 1);
                continue;
            }
            if (!variants.TryGetValue(entry.SourceVariantId, out var sourceVariant) || source.VariantId != sourceVariant.Id)
                throw new ArgumentException("Evidence source and owner belong to different variants.", nameof(evidence));
            var targetKey = entry.CanonicalTargetMemberId ?? record.TargetMemberId ?? record.TargetEntityId;
            var target = symbols.GetValueOrDefault(targetKey);
            if (target is null && entry.TargetIsExternal && entry.TargetSymbol is { } external)
                target = External(external, entry.SourceVariantId);
            if (target is null)
            {
                Count("roslyn.missingTarget", 1);
                continue;
            }
            var certainty = record.Confidence switch
            {
                "resolved" => HarnessCertainty.Resolved,
                "candidate" or "inferred" => HarnessCertainty.Candidate,
                _ => HarnessCertainty.Unresolved
            };
            if (certainty != HarnessCertainty.Resolved) Count("roslyn.nonResolvedEvidence", 1);
            if (string.IsNullOrEmpty(record.SourceContentHash) || record.PhysicalSpan is null)
                Count("roslyn.sourceEvidenceUnavailable", 1);
            var location = new HarnessLocation(record.DocumentId, null, record.SourceContentHash,
                record.PhysicalSpan is { } span ? new(span.Start, span.Length) : null);
            var kinds = entry.Access switch
            {
                "read" => new[] { "reads" }, "write" => new[] { "writes" },
                "readWrite" => new[] { "reads", "writes" },
                null => new[] { record.Kind == "memberAccess" ? "references_member" : record.Kind },
                _ => throw new ArgumentException("Unsupported access provenance.", nameof(evidence))
            };
            foreach (var kind in kinds)
            {
                var id = HarnessIdentity.EdgeId(workspaceUuid, source.Id, target.Id, kind,
                    record.DocumentId, record.SourceContentHash, location.RawSpan, entry.Producer ?? "roslyn");
                var edge = new HarnessEdge(id, source.LogicalSymbolId, target.LogicalSymbolId,
                    source.Id, target.Id, source.VariantId, kind, certainty,
                    entry.Producer ?? "roslyn", location, record.Origin);
                // Duplicate evidence keeps the least certain classification.
                if (edges.TryGetValue(id, out var previous) && (int)previous.Certainty > (int)edge.Certainty) edge = previous;
                edges[id] = edge;
            }
        }
        if (coverage == HarnessCoverage.CompleteWithinScope && diagnostics.Count > 0) coverage = HarnessCoverage.Partial;
        var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
            HarnessGraphContract.IdentityVersion, workspaceId, snapshotId, generation, coverage,
            Freeze(variants.Values.OrderBy(v => v.Id, StringComparer.Ordinal)),
            Freeze(nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal)),
            Freeze(occurrences.Values.OrderBy(o => o.Id, StringComparer.Ordinal)),
            Freeze(edges.Values.OrderBy(e => e.Id, StringComparer.Ordinal)),
            Freeze(legacy.OrderBy(l => l.LegacyId, StringComparer.Ordinal).ThenBy(l => l.OccurrenceId, StringComparer.Ordinal)), null,
            Freeze(diagnostics.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => new HarnessGraphDiagnostic(d.Key, d.Value))));
        HarnessGraphContract.ValidateHeader(graph);
        cancellationToken.ThrowIfCancellationRequested();
        return graph;

        HarnessProjectVariant ProjectFor(string key) => projectByKey.TryGetValue(key, out var p) ? p
            : throw new ArgumentException("Every indexed declaration needs an explicit project variant.", nameof(projects));
        void Count(string code, int count)
        {
            if (count > 0) diagnostics[code] = checked(diagnostics.GetValueOrDefault(code) + count);
        }
        void AddSymbol(string legacyId, string logicalId, HarnessNodeKind kind, string name, string parentId,
            string signature, string key, IReadOnlyList<SymbolDeclarationLocation> declarations)
        {
            var locations = Freeze(declarations.Select(Location).Distinct().OrderBy(l => l.SourceId, StringComparer.Ordinal)
                .ThenBy(l => l.RawSpan?.Start));
            var location = locations.FirstOrDefault();
            // Logical identity has no single TFM-specific location; those live on occurrences.
            var node = new HarnessNode(logicalId, kind, name, parentId, null, signature);
            if (nodes.TryGetValue(logicalId, out var prior) && prior != node)
                throw new ArgumentException("Logical declaration metadata differs across variants.", nameof(index));
            nodes[logicalId] = node;
            var variant = variants[key].Id;
            var id = HarnessIdentity.SymbolOccurrenceId(workspaceUuid, logicalId, variant);
            var occurrence = new HarnessSymbolOccurrence(id, logicalId, variant, location, locations);
            occurrences.Add(id, occurrence);
            symbols.Add(legacyId, occurrence);
            legacy.Add(new(2, legacyId, logicalId, id));
        }
        HarnessLocation Location(SymbolDeclarationLocation declaration)
        {
            if (!documents.TryGetValue(declaration.DocumentId, out var document))
                throw new ArgumentException("A declaration's source document is missing.", nameof(index));
            if (string.IsNullOrEmpty(document.ContentHash)) Count("roslyn.sourceEvidenceUnavailable", 1);
            return new(document.Id, null, document.ContentHash, new(declaration.Start, declaration.Length));
        }
        HarnessSymbolOccurrence External(HarnessTargetSymbol symbol, string key)
        {
            var typeId = HarnessIdentity.ExternalSymbolId(workspaceUuid, symbol.AssemblyIdentity, "type", symbol.TypeCanonicalSignature);
            nodes.TryAdd(typeId, new(typeId, HarnessNodeKind.ExternalSymbol, symbol.TypeName, workspaceId, null, symbol.TypeCanonicalSignature));
            var id = symbol.LegacyMemberId is null ? typeId
                : HarnessIdentity.ExternalSymbolId(workspaceUuid, symbol.AssemblyIdentity, symbol.Kind, symbol.CanonicalSignature);
            nodes.TryAdd(id, new(id, HarnessNodeKind.ExternalSymbol, symbol.Name, typeId, null, symbol.CanonicalSignature));
            var variant = variants[key].Id;
            var occurrenceId = HarnessIdentity.SymbolOccurrenceId(workspaceUuid, id, variant);
            if (!occurrences.TryGetValue(occurrenceId, out var occurrence))
            {
                occurrence = new(occurrenceId, id, variant, null);
                occurrences.Add(occurrenceId, occurrence);
            }
            legacy.Add(new(2, symbol.LegacyMemberId ?? symbol.LegacyTypeId, id, occurrenceId));
            return occurrence;
        }
    }

    private static IEnumerable<CollectedEvidence> ExpandDeclarationOwners(CollectedEvidence entry)
    {
        if (entry.HarnessDeclarationOwners is not { Count: > 0 } owners)
        {
            yield return entry;
            yield break;
        }
        foreach (var owner in owners)
            yield return entry with
            {
                CanonicalSourceMemberId = owner.SourceMemberId,
                Evidence = entry.Evidence with { SourceMemberId = owner.SourceMemberId, PublicSurface = owner.PublicSurface }
            };
    }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
}
