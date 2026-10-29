namespace SharpDeps.Mcp;

using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using SharpDeps.Cli;
using SharpDeps.Query;

public sealed record McpHostOptions(string Root, string Index = ".sharpdeps/index.sqlite", bool Trusted = false, bool AllowMutations = false)
{
    public static McpHostOptions Parse(string[] args)
    {
        string? root = null, index = null;
        var trusted = false; var mutations = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--trusted" when !trusted: trusted = true; break;
                case "--allow-mutations" when !mutations: mutations = true; break;
                case "--root" when root is null: root = Value(); break;
                case "--index" when index is null: index = Value(); break;
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
        return new(root, index ?? ".sharpdeps/index.sqlite", trusted, mutations);
    }
}

/// <summary>Delegates to the existing CLI/Query contract; never starts its own watcher or writer.</summary>
public sealed class McpQueryAdapter(McpHostOptions host)
{
    private readonly SemaphoreSlim slots = new(4, 4);
    private static readonly JsonElement ExploreSchema = JsonDocument.Parse("""
        {"type":"object","properties":{"query":{"type":"string","minLength":1,"maxLength":1024,"description":"Names, exact identities, or source paths to explore together."},"projectId":{"type":"string","maxLength":128},"variantId":{"type":"string","maxLength":128},"scopePolicy":{"type":"string","enum":["unique","prefer-debug"],"default":"unique","description":"Optionally prefer the single saved Debug variant within one matching project; explicit scopes take precedence."},"pathPrefix":{"type":"string","maxLength":512},"pathTo":{"type":"string","minLength":1,"maxLength":128,"description":"Exact target occurrence ID for one retained call witness; query must be one exact source occurrence ID."},"freshness":{"type":"string","enum":["allow-stale","require-fresh","refresh"]},"format":{"type":"string","enum":["agent","audit"],"default":"agent","description":"Agent is concise; audit includes full saved identities and provenance."},"maxChars":{"type":"integer","minimum":2048,"maximum":1000000},"maxBytes":{"type":"integer","minimum":2048,"maximum":4000000},"maxMilliseconds":{"type":"integer","minimum":10,"maximum":30000}},"required":["query"],"additionalProperties":false}
        """).RootElement.Clone();
    private static readonly JsonElement MutationSchema = JsonDocument.Parse("""
        {"type":"object","properties":{"arguments":{"type":"array","items":{"type":"string","maxLength":8192},"maxItems":192,"description":"CLI indexing flags and values. Omit root/index/trusted/quiet: the host fixes those."}},"additionalProperties":false}
        """).RootElement.Clone();

    public IList<Tool> ListTools()
    {
        var tools = new List<Tool> { MakeTool("explore",
            "Find related .NET implementations and reference evidence for names or paths in one request. Returns bounded source ranges, certainty, freshness and omissions; defaults to concise agent format. Set format=audit for full saved identities and provenance. Source text requires host trust.",
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
            var explored = await CliApplication.ExploreAsync(host.Root, host.Index, host.Trusted, exploration!, token: token);
            return new() { Content = [new TextContentBlock { Text = explored.Text }], IsError = explored.ExitCode != 0 };
        }
        finally { slots.Release(); }
    }

    private static ExploreRequest ParseExploreRequest(CallToolRequestParams request)
    {
        if (request.Arguments is not { } properties || !properties.ContainsKey("query")) throw Invalid();
        string? query = null, project = null, variant = null, pathPrefix = null, pathTo = null,
            freshness = null, format = null, scopePolicy = null;
        int? maxChars = null, maxBytes = null, maxMilliseconds = null;
        try
        {
            foreach (var (name, value) in properties)
            {
                if (name is "maxChars" or "maxBytes" or "maxMilliseconds")
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw Invalid();
                    switch (name)
                    {
                        case "maxChars": maxChars = number; break;
                        case "maxBytes": maxBytes = number; break;
                        case "maxMilliseconds": maxMilliseconds = number; break;
                    }
                    continue;
                }
                if (name is not ("query" or "projectId" or "variantId" or "pathPrefix" or "pathTo" or "freshness" or "format" or "scopePolicy")) throw Invalid();
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text
                    || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl) || text.StartsWith("--", StringComparison.Ordinal)) throw Invalid();
                ExploreRequestFactory.Text(name, text);
                switch (name)
                {
                    case "query": query = text; break;
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
                ExploreRequestFactory.Format(format, ExploreFormat.Agent), ExploreRequestFactory.ScopePolicy(scopePolicy), pathTo));
        }
        catch (CliFailure) { throw Invalid(); }
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
                    || text is "--root" or "--index" or "--trusted" or "--quiet") throw Invalid();
                arguments.Add(text);
            }
        }
    }

    private static McpProtocolException Invalid() => new("MCP_ARGUMENT_INVALID", McpErrorCode.InvalidParams);
}
