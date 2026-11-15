namespace SharpDeps.Mcp;

using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using SharpDeps.Cli;
using SharpDeps.Query;

public sealed record McpHostOptions(string Root, string Index = ".sharpdeps/index.sqlite", bool Trusted = false, bool AllowMutations = false)
{
    public string? MetricsDirectory { get; init; }

    public static McpHostOptions Parse(string[] args)
    {
        string? root = null, index = null, metrics = null;
        var trusted = false; var mutations = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--trusted" when !trusted: trusted = true; break;
                case "--allow-mutations" when !mutations: mutations = true; break;
                case "--root" when root is null: root = Value(); break;
                case "--index" when index is null: index = Value(); break;
                case "--metrics-directory" when metrics is null: metrics = Value(); break;
                default: throw new ArgumentException("MCP_HOST_ARGUMENT_INVALID");
            }
            string Value()
            {
                if (++i >= args.Length || args[i].Length is 0 or > 4096 || args[i].StartsWith("--", StringComparison.Ordinal)
                    || args[i].Any(char.IsControl)) throw new ArgumentException("MCP_HOST_ARGUMENT_INVALID");
                return args[i];
            }
        }
        if (root is null || !Path.IsPathFullyQualified(root) || mutations && !trusted)
            throw new ArgumentException("MCP_HOST_ARGUMENT_INVALID");
        return new(root, index ?? ".sharpdeps/index.sqlite", trusted, mutations) { MetricsDirectory = metrics };
    }
}

/// <summary>Delegates to the existing CLI/Query contract; never starts its own watcher or writer.</summary>
public sealed class McpQueryAdapter(McpHostOptions host) : IAsyncDisposable
{
    private readonly PreparedExplorationHost preparation = new();
    private readonly SemaphoreSlim slots = new(4, 4);
    private static readonly JsonElement ExploreSchema = JsonDocument.Parse("""
        {"type":"object","properties":{"query":{"type":"string","maxLength":1024,"description":"A plain string, not an object. Put scope and budget fields beside query in one flat arguments object. Names, exact identities, or source paths by default; exact saved HttpGet text in literal mode."},"queryKind":{"type":"string","enum":["symbol","http-get-literal"],"default":"symbol"},"projectId":{"type":"string","maxLength":128},"variantId":{"type":"string","maxLength":128},"scopePolicy":{"type":"string","enum":["discover","unique","prefer-debug"],"description":"When omitted, ordinary unscoped names/paths use discover; explicit project or variant uses unique. Exact saved IDs, pathTo and http-get-literal use unique and reject discover. Discover returns separate saved project/variant groups; prefer-debug chooses only a single saved Debug variant within one project."},"pathPrefix":{"type":"string","maxLength":512},"pathTo":{"type":"string","minLength":1,"maxLength":128,"description":"Exact target occurrence ID for one retained call witness in symbol mode; query must be one exact source occurrence ID."},"freshness":{"type":"string","enum":["allow-stale","require-fresh","refresh"]},"ownerStatus":{"type":"boolean","default":false,"description":"Observe an existing coordinator; status is not freshness proof."},"format":{"type":"string","enum":["agent","audit"],"default":"agent","description":"Agent is concise; audit includes full saved identities and provenance."},"maxChars":{"type":"integer","minimum":2048,"maximum":1000000},"maxBytes":{"type":"integer","minimum":2048,"maximum":4000000},"prepareMaxMilliseconds":{"type":"integer","default":30000,"minimum":10,"maximum":30000,"description":"split-v1: finite preparation budget for full saved-index validation and standard derived/lexical indexes. Omitted: 30000 ms. Does not extend maxMilliseconds."},"maxMilliseconds":{"type":"integer","default":1000,"minimum":10,"maximum":30000,"description":"split-v1: finite query budget after preparation. Omitted: 1000 ms; zero is invalid. The total request upper bound is prepareMaxMilliseconds plus maxMilliseconds. Defaults are not latency guarantees; keep frozen evaluation budgets unchanged."}},"required":["query"],"anyOf":[{"properties":{"queryKind":{"const":"http-get-literal"}},"required":["queryKind"],"not":{"required":["pathTo"]}},{"properties":{"queryKind":{"const":"symbol"},"query":{"minLength":1}}}],"examples":[{"query":"Service","format":"agent","prepareMaxMilliseconds":30000,"maxMilliseconds":1000}],"additionalProperties":false}
        """).RootElement.Clone();
    private static readonly JsonElement MutationSchema = JsonDocument.Parse("""
        {"type":"object","properties":{"arguments":{"type":"array","items":{"type":"string","maxLength":8192},"maxItems":192,"description":"CLI indexing flags and values. Omit root/index/trusted/quiet: the host fixes those."}},"additionalProperties":false}
        """).RootElement.Clone();

