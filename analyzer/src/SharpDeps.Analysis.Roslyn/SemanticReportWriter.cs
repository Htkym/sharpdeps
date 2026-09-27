// Semantic report writer (SD-011 / SD-013): builds the v2 snapshot from a semantic
// load result.
//
// Symbol relations are emitted at type granularity. Coarser views (namespace, project)
// are derived host-side from the same relations using each type's namespace and
// project variant, so an edge always has exactly one set of evidence records and no
// evidence file duplicates them per granularity.
//
// Cycle groups carry the witnesses computed over the resolved relations; they are
// real directed cycles built from edges that exist in the graph.

namespace SharpDeps.Analysis.Roslyn;

using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Graph;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Roslyn.Evidence;
using SharpDeps.Analysis.Roslyn.Symbols;

public static class SemanticReportWriter
{
    public const string AnalyzerVersion = "sharpdeps-semantic-0.1.0";
    private const string Basis = "symbolResolved";
    private const string Mode = "semantic";

    public sealed record Result(AnalysisSnapshot Snapshot, string EvidenceNdjson, string DeclarationsNdjson);

    public static Result Write(
        SemanticLoadResult load,
        SymbolIndex index,
        IReadOnlyList<CollectedEvidence> evidence,
        OperationCollectionStats operationStats,
        DateTimeOffset createdAt,
        string targetPath,
        string? analysisId = null,
        IReadOnlyDictionary<string, string>? externalTypes = null)
    {
        var report = load.Report;
        var configuration = report.Configuration;
        var platform = report.Profile.Platform;
        var rootDirectory = Path.GetDirectoryName(Path.GetFullPath(targetPath)) ?? ".";
        var rootId = Identity.WorkspaceRootId(rootDirectory);

        // Variant key (loader) → v2 project/variant ids.
        var logicalIdByVariantKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var variantIdByVariantKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var projectNameByVariantKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var variants = new List<ProjectVariant>();
        var projects = new List<AnalysisProject>();

        var loadedVariants = report.Variants.ToArray();
        foreach (var variant in loadedVariants)
        {
            var relativePath = Identity.NormalizeRelativePath(
                Path.GetRelativePath(rootDirectory, variant.ProjectPath));
            var logicalId = Identity.ProjectLogicalId(
                rootId,
                relativePath);
            var variantId = Identity.ProjectVariantId(
                logicalId,
                variant.TargetFramework ?? "(not specified)",
                configuration,
                platform);

            logicalIdByVariantKey[variant.VariantKey] = logicalId;
            variantIdByVariantKey[variant.VariantKey] = variantId;
            projectNameByVariantKey[variant.VariantKey] = variant.ProjectName;

            variants.Add(new ProjectVariant(
                variantId,
                logicalId,
                variant.TargetFramework ?? "(not specified)",
                configuration,
                platform,
                variant.TargetFramework is null ? "notSpecified" : "targetFramework"));
        }

        // One project entry per loaded variant, each with its own logical id (SD-025).
        foreach (var variant in loadedVariants.OrderBy(
                     entry => logicalIdByVariantKey[entry.VariantKey],
                     StringComparer.Ordinal))
        {
            var relativePath = Identity.NormalizeRelativePath(
                Path.GetRelativePath(rootDirectory, variant.ProjectPath));
            projects.Add(new AnalysisProject(
                Identity.ProjectLogicalId(rootId, relativePath + "#" + (variant.TargetFramework ?? "(not specified)")),
                variantIdByVariantKey[variant.VariantKey],
                variant.ProjectName,
                relativePath,
                relativePath.Contains('/') ? relativePath[..relativePath.LastIndexOf('/')] : string.Empty,
                variant.Kind,
                variant.TargetFramework ?? "(not specified)",
                configuration,
                platform,
                [],
                variant.LoadState,
                ProjectLimitations(variant)));
        }

        var profileHash = Identity.ProfileHash(
            configuration,
            platform,
            variants.Select(variant => (variant.ProjectLogicalId, variant.TargetFramework)));

        // Namespaces and types, translated to v2 ids.
        var namespaceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var namespaces = new List<AnalysisNamespace>();
        foreach (var node in index.Namespaces)
        {
            if (!variantIdByVariantKey.TryGetValue(node.ProjectVariantId, out var variantId))
            {
                continue;
            }

            var id = Identity.NamespaceId(variantId, node.Name);
            namespaceIds[node.Id] = id;
            namespaces.Add(new AnalysisNamespace(id, variantId, node.Name, node.TypeCount, null));
        }

        // The collectors key ids by the loader's variant key; the snapshot keys them by
        // the v2 variant id, so every id is translated through this map.
        var entityIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var types = new List<AnalysisType>();
        foreach (var type in index.Types)
        {
            if (!variantIdByVariantKey.TryGetValue(type.ProjectVariantId, out var variantId))
            {
                continue;
            }

            // Deterministic declaration key: the documentation id when available,
            // otherwise the full name with its arity.
            var key = type.SymbolKey;
            var id = Identity.TypeId(variantId, key);
            entityIds[type.Id] = id;
            types.Add(new AnalysisType(
                id,
                variantId,
                type.NamespaceId is not null && namespaceIds.TryGetValue(type.NamespaceId, out var namespaceId)
                    ? namespaceId
                    : null,
                type.Name,
                type.FullName,
                type.DocumentationId,
                type.Kind,
                type.Accessibility,
                type.IsPartial,
                Math.Max(1, type.Declarations.Count),
                index.Members.Count(member => member.TypeId == type.Id),
                type.IsExternal,
                type.Declarations.Count > 0 && type.Declarations.All(declaration =>
                    index.Documents.Any(document => document.Id == declaration.DocumentId && document.Origin == "generatedSource"))));
        }

        // External types become nodes too, grouped under one synthetic external project
        // so an edge never points at an entity that is missing from the model.
        if (externalTypes is { Count: > 0 })
        {
            var externalLogicalId = Identity.ProjectLogicalId(rootId, "(external)");
            var externalVariantId = Identity.ProjectVariantId(
                externalLogicalId,
                "external",
                configuration,
                platform);
            variants.Add(new ProjectVariant(
                externalVariantId,
                externalLogicalId,
                "external",
                configuration,
                platform,
                "inferred"));
            projects.Add(new AnalysisProject(
                externalLogicalId,
                externalVariantId,
                "(external)",
                "(external)",
                string.Empty,
                "unknown",
                "external",
                configuration,
                platform,
                [],
                "loaded",
                []));

            foreach (var (id, displayName) in externalTypes.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                types.Add(new AnalysisType(
                    id,
                    externalVariantId,
                    null,
                    displayName,
                    displayName,
                    null,
                    "unknown",
                    "public",
                    false,
                    1,
                    0,
                    true));
            }
        }

        // Relations: one per relation id the collectors already computed, so counts and
        // public-surface statistics keep their meaning.
        var relations = new List<AnalysisRelation>();
        var evidenceLines = new List<EvidenceRecord>();
        foreach (var group in evidence
                     .GroupBy(entry => entry.Evidence.RelationId, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var first = group.First();
            var sourceId = Translate(first.Evidence.SourceEntityId, entityIds);
            var targetId = Translate(first.Evidence.TargetEntityId, entityIds);
            if (sourceId is null || targetId is null)
            {
                continue;
            }

            var kinds = group
                .Select(entry => entry.Evidence.Kind)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(kind => kind, StringComparer.Ordinal)
                .ToArray();

            relations.Add(new AnalysisRelation(
                group.Key,
                sourceId,
                targetId,
                Basis,
                kinds,
                group.Count(),
                group.Select(entry => entry.Evidence.SourceMemberId).Where(id => id is not null).Distinct(StringComparer.Ordinal).Count(),
                Math.Max(1, group.Select(entry => entry.Evidence.DocumentId).Distinct(StringComparer.Ordinal).Count()),
                group.Count(entry => string.Equals(entry.Evidence.Origin, "generatedSource", StringComparison.Ordinal)),
                group.Count(entry => entry.Evidence.PublicSurface),
                "resolved",
                null));

            foreach (var entry in group.OrderBy(entry => entry.Evidence.Id, StringComparer.Ordinal))
            {
                evidenceLines.Add(entry.Evidence with
                {
                    SourceEntityId = sourceId,
                    TargetEntityId = targetId,
                    SourceTypeId = Translate(entry.Evidence.SourceTypeId, entityIds),
                    TargetTypeId = Translate(entry.Evidence.TargetTypeId, entityIds)
                });
            }
        }

        var sourceDocuments = index.Documents.ToDictionary(document => document.Id);
        var projectsByVariant = projects.ToDictionary(project => project.VariantId);
        foreach (var reference in report.References)
        {
            if (reference.TargetVariantKey is null
                || !variantIdByVariantKey.TryGetValue(reference.SourceVariantKey, out var sourceVariant)
                || !variantIdByVariantKey.TryGetValue(reference.TargetVariantKey, out var targetVariant)) continue;
            var source = projectsByVariant[sourceVariant];
            var target = projectsByVariant[targetVariant];
            var relationId = Identity.RelationId("projectEvaluated", source.Id, target.Id, profileHash);
            if (relations.Any(relation => relation.Id == relationId)) continue;
            var bytes = File.ReadAllBytes(Path.GetFullPath(Path.Combine(rootDirectory, source.RelativePath)));
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var documentId = Identity.DocumentId(rootId, source.RelativePath, "userSource");
            sourceDocuments[documentId] = new SourceDocument(documentId, source.RelativePath, "userSource", hash, bytes.LongLength, null);
            // An evaluated reference can originate in an imported target or expression.
            // Keep its evaluated input and profile without inventing a declaration span.
            relations.Add(new AnalysisRelation(relationId, source.Id, target.Id, "projectEvaluated",
                ["projectEvaluated"], 1, 0, 1, 0, 0, "resolved", null));
            evidenceLines.Add(new EvidenceRecord(Identity.EvidenceId(relationId, "projectEvaluated", documentId, "evaluated"), relationId,
                source.Id, target.Id, null, null, null, null, "projectEvaluated", "userSource", documentId,
                null, null, hash, "resolved", false, null));
        }

        var typeById = types.ToDictionary(type => type.Id, StringComparer.Ordinal);
        var projectByVariant = projects.ToDictionary(project => project.VariantId, project => project.Id);
        var graphEvidence = evidence.Select(entry => entry.ToGraphEvidence() with
        {
            SourceEntityId = Translate(entry.Evidence.SourceEntityId, entityIds)!,
            TargetEntityId = Translate(entry.Evidence.TargetEntityId, entityIds)!
        }).Concat(evidenceLines.Where(entry => entry.Kind == "projectEvaluated").Select(entry => new GraphEvidence(
            "projectEvaluated", entry.SourceEntityId, entry.TargetEntityId, entry.Kind, null, entry.DocumentId,
            false, false, "resolved", null, null, entry.SourceEntityId, entry.TargetEntityId, false))).ToArray();
        var cycles = new List<CycleGroup>();
        foreach (var granularity in Enum.GetValues<GraphGranularity>())
        {
            var scope = granularity.ToString().ToLowerInvariant();
            string? ParentOf(string id) => granularity switch
            {
                GraphGranularity.Type => id,
                GraphGranularity.Namespace => typeById.GetValueOrDefault(id)?.NamespaceId,
                _ => typeById.TryGetValue(id, out var type)
                    ? projectByVariant.GetValueOrDefault(type.ProjectVariantId)
                    : projects.Any(project => project.Id == id) ? id : null
            };
            var graph = AnalysisGraphBuilder.Build(graphEvidence.Where(entry =>
                granularity == GraphGranularity.Project || entry.Basis != "projectEvaluated").ToArray(), granularity, ParentOf);
            foreach (var cycle in GraphCycles.Find(graph))
            {
                var members = cycle.MemberIds.ToHashSet(StringComparer.Ordinal);
                var internalRelations = relations.Where(relation =>
                    relation.Basis == cycle.Basis && members.Contains(ParentOf(relation.SourceEntityId) ?? string.Empty)
                    && members.Contains(ParentOf(relation.TargetEntityId) ?? string.Empty)
                    && ParentOf(relation.SourceEntityId) != ParentOf(relation.TargetEntityId)).ToArray();
                var witnessIds = cycle.WitnessEdges.Select(edge => internalRelations
                    .Where(relation => ParentOf(relation.SourceEntityId) == edge.SourceEntityId
                        && ParentOf(relation.TargetEntityId) == edge.TargetEntityId)
                    .OrderByDescending(relation => relation.EvidenceCount)
                    .ThenBy(relation => relation.Id, StringComparer.Ordinal).First().Id).ToArray();
                cycles.Add(new CycleGroup(
                    Identity.CycleGroupId(scope, cycle.Basis, cycle.MemberIds), scope, cycle.Basis,
                    cycle.MemberIds, internalRelations.Select(relation => relation.Id).ToArray(),
                    new CycleWitness(cycle.WitnessEdges.Select(edge => edge.SourceEntityId).ToArray(), witnessIds),
                    false));
            }
        }

        var (evidenceNdjson, evidenceIndex) = WriteEvidence(evidenceLines);
        var (declarationsNdjson, declarationIndex) = WriteDeclarations(
            index.Types,
            entityIds,
            variantIdByVariantKey);
        var limitations = BuildLimitations(report, operationStats);

        var loaded = report.Variants.Count(variant => variant.LoadState == "loaded");
        var failed = report.Variants.Count(variant => variant.LoadState == "failed");
        var complete = failed == 0
            && operationStats.UnresolvedOperations == 0
            && !limitations.Any(limitation => limitation.Code
                is "semantic.loadFailed"
                or "semantic.workspaceDiagnostics"
                or "semantic.nonCSharpProjects"
                or "semantic.compilationErrors"
                or "semantic.referencesUnresolved"
                or "semantic.generatedDocumentsUnavailable"
                or "semantic.generatedDocumentContentUnavailable");

        var createdAtText = createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var resolvedAnalysisId = analysisId ?? Identity.AnalysisId(rootId, Mode, profileHash, createdAtText);

        var snapshot = new AnalysisSnapshot(
            SchemaVersion: 2,
            AnalyzerVersion: AnalyzerVersion,
            AnalysisId: resolvedAnalysisId,
            CreatedAt: createdAtText,
            Target: new TargetDescriptor(
                TargetKind(targetPath),
                rootId,
                Identity.NormalizeRelativePath(Path.GetFileName(targetPath))),
            Mode: Mode,
            Profile: new AnalysisProfile(configuration, platform, variants, profileHash),
            Capabilities: new AnalysisCapabilities(
                TypeGraph: true,
                Evidence: true,
                GeneratedDocuments: load.GeneratedDocuments.Count > 0,
                CycleWitness: true,
                Search: true),
            Completeness: limitations.Any(l => l.Code == "semantic.loadFailed") ? "failed"
                : complete ? "completeWithinScope" : "partial",
            Coverage: new AnalysisCoverage(
                Discovered: Math.Max(report.Coverage.Discovered, loaded + failed),
                Loaded: loaded,
                Analyzed: loaded,
                Failed: failed,
                Skipped: report.Coverage.Skipped,
                Unresolved: operationStats.UnresolvedOperations + report.Coverage.Unresolved),
            Projects: projects,
            Namespaces: namespaces,
            Types: types,
            Relations: relations,
            CycleGroups: cycles,
            Diagnostics: report.Diagnostics
                .Select((diagnostic, position) => new AnalysisDiagnostic(
                    Identity.DiagnosticId("semantic.workspaceWarning", null, $"{position}:{diagnostic.Message}"),
                    "warning",
                    "semantic.workspaceWarning",
                    diagnostic.Message,
                    null,
                    null,
                    resolvedAnalysisId))
                .ToArray(),
            EvidenceIndex: evidenceIndex,
            DeclarationIndex: declarationIndex,
            SourceManifest: sourceDocuments.Values.OrderBy(document => document.RelativePath, StringComparer.Ordinal).ToArray(),
            Limitations: limitations);

        return new Result(snapshot, evidenceNdjson, declarationsNdjson);
    }

    private static IReadOnlyList<Limitation> ProjectLimitations(ProjectVariantInfo variant)
    {
        var result = new List<Limitation>();
        if (variant.FailureReason is not null)
            result.Add(new Limitation("semantic.projectLoadFailed", variant.FailureReason));
        if (variant.ErrorDiagnosticCount > 0)
            result.Add(new Limitation("semantic.compilationErrors", $"{variant.ErrorDiagnosticCount} compilation error(s). References may be incomplete.", Count: variant.ErrorDiagnosticCount));
        if (variant.GeneratedDocumentError is not null)
            result.Add(new Limitation("semantic.generatedDocumentsUnavailable", variant.GeneratedDocumentError));
        return result;
    }

    /// <summary>
    /// Declarations grouped by type id, in declaration order, so the editor can resolve a
    /// cursor to a type and a type back to its declaration without the compilation.
    /// </summary>
    private static (string Ndjson, DeclarationIndex? Index) WriteDeclarations(
        IReadOnlyList<Symbols.IndexedType> types,
        IReadOnlyDictionary<string, string> entityIds,
        IReadOnlyDictionary<string, string> variantIds)
    {
        var builder = new System.Text.StringBuilder();
        var entries = new List<DeclarationIndexEntry>();
        long offset = 0;

        foreach (var type in types.OrderBy(entry => entry.Id, StringComparer.Ordinal))
        {
            // Types that are not part of the v2 model (external or compiler-generated)
            // have no snapshot id, so their declarations cannot be addressed.
            if (type.Declarations.Count == 0 || !entityIds.TryGetValue(type.Id, out var typeId))
            {
                continue;
            }

            var variantId = variantIds.TryGetValue(type.ProjectVariantId, out var mapped)
                ? mapped
                : type.ProjectVariantId;
            var start = offset;
            var index = 0;
            foreach (var declaration in type.Declarations)
            {
                var record = new DeclarationRecord(
                    typeId,
                    variantId,
                    declaration.DocumentId,
                    declaration.RelativePath,
                    declaration.ToPhysicalSpan(),
                    index,
                    type.IsPartial);
                var line = System.Text.Json.JsonSerializer.Serialize(
                    record,
                    EvidenceJsonContext.Default.DeclarationRecord) + "\n";
                builder.Append(line);
                offset += System.Text.Encoding.UTF8.GetByteCount(line);
                index++;
            }

            entries.Add(new DeclarationIndexEntry(typeId, start, index));
        }

        if (entries.Count == 0)
        {
            return (string.Empty, null);
        }

        return (builder.ToString(), new DeclarationIndex("ndjson", "declarations.ndjson", offset, entries));
    }

    private static string? Translate(
        string? entityId,
        IReadOnlyDictionary<string, string> entityIds)
        => entityId is not null && entityIds.TryGetValue(entityId, out var mapped) ? mapped : entityId;

    private static (string Ndjson, EvidenceIndex Index) WriteEvidence(IReadOnlyList<EvidenceRecord> records)
    {
        var builder = new System.Text.StringBuilder();
        var entries = new List<EvidenceIndexEntry>();
        long offset = 0;

        foreach (var group in records
                     .OrderBy(record => record.RelationId, StringComparer.Ordinal)
                     .ThenBy(record => record.Id, StringComparer.Ordinal)
                     .GroupBy(record => record.RelationId, StringComparer.Ordinal))
        {
            var start = offset;
            var count = 0;
            foreach (var record in group)
            {
                var line = System.Text.Json.JsonSerializer.Serialize(
                    record,
                    EvidenceJsonContext.Default.EvidenceRecord) + "\n";
                builder.Append(line);
                offset += System.Text.Encoding.UTF8.GetByteCount(line);
                count++;
            }

            entries.Add(new EvidenceIndexEntry(group.Key, start, count));
        }

        return (builder.ToString(), new EvidenceIndex("ndjson", "evidence.ndjson", offset, entries));
    }

    private static IReadOnlyList<Limitation> BuildLimitations(
        SemanticProbeReport report,
        OperationCollectionStats operationStats)
    {
        var limitations = new List<Limitation>(report.Limitations.Select(limitation => new Limitation(
            limitation.Code,
            limitation.Message,
            null,
            limitation.Count)));

        if (operationStats.UnresolvedOperations > 0)
        {
            limitations.Add(new Limitation(
                "semantic.unresolvedReferences",
                "Some references could not be resolved to a symbol; they are not shown as dependencies.",
                null,
                operationStats.UnresolvedOperations));
        }

        if (operationStats.DynamicReferences > 0)
        {
            limitations.Add(new Limitation(
                "semantic.dynamicReferences",
                "Dynamic references cannot be resolved statically and are excluded from the graph.",
                null,
                operationStats.DynamicReferences));
        }

        if (operationStats.CandidateOnlySymbols > 0)
        {
            limitations.Add(new Limitation(
                "semantic.candidateSymbols",
                "Some references bound to candidate symbols only; they were not promoted to confirmed references.",
                null,
                operationStats.CandidateOnlySymbols));
        }

        return limitations;
    }

    private static string TargetKind(string targetPath) => Path.GetExtension(targetPath).ToLowerInvariant() switch
    {
        ".sln" => "solution",
        ".slnx" => "slnx",
        _ => "project"
    };

}
