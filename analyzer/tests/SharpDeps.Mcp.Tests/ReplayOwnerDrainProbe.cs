namespace SharpDeps.Mcp.Tests;

using System.Text;
using System.Text.Json;

// Test-only stdio producer: the owner cannot finish until the observer closes stdin.
internal static class ReplayOwnerDrainProbe
{
    internal const string OwnerId = "abcdef0123456789abcdef0123456789";
    private const string RequestId = "0123456789abcdef0123456789abcdef";
    private const string OwnerRunId = "fedcba9876543210fedcba9876543210";

    internal static async Task<int> RunAsync(string[] args)
    {
        var options = McpHostOptions.Parse(args);
        var sink = options.MetricsDirectory ?? throw new InvalidDataException("Probe requires phase metrics.");
        var ownerDirectory = Path.Combine(sink, "owners", OwnerId);
        Directory.CreateDirectory(ownerDirectory);
        await using var adapter = new McpQueryAdapter(options);
        var calls = 0;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var request = JsonDocument.Parse(line);
            if (!request.RootElement.TryGetProperty("id", out var id))
                continue;
            object result;
            switch (request.RootElement.GetProperty("method").GetString())
            {
                case "initialize":
                    result = new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } },
                        serverInfo = new { name = "held-owner-test-probe", version = "1" } };
                    break;
                case "tools/list":
                    result = new { tools = adapter.ListTools().Select(tool => new { name = tool.Name, inputSchema = tool.InputSchema }) };
                    break;
                case "tools/call":
                    calls++;
                    WriteTrace(Path.Combine(sink, RequestId + ".jsonl"), RequestId, "explore", terminal: true);
                    WriteTrace(Path.Combine(ownerDirectory, OwnerRunId + ".jsonl"), OwnerRunId, "explore-prepare", terminal: false);
                    result = new { isError = true, content = new[] { new { type = "text", text = "Error PREPARATION_TIMEOUT: deterministic held owner." } } };
                    break;
                default:
                    throw new InvalidDataException("Unexpected probe method.");
            }
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.GetInt32(), result }));
            await Console.Out.FlushAsync();
        }
        WriteTrace(Path.Combine(ownerDirectory, OwnerRunId + ".jsonl"), OwnerRunId, "explore-prepare", terminal: true);
        FixedQueryReplay.WriteJson(Path.Combine(options.Root, "owner-probe-shutdown.json"), new { calls, ownerCompletedAfterEof = true });
        return 0;
    }

    private static void WriteTrace(string path, string runId, string operation, bool terminal)
    {
        var payloads = new List<object>
        {
            new { type = "run.start", schema = "sharpdeps-phase-metrics/1", operation, timing = "inclusive-and-measured-child-exclusive" },
            new { type = "decision", stage = "explore.host-owner", reason = OwnerId }
        };
        if (terminal)
        {
            payloads.Add(new { type = "phase", phase = "run", spanId = 1, parentSpanId = (int?)null,
                startedMilliseconds = 0, inclusiveMilliseconds = 1, measuredChildExclusiveMilliseconds = 1,
                overlappingChildren = false, unfinishedChildren = false });
            payloads.Add(new { type = "run.end", outcome = "cancelled" });
        }
        File.WriteAllText(path, string.Join('\n', payloads.Select((payload, index)
            => JsonSerializer.Serialize(new { runId, sequence = index + 1, payload }))) + "\n", new UTF8Encoding(false));
    }
}
