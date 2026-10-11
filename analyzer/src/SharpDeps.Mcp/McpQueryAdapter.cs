namespace SharpDeps.Mcp;

using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using SharpDeps.Cli;

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
    private static readonly Dictionary<string, string> Queries = new(StringComparer.Ordinal)
    {
        ["status"] = "Saved snapshot status; freshness is not a live verification.",
        ["search"] = "Literal saved-index search. arguments: [--term, text].",
        ["symbol"] = "Saved symbol evidence. arguments: [--id, graph-id].",
        ["callers"] = "Saved caller evidence. arguments: [--id, graph-id].",
        ["callees"] = "Saved callee evidence. arguments: [--id, graph-id].",
        ["impact"] = "Saved impact paths. arguments: [--id, graph-id]; --dependencies reverses direction.",
        ["context"] = "Budgeted saved context. arguments: [--id, graph-id]; source snippets require host trust."
    };
    private static readonly JsonElement InputSchema = JsonDocument.Parse("""
        {"type":"object","properties":{"arguments":{"type":"array","items":{"type":"string","maxLength":8192},"maxItems":192,"description":"Existing CLI flags and values for this command, including scope/freshness/budget. Omit root/index/trusted/quiet: the host fixes those."}},"additionalProperties":false}
        """).RootElement.Clone();

    public IList<Tool> ListTools()
    {
        var tools = Queries.Select(item => MakeTool(item.Key, item.Value, readOnly: true)).ToList();
        if (host.AllowMutations && host.Trusted)
            foreach (var name in new[] { "index", "update" })
                tools.Add(MakeTool(name, "Explicit trusted CLI extraction; arguments: [--target, root-contained.csproj|.sln|.slnx]. May execute MSBuild/generators. Uses the existing lease/IPC owner.", readOnly: false));
        return tools;
    }

    private static Tool MakeTool(string name, string description, bool readOnly) => new()
    {
        Name = name, Description = description, InputSchema = InputSchema,
        Annotations = new() { ReadOnlyHint = readOnly, DestructiveHint = !readOnly, OpenWorldHint = !readOnly }
    };

    public async ValueTask<CallToolResult> CallAsync(CallToolRequestParams request, CancellationToken token = default)
    {
        if (!Queries.ContainsKey(request.Name)
            && !(host.Trusted && host.AllowMutations && request.Name is "index" or "update"))
            throw new McpProtocolException("MCP_TOOL_UNKNOWN", McpErrorCode.InvalidParams);
        if (request.Arguments is { } properties && properties.Keys.Any(key => key != "arguments"))
            throw new McpProtocolException("MCP_ARGUMENT_INVALID", McpErrorCode.InvalidParams);
        var arguments = new List<string> { request.Name, "--root", host.Root, "--index", host.Index, "--quiet" };
        if (host.Trusted) arguments.Add("--trusted");
        if (request.Arguments?.TryGetValue("arguments", out var values) == true)
        {
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 192)
                throw new McpProtocolException("MCP_ARGUMENT_INVALID", McpErrorCode.InvalidParams);
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text
                    || text.Length is 0 or > 8192 || text.Any(char.IsControl)
                    || text is "--root" or "--index" or "--trusted" or "--quiet")
                    throw new McpProtocolException("MCP_ARGUMENT_INVALID", McpErrorCode.InvalidParams);
                arguments.Add(text);
            }
        }
        if (!await slots.WaitAsync(0, token))
            throw new McpProtocolException("MCP_BUSY", McpErrorCode.InternalError);
        try
        {
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
            int exit;
            string json;
            if (request.Name is "index" or "update")
                (exit, json) = await CliMutationProcess.RunAsync(arguments.ToArray(), token);
            else
            {
                exit = await CliApplication.RunAsync(arguments.ToArray(), output, diagnostics, token);
                json = output.ToString().TrimEnd();
            }
            using var document = JsonDocument.Parse(json);
            return new()
            {
                Content = [new TextContentBlock { Text = json }],
                StructuredContent = document.RootElement.Clone(), IsError = exit != 0
            };
        }
        finally { slots.Release(); }
    }
}
