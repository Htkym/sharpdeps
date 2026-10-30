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
        ArgumentNullException.ThrowIfNull(request);
        QueryText(request.Query, request.QueryKind);
        Text("projectId", request.ProjectId);
        Text("variantId", request.VariantId);
        Text("pathPrefix", request.PathPrefix);
        Text("pathTo", request.PathTo);
        var budget = request.Budget ?? Budget();
        if (!Enum.IsDefined(request.Freshness) || !Enum.IsDefined(request.Format) || !Enum.IsDefined(request.ScopePolicy)
            || request.QueryKind == ExploreQueryKind.HttpGetLiteral && request.PathTo is not null
            || budget.MaxNodes is < 1 or > 500 || budget.MaxEdges is < 1 or > 1000 || budget.MaxDepth is < 1 or > 8
            || budget.MaxMilliseconds is < 10 or > 30000 || budget.MaxChars is < 2048 or > 1000000
            || budget.MaxBytes is < 2048 or > 4000000)
            throw new CliFailure("ARGUMENT_INVALID", 2);
        return request with { Budget = budget };
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

    internal static ExploreScopePolicy ScopePolicy(string? value) => value switch
    {
        null or "unique" => ExploreScopePolicy.RequireUnique,
        "prefer-debug" => ExploreScopePolicy.PreferDebug,
        _ => throw new CliFailure("ARGUMENT_INVALID", 2)
    };
}
