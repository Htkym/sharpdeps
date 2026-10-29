namespace SharpDeps.Mcp.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
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
        var code = Fixture.Code.TrimEnd('\n') + " // " + string.Concat(Enumerable.Repeat("検証😀 \" \\", 80)) + "\r\n";
        using var fixture = new Fixture(addScopes: scopePolicy == "prefer-debug", code: code);
        var adapter = new McpQueryAdapter(new(fixture.Root, Trusted: trusted));
        var arguments = new List<string> { "explore", "--root", fixture.Root, "--term", "Service", "--format", format ?? "agent" };
        if (trusted) arguments.Add("--trusted");
        if (scopePolicy is not null) arguments.AddRange(["--scope-policy", scopePolicy]);
        var (exit, text, result) = await AssertEquivalent(adapter, Explore("Service", format, scopePolicy), arguments);
        Assert.Equal(0, exit);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        AssertWireBudget(result, 12000, 48000);
        Assert.DoesNotContain("Owner observation ", text, StringComparison.Ordinal);
        if (scopePolicy is null) Assert.DoesNotContain("Scope policy=", text, StringComparison.Ordinal);
        if (trusted)
        {
            Assert.Contains(fixture.SavedText, text, StringComparison.Ordinal);
            if (format == "audit") Assert.Contains(fixture.SourceHash, text, StringComparison.Ordinal);
            else Assert.DoesNotContain(fixture.SourceHash, text, StringComparison.Ordinal);
            var current = Explore("Service", format, scopePolicy);
            current.Arguments!["freshness"] = JsonSerializer.SerializeToElement("require-fresh");
            var (currentExit, currentText, _) = await AssertEquivalent(adapter, current,
                [.. arguments, "--freshness", "require-fresh"]);
            Assert.NotEqual(0, currentExit);
            Assert.DoesNotContain(fixture.SavedText, currentText, StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(fixture.Root, "App.cs"), code.Replace("=> 7", "=> 8", StringComparison.Ordinal), new UTF8Encoding(false));
            try
            {
                var (_, changedText, _) = await AssertEquivalent(adapter, Explore("Service", format, scopePolicy), arguments);
                Assert.Contains("SOURCE_CHANGED_SINCE_SNAPSHOT", changedText, StringComparison.Ordinal);
                Assert.DoesNotContain(fixture.SavedText, changedText, StringComparison.Ordinal);
            }
            finally { File.WriteAllText(Path.Combine(fixture.Root, "App.cs"), code, new UTF8Encoding(false)); }
        }
        else Assert.DoesNotContain(fixture.SavedText, text, StringComparison.Ordinal);

        var pathArguments = new List<string>(arguments);
        pathArguments[pathArguments.IndexOf("--term") + 1] = fixture.Occurrence;
        pathArguments.AddRange(["--project-id", fixture.Project, "--variant", fixture.Variant, "--path-prefix", "App.cs",
            "--path-to", fixture.Occurrence, "--freshness", "allow-stale", "--max-chars", "10000", "--max-bytes", "40000", "--max-milliseconds", "1500", "--owner-status"]);
        var pathRequest = Explore(fixture.Occurrence, format, scopePolicy, fixture.Occurrence);
        foreach (var (name, value) in new[] { ("projectId", fixture.Project), ("variantId", fixture.Variant),
            ("pathPrefix", "App.cs"), ("freshness", "allow-stale") })
            pathRequest.Arguments![name] = JsonSerializer.SerializeToElement(value);
        pathRequest.Arguments!["maxChars"] = JsonSerializer.SerializeToElement(10000);
        pathRequest.Arguments["maxBytes"] = JsonSerializer.SerializeToElement(40000);
        pathRequest.Arguments["maxMilliseconds"] = JsonSerializer.SerializeToElement(1500);
        pathRequest.Arguments["ownerStatus"] = JsonSerializer.SerializeToElement(true);
        var (pathExit, pathText, pathResult) = await AssertEquivalent(adapter, pathRequest, pathArguments);
        Assert.Equal(0, pathExit);
        Assert.Contains($"Retained path status=found source={fixture.Occurrence} target={fixture.Occurrence} hops=0 shown=0/0", pathText, StringComparison.Ordinal);
        const string absentOwner = "Owner observation state=absent generation=unknown pin=unknown revision=unknown desired=unknown; status only, not freshness proof.";
        Assert.Contains(absentOwner, pathText, StringComparison.Ordinal);
        Assert.Contains("Freshness=unverified", pathText, StringComparison.Ordinal);
        if (!trusted) Assert.DoesNotContain(fixture.SavedText, pathText, StringComparison.Ordinal);
        AssertWireBudget(pathResult, 10000, 40000);

        if (format is not null)
        {
            var missingArguments = new List<string>(arguments);
            missingArguments[missingArguments.IndexOf("--term") + 1] = "NoSuchSavedSymbol";
            var (missingExit, missingText, _) = await AssertEquivalent(adapter, Explore("NoSuchSavedSymbol", format, scopePolicy), missingArguments);
            Assert.Equal(2, missingExit);
            Assert.Contains("SYMBOL_NOT_FOUND", missingText, StringComparison.Ordinal);
        }
        if (format == "audit")
        {
            var defaultArguments = new List<string>(arguments);
            defaultArguments.RemoveRange(defaultArguments.IndexOf("--format"), 2);
            var defaultRequest = Explore("Service", "audit", scopePolicy);
            defaultRequest.Arguments!["ownerStatus"] = JsonSerializer.SerializeToElement(false);
            var (defaultExit, defaultText, _) = await AssertEquivalent(adapter, defaultRequest, defaultArguments);
            Assert.Equal(0, defaultExit);
            Assert.Equal(text, defaultText);

            var missingIndex = new McpQueryAdapter(new(fixture.Root, Index: "missing.sqlite", Trusted: trusted));
            var (hostExit, hostText, _) = await AssertEquivalent(missingIndex, Explore("Service", format, scopePolicy),
                [.. arguments, "--index", "missing.sqlite"]);
            Assert.Equal(3, hostExit);
            using var hostError = JsonDocument.Parse(hostText);
            Assert.Equal("1", hostError.RootElement.GetProperty("apiVersion").GetString());
            Assert.Equal("explore", hostError.RootElement.GetProperty("command").GetString());
            Assert.Equal("cli", hostError.RootElement.GetProperty("requestId").GetString());
            var hostIssue = Assert.Single(hostError.RootElement.GetProperty("errors").EnumerateArray());
            Assert.Equal("INDEX_MISSING", hostIssue.GetProperty("code").GetString());
            Assert.Equal("See CLI contract for the recovery action.", hostIssue.GetProperty("message").GetString());
            Assert.DoesNotContain(fixture.Root, hostText, StringComparison.Ordinal);

            var invalidPathArguments = new List<string>(arguments);
            invalidPathArguments[invalidPathArguments.IndexOf("--term") + 1] = fixture.Occurrence;
            invalidPathArguments.AddRange(["--path-to", "Service"]);
            var (invalidPathExit, invalidPathText, _) = await AssertEquivalent(adapter,
                Explore(fixture.Occurrence, format, scopePolicy, "Service"), invalidPathArguments);
            Assert.Equal(2, invalidPathExit);
            using var invalidPath = JsonDocument.Parse(invalidPathText);
            Assert.Equal("ARGUMENT_INVALID", invalidPath.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
            var (precedenceExit, precedenceText, _) = await AssertEquivalent(missingIndex,
                Explore(fixture.Occurrence, format, scopePolicy, "Service"), [.. invalidPathArguments, "--index", "missing.sqlite"]);
            Assert.Equal(3, precedenceExit);
            using var precedence = JsonDocument.Parse(precedenceText);
            Assert.Equal("INDEX_MISSING", precedence.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());

            var refresh = Explore("Service", format, scopePolicy);
            refresh.Arguments!["freshness"] = JsonSerializer.SerializeToElement("refresh");
            var (refreshExit, refreshText, _) = await AssertEquivalent(adapter, refresh, [.. arguments, "--freshness", "refresh"]);
            Assert.Equal(3, refreshExit);
            Assert.Contains("UPDATE_REQUIRED", refreshText, StringComparison.Ordinal);
            var tight = Explore("Service", format, scopePolicy);
            tight.Arguments!["maxChars"] = JsonSerializer.SerializeToElement(2048);
            tight.Arguments["maxBytes"] = JsonSerializer.SerializeToElement(2048);
            tight.Arguments["ownerStatus"] = JsonSerializer.SerializeToElement(true);
            var fullWire = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);
            Assert.True(fullWire.Length > 2048 || Encoding.UTF8.GetByteCount(fullWire) > 2048);
            var (tightExit, tightText, tightResult) = await AssertEquivalent(adapter, tight,
                [.. arguments, "--max-chars", "2048", "--max-bytes", "2048", "--owner-status"]);
            Assert.True(tightExit == 0 || tightExit == 4 && tightText.Contains("OUTPUT_BUDGET_TOO_SMALL", StringComparison.Ordinal));
            AssertWireBudget(tightResult, 2048, 2048);
            if (tightExit == 0) Assert.Contains(absentOwner, tightText, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.SavedText, tightText, StringComparison.Ordinal);
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await adapter.CallAsync(Explore("Service", format, scopePolicy), cancelled.Token));
        using var reader = IndexReader.Open(fixture.IndexPath, fixture.Workspace);
        Assert.Equal(1, reader.Snapshot().Graph.Generation);
    }

    private static async Task<(int Exit, string Text, CallToolResult Result)> AssertEquivalent(
        McpQueryAdapter adapter, CallToolRequestParams request, IReadOnlyList<string> arguments)
    {
        using var output = new StringWriter();
        var exit = await CliApplication.RunAsync(arguments.ToArray(), output, TextWriter.Null);
        var result = await adapter.CallAsync(request);
        Assert.Equal(exit != 0, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Null(result.StructuredContent);
        var cliText = output.ToString();
        Assert.EndsWith(output.NewLine, cliText, StringComparison.Ordinal);
        Assert.Equal(cliText[..^output.NewLine.Length], text);
        return (exit, text, result);
    }

    private static void AssertWireBudget(CallToolResult result, int maxChars, int maxBytes)
    {
        var wire = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);
        Assert.InRange(wire.Length, 0, maxChars);
        Assert.InRange(Encoding.UTF8.GetByteCount(wire), 0, maxBytes);
        using var encoded = JsonDocument.Parse(wire);
        Assert.False(encoded.RootElement.TryGetProperty("structuredContent", out _));
        Assert.Equal(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text,
            Assert.Single(encoded.RootElement.GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
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

    private static CallToolRequestParams Explore(string query, string? format = null, string? scopePolicy = null, string? pathTo = null)
    {
        var arguments = new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement(query) };
        if (format is not null) arguments.Add("format", JsonSerializer.SerializeToElement(format));
        if (scopePolicy is not null) arguments.Add("scopePolicy", JsonSerializer.SerializeToElement(scopePolicy));
        if (pathTo is not null) arguments.Add("pathTo", JsonSerializer.SerializeToElement(pathTo));
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
        public string SavedText { get; }
        public string SourceHash { get; }
        public string Project => HarnessIdentity.ProjectId(Workspace, "App.csproj");
        public string Variant => HarnessIdentity.VariantId(Workspace, Project, "net10.0", "Debug");
        public string Symbol { get; }
        public string Occurrence { get; }
        public Fixture(bool addScopes = false, string? code = null)
        {
            code ??= Code;
            SavedText = code.TrimEnd('\r', '\n');
            SourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();
            Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
            File.WriteAllText(Path.Combine(Root, ".sharpdeps", "workspace.json"), JsonSerializer.Serialize(
                new { schemaVersion = 1, workspaceUuid = Workspace, cursorKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
            File.WriteAllText(Path.Combine(Root, "App.cs"), code, new UTF8Encoding(false));
            var location = new HarnessLocation("App.cs", null, SourceHash, new(0, SavedText.Length));
            var workspace = HarnessIdentity.WorkspaceId(Workspace);
            var project = HarnessIdentity.ProjectId(Workspace, "App.csproj");
            var variant = HarnessIdentity.VariantId(Workspace, project, "net10.0", "Debug");
            Symbol = HarnessIdentity.LogicalSymbolId(Workspace, project, "type", "T:Service");
            Occurrence = HarnessIdentity.SymbolOccurrenceId(Workspace, Symbol, variant);
            var variants = new List<HarnessVariant> { new(variant, project, "net10.0", "Debug", null, null, null) };
            var occurrences = new List<HarnessSymbolOccurrence>
            { new(Occurrence, Symbol, variant, location, [location]) };
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
            writer.Commit(IndexStage.Create(new IndexSnapshot(graph, [new("App.cs", "App.cs", "source", SourceHash, Encoding.UTF8.GetByteCount(code), code.Length)], [], [], [new(Symbol, "Service", "Service", "", "", "App.cs")])));
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
