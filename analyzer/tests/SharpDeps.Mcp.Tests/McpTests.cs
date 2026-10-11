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
        Assert.Equal(7, adapter.ListTools().Count);
        Assert.Throws<ArgumentException>(() => McpHostOptions.Parse(["--root", root, "--allow-mutations"]));
        await Assert.ThrowsAsync<McpProtocolException>(async () => await adapter.CallAsync(new() { Name = "index" }));
        await Assert.ThrowsAsync<McpProtocolException>(async () => await adapter.CallAsync(new()
        {
            Name = "search", Arguments = new Dictionary<string, JsonElement>
            { ["arguments"] = JsonSerializer.SerializeToElement(new[] { "--root", root, "--term", "Service" }) }
        }));
        await Assert.ThrowsAsync<McpProtocolException>(async () => await adapter.CallAsync(new()
        {
            Name = "status", Arguments = new Dictionary<string, JsonElement> { ["trusted"] = JsonSerializer.SerializeToElement(true) }
        }));
        Assert.False(Directory.Exists(root));
        Assert.Equal(9, new McpQueryAdapter(new(root, Trusted: true, AllowMutations: true)).ListTools().Count);
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
}
