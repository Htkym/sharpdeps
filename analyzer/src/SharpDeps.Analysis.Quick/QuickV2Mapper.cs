// Maps the Quick analysis (v1 report + source index) to the v2 model (SD-006).
//
// Quick knows declaration sites only, so every relation is `projectDeclared` or
// `usingInferred` and every evidence record is `inferred`. Namespaces are keyed by
// project variant; when a using matches the same namespace name in several
// projects the mapper keeps every candidate and marks the relation as ambiguous
// instead of picking one. Cycle groups are computed over the full analyzed graph
// (never over the display projection) and carry no witness.

namespace SharpDeps.Analysis.Quick;

using System.Globalization;
using System.Text;
using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Graph;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Core.Paths;

public static class QuickV2Mapper
{
    public const string AnalyzerVersion = "sharpdeps-quick-0.1.0";
    private const string Configuration = "Debug";
    private const string Mode = "quick";

    public sealed record Result(AnalysisSnapshot Snapshot, string EvidenceNdjson);

    public static Result Map(
        CodeMapReport report,
        QuickSourceIndex index,
        DateTimeOffset createdAt,
        string analyzerVersion = AnalyzerVersion,
        string? analysisId = null)
    {
        var solutionDirectory = Path.GetDirectoryName(report.SolutionPath) ?? report.SolutionPath;
        var rootId = Identity.WorkspaceRootId(solutionDirectory);
        var createdAtText = createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        var projectIdByName = new Dictionary<string, string>(StringComparer.Ordinal);
        var projectIdByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var variantIdByProject = new Dictionary<string, string>(StringComparer.Ordinal);
        var projectNameById = new Dictionary<string, string>(StringComparer.Ordinal);
        var variants = new List<ProjectVariant>();
        var projects = new List<AnalysisProject>();

        foreach (var project in report.Projects)
        {
            var relativePath = Identity.NormalizeRelativePath(project.RelativePath);
            var fullPath = Path.GetFullPath(Path.Combine(solutionDirectory, project.RelativePath));
            var logicalId = Identity.ProjectLogicalId(rootId, relativePath);
            var variantId = Identity.ProjectVariantId(logicalId, project.TargetFramework, Configuration, null);

            projectIdByName[project.Name] = logicalId;
            projectIdByPath[ProjectPaths.NormalizePathKey(fullPath)] = logicalId;
            variantIdByProject[project.Name] = variantId;
            projectNameById[logicalId] = project.Name;

            variants.Add(new ProjectVariant(
                variantId,
                logicalId,
                project.TargetFramework,
                Configuration,
                null,
                string.IsNullOrWhiteSpace(project.TargetFramework) || project.TargetFramework == "(not specified)"
                    ? "notSpecified"
                    : "targetFramework"));

            projects.Add(new AnalysisProject(
                logicalId,
                variantId,
                project.Name,
                relativePath,
                project.GroupPath,
                ToProjectKind(project.Kind),
                project.TargetFramework,
                Configuration,
                null,
                [],
                "loaded",
                []));
        }

        var profileHash = Identity.ProfileHash(
            Configuration,
            null,
            variants.Select(variant => (variant.ProjectLogicalId, variant.TargetFramework)));

        var namespaces = new List<AnalysisNamespace>();
        var namespaceIdByProjectAndName = new Dictionary<(string Project, string Name), string>();
        var namespaceNameById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in index.NamespaceDeclarations
                     .GroupBy(declaration => (declaration.ProjectName, declaration.NamespaceName))
                     .OrderBy(group => group.Key.ProjectName, StringComparer.Ordinal)
                     .ThenBy(group => group.Key.NamespaceName, StringComparer.Ordinal))
        {
            if (!variantIdByProject.TryGetValue(group.Key.ProjectName, out var variantId))
            {
                continue;
            }

            var id = Identity.NamespaceId(variantId, group.Key.NamespaceName);
            namespaceIdByProjectAndName[(group.Key.ProjectName, group.Key.NamespaceName)] = id;
            namespaceNameById[id] = group.Key.NamespaceName;
            namespaces.Add(new AnalysisNamespace(
                id,
                variantId,
                group.Key.NamespaceName,
                0,
                group.OrderBy(declaration => declaration.Span.Start).First().DocumentId));
        }

