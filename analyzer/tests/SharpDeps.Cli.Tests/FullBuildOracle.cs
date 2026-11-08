namespace SharpDeps.Cli.Tests;

using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Index;
using SharpDeps.Query;

internal static class FullBuildOracle
{
    internal static bool IsExecutionDiagnostic(string code) => code is "cli.semanticGraphReused" or "cli.semanticWorkspaceReused"
        or "cli.semanticVariantsLoaded" or "cli.projectCollectorsExtracted" or "cli.projectCollectorsReused"
        || IsVariantCounter(code, "cli.projectExtracted.") || IsVariantCounter(code, "cli.projectReused.");

    private static bool IsVariantCounter(string code, string prefix)
        => code.StartsWith(prefix + "hvar_", StringComparison.Ordinal)
            && code.Length == prefix.Length + 69
            && code.AsSpan(prefix.Length + 5).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal static IReadOnlyList<string> Differences(OracleObservation expected, OracleObservation actual)
    {
        var differences = new List<string>();
        Compare(JsonSerializer.SerializeToElement(Normalize(expected), QueryJson.Options),
            JsonSerializer.SerializeToElement(Normalize(actual), QueryJson.Options), "$", differences);
        return differences;
    }

    private static object Normalize(OracleObservation observation)
    {
        var snapshot = observation.Snapshot;
        var graph = snapshot.Graph;
        return new
        {
            Snapshot = snapshot with
            {
                Graph = graph with
                {
                    SnapshotId = "<run>",
                    Generation = 0,
                    Variants = graph.Variants.OrderBy(v => v.Id, StringComparer.Ordinal).ToArray(),
                    Nodes = graph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToArray(),
                    SymbolOccurrences = graph.SymbolOccurrences.OrderBy(o => o.Id, StringComparer.Ordinal)
                        .Select(o => o with { Declarations = o.Declarations?.OrderBy(Json, StringComparer.Ordinal).ToArray() }).ToArray(),
                    Edges = graph.Edges.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray(),
                    LegacyReferences = graph.LegacyReferences.OrderBy(Json, StringComparer.Ordinal).ToArray(),
                    Diagnostics = (graph.Diagnostics ?? []).Where(d => !IsExecutionDiagnostic(d.Code))
                        .OrderBy(d => d.Code, StringComparer.Ordinal).ToArray()
                },
                Files = snapshot.Files.OrderBy(f => f.SourceId, StringComparer.Ordinal).Select(f => f with
                {
                    Reuse = f.Reuse is not { Projects: { } projects } reuse ? f.Reuse : reuse with
                    {
                        Projects = projects with { StructureHash = "<execution-proof>" }
                    }
                }).ToArray(),
                Documents = snapshot.Documents.OrderBy(d => d.DocumentUuid).Select(d => d with
                {
                    Sections = d.Sections.OrderBy(s => s.LocalKey, StringComparer.Ordinal).ToArray()
                }).ToArray(),
                Aliases = snapshot.Aliases.OrderBy(Json, StringComparer.Ordinal).ToArray(),
                SearchText = snapshot.SearchText.OrderBy(s => s.NodeId, StringComparer.Ordinal).ToArray(),
                GeneratedBodies = snapshot.StoredGeneratedBodies.OrderBy(b => b.SourceId, StringComparer.Ordinal).ToArray(),
                PackageBodies = snapshot.StoredPackageBodies.OrderBy(b => b.SourceId, StringComparer.Ordinal).ToArray()
            },
            observation.Fts,
            Queries = observation.Queries.ToDictionary(p => p.Key, p => NormalizeQuery(p.Value, graph), StringComparer.Ordinal)
        };
    }

    private static QueryEnvelope NormalizeQuery(QueryEnvelope query, HarnessGraphEnvelope graph)
    {
        var saved = (graph.Diagnostics ?? []).Take(16).ToArray();
        var projected = saved.Select(d => new QueryIssue(d.Code.Length <= 64 ? d.Code : "GRAPH_DIAGNOSTIC",
            "Saved graph diagnostic count: " + d.Count)).ToArray();
        var diagnostics = query.Diagnostics;
        if (diagnostics.Count >= projected.Length && diagnostics.TakeLast(projected.Length).SequenceEqual(projected))
            diagnostics = diagnostics.Take(diagnostics.Count - projected.Length).Concat(saved
                .Select((d, index) => (Diagnostic: d, Issue: projected[index]))
                .Where(p => !IsExecutionDiagnostic(p.Diagnostic.Code)).Select(p => p.Issue)).ToArray();
        return query with
        {
            Snapshot = query.Snapshot with { Id = "<run>", Generation = 0, CheckedAt = null },
            Diagnostics = diagnostics,
            Budget = query.Budget with { UsedChars = 0, UsedBytes = 0, EstimatedTokens = 0 }
        };
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, QueryJson.Options);

    private static void Compare(JsonElement expected, JsonElement actual, string path, List<string> differences)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            differences.Add(path + ": JSON kind differs");
            return;
        }
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var properties = expected.EnumerateObject().Select(p => p.Name)
                .Union(actual.EnumerateObject().Select(p => p.Name)).Order(StringComparer.Ordinal);
            foreach (var property in properties)
            {
                if (!expected.TryGetProperty(property, out var left) || !actual.TryGetProperty(property, out var right))
                    differences.Add(path + "." + property + ": property missing");
                else
                    Compare(left, right, path + "." + property, differences);
            }
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            if (expected.GetArrayLength() != actual.GetArrayLength())
                differences.Add(path + ": array length differs");
            for (var i = 0; i < Math.Min(expected.GetArrayLength(), actual.GetArrayLength()); i++)
                Compare(expected[i], actual[i], path + "[" + i + "]", differences);
        }
        else if (expected.GetRawText() != actual.GetRawText())
            differences.Add(path + ": " + expected.GetRawText() + " != " + actual.GetRawText());
    }
}
