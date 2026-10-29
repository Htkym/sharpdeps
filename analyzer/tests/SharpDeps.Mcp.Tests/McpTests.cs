namespace SharpDeps.Mcp.Tests;

using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

public sealed class McpTests
{
    [Fact]
    public async Task HostCapabilityCannotBeEscalatedByToolArguments()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sharpdeps-mcp-not-created-" + Guid.NewGuid().ToString("N")));
        var adapter = new McpQueryAdapter(new(root));
        var tool = Assert.Single(adapter.ListTools());
        Assert.Equal("explore", tool.Name);
        Assert.Equal("query", tool.InputSchema.GetProperty("required")[0].GetString());
        Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("agent", tool.InputSchema.GetProperty("properties").GetProperty("format").GetProperty("default").GetString());
        Assert.Equal("unique", tool.InputSchema.GetProperty("properties").GetProperty("scopePolicy").GetProperty("default").GetString());
        Assert.Equal("boolean", tool.InputSchema.GetProperty("properties").GetProperty("ownerStatus").GetProperty("type").GetString());
        Assert.False(tool.InputSchema.GetProperty("properties").GetProperty("ownerStatus").GetProperty("default").GetBoolean());
        Assert.Throws<ArgumentException>(() => McpHostOptions.Parse(["--root", root, "--allow-mutations"]));
        await Assert.ThrowsAsync<McpProtocolException>(async () => await adapter.CallAsync(new() { Name = "index" }));
        await Assert.ThrowsAsync<McpProtocolException>(async () => await adapter.CallAsync(new()
        {
            Name = "explore", Arguments = new Dictionary<string, JsonElement>
            { ["arguments"] = JsonSerializer.SerializeToElement(new[] { "--root", root, "--term", "Service" }) }
        }));
        await Assert.ThrowsAsync<McpProtocolException>(async () => await adapter.CallAsync(new()
        {
            Name = "explore", Arguments = new Dictionary<string, JsonElement> { ["trusted"] = JsonSerializer.SerializeToElement(true) }
        }));
        Assert.False(Directory.Exists(root));
        Assert.Equal(3, new McpQueryAdapter(new(root, Trusted: true, AllowMutations: true)).ListTools().Count);
    }

    [Fact]
    public async Task TypedExploreArgumentsAreValidatedBeforeOpeningTheWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "sharpdeps-mcp-not-created-" + Guid.NewGuid().ToString("N"));
        var adapter = new McpQueryAdapter(new(root));
        foreach (var json in new[] {
            "{}", "{\"query\":\"\"}", "{\"query\":null}", "{\"query\":\"--trusted\"}",
            "{\"query\":\"Service\\n\"}",
            "{\"query\":\"Service\",\"root\":\"elsewhere\"}",
            "{\"query\":\"Service\",\"Format\":\"agent\"}",
            "{\"query\":\"Service\",\"maxChars\":2047}",
            "{\"query\":\"Service\",\"maxChars\":2048.5}",
            "{\"query\":\"Service\",\"maxChars\":null}",
            "{\"query\":\"Service\",\"maxBytes\":4000001}",
            "{\"query\":\"Service\",\"maxBytes\":null}",
            "{\"query\":\"Service\",\"maxMilliseconds\":\"1000\"}",
            "{\"query\":\"Service\",\"maxMilliseconds\":null}",
            "{\"query\":\"Service\",\"maxNodes\":1}",
            "{\"query\":\"Service\",\"freshness\":\"current\"}",
            "{\"query\":\"Service\",\"format\":\"verbose\"}",
            "{\"query\":\"Service\",\"format\":\"Agent\"}",
            "{\"query\":\"Service\",\"format\":1}",
            "{\"query\":\"Service\",\"format\":null}",
            "{\"query\":\"Service\",\"scopePolicy\":\"auto\"}",
            "{\"query\":\"Service\",\"scopePolicy\":\"PreferDebug\"}",
            "{\"query\":\"Service\",\"scopePolicy\":true}",
            "{\"query\":\"Service\",\"scopePolicy\":null}",
            "{\"query\":\"Service\",\"ownerStatus\":null}",
            "{\"query\":\"Service\",\"ownerStatus\":\"true\"}",
            "{\"query\":\"Service\",\"ownerStatus\":1}",
            "{\"query\":\"Service\",\"pathTo\":true}",
            "{\"query\":\"Service\",\"pathTo\":null}",
            "{\"query\":\"Service\",\"pathTo\":\" \"}",
            JsonSerializer.Serialize(new { query = "Service", pathTo = new string('x', 129) }) })
        {
            var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
            var error = await Assert.ThrowsAsync<McpProtocolException>(async () => await adapter.CallAsync(new()
            { Name = "explore", Arguments = properties }));
            Assert.Equal("MCP_ARGUMENT_INVALID", error.Message);
        }
        await Assert.ThrowsAsync<McpProtocolException>(async () => await adapter.CallAsync(new() { Name = "search" }));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task StdioInputBoundsBytesAndRejectsIncompleteUtf8BeforeJsonAllocation()
    {
        var buffer = new byte[4096];
        using var input = new BoundedMcpInput(new MemoryStream(Encoding.UTF8.GetBytes(new string('a', BoundedMcpInput.MaximumMessageBytes + 1))));
        await Assert.ThrowsAsync<IOException>(async () => { while (await input.ReadAsync(buffer.AsMemory()) > 0) { } });
        using var invalid = new BoundedMcpInput(new MemoryStream([0xe3, 0x81]));
        await Assert.ThrowsAsync<IOException>(async () => { while (await invalid.ReadAsync(buffer.AsMemory()) > 0) { } });
        using var lines = new BoundedMcpInput(new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("日本語\n", 10000)))));
        while (await lines.ReadAsync(buffer.AsMemory()) > 0) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroLengthReadsPreservePendingUtf8(bool asynchronous)
    {
        var expected = Encoding.UTF8.GetBytes("\u65e5\n");
        var actual = new byte[expected.Length];
        using var input = new BoundedMcpInput(new MemoryStream(expected));
        if (asynchronous)
        {
            Assert.Equal(1, await input.ReadAsync(actual.AsMemory(0, 1)));
            Assert.Equal(0, await input.ReadAsync(Memory<byte>.Empty));
            Assert.Equal(expected.Length - 1, await input.ReadAsync(actual.AsMemory(1)));
            Assert.Equal(0, await input.ReadAsync(actual.AsMemory()));
        }
        else
        {
            Assert.Equal(1, input.Read(actual, 0, 1));
            Assert.Equal(0, input.Read(actual, 1, 0));
            Assert.Equal(expected.Length - 1, input.Read(actual, 1, actual.Length - 1));
            Assert.Equal(0, input.Read(actual, 0, actual.Length));
        }
        Assert.Equal(expected, actual);
    }
}
