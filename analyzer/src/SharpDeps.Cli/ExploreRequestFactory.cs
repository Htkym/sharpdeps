namespace SharpDeps.Cli;

using SharpDeps.Query;

internal static class ExploreRequestFactory
{
    internal static QueryBudget Budget(int? maxNodes = null, int? maxEdges = null, int? maxDepth = null,
        int? maxMilliseconds = null, int? maxChars = null, int? maxBytes = null)
        => new(maxNodes ?? 60, maxEdges ?? 120, maxDepth ?? 2, maxMilliseconds ?? 1000,
            maxChars ?? 12000, maxBytes ?? 48000);

    internal static void Text(string name, string? value)
    {
        var maximum = name switch { "query" => 1024, "pathPrefix" => 512, _ => 128 };
        if (value is not null && (value.Length == 0 || value.Length > maximum || value.Any(char.IsControl)
            || name != "pathPrefix" && string.IsNullOrWhiteSpace(value)))
            throw new CliFailure("ARGUMENT_INVALID", 2);
    }

    internal static ExploreRequest Validate(ExploreRequest request)
    {
        try { return ExplorationService.Normalize(request); }
        catch (ArgumentException) { throw new CliFailure("ARGUMENT_INVALID", 2); }
    }

    internal static void QueryText(string? value, ExploreQueryKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new CliFailure("ARGUMENT_INVALID", 2);
        if (kind == ExploreQueryKind.Symbol)
        {
            Text("query", value);
            if (string.IsNullOrWhiteSpace(value)) throw new CliFailure("ARGUMENT_INVALID", 2);
            return;
        }
        if (value is null || value.Length > 1024 || value.Any(char.IsControl))
            throw new CliFailure("ARGUMENT_INVALID", 2);
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw new CliFailure("ARGUMENT_INVALID", 2);
            }
            else if (char.IsLowSurrogate(value[i])) throw new CliFailure("ARGUMENT_INVALID", 2);
        }
    }

    internal static ExploreQueryKind QueryKind(string? value) => value switch
    {
        null or "symbol" => ExploreQueryKind.Symbol,
        "http-get-literal" => ExploreQueryKind.HttpGetLiteral,
        _ => throw new CliFailure("ARGUMENT_INVALID", 2)
    };

    internal static QueryFreshnessPolicy Freshness(string? value) => value switch
    {
        null or "allow-stale" => QueryFreshnessPolicy.AllowStale,
        "require-fresh" => QueryFreshnessPolicy.RequireFresh,
        "refresh" => QueryFreshnessPolicy.Refresh,
        _ => throw new CliFailure("ARGUMENT_INVALID", 2)
    };

    internal static ExploreFormat Format(string? value, ExploreFormat fallback) => value switch
    {
        null => fallback,
        "audit" => ExploreFormat.Audit,
        "agent" => ExploreFormat.Agent,
        _ => throw new CliFailure("ARGUMENT_INVALID", 2)
    };

    internal static ExploreScopePolicy ScopePolicy(string? value, string query, string? projectId = null,
        string? variantId = null, string? pathTo = null, ExploreQueryKind queryKind = ExploreQueryKind.Symbol) => value switch
    {
        null => projectId is null && variantId is null && pathTo is null
            && queryKind == ExploreQueryKind.Symbol && !ExplorationService.HasSavedIdentity(query)
                ? ExploreScopePolicy.Discover : ExploreScopePolicy.RequireUnique,
        "unique" => ExploreScopePolicy.RequireUnique,
        "prefer-debug" => ExploreScopePolicy.PreferDebug,
        "discover" => ExploreScopePolicy.Discover,
        _ => throw new CliFailure("ARGUMENT_INVALID", 2)
    };
}
