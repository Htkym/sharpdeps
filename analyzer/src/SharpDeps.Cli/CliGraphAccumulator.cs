namespace SharpDeps.Cli;

using SharpDeps.Analysis.Contracts.Harness;

internal sealed class CliGraphAccumulator
{
    private readonly HarnessGraphEnvelope header;
    private readonly Dictionary<string, HarnessNode> nodes;
    private readonly Dictionary<string, HarnessVariant> variants;
    private readonly Dictionary<string, HarnessSymbolOccurrence> occurrences;
    private readonly Dictionary<string, HarnessEdge> edges;
    private readonly List<HarnessLegacyReference> legacy;
    private readonly HashSet<HarnessLegacyReference> legacySet;
    private HarnessMarkdownIdentity? markdown;
    private HarnessCoverage coverage;

    public CliGraphAccumulator(HarnessGraphEnvelope graph)
    {
        header = graph;
        nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        variants = graph.Variants.ToDictionary(v => v.Id, StringComparer.Ordinal);
        occurrences = graph.SymbolOccurrences.ToDictionary(o => o.Id, StringComparer.Ordinal);
        edges = graph.Edges.ToDictionary(e => e.Id, StringComparer.Ordinal);
        legacy = graph.LegacyReferences.Distinct().ToList();
        legacySet = legacy.ToHashSet();
        markdown = graph.Markdown;
        coverage = graph.Coverage;
    }

    public void Add(HarnessGraphEnvelope graph)
    {
        if (header.Format != graph.Format || header.SchemaVersion != graph.SchemaVersion
            || header.IdentityVersion != graph.IdentityVersion || header.WorkspaceId != graph.WorkspaceId
            || header.SnapshotId != graph.SnapshotId || header.Generation != graph.Generation
            || markdown is not null && graph.Markdown is not null && markdown != graph.Markdown)
            throw new CliIndexException("CLI_GRAPH_MERGE_CONFLICT");
        AddById(nodes, graph.Nodes, n => n.Id);
        AddById(variants, graph.Variants, v => v.Id);
        AddById(occurrences, graph.SymbolOccurrences, o => o.Id);
        AddById(edges, graph.Edges, e => e.Id);
        foreach (var reference in graph.LegacyReferences)
            if (legacySet.Add(reference))
                legacy.Add(reference);
        markdown ??= graph.Markdown;
        coverage = coverage == HarnessCoverage.Failed || graph.Coverage == HarnessCoverage.Failed ? HarnessCoverage.Failed
            : coverage == HarnessCoverage.Partial || graph.Coverage == HarnessCoverage.Partial ? HarnessCoverage.Partial
            : HarnessCoverage.CompleteWithinScope;
    }

    public HarnessGraphEnvelope Freeze() => header with
    {
        Nodes = Array.AsReadOnly(nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal).ToArray()),
        Variants = Array.AsReadOnly(variants.Values.OrderBy(v => v.Id, StringComparer.Ordinal).ToArray()),
        SymbolOccurrences = Array.AsReadOnly(occurrences.Values.OrderBy(o => o.Id, StringComparer.Ordinal).ToArray()),
        Edges = Array.AsReadOnly(edges.Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray()),
        LegacyReferences = Array.AsReadOnly(legacy.ToArray()),
        Markdown = markdown,
        Coverage = coverage
    };

    private static void AddById<T>(Dictionary<string, T> target, IEnumerable<T> values, Func<T, string> id) where T : notnull
    {
        foreach (var value in values)
        {
            var key = id(value);
            if (target.TryGetValue(key, out var old) && !EqualityComparer<T>.Default.Equals(old, value))
                throw new CliIndexException("CLI_GRAPH_MERGE_CONFLICT");
            target[key] = value;
        }
    }
}
