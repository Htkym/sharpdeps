namespace SharpDeps.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using SharpDeps.Cli;
using SharpDeps.Query;

public sealed partial class McpQueryAdapter
{
    private static IEnumerable<Tool> SymbolTools()
    {
        yield return MakeTool("find_symbols", "Find indexed declarations by name or source path, without source bodies. Returns exact selectors and separate project/variant scopes; no setup step. Use known selectors directly with read_symbols. No hit is not global absence.", SymbolSchema("find_symbols"), true);
        yield return MakeTool("read_symbols", "Read declarations for up to 8 exact symbol selectors in one explicit project/variant scope with one shared finite budget. Requires source trust. No related bodies, implicit setup or permission escalation. Reuse sufficient evidence already in context.", SymbolSchema("read_symbols"), true);
        yield return MakeTool("relations", "Read one-hop saved relations for up to 8 exact selectors in one explicit project/variant scope. No recursive expansion or source bodies. Saved edges are not runtime proof; preserve uncertainty and omissions.", SymbolSchema("relations"), true);
    }

    private static JsonElement SymbolSchema(string tool)
    {
        var properties = JsonNode.Parse("""
            {"projectId":{"type":"string","minLength":1,"maxLength":128},"variantId":{"type":"string","minLength":1,"maxLength":128},"pathPrefix":{"type":"string","minLength":1,"maxLength":512},"freshness":{"type":"string","enum":["allow-stale","require-fresh","refresh"],"default":"allow-stale"},"maxResults":{"type":"integer","minimum":1,"maximum":32,"default":16},"maxChars":{"type":"integer","minimum":2048,"maximum":1000000},"maxBytes":{"type":"integer","minimum":2048,"maximum":4000000},"maxMilliseconds":{"type":"integer","minimum":10,"maximum":30000,"default":1000},"prepareMaxMilliseconds":{"type":"integer","minimum":10,"maximum":30000,"default":30000}}
            """)!.AsObject();
        JsonArray required;
        if (tool == "find_symbols")
        {
            properties["query"] = JsonNode.Parse("""{"type":"string","minLength":1,"maxLength":1024}""");
            required = new("query");
        }
        else
        {
            properties["selectors"] = JsonNode.Parse("""{"type":"array","minItems":1,"maxItems":8,"items":{"type":"string","minLength":1,"maxLength":128,"pattern":"^hocc_"}}""");
            required = new("selectors", "projectId", "variantId");
        }
        if (tool == "relations")
        {
            properties["direction"] = JsonNode.Parse("""{"type":"string","enum":["incoming","outgoing"],"default":"outgoing"}""");
            properties["edgeKinds"] = JsonNode.Parse("""{"type":"array","minItems":1,"maxItems":9,"items":{"type":"string","enum":["calls","constructs","reads","writes","references_member","overrides","implements_explicitly","dispatch_candidates","candidate_relation"]}}""");
        }
        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false
        });
    }

    private static SymbolQueryRequest ParseSymbolRequest(CallToolRequestParams request)
    {
        if (request.Arguments is not { } properties) throw Invalid();
        var operation = request.Name switch
        {
            "find_symbols" => SymbolQueryOperation.FindSymbols,
            "read_symbols" => SymbolQueryOperation.ReadSymbols,
            "relations" => SymbolQueryOperation.Relations,
            _ => throw Invalid()
        };
        string? query = null, project = null, variant = null, path = null, freshness = null;
        string direction = "outgoing";
        IReadOnlyList<string>? selectors = null, kinds = null;
        int? maxChars = null, maxBytes = null, milliseconds = null;
        int preparationMilliseconds = 30000, maxResults = 16;
        var allowedProperties = SymbolSchema(request.Name).GetProperty("properties");
        try
        {
            foreach (var (name, value) in properties)
            {
                if (!allowedProperties.TryGetProperty(name, out _)) throw Invalid();
                if (name is "selectors" or "edgeKinds")
                {
                    var limit = name == "selectors" ? 8 : 9;
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 1 || value.GetArrayLength() > limit) throw Invalid();
                    var strings = new List<string>();
                    foreach (var item in value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text
                            || string.IsNullOrWhiteSpace(text) || text.Length > 128 || text.Any(char.IsControl)) throw Invalid();
                        strings.Add(text);
                    }
                    if (name == "selectors") selectors = strings; else kinds = strings;
                }
                else if (name is "maxChars" or "maxBytes" or "maxMilliseconds" or "prepareMaxMilliseconds" or "maxResults")
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw Invalid();
                    switch (name)
                    {
                        case "maxChars": maxChars = number; break;
                        case "maxBytes": maxBytes = number; break;
                        case "maxMilliseconds": milliseconds = number; break;
                        case "prepareMaxMilliseconds": preparationMilliseconds = number; break;
                        case "maxResults": maxResults = number; break;
                    }
                }
                else
                {
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text
                        || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl) || text.StartsWith("--", StringComparison.Ordinal)) throw Invalid();
                    ExploreRequestFactory.Text(name, text);
                    switch (name)
                    {
                        case "query": query = text; break;
                        case "projectId": project = text; break;
                        case "variantId": variant = text; break;
                        case "pathPrefix": path = text; break;
                        case "freshness": freshness = text; break;
                        case "direction": direction = text; break;
                    }
                }
            }
            return ExplorationService.NormalizeSymbols(new(operation, query, selectors, project, variant, path,
                ExploreRequestFactory.Freshness(freshness), ExploreRequestFactory.Budget(maxMilliseconds: milliseconds, maxChars: maxChars, maxBytes: maxBytes))
            { PrepareMaxMilliseconds = preparationMilliseconds, MaxResults = maxResults, Direction = direction, EdgeKinds = kinds });
        }
        catch (Exception error) when (error is ArgumentException or CliFailure or JsonException or InvalidOperationException) { throw Invalid(); }
    }
}