    public IList<Tool> ListTools()
    {
        var tools = new List<Tool> { MakeTool("explore",
            "Use for indexed .NET symbols, dependencies and cross-file implementation paths. Send flat arguments, for example {\"query\":\"Service\",\"prepareMaxMilliseconds\":30000,\"maxMilliseconds\":1000}; query is a string, not an object. Budget contract split-v1 separates finite preparation (prepareMaxMilliseconds, default 30000 ms) from query (maxMilliseconds, default 1000 ms); both accept 10..30000, not zero. Total request time is bounded by their sum. Preparation includes full validation and standard derived/lexical indexes. Do not enlarge a frozen budget or retry without authorization. Start with short names or paths; keep saved project/variant groups separate and follow exact returned selectors. Returns bounded source, certainty, freshness and omissions; source needs host trust. Use normal read/search for simple text or out-of-scope work and respect user opt-out.",
            ExploreSchema, readOnly: true) };
        if (host.AllowMutations && host.Trusted)
            foreach (var name in new[] { "index", "update" })
                tools.Add(MakeTool(name, "Explicit trusted CLI extraction; arguments: [--target, root-contained.csproj|.sln|.slnx]. May execute MSBuild/generators. Uses the existing lease/IPC owner.", MutationSchema, readOnly: false));
        return tools;
    }

    private static Tool MakeTool(string name, string description, JsonElement schema, bool readOnly) => new()
    {
        Name = name, Description = description, InputSchema = schema,
        Annotations = new() { ReadOnlyHint = readOnly, DestructiveHint = !readOnly, OpenWorldHint = !readOnly }
    };

    public async ValueTask<CallToolResult> CallAsync(CallToolRequestParams request, CancellationToken token = default)
    {
        var mutation = request.Name is "index" or "update";
        if (request.Name != "explore" && !(host.Trusted && host.AllowMutations && mutation))
            throw new McpProtocolException("MCP_TOOL_UNKNOWN", McpErrorCode.InvalidParams);
        List<string>? arguments = null;
        ExploreRequest? exploration = null;
        if (mutation)
        {
            arguments = [request.Name, "--root", host.Root, "--index", host.Index, "--quiet"];
            if (host.Trusted) arguments.Add("--trusted");
            AddMutationArguments(request, arguments);
            if (host.MetricsDirectory is { } directory)
                arguments.AddRange(["--metrics-directory", directory]);
        }
        else exploration = ParseExploreRequest(request);
        if (!await slots.WaitAsync(0, token))
            throw new McpProtocolException("MCP_BUSY", McpErrorCode.InternalError);
        try
        {
            if (mutation)
            {
                var (exit, json) = await CliMutationProcess.RunAsync(arguments!.ToArray(), token);
                using var document = JsonDocument.Parse(json);
                return new()
                {
                    Content = [new TextContentBlock { Text = json }],
                    StructuredContent = document.RootElement.Clone(), IsError = exit != 0
                };
            }
            var explored = await CliApplication.ExploreWithHostAsync(host.Root, host.Index, host.Trusted, exploration!,
                host.MetricsDirectory, preparation, token: token);
            return new() { Content = [new TextContentBlock { Text = explored.Text }], IsError = explored.ExitCode != 0 };
        }
        finally { slots.Release(); }
    }

