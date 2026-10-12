namespace SharpDeps.Analysis.Quick;

using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Identity;

/// <summary>Syntax identities and bounded BaseList matching; no compiler symbol binding.</summary>
internal static class QuickTypeMapper
{
    internal sealed record Issue(string Code, string Message, string? TypeId);
    internal sealed record Reference(string SourceId, string TargetId, QuickTypeBaseReference Syntax);
    internal sealed record Result(IReadOnlyList<AnalysisType> Types, IReadOnlyList<DeclarationRecord> Declarations,
        IReadOnlyList<Reference> References, IReadOnlyList<Issue> Issues);
    private sealed record Group(string Id, string VariantId, string NamespaceName,
        string? FileScope, bool Conflicted, IReadOnlyList<QuickTypeDeclaration> Declarations);

    public static Result Map(QuickSourceIndex index, IReadOnlyDictionary<string, string> variants,
        Func<string, string, string?> namespaceId)
    {
        var documents = index.Documents.ToDictionary(document => document.Id, StringComparer.Ordinal);
        var declarations = (index.TypeDeclarations ?? [])
            .Where(declaration => variants.ContainsKey(declaration.ProjectPath) && documents.ContainsKey(declaration.DocumentId))
            .DistinctBy(declaration => (declaration.ProjectPath, declaration.DocumentId, declaration.Span.Start, declaration.Span.Length))
            .OrderBy(declaration => declaration.TypeSegments.Count)
            .ThenBy(declaration => documents[declaration.DocumentId].RelativePath, StringComparer.Ordinal)
            .ThenBy(declaration => declaration.Span.Start).ToArray();
        var byDeclaration = new Dictionary<(string Project, string Document, int Start, int Length), Group>();
        var owners = declarations.ToDictionary(declaration =>
            (declaration.ProjectPath, declaration.DocumentId, declaration.Span.Start, declaration.Span.Length));
        var groups = new List<Group>();
        var typeIds = new HashSet<string>(StringComparer.Ordinal);
        var issues = new List<Issue>();
        foreach (var depth in declarations.GroupBy(declaration => declaration.TypeSegments.Count))
        {
            var candidates = depth.Select(declaration =>
            {
                var parents = declaration.ContainingSpans.Select(span =>
                    byDeclaration.GetValueOrDefault((declaration.ProjectPath, declaration.DocumentId, span.Start, span.Length))).ToArray();
                var incompleteParent = parents.Any(parent => parent is null || parent.Conflicted);
                var fileScope = declaration.IsFileScoped ? documents[declaration.DocumentId].RelativePath : null;
                var key = CanonicalKey(declaration, fileScope);
                // A conflicted outer declaration must never merge its nested types.
                if (incompleteParent) key += "|parent:" + string.Join(',', parents.Select(parent => parent?.Id ?? "missing"));
                return (Declaration: declaration, Key: key, FileScope: fileScope, IncompleteParent: incompleteParent);
            });
            foreach (var candidate in candidates.GroupBy(item => (Variant: variants[item.Declaration.ProjectPath], item.Key))
                         .OrderBy(group => group.Key.Key, StringComparer.Ordinal))
            {
                var entries = candidate.ToArray();
                var conflict = entries.Any(entry => entry.IncompleteParent || entry.Declaration.HasSyntaxErrors)
                    || entries.Select(entry => entry.Declaration.SyntaxKind).Distinct(StringComparer.Ordinal).Count() != 1
                    || (entries.Length > 1 && entries.Any(entry => !entry.Declaration.IsPartial));
                string? conflictTypeId = null;
                foreach (var partitionGroup in entries.GroupBy(entry => conflict
                             ? $"{entry.Declaration.DocumentId}:{entry.Declaration.Span.Start}:{entry.Declaration.Span.Length}" : ""))
                {
                    var partition = partitionGroup.ToArray();
                    var first = partition[0];
                    var key = candidate.Key.Key;
                    if (conflict) key += FormattableString.Invariant($"|decl:{first.Declaration.DocumentId}:{first.Declaration.Span.Start}:{first.Declaration.Span.Length}:{first.Declaration.SyntaxKind}");
                    var group = new Group(Identity.TypeId(candidate.Key.Variant, key), candidate.Key.Variant,
                        string.Join('.', first.Declaration.NamespaceSegments), first.FileScope, conflict,
                        partition.Select(entry => entry.Declaration).ToArray());
                    if (!typeIds.Add(group.Id)) throw new InvalidDataException("Quick type identity collision: " + group.Id);
                    conflictTypeId ??= group.Id;
                    groups.Add(group);
                    foreach (var declaration in group.Declarations)
                        byDeclaration[(declaration.ProjectPath, declaration.DocumentId, declaration.Span.Start, declaration.Span.Length)] = group;
                }
                if (conflict)
                    issues.Add(new Issue("quick.typeDeclarationConflict",
                        $"{entries.Length} syntax declaration(s) cannot form one unambiguous partial type.", conflictTypeId));
            }
        }

        var types = new List<AnalysisType>();
        var records = new List<DeclarationRecord>();
        foreach (var group in groups.OrderBy(group => group.Id, StringComparer.Ordinal))
        {
            var first = group.Declarations[0];
            var own = first.TypeSegments[^1];
            var fullName = string.Join('.', first.NamespaceSegments.Concat(first.TypeSegments.Select(segment =>
                string.IsNullOrEmpty(segment.Name) ? "(missing type)" : segment.Name + (segment.Arity > 0 ? $"`{segment.Arity}" : ""))));
            types.Add(new AnalysisType(group.Id, group.VariantId, namespaceId(first.ProjectPath, first.SourceNamespaceName),
                string.IsNullOrEmpty(own.Name) ? "(missing type)" : own.Name, fullName, null, first.Kind,
                group.Declarations.Select(declaration => declaration.Accessibility).Distinct(StringComparer.Ordinal).Count() == 1
                    ? first.Accessibility : "unknown",
                group.Declarations.All(declaration => declaration.IsPartial), group.Declarations.Count, null, false, null));
            var ordinal = 0;
            foreach (var declaration in group.Declarations.OrderBy(declaration => documents[declaration.DocumentId].RelativePath, StringComparer.Ordinal)
                         .ThenBy(declaration => declaration.Span.Start).ThenBy(declaration => declaration.Span.Length))
                records.Add(new DeclarationRecord(group.Id, group.VariantId, declaration.DocumentId,
                    documents[declaration.DocumentId].RelativePath, declaration.Span.ToPhysicalSpan(), ordinal++, declaration.IsPartial));
        }

        var references = new List<Reference>();
        var globalBarriers = (index.TypeLookupBarriers ?? []).Where(barrier => barrier.DocumentId is null)
            .Select(barrier => barrier.ProjectPath).ToHashSet(StringComparer.Ordinal);
        var fileBarriers = (index.TypeLookupBarriers ?? []).Where(barrier => barrier.DocumentId is not null)
            .Select(barrier => (barrier.ProjectPath, barrier.DocumentId)).ToHashSet();
        var incomplete = index.TypeDeclarationsOmitted > 0 || index.Skips.Count > 0;
        var simpleTargets = groups.Where(group => group.Declarations[0].TypeSegments.Count == 1
                && group.Declarations[0].TypeSegments[0].Arity == 0)
            .GroupBy(group => (group.VariantId, group.NamespaceName, group.Declarations[0].TypeSegments[0].Name))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var qualifiedTargets = groups.Where(group => group.Declarations[0].TypeSegments.All(segment => segment.Arity == 0))
            .GroupBy(group => (group.VariantId, Name: string.Join('.', group.Declarations[0].NamespaceSegments
                .Concat(group.Declarations[0].TypeSegments.Select(segment => segment.Name)))))
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var reference in (index.TypeBaseReferences ?? []).DistinctBy(reference =>
                     (reference.ProjectPath, reference.DocumentId, reference.DeclarationStart, reference.DeclarationLength, reference.Span.Start, reference.Span.Length))
                     .OrderBy(reference => reference.ProjectPath, StringComparer.Ordinal)
                     .ThenBy(reference => documents.GetValueOrDefault(reference.DocumentId)?.RelativePath, StringComparer.Ordinal)
                     .ThenBy(reference => reference.Span.Start))
        {
            var ownerKey = (reference.ProjectPath, reference.DocumentId, reference.DeclarationStart, reference.DeclarationLength);
            if (!byDeclaration.TryGetValue(ownerKey, out var source)) continue;
            var owner = owners[ownerKey];
            string? refusal = incomplete ? "InventoryIncomplete" : source.Conflicted ? "DeclarationConflict" : null;
            if (refusal is null && (reference.Shape == "unsupported"
                || (reference.Shape == "simple" && (owner.TypeSegments.Count != 1 || owner.TypeSegments.Any(segment => segment.Arity > 0)
                    || globalBarriers.Contains(reference.ProjectPath) || fileBarriers.Contains((reference.ProjectPath, reference.DocumentId))))))
                refusal = "Unsupported";
            if (refusal is not null)
            {
                issues.Add(new Issue("quick.typeReference" + refusal, "BaseList target was withheld: " + refusal + ".", source.Id));
                continue;
            }
            var named = reference.Shape == "simple"
                ? simpleTargets.GetValueOrDefault((source.VariantId, source.NamespaceName, reference.NameSegments[0])) ?? []
                : qualifiedTargets.GetValueOrDefault((source.VariantId, string.Join('.', reference.NameSegments))) ?? [];
            var candidates = named.Where(target => target.FileScope is null
                || target.FileScope == documents[reference.DocumentId].RelativePath).ToArray();
            if (candidates.Length != 1 || candidates[0].Conflicted || candidates[0].Id == source.Id)
            {
                var code = candidates.Length == 0 ? "Unmatched" : candidates.Length > 1 ? "Ambiguous"
                    : candidates[0].Conflicted ? "DeclarationConflict" : "SelfReference";
                issues.Add(new Issue("quick.typeReference" + code, $"BaseList target was withheld: {code}; {candidates.Length} candidate(s).", source.Id));
                continue;
            }
            references.Add(new Reference(source.Id, candidates[0].Id, reference));
        }
        return new Result(types, records, references, issues);
    }

    private static string CanonicalKey(QuickTypeDeclaration declaration, string? fileScope)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStartArray();
            foreach (var segment in declaration.NamespaceSegments) writer.WriteStringValue(segment);
            writer.WriteEndArray();
            writer.WriteStartArray();
            foreach (var segment in declaration.TypeSegments)
            {
                writer.WriteStartArray(); writer.WriteStringValue(segment.Name); writer.WriteNumberValue(segment.Arity); writer.WriteEndArray();
            }
            writer.WriteEndArray();
            if (fileScope is null) writer.WriteNullValue(); else writer.WriteStringValue(fileScope);
            writer.WriteEndArray();
        }
        return "quick-syntax-v1:" + Encoding.UTF8.GetString(stream.ToArray());
    }

    public static (string Ndjson, DeclarationIndex? Index) WriteDeclarations(IReadOnlyList<DeclarationRecord> records)
    {
        var builder = new StringBuilder();
        var entries = new List<DeclarationIndexEntry>();
        long offset = 0;
        foreach (var group in records.GroupBy(record => record.TypeId).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var start = offset;
            foreach (var record in group)
            {
                var line = JsonSerializer.Serialize(record, EvidenceJsonContext.Default.DeclarationRecord) + "\n";
                builder.Append(line);
                offset += Encoding.UTF8.GetByteCount(line);
            }
            entries.Add(new DeclarationIndexEntry(group.Key, start, group.Count()));
        }
        return (builder.ToString(), entries.Count == 0 ? null : new DeclarationIndex("ndjson", "declarations.ndjson", offset, entries));
    }
}
