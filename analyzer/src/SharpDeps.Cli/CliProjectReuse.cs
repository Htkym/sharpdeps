namespace SharpDeps.Cli;

using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Roslyn;
using SharpDeps.Analysis.Roslyn.Evidence;
using SharpDeps.Analysis.Roslyn.Symbols;
using SharpDeps.Index;

internal sealed record CliProjectSelection(string Key, string VariantId, string ProjectId,
    IReadOnlyList<string> SourceIds, IReadOnlyList<string> References);

/// <summary>Snapshot-neutral collector data, not compilations or executable/source buffers.</summary>
internal static class CliProjectReuse
{
    internal const string Version = "sharpdeps-cli-project-reuse/6";
    internal static bool IsExecutionDiagnostic(string code) => code is "cli.semanticVariantsLoaded"
        or "cli.projectCollectorsExtracted" or "cli.projectCollectorsReused"
        || code.StartsWith("cli.projectExtracted.", StringComparison.Ordinal)
        || code.StartsWith("cli.projectReused.", StringComparison.Ordinal);
    internal sealed record Symbols(IndexedNamespace[] Namespaces, IndexedType[] Types,
        IndexedMember[] Members, bool HasTopLevelStatements);
    internal sealed record Extraction(Symbols Symbols, CollectedEvidence[] Evidence, OperationCollectionStats Stats);
    internal sealed record Plan(IReadOnlyDictionary<string, Extraction> Saved, IReadOnlySet<string> Dirty);

    internal static Plan? Select(IndexSnapshot previous, IndexProjectReuse? proof, string? structureHash,
        IReadOnlyList<CliProjectSelection> selections, IReadOnlyDictionary<string, IndexFile> current,
        CancellationToken token)
    {
        if (proof is null || proof.Version != Version || structureHash is null || proof.StructureHash != structureHash
            || selections.Count is < 1 or > 64 || proof.Variants.Count != selections.Count) return null;
        var old = proof.Variants.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var selected = selections.ToDictionary(p => p.Key, StringComparer.Ordinal);
        if (!old.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(selected.Keys)) return null;
        var oldFiles = previous.Files.ToDictionary(f => f.SourceId, StringComparer.Ordinal);
        var oldSources = old.Values.SelectMany(p => p.SourceIds).ToHashSet(StringComparer.Ordinal);
        var newSources = selections.SelectMany(p => p.SourceIds).ToHashSet(StringComparer.Ordinal);
        // Together with the complete non-source/directory inventory hash, these sets prove every
        // added path from the new compilation and every deleted path from the old actual owners.
        if (!oldSources.SetEquals(oldFiles.Values.Where(f => !f.Deleted && f.Kind == "source").Select(f => f.SourceId))
            || !newSources.SetEquals(current.Values.Where(f => !f.Deleted && f.Kind == "source").Select(f => f.SourceId))) return null;
        var added = newSources.Except(oldSources, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var removed = oldSources.Except(newSources, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        // Addition and deletion are independent owner facts; never infer rename history or transfer IDs.
        var oldPaths = oldFiles.Values.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
        var newPaths = current.Values.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
        if (oldSources.Intersect(newSources, StringComparer.Ordinal).Any(source =>
                oldFiles[source].RelativePath != current[source].RelativePath)
            || added.Any(source => oldPaths.Contains(current[source].RelativePath))
            || removed.Any(source => newPaths.Contains(oldFiles[source].RelativePath))) return null;
        var saved = new Dictionary<string, Extraction>(StringComparer.Ordinal);
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in selections)
        {
            token.ThrowIfCancellationRequested();
            var prior = old[selection.Key];
            if (prior.VariantId != selection.VariantId || prior.ProjectId != selection.ProjectId
                || prior.References.Any(key => !selected.ContainsKey(key))) return null;
            var data = Read(prior, oldFiles);
            if (data is null) return null;
            saved.Add(selection.Key, data);
            var beforeSources = prior.SourceIds.ToHashSet(StringComparer.Ordinal);
            var afterSources = selection.SourceIds.ToHashSet(StringComparer.Ordinal);
            if (!beforeSources.SetEquals(afterSources)) dirty.Add(selection.Key);
            // Check the union of actual old/new owners, including retained shared inputs.
            // Deleted inputs are read only from the old snapshot, never the live manifest.
            foreach (var source in beforeSources.Union(afterSources, StringComparer.Ordinal))
            {
                if (added.Contains(source) || removed.Contains(source)) { dirty.Add(selection.Key); continue; }
                if (!oldFiles.TryGetValue(source, out var before) || !current.TryGetValue(source, out var after)) return null;
                if (before.ContentHash != after.ContentHash || before.ByteLength != after.ByteLength
                    || before.Utf16Length != after.Utf16Length) dirty.Add(selection.Key);
            }
            if (!prior.References.ToHashSet(StringComparer.Ordinal).SetEquals(selection.References)) dirty.Add(selection.Key);
        }
        // Both old and current reference relations participate; removed references must not hide a dependent.
        var reverse = selections.SelectMany(s => old[s.Key].References.Concat(s.References).Distinct(StringComparer.Ordinal)
                .Select(target => (Target: target, Source: s.Key)))
            .GroupBy(e => e.Target, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Source).ToArray(), StringComparer.Ordinal);
        var queue = new Queue<string>(dirty);
        while (queue.TryDequeue(out var key))
        {
            token.ThrowIfCancellationRequested();
            foreach (var owner in reverse.GetValueOrDefault(key) ?? [])
                if (dirty.Add(owner)) queue.Enqueue(owner);
        }
        // No extraction saving is reported when every collector must run.
        return dirty.Count > 0 && dirty.Count < selections.Count ? new(saved, dirty) : null;
    }

