namespace SharpDeps.Mcp.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Cli;
using SharpDeps.Index;
using SharpDeps.Query;
using Xunit;

public sealed class McpFlowTests
{
    [Fact]
    public async Task SavedQueriesMatchCliAndKeepTheCommittedGeneration()
    {
        using var fixture = new Fixture();
        var adapter = new McpQueryAdapter(new(fixture.Root));
        foreach (var command in new[] { "status", "search", "symbol", "callers", "callees", "impact", "context" })
        {
            string[] args = command switch { "status" => [], "search" => ["--term", "Service"], _ => ["--id", fixture.Symbol] };
            using var stdout = new StringWriter();
            Assert.Equal(0, await CliApplication.RunAsync([command, "--root", fixture.Root, .. args], stdout, TextWriter.Null));
            var result = await adapter.CallAsync(Call(command, args));
            Assert.False(result.IsError);
            Assert.Equal(stdout.ToString().TrimEnd(), Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            Assert.Equal(stdout.ToString().TrimEnd(), result.StructuredContent!.Value.GetRawText());
            Assert.Equal(1, result.StructuredContent.Value.GetProperty("snapshot").GetProperty("generation").GetInt64());
        }
        var invalid = await adapter.CallAsync(Call("search", ["--term", "Service", "--page-size", "501"]));
        Assert.True(invalid.IsError);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await adapter.CallAsync(Call("status", []), cancelled.Token));
        using var reader = IndexReader.Open(fixture.IndexPath, fixture.Workspace);
        Assert.Equal(1, reader.Snapshot().Graph.Generation);
    }

    [Fact]
    public async Task ActualStdioSdkLifecycleReturnsOnlyRpcAndClosesOnEof()
    {
        using var fixture = new Fixture();
        var analyzer = new DirectoryInfo(AppContext.BaseDirectory);
        while (analyzer is not null && !File.Exists(Path.Combine(analyzer.FullName, "SharpDeps.Analyzer.slnx"))) analyzer = analyzer.Parent;
        Assert.NotNull(analyzer);
        var configuration = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.Name;
        var dll = Path.Combine(analyzer.FullName, "src", "SharpDeps.Mcp", "bin", configuration, "net10.0", "SharpDeps.Mcp.dll");
        Assert.True(File.Exists(dll));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { dll, "--root", fixture.Root }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"sharpdeps-test","version":"1"}}}""");
            using (var ready = await Reply(1)) Assert.Equal("2025-11-25", ready.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
            await Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await Send("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            using (var listed = await Reply(2)) Assert.Equal(7, listed.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength());
            await Send("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search","arguments":{"arguments":["--term","Service"]}}}""");
            using (var query = await Reply(3))
            {
                var result = query.RootElement.GetProperty("result");
                Assert.False(result.GetProperty("isError").GetBoolean());
                Assert.Equal(1, result.GetProperty("structuredContent").GetProperty("snapshot").GetProperty("generation").GetInt64());
                Assert.Single(result.GetProperty("structuredContent").GetProperty("items").EnumerateArray());
            }
            await Send("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"index","arguments":{"arguments":["--target","App.csproj"]}}}""");
            using (var rejected = await Reply(4)) Assert.Equal(-32602, rejected.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(await stderr));
        }
        finally
        {
            // This test created this exact Process handle; a timed-out fixture must not leave its host running.
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
        async Task Send(string line) { await process.StandardInput.WriteLineAsync(line); await process.StandardInput.FlushAsync(); }
        async Task<JsonDocument> Reply(int id)
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(line);
            var reply = JsonDocument.Parse(line);
            Assert.Equal("2.0", reply.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal(id, reply.RootElement.GetProperty("id").GetInt32());
            return reply;
        }
    }

    private static CallToolRequestParams Call(string name, string[] args) => new()
    { Name = name, Arguments = new Dictionary<string, JsonElement> { ["arguments"] = JsonSerializer.SerializeToElement(args) } };

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sharpdeps-mcp-" + Guid.NewGuid().ToString("N"));
        public Guid Workspace { get; } = Guid.NewGuid();
        public string IndexPath => Path.Combine(Root, ".sharpdeps", "index.sqlite");
        public string Symbol { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
            File.WriteAllText(Path.Combine(Root, ".sharpdeps", "workspace.json"), JsonSerializer.Serialize(
                new { schemaVersion = 1, workspaceUuid = Workspace, cursorKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
            var workspace = HarnessIdentity.WorkspaceId(Workspace);
            var project = HarnessIdentity.ProjectId(Workspace, "App.csproj");
            var variant = HarnessIdentity.VariantId(Workspace, project, "net10.0", "Debug");
            Symbol = HarnessIdentity.LogicalSymbolId(Workspace, project, "type", "T:Service");
            var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
                HarnessGraphContract.IdentityVersion, workspace, "mcp-fixture", 1, HarnessCoverage.Partial,
                [new(variant, project, "net10.0", "Debug", null, null, null)],
                [new(workspace, HarnessNodeKind.Workspace, "Workspace", null, null), new(project, HarnessNodeKind.Project, "App", workspace, null), new(Symbol, HarnessNodeKind.Type, "Service", project, null)],
                [new(HarnessIdentity.SymbolOccurrenceId(Workspace, Symbol, variant), Symbol, variant, null)], [], [], null);
            using var writer = IndexWriter.Open(Root, IndexPath, Workspace, isTrusted: true);
            writer.Commit(IndexStage.Create(new IndexSnapshot(graph, [], [], [], [new(Symbol, "Service", "Service", "", "", "App.cs")])));
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