    private static ExploreRequest ParseExploreRequest(CallToolRequestParams request)
    {
        if (request.Arguments is not { } properties || !properties.ContainsKey("query")) throw Invalid();
        string? query = null, project = null, variant = null, pathPrefix = null, pathTo = null,
            freshness = null, format = null, scopePolicy = null;
        int? maxChars = null, maxBytes = null, maxMilliseconds = null, prepareMaxMilliseconds = null;
        var observeOwner = false;
        try
        {
            var queryKind = ExploreQueryKind.Symbol;
            if (properties.TryGetValue("queryKind", out var kind))
            {
                if (kind.ValueKind != JsonValueKind.String || kind.GetString() is not { } text) throw Invalid();
                queryKind = ExploreRequestFactory.QueryKind(text);
            }
            foreach (var (name, value) in properties)
            {
                if (name == "queryKind") continue;
                if (name == "query")
                {
                    if (value.ValueKind == JsonValueKind.Object)
                        throw new McpProtocolException(
                            """MCP_ARGUMENT_INVALID: explore arguments must be flat. query must be a string, for example {"query":"Service","prepareMaxMilliseconds":30000,"maxMilliseconds":1000}. Put scope and budget fields beside query, not inside it. Correct the arguments only if another call is authorized.""",
                            McpErrorCode.InvalidParams);
                    if (value.ValueKind != JsonValueKind.String) throw Invalid();
                    query = value.GetString();
                    ExploreRequestFactory.QueryText(query, queryKind);
                    if (queryKind == ExploreQueryKind.Symbol && query!.StartsWith("--", StringComparison.Ordinal)) throw Invalid();
                    continue;
                }
                if (name == "ownerStatus")
                {
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
                    observeOwner = value.GetBoolean();
                    continue;
                }
                if (name is "maxChars" or "maxBytes" or "maxMilliseconds" or "prepareMaxMilliseconds")
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw Invalid();
                    switch (name)
                    {
                        case "maxChars": maxChars = number; break;
                        case "maxBytes": maxBytes = number; break;
                        case "maxMilliseconds": maxMilliseconds = number; break;
                        case "prepareMaxMilliseconds": prepareMaxMilliseconds = number; break;
                    }
                    continue;
                }
                if (name is not ("projectId" or "variantId" or "pathPrefix" or "pathTo" or "freshness" or "format" or "scopePolicy")) throw Invalid();
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text
                    || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl) || text.StartsWith("--", StringComparison.Ordinal)) throw Invalid();
                ExploreRequestFactory.Text(name, text);
                switch (name)
                {
                    case "projectId": project = text; break;
                    case "variantId": variant = text; break;
                    case "pathPrefix": pathPrefix = text; break;
                    case "pathTo": pathTo = text; break;
                    case "freshness": freshness = text; break;
                    case "format": format = text; break;
                    case "scopePolicy": scopePolicy = text; break;
                }
            }
            return ExploreRequestFactory.Validate(new(query!, project, variant, pathPrefix,
                ExploreRequestFactory.Freshness(freshness),
                ExploreRequestFactory.Budget(maxMilliseconds: maxMilliseconds, maxChars: maxChars, maxBytes: maxBytes),
                ExploreRequestFactory.Format(format, ExploreFormat.Agent),
                ExploreRequestFactory.ScopePolicy(scopePolicy, query!, project, variant, pathTo, queryKind), pathTo)
                { ObserveOwner = observeOwner, QueryKind = queryKind, PrepareMaxMilliseconds = prepareMaxMilliseconds ?? 30000 });
        }
        catch (Exception error) when (error is CliFailure or JsonException or InvalidOperationException) { throw Invalid(); }
    }

    private static void AddMutationArguments(CallToolRequestParams request, List<string> arguments)
    {
        if (request.Arguments is { } properties && properties.Keys.Any(key => key != "arguments")) throw Invalid();
        if (request.Arguments?.TryGetValue("arguments", out var values) == true)
        {
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 192) throw Invalid();
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text
                    || text.Length is 0 or > 8192 || text.Any(char.IsControl)
                    || text is "--root" or "--index" or "--trusted" or "--quiet" or "--metrics-directory") throw Invalid();
                arguments.Add(text);
            }
        }
    }

    private static McpProtocolException Invalid() => new("MCP_ARGUMENT_INVALID", McpErrorCode.InvalidParams);

    public ValueTask DisposeAsync() => preparation.DisposeAsync();
}