    internal static IReadOnlyList<CliProjectSelection>? Selections(Guid workspace, IReadOnlyList<HarnessProjectVariant> projects,
        IReadOnlyList<SymbolIndexInput> inputs, IReadOnlyDictionary<string, IReadOnlyList<string>> sourceIds,
        SemanticLoadResult loaded)
    {
        if (projects.Count is < 1 or > 64 || projects.Any(p => p.TargetFramework != "net10.0")
            || projects.GroupBy(p => p.ProjectId, StringComparer.Ordinal).Any(g => g.Count() != 1)) return null;
        var keys = projects.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        if (loaded.Report.References.Any(r => !keys.Contains(r.SourceVariantKey) || r.Resolution != "exact"
            || r.TargetVariantKey is null || !keys.Contains(r.TargetVariantKey))) return null;
        var result = new List<CliProjectSelection>();
        foreach (var project in projects)
        {
            if (!loaded.DefiningVariantByAssembly.TryGetValue(project.Key, out var referenceMap)
                || referenceMap.Values.Any(key => !keys.Contains(key))) return null;
            var input = inputs.Single(i => i.VariantId == project.Key);
            // Verify actual compilation project references also map to a selected defining assembly.
            foreach (var reference in input.Compilation.References.OfType<Microsoft.CodeAnalysis.CompilationReference>())
                if (!referenceMap.TryGetValue(reference.Compilation.AssemblyName ?? "", out var target) || !keys.Contains(target))
                    return null;
            result.Add(new(project.Key, SharpDeps.Analysis.Core.Identity.HarnessIdentity.VariantId(workspace,
                project.ProjectId, project.TargetFramework, project.Configuration, project.Platform, project.RuntimeIdentifier),
                project.ProjectId, sourceIds[project.Key].Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                referenceMap.Values.Where(k => k != project.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
        }
        return result;
    }

    internal static Symbols CaptureSymbols(SymbolIndex symbols) => new(
        symbols.Namespaces.ToArray(), symbols.Types.ToArray(), symbols.Members.ToArray(), symbols.Summary.HasTopLevelStatements);

    internal static SymbolIndex CombineSymbols(IEnumerable<Symbols> parts, SourceDocumentRegistry documents, int variantCount)
    {
        var all = parts.ToArray();
        var namespaces = all.SelectMany(p => p.Namespaces).OrderBy(n => n.ProjectVariantId, StringComparer.Ordinal)
            .ThenBy(n => n.Name, StringComparer.Ordinal).ToArray();
        var types = all.SelectMany(p => p.Types).OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
        var members = all.SelectMany(p => p.Members).OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
        return new(namespaces, types, members, documents.Documents, SymbolPositionIndex.Build(types),
            new(variantCount, namespaces.Length, types.Length, members.Length, types.Count(t => t.IsPartial),
                types.Count(t => t.IsNested), types.Count(t => t.IsFileLocal), types.Count(t => t.IsGeneric),
                all.Any(p => p.HasTopLevelStatements)));
    }

    internal static IndexProjectReuse? Capture(string? structureHash, IReadOnlyList<CliProjectSelection>? selections,
        IReadOnlyDictionary<string, Extraction> extractions, IReadOnlyDictionary<string, IndexFile> files)
    {
        if (structureHash is null || selections is null) return null;
        var proof = new List<IndexProjectExtraction>();
        long length = 0;
        foreach (var selection in selections)
        {
            var data = JsonSerializer.SerializeToElement(extractions[selection.Key]);
            var item = new IndexProjectExtraction(selection.Key, selection.VariantId, selection.ProjectId,
                selection.SourceIds, selection.References, data);
            length += data.GetRawText().Length;
            if (length > IndexSnapshotValidator.MaximumSearchTextUtf16 || Read(item, files) is null) return null;
            proof.Add(item);
        }
        return new(Version, structureHash, proof);
    }

    private static Extraction? Read(IndexProjectExtraction proof, IReadOnlyDictionary<string, IndexFile> files)
    {
        try
        {
            var data = proof.Data.Deserialize<Extraction>();
            if (data?.Symbols is not { Namespaces: not null, Types: not null, Members: not null } symbols
                || data.Evidence is null || data.Stats is null
                || new[] { symbols.Namespaces.Length, symbols.Types.Length, symbols.Members.Length, data.Evidence.Length }
                    .Any(count => count > IndexSnapshotValidator.MaximumCollectionItems)
                || new[] { data.Stats.BodiesScanned, data.Stats.UnresolvedOperations, data.Stats.DynamicReferences,
                    data.Stats.CandidateOnlySymbols }.Any(count => count < 0)) return null;
            var sources = proof.SourceIds.ToHashSet(StringComparer.Ordinal);
            bool Declaration(SymbolDeclarationLocation d) => sources.Contains(d.DocumentId)
                && files.TryGetValue(d.DocumentId, out var file) && d.Start >= 0 && d.Length >= 0
                && (long)d.Start + d.Length <= file.Utf16Length;
            if (symbols.Namespaces.Any(n => n is null || n.ProjectVariantId != proof.Key
                    || n.RepresentativeDocumentId is not null && !sources.Contains(n.RepresentativeDocumentId))
                || symbols.Types.Any(t => t is null || t.ProjectVariantId != proof.Key
                    || t.Declarations is null || t.Declarations.Any(d => !Declaration(d)))
                || symbols.Members.Any(m => m is null || m.ProjectVariantId != proof.Key
                    || m.Declarations is null || m.Declarations.Any(d => !Declaration(d))
                    || m.HarnessDeclarations?.Any(d => !Declaration(d)) == true)
                || data.Evidence.Any(e => e is null || e.Evidence is null || e.SourceVariantId != proof.Key
                    || !sources.Contains(e.Evidence.DocumentId)
                    || !files.TryGetValue(e.Evidence.DocumentId, out var file) || e.Evidence.SourceContentHash != file.ContentHash))
                return null;
            return data;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        { return null; }
    }
}
