namespace SharpDeps.Cli;

using System.Globalization;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Query;

internal sealed class CliArguments
{
    private readonly Dictionary<string, List<string>> values = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Switches = new(StringComparer.Ordinal)
    { "trusted", "quiet", "include-candidates", "no-documents", "dependencies", "require-complete" };
    private static readonly HashSet<string> Repeated = new(StringComparer.Ordinal) { "project", "variant", "edge-kind" };
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal) { "root", "index", "trusted", "quiet", "request-id" };
    private static readonly HashSet<string> Index = new(StringComparer.Ordinal)
    { "target", "configuration", "platform", "project", "tfm", "timeout-seconds" };
    private static readonly HashSet<string> Query = new(StringComparer.Ordinal)
    { "term", "id", "ids", "project-id", "path-prefix", "variant", "node-kind", "edge-kind", "include-candidates",
      "no-documents", "dependencies", "freshness", "require-complete", "page-size", "cursor", "max-nodes", "max-edges",
      "max-depth", "max-milliseconds", "max-chars", "max-bytes", "kind" };
    public string Command { get; private set; } = "help";
    public string RequestId => Get("request-id") ?? "cli";
    public bool Has(string name) => values.ContainsKey(name);
    public string? Get(string name) => values.TryGetValue(name, out var list) ? list[0] : null;
    public string Require(string name) => Get(name) ?? throw new CliFailure("ARGUMENT_REQUIRED", 2);
    public IReadOnlyList<string>? Many(string name) => values.TryGetValue(name, out var list) ? list : null;
    public int Number(string name, int fallback, int min, int max)
    {
        if (!Has(name)) return fallback;
        if (!int.TryParse(Get(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new CliFailure("ARGUMENT_INVALID", 2);
        return value;
    }

    public static CliArguments Parse(string[] args)
    {
        var parsed = new CliArguments();
        if (args.Length == 0 || args is ["--help"] or ["-h"]) return parsed;
        if (args.Length > 200) throw new CliFailure("ARGUMENT_INVALID", 2);
        parsed.Command = args[0].ToLowerInvariant();
        if (parsed.Command is not ("index" or "update" or "query" or "doctor" or "help")
            && !Enum.TryParse<QueryKind>(parsed.Command, true, out _)) throw new CliFailure("COMMAND_UNKNOWN", 2);
        var allowed = new HashSet<string>(Common, StringComparer.Ordinal);
        if (parsed.Command is "index" or "update") allowed.UnionWith(Index);
        else if (parsed.Command is not ("help" or "doctor")) allowed.UnionWith(Query);
        for (var i = 1; i < args.Length; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal)) throw new CliFailure("ARGUMENT_INVALID", 2);
            var name = argument[2..];
            if (!allowed.Contains(name)) throw new CliFailure("ARGUMENT_UNKNOWN", 2);
            var value = "true";
            if (!Switches.Contains(name))
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new CliFailure("ARGUMENT_REQUIRED", 2);
                value = args[i];
                if (value.Length > 8192 || value.Length == 0 || value.Any(char.IsControl)) throw new CliFailure("ARGUMENT_INVALID", 2);
            }
            if (!parsed.values.TryGetValue(name, out var list)) parsed.values.Add(name, list = []);
            else if (!Repeated.Contains(name)) throw new CliFailure("ARGUMENT_DUPLICATE", 2);
            list.Add(value);
        }
        if (parsed.RequestId.Length > 64 || string.IsNullOrWhiteSpace(parsed.RequestId)) throw new CliFailure("ARGUMENT_INVALID", 2);
        if (parsed.Has("kind") && parsed.Command != "query") throw new CliFailure("ARGUMENT_INVALID", 2);
        if (parsed.Has("tfm") && !parsed.Has("project")) throw new CliFailure("PROJECT_SELECTION_REQUIRED", 2);
        return parsed;
    }

    public QueryRequest QueryRequest()
    {
        var kind = EnumValue<QueryKind>(Command == "query" ? Require("kind") : Command);
        var selectors = new[] { "term", "id", "ids" }.Where(Has).ToArray();
        if (kind == QueryKind.Status ? selectors.Length != 0 : selectors.Length != 1)
            throw new CliFailure("ARGUMENT_INVALID", 2);
        if (kind == QueryKind.Search && !Has("term")
            || kind is QueryKind.Symbol or QueryKind.Callers or QueryKind.Callees && Has("term")
            || Has("dependencies") && kind != QueryKind.Impact
            || kind == QueryKind.Status && new[] { "project-id", "path-prefix", "node-kind" }.Any(Has)
            || new[] { "edge-kind", "include-candidates", "no-documents", "max-edges", "max-depth" }.Any(Has)
                && kind is QueryKind.Status or QueryKind.Search or QueryKind.Symbol)
            throw new CliFailure("ARGUMENT_UNSUPPORTED", 2);
        foreach (var field in new[] { "term", "id", "project-id" })
            if (Get(field) is { } value && (value.Length > 128 || string.IsNullOrWhiteSpace(value)))
                throw new CliFailure("ARGUMENT_INVALID", 2);
        if (Get("path-prefix") is { Length: > 512 }) throw new CliFailure("ARGUMENT_INVALID", 2);
        var ids = Get("ids")?.Split(',', StringSplitOptions.None);
        if (ids is not null && (ids.Length > 64 || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128)))
            throw new CliFailure("ARGUMENT_INVALID", 2);
        foreach (var (name, count, length) in new[] { ("variant", 64, 128), ("edge-kind", 32, 64) })
            if (Many(name) is { } list && (list.Count > count || list.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > length)))
                throw new CliFailure("ARGUMENT_INVALID", 2);
        HarnessNodeKind? nodeKind = Has("node-kind") ? EnumValue<HarnessNodeKind>(Require("node-kind")) : null;
        var freshness = Get("freshness") switch
        {
            null or "allow-stale" => QueryFreshnessPolicy.AllowStale,
            "require-fresh" => QueryFreshnessPolicy.RequireFresh,
            "refresh" => QueryFreshnessPolicy.Refresh,
            _ => throw new CliFailure("ARGUMENT_INVALID", 2)
        };
        return new(RequestId, kind, Get("term"), Get("id"), ids,
            new QueryScope(nodeKind, Get("project-id"), Get("path-prefix"), Many("variant")), Many("edge-kind"),
            Has("include-candidates"), !Has("no-documents"), !Has("dependencies"), freshness,
            Has("require-complete"), Number("page-size", 100, 1, 500), Get("cursor"),
            new QueryBudget(Number("max-nodes", 60, 1, 500), Number("max-edges", 120, 1, 1000),
                Number("max-depth", 2, 1, 8), Number("max-milliseconds", 1000, 10, 30000),
                Number("max-chars", 18000, 2048, 1000000), Number("max-bytes", 64000, 2048, 4000000)));
    }

    private static T EnumValue<T>(string text) where T : struct, Enum
        => Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value) && !int.TryParse(text, out _)
            ? value : throw new CliFailure("ARGUMENT_INVALID", 2);
}
