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
    [Theory]
    [InlineData(false, null, null)]
    [InlineData(true, null, null)]
    [InlineData(true, "agent", "prefer-debug")]
    [InlineData(true, "audit", "unique")]
    public async Task ExploreMatchesCliAndKeepsSourceTrustAndTheCommittedGeneration(bool trusted, string? format, string? scopePolicy)
    {
        using var fixture = new Fixture(addScopes: scopePolicy == "prefer-debug");
        var adapter = new McpQueryAdapter(new(fixture.Root, Trusted: trusted));
        using var stdout = new StringWriter();
        var arguments = new List<string> { "explore", "--root", fixture.Root, "--term", "Service", "--format", format ?? "agent" };
        if (trusted) arguments.Add("--trusted");
        if (scopePolicy is not null) arguments.AddRange(["--scope-policy", scopePolicy]);
        var exit = await CliApplication.RunAsync(arguments.ToArray(), stdout, TextWriter.Null);
        var result = await adapter.CallAsync(Explore("Service", format, scopePolicy));
        Assert.Equal(exit != 0, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        var cliText = stdout.ToString();
        Assert.EndsWith(stdout.NewLine, cliText, StringComparison.Ordinal);
        Assert.Equal(cliText[..^stdout.NewLine.Length], text);
        Assert.Null(result.StructuredContent);
        if (trusted)
        {
            Assert.False(result.IsError);
            Assert.Contains(Fixture.Code.TrimEnd('\n'), text, StringComparison.Ordinal);
            if (format == "audit") Assert.Contains(fixture.SourceHash, text, StringComparison.Ordinal);
            else Assert.DoesNotContain(fixture.SourceHash, text, StringComparison.Ordinal);
            var current = Explore("Service", format, scopePolicy);
            current.Arguments!["freshness"] = JsonSerializer.SerializeToElement("require-fresh");
            var unverified = await adapter.CallAsync(current);
            Assert.True(unverified.IsError);
            Assert.DoesNotContain(Fixture.Code.TrimEnd('\n'), Assert.IsType<TextContentBlock>(Assert.Single(unverified.Content)).Text, StringComparison.Ordinal);
        }
        else Assert.DoesNotContain(Fixture.Code.TrimEnd('\n'), text, StringComparison.Ordinal);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await adapter.CallAsync(Explore("Service", format, scopePolicy), cancelled.Token));
        using var reader = IndexReader.Open(fixture.IndexPath, fixture.Workspace);
        Assert.Equal(1, reader.Snapshot().Graph.Generation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualStdioSdkLifecycleReturnsOnlyRpcAndClosesOnEof(bool allowMutations)
    {
        using var fixture = new Fixture();
        var analyzer = new DirectoryInfo(AppContext.BaseDirectory);
        while (analyzer is not null && !File.Exists(Path.Combine(analyzer.FullName, "SharpDeps.Analyzer.slnx"))) analyzer = analyzer.Parent;
        Assert.NotNull(analyzer);
        var configuration = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.Name;
        var dll = Path.Combine(analyzer.FullName, "src", "SharpDeps.Mcp", "bin", configuration, "net10.0", "SharpDeps.Mcp.dll");
        Assert.True(File.Exists(dll));
        var executable = Environment.GetEnvironmentVariable("SHARPDEPS_MCP_TEST_EXECUTABLE");
        var start = new ProcessStartInfo(executable ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (executable is null) start.ArgumentList.Add(dll);
        foreach (var argument in new[] { "--root", fixture.Root, "--trusted" }) start.ArgumentList.Add(argument);
        if (allowMutations)
        {
            start.ArgumentList.Add("--allow-mutations");
            File.WriteAllText(Path.Combine(fixture.Root, "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
                """);
        }
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"sharpdeps-test","version":"1"}}}""");
            using (var ready = await Reply(1)) Assert.Equal("2025-11-25", ready.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
            await Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await Send("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            using (var listed = await Reply(2)) Assert.Equal(allowMutations ? 3 : 1, listed.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength());
            await Send("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"explore","arguments":{"query":"Service"}}}""");
            using (var query = await Reply(3))
            {
                var result = query.RootElement.GetProperty("result");
                Assert.False(result.GetProperty("isError").GetBoolean());
                Assert.False(result.TryGetProperty("structuredContent", out _));
                var source = Assert.Single(result.GetProperty("content").EnumerateArray()).GetProperty("text").GetString()!;
                Assert.Contains(Fixture.Code.TrimEnd('\n'), source, StringComparison.Ordinal);
                Assert.DoesNotContain(fixture.SourceHash, source, StringComparison.Ordinal);
            }
            await Send("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"index","arguments":{"arguments":["--target","App.csproj"]}}}""");
            using (var indexed = await Reply(4))
            {
                if (allowMutations) AssertMutation(indexed, "index");
                else Assert.Equal(-32602, indexed.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }
            if (allowMutations)
            {
                File.AppendAllText(Path.Combine(fixture.Root, "App.cs"), " public sealed class Added { }");
                await Send("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"update","arguments":{"arguments":["--target","App.csproj"]}}}""");
                using (var updated = await Reply(5)) AssertMutation(updated, "update");
                await Send("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"explore","arguments":{"query":"Added","format":"audit"}}}""");
                using var saved = await Reply(6);
                var result = saved.RootElement.GetProperty("result");
                Assert.False(result.GetProperty("isError").GetBoolean());
                Assert.False(result.TryGetProperty("structuredContent", out _));
                var source = Assert.Single(result.GetProperty("content").EnumerateArray()).GetProperty("text").GetString()!;
                Assert.Contains("public sealed class Added { }", source, StringComparison.Ordinal);
                Assert.Contains(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Root, "App.cs")))).ToLowerInvariant(), source, StringComparison.Ordinal);
                using var latest = IndexReader.Open(fixture.IndexPath, fixture.Workspace);
                Assert.Equal(3, latest.Snapshot().Graph.Generation);
            }
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
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(90));
            Assert.NotNull(line);
            var reply = JsonDocument.Parse(line);
            Assert.Equal("2.0", reply.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal(id, reply.RootElement.GetProperty("id").GetInt32());
            return reply;
        }
        static void AssertMutation(JsonDocument reply, string command)
        {
            var result = reply.RootElement.GetProperty("result");
            Assert.False(result.GetProperty("isError").GetBoolean(), result.GetRawText());
            var content = result.GetProperty("structuredContent");
            Assert.Equal(command, content.GetProperty("command").GetString());
            Assert.Empty(content.GetProperty("errors").EnumerateArray());
        }
    }

    private static CallToolRequestParams Explore(string query, string? format = null, string? scopePolicy = null)
    {
        var arguments = new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement(query) };
        if (format is not null) arguments.Add("format", JsonSerializer.SerializeToElement(format));
        if (scopePolicy is not null) arguments.Add("scopePolicy", JsonSerializer.SerializeToElement(scopePolicy));
        return new() { Name = "explore", Arguments = arguments };
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Base = OperatingSystem.IsMacOS()
            ? "/private/tmp/sharpdeps-mcp-fixtures" : Path.Combine(Path.GetTempPath(), "sharpdeps-mcp-fixtures");
        public string Root { get; } = Path.Combine(Base, Guid.NewGuid().ToString("N"));
        public Guid Workspace { get; } = Guid.NewGuid();
        public string IndexPath => Path.Combine(Root, ".sharpdeps", "index.sqlite");
        public const string Code = "public sealed class Service { public int Value => 7; }\n";
        public string SourceHash { get; } = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Code))).ToLowerInvariant();
        public string Symbol { get; }
        public Fixture(bool addScopes = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
            File.WriteAllText(Path.Combine(Root, ".sharpdeps", "workspace.json"), JsonSerializer.Serialize(
                new { schemaVersion = 1, workspaceUuid = Workspace, cursorKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
            File.WriteAllText(Path.Combine(Root, "App.cs"), Code, new System.Text.UTF8Encoding(false));
            var location = new HarnessLocation("App.cs", null, SourceHash, new(0, Code.Length - 1));
            var workspace = HarnessIdentity.WorkspaceId(Workspace);
            var project = HarnessIdentity.ProjectId(Workspace, "App.csproj");
            var variant = HarnessIdentity.VariantId(Workspace, project, "net10.0", "Debug");
            Symbol = HarnessIdentity.LogicalSymbolId(Workspace, project, "type", "T:Service");
            var variants = new List<HarnessVariant> { new(variant, project, "net10.0", "Debug", null, null, null) };
            var occurrences = new List<HarnessSymbolOccurrence>
            { new(HarnessIdentity.SymbolOccurrenceId(Workspace, Symbol, variant), Symbol, variant, location, [location]) };
            if (addScopes)
            {
                var alternate = HarnessIdentity.VariantId(Workspace, project, "net10.0", "Release");
                variants.Add(new(alternate, project, "net10.0", "Release", null, null, null));
                occurrences.Add(new(HarnessIdentity.SymbolOccurrenceId(Workspace, Symbol, alternate), Symbol, alternate, location, [location]));
            }
            var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
                HarnessGraphContract.IdentityVersion, workspace, "mcp-fixture", 1, HarnessCoverage.Partial,
                variants,
                [new(workspace, HarnessNodeKind.Workspace, "Workspace", null, null), new(project, HarnessNodeKind.Project, "App", workspace, null), new(Symbol, HarnessNodeKind.Type, "Service", project, null)],
                occurrences, [], [], null);
            using var writer = IndexWriter.Open(Root, IndexPath, Workspace, isTrusted: true);
            writer.Commit(IndexStage.Create(new IndexSnapshot(graph, [new("App.cs", "App.cs", "source", SourceHash, System.Text.Encoding.UTF8.GetByteCount(Code), Code.Length)], [], [], [new(Symbol, "Service", "Service", "", "", "App.cs")])));
        }
        public void Dispose()
        {
            var prefix = Path.GetFullPath(Base) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!Path.GetFullPath(Root).StartsWith(prefix, comparison)) throw new InvalidOperationException("Fixture cleanup escaped its dedicated root.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