        var namespaceCandidatesByName = namespaces
            .GroupBy(node => node.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var relations = new Dictionary<string, PendingRelation>(StringComparer.Ordinal);
        var evidenceByRelation = new Dictionary<string, List<EvidenceRecord>>(StringComparer.Ordinal);
        var documentById = index.Documents.ToDictionary(document => document.Id, StringComparer.Ordinal);
        var externalReferences = 0;
        var conditionalReferences = 0;
        var ambiguousRelations = 0;

        void AddEvidence(string relationId, string kind, QuickDocument? document, QuickSpan? span)
        {
            if (document is null)
            {
                return;
            }

            var record = new EvidenceRecord(
                Identity.EvidenceId(relationId, kind, document.Id, span?.SpanKey ?? "document"),
                relationId,
                relations[relationId].SourceEntityId,
                relations[relationId].TargetEntityId,
                null,
                null,
                null,
                null,
                kind,
                document.Origin,
                document.Id,
                span?.ToPhysicalSpan(),
                null,
                document.ContentHash,
                "inferred",
                false,
                null);

            if (!evidenceByRelation.TryGetValue(relationId, out var list))
            {
                list = [];
                evidenceByRelation[relationId] = list;
            }

            if (list.All(existing => existing.Id != record.Id))
            {
                list.Add(record);
            }
        }

        string AddRelation(string sourceId, string targetId, string basis, string kind, bool ambiguous)
        {
            var relationId = Identity.RelationId(basis, sourceId, targetId, profileHash);
            if (relations.TryGetValue(relationId, out var existing))
            {
                if (ambiguous && existing.AmbiguousCandidates is null)
                {
                    relations[relationId] = existing with { AmbiguousCandidates = 2 };
                }

                return relationId;
            }

            relations[relationId] = new PendingRelation(
                relationId,
                sourceId,
                targetId,
                basis,
                kind,
                ambiguous ? 2 : null);
            if (ambiguous)
            {
                ambiguousRelations++;
            }

            return relationId;
        }

        foreach (var reference in index.ProjectReferences)
        {
            if (reference.IsConditional)
            {
                conditionalReferences++;
            }

            if (!projectIdByName.TryGetValue(reference.SourceProjectName, out var sourceId))
            {
                continue;
            }

            if (!projectIdByPath.TryGetValue(ProjectPaths.NormalizePathKey(reference.TargetFullPath), out var targetId))
            {
                externalReferences++;
                continue;
            }

            var relationId = AddRelation(sourceId, targetId, "projectDeclared", "projectDeclared", ambiguous: false);
            documentById.TryGetValue(reference.DocumentId, out var document);
            AddEvidence(relationId, "projectDeclared", document, reference.Span);
        }

        var globalUsingsByProject = index.GlobalUsings
            .GroupBy(global => global.ProjectName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        foreach (var usage in index.FileUsages)
        {
            // A project-level `global using` applies to every file of the project, but
            // its evidence stays at the declaration site.
            var effectiveUsings = new List<(QuickUsing Using, QuickDocument? Document)>();
            documentById.TryGetValue(usage.DocumentId, out var usageDocument);
            foreach (var directive in usage.Usings)
            {
                effectiveUsings.Add((directive, usageDocument));
            }

            if (globalUsingsByProject.TryGetValue(usage.ProjectName, out var globals))
            {
                foreach (var global in globals)
                {
                    documentById.TryGetValue(global.DocumentId, out var globalDocument);
                    effectiveUsings.Add((new QuickUsing(global.NamespaceName, global.Span), globalDocument));
                }
            }

            foreach (var (directive, document) in effectiveUsings)
            {
                if (!namespaceCandidatesByName.TryGetValue(directive.NamespaceName, out var candidates))
                {
                    continue;
                }

                foreach (var declared in usage.DeclaredNamespaces)
                {
                    if (!namespaceIdByProjectAndName.TryGetValue((usage.ProjectName, declared), out var sourceId))
                    {
                        continue;
                    }

                    var targets = candidates
                        .Where(candidate => !string.Equals(candidate.Name, declared, StringComparison.Ordinal))
                        .ToArray();
                    if (targets.Length == 0)
                    {
                        continue;
                    }

                    foreach (var target in targets)
                    {
                        var relationId = AddRelation(
                            sourceId,
                            target.Id,
                            "usingInferred",
                            "usingInferred",
                            ambiguous: targets.Length > 1);
                        AddEvidence(relationId, "usingInferred", document, directive.Span);
                    }
                }
            }
        }

        var projectRelations = relations.Values.Where(relation => relation.Basis == "projectDeclared").ToArray();
        var namespaceRelations = relations.Values.Where(relation => relation.Basis == "usingInferred").ToArray();

        var cycleGroups = new List<CycleGroup>();
        cycleGroups.AddRange(BuildCycleGroups("project", projectRelations));
        cycleGroups.AddRange(BuildCycleGroups("namespace", namespaceRelations));

        var evidenceOrdered = evidenceByRelation
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .SelectMany(entry => entry.Value.OrderBy(record => record.Id, StringComparer.Ordinal))
            .ToArray();

        var (evidenceNdjson, evidenceIndex) = WriteEvidence(evidenceOrdered);

        var mappedRelations = relations.Values
            .OrderBy(relation => relation.Id, StringComparer.Ordinal)
            .Select(relation =>
            {
                var evidence = evidenceByRelation.GetValueOrDefault(relation.Id) ?? [];
                return new AnalysisRelation(
                    relation.Id,
                    relation.SourceEntityId,
                    relation.TargetEntityId,
                    relation.Basis,
                    [relation.Kind],
                    Math.Max(1, evidence.Count),
                    0,
                    Math.Max(1, evidence.Select(record => record.DocumentId).Distinct(StringComparer.Ordinal).Count()),
                    0,
                    0,
                    "inferred",
                    relation.AmbiguousCandidates);
            })
            .ToArray();

        var skipped = index.Skips.Count;
        var limitations = BuildLimitations(
            skipped,
            externalReferences,
            conditionalReferences,
            ambiguousRelations,
            index.Skips);

        // The caller (extension host) may fix the id up front so progress messages and
        // the stored result refer to the same analysis.
        var resolvedAnalysisId = analysisId ?? Identity.AnalysisId(rootId, Mode, profileHash, createdAtText);

        var snapshot = new AnalysisSnapshot(
            SchemaVersion: 2,
            AnalyzerVersion: analyzerVersion,
            AnalysisId: resolvedAnalysisId,
            CreatedAt: createdAtText,
            Target: new TargetDescriptor(TargetKind(report.SolutionPath), rootId, Identity.NormalizeRelativePath(Path.GetFileName(report.SolutionPath))),
            Mode: Mode,
            Profile: new AnalysisProfile(Configuration, null, variants, profileHash),
            Capabilities: new AnalysisCapabilities(
                TypeGraph: false,
                Evidence: true,
                GeneratedDocuments: false,
                CycleWitness: false,
                Search: true),
            Completeness: "partial",
            Coverage: new AnalysisCoverage(
                Discovered: Math.Max(report.ProjectCount, report.Projects.Count),
                Loaded: report.Projects.Count,
                Analyzed: report.Projects.Count,
                Failed: Math.Max(0, Math.Max(report.ProjectCount, report.Projects.Count) - report.Projects.Count),
                Skipped: skipped,
                Unresolved: 0),
            Projects: projects,
            Namespaces: namespaces,
            Types: [],
            Relations: mappedRelations,
            CycleGroups: cycleGroups,
            Diagnostics: report.Warnings
                .Select((message, position) => new AnalysisDiagnostic(
                    Identity.DiagnosticId("quick.warning", null, $"{position}:{message}"),
                    "warning",
                    "quick.warning",
                    message,
                    null,
                    null,
                    resolvedAnalysisId))
                .ToArray(),
            EvidenceIndex: evidenceIndex,
            DeclarationIndex: null,
            SourceManifest: index.Documents
                .Select(document => new SourceDocument(
                    document.Id,
                    document.RelativePath,
                    document.Origin,
                    document.ContentHash,
                    document.ByteLength,
                    null))
                .ToArray(),
            Limitations: limitations);

        return new Result(snapshot, evidenceNdjson);
    }

    private sealed record PendingRelation(
        string Id,
        string SourceEntityId,
        string TargetEntityId,
        string Basis,
        string Kind,
        int? AmbiguousCandidates);

    private static (string Ndjson, EvidenceIndex Index) WriteEvidence(IReadOnlyList<EvidenceRecord> records)
    {
        var builder = new StringBuilder();
        var entries = new List<EvidenceIndexEntry>();
        long offset = 0;

        foreach (var group in records.GroupBy(record => record.RelationId))
        {
            var start = offset;
            var count = 0;
            foreach (var record in group)
            {
                var line = System.Text.Json.JsonSerializer.Serialize(
                    record,
                    EvidenceJsonContext.Default.EvidenceRecord) + "\n";
                builder.Append(line);
                offset += Encoding.UTF8.GetByteCount(line);
                count++;
            }

            entries.Add(new EvidenceIndexEntry(group.Key, start, count));
        }

        return (builder.ToString(), new EvidenceIndex("ndjson", "evidence.ndjson", offset, entries));
    }

    private static IReadOnlyList<CycleGroup> BuildCycleGroups(
        string scope,
        IReadOnlyList<PendingRelation> relations)
    {
        if (relations.Count == 0)
        {
            return [];
        }

        var nodeKeys = relations
            .SelectMany(relation => new[] { relation.SourceEntityId, relation.TargetEntityId })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // The detector's name map is only used for its display list, and v2 cycle
        // members must be entity ids, so the keys map to themselves.
        var nameByKey = nodeKeys.ToDictionary(key => key, key => key, StringComparer.Ordinal);
        var edges = relations
            .Select(relation => new CodeMapEdge(
                relation.SourceEntityId,
                relation.TargetEntityId,
                relation.SourceEntityId,
                relation.TargetEntityId,
                1))
            .ToArray();

        var result = CycleDetector.Analyze(nodeKeys, edges, nameByKey);
        var basis = relations[0].Basis;

        return result
            .ToCycles(scope)
            .Select(cycle =>
            {
                var members = cycle.Nodes;
                var internalRelations = relations
                    .Where(relation => members.Contains(relation.SourceEntityId) && members.Contains(relation.TargetEntityId))
                    .Select(relation => relation.Id)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();

                return new CycleGroup(
                    Identity.CycleGroupId(scope, basis, members),
                    scope,
                    basis,
                    members,
                    internalRelations,
                    null,
                    false);
            })
            .ToArray();
    }

    private static IReadOnlyList<Limitation> BuildLimitations(
        int skipped,
        int externalReferences,
        int conditionalReferences,
        int ambiguousRelations,
        IReadOnlyList<QuickFileSkip> skips)
    {
        var limitations = new List<Limitation>
        {
            new(
                "quick.typeGraphUnavailable",
                "Quick analysis reads project files and C# syntax only; it cannot resolve symbol usage, so the model has no types and no type-level relations."),
            new(
                "quick.usingInferred",
                "Namespace relations come from using directives. They are inferred dependencies and are not usage counts."),
            new(
                "quick.evidenceIsDeclarationOnly",
                "Evidence positions point at the declaration site (using directive or ProjectReference item), not at code that uses the dependency."),
            new(
                "quick.witnessUnavailable",
                "Cycle groups come from strongly connected components of inferred relations; no verified cycle path is available."),
            new(
                "quick.xmlSpanIsDeclarationLine",
                "ProjectReference spans cover the declaration line in the project file; the exact attribute range is not recorded.")
        };

        if (ambiguousRelations > 0)
        {
            limitations.Add(new Limitation(
                "quick.namespaceAmbiguousCandidates",
                "Some using directives match the same namespace name in more than one project. Every candidate is kept and marked, and no single target is assumed.",
                null,
                ambiguousRelations));
        }

        if (externalReferences > 0)
        {
            limitations.Add(new Limitation(
                "quick.externalProjectReferences",
                "Project references that point outside the analyzed target are not part of the model.",
                null,
                externalReferences));
        }

        if (conditionalReferences > 0)
        {
            limitations.Add(new Limitation(
                "quick.conditionNotEvaluated",
                "Conditional ProjectReference items were not evaluated against a configuration; edges may not exist in the selected build.",
                null,
                conditionalReferences));
        }

        if (skipped > 0)
        {
            var reasons = skips
                .GroupBy(skip => skip.Reason, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}: {group.Count()}");
            limitations.Add(new Limitation(
                "quick.sourceFilesSkipped",
                $"Some source files were not analyzed ({string.Join(", ", reasons)}).",
                null,
                skipped));
        }

        return limitations;
    }

    private static string ToProjectKind(string kind) => kind switch
    {
        "app" or "web" or "library" or "test" or "desktop" => kind,
        _ => "unknown"
    };

    private static string TargetKind(string solutionPath) => Path.GetExtension(solutionPath).ToLowerInvariant() switch
    {
        ".sln" => "solution",
        ".slnx" => "slnx",
        _ => "project"
    };
}
