namespace SharpDeps.Cli.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Index;
using Xunit;

public sealed class CliPromptContextTests
{
    [Theory]
    [InlineData("Explain the dependencies of `Service`", "Service")]
    [InlineData("Service.cs の呼び出し関係を調べて", "Service.cs")]
    [InlineData("Find the literal Service in Service.cs", null)]
    [InlineData("Explain dependencies without SharpDeps for `Service`", null)]
    [InlineData("Explain dependencies of the whole repository", null)]
    [InlineData("Explain dependencies of `../Service.cs`", null)]
    [InlineData("Explain dependencies of `/outside/Service.cs`", null)]
    [InlineData("Explain dependencies of ../Service.cs", null)]
    [InlineData("Explain dependencies of /outside/Service.cs", null)]
    [InlineData("Explain dependencies of C:/outside/Service.cs", null)]
    [InlineData("Explain dependencies of `One` `Two` `Three` `Four`", "One Two Three")]
    [InlineData("Disable SharpDeps and explain dependencies of `Service`", null)]
    [InlineData("Skip SharpDeps; explain dependencies of `Service`", null)]
    [InlineData("Turn off SharpDeps; explain dependencies of `Service`", null)]
    [InlineData("SharpDepsを使わずに `Service` の依存を説明", null)]
    [InlineData("SharpDepsは使用しない。`Service` の依存を説明", null)]
    [InlineData("How does DbContext call SaveChangesAsync?", "DbContext SaveChangesAsync")]
    [InlineData("DbContextからsaveChangesAsyncへの呼出し経路は？", "DbContext saveChangesAsync")]
    [InlineData("Which implementation calls saveChanges()?", "saveChanges")]
    [InlineData("Explain the Implementation and Dependencies of HTTP", null)]
    [InlineData("Explain dependencies of `/outside/DbContext` and SaveChangesAsync", null)]
    [InlineData("Explain dependencies of ../DbContext and SaveChangesAsync", null)]
    [InlineData("Disable SharpDeps; explain DbContext calls", null)]
    [InlineData("Explain implementation (details) of DbContext", "DbContext")]
    [InlineData("Explain caller/callee of DbContext", "DbContext")]
    public void EligibilityIsNarrowAndOptOutWins(string prompt, string? expected)
        => Assert.Equal(expected, CliPromptContext.SelectQuery(prompt));

    [Fact]
    public void WrappingPreservesEvidenceOrRejectsItWithoutTruncation()
    {
        const string text = "SharpDeps explore generation=1 snapshot=x coverage=Partial Freshness=Unverified\nOutcome=source source=returned range=complete-saved-declaration\nSOURCE \"text\"\nOmitted: detail\n";
        var wrapped = CliPromptContext.Wrap(text);
        Assert.NotNull(wrapped);
        using var outer = JsonDocument.Parse(wrapped);
        var hook = outer.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("UserPromptSubmit", hook.GetProperty("hookEventName").GetString());
        Assert.Equal(text, Evidence(hook.GetProperty("additionalContext").GetString()!));
        Assert.Null(CliPromptContext.Wrap(text + new string('x', CliPromptContext.OutputBytes)));
        Assert.Null(CliPromptContext.Wrap(text.Replace("source=returned", "source=unavailable")));
    }

    [Fact]
    public async Task InputBoundsAndFixedWorkspaceNeverExplore()
    {
        using var fixture = new Fixture(false);
        foreach (var input in new[] { new string('x', CliPromptContext.InputBytes + 1), "{", JsonSerializer.Serialize(new { prompt = "dependencies of `Service`", cwd = Path.GetTempPath() }) })
        {
            var result = await Run(fixture, input);
            Assert.Empty(result.Output);
            Assert.DoesNotContain("\"state\":\"attempted\"", result.Status);
        }
        var disabled = await Run(fixture, Payload(fixture), "--disabled");
        Assert.Empty(disabled.Output);
        Assert.Contains("disabled", disabled.Status);
        var missing = await Run(fixture, Payload(fixture));
        Assert.Empty(missing.Output);
        Assert.Contains("index-unavailable", missing.Status);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Payload(fixture)));
        var output = new StringWriter();
        await CliPromptContext.RunAsync(["--root", fixture.Root, "--index", fixture.IndexPath], stream, output, new StringWriter());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task InvalidConfigurationCannotBlockPromptOrEscapeRoot()
    {
        using var f = new Fixture(false);
        foreach (var args in new string[][] {
            ["--unknown"], ["--trusted"],
            ["--trusted", "--root", "relative", "--index", "index.sqlite"],
            ["--trusted", "--root", f.Root, "--index", "../outside.sqlite"],
            ["--trusted", "--root", f.Root, "--index", f.IndexPath, "--max-milliseconds", "0"] })
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(Payload(f)));
            var output = new StringWriter(); var error = new StringWriter();
            Assert.Equal(0, await CliPromptContext.RunAsync(args, input, output, error));
            Assert.Empty(output.ToString());
            Assert.Contains("\"state\":\"error\"", error.ToString());
            Assert.DoesNotContain("\"state\":\"attempted\"", error.ToString());
        }
    }

    [Fact]
    public async Task LinkedIndexIsRejectedAndInputCannotEnableTrust()
    {
        using var f = new Fixture(false);
        var target = Path.Combine(f.Root, "target.sqlite");
        File.WriteAllText(target, "sentinel");
        var link = Path.Combine(f.Root, "linked.sqlite");
        File.CreateSymbolicLink(link, target);
        try
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(Payload(f)));
            var output = new StringWriter(); var error = new StringWriter();
            Assert.Equal(0, await CliPromptContext.RunAsync(["--trusted", "--root", f.Root, "--index", link], input, output, error));
            Assert.Empty(output.ToString());
            Assert.Contains("\"state\":\"error\"", error.ToString());
            Assert.Equal("sentinel", File.ReadAllText(target));
        }
        finally { File.Delete(link); }
        var disabled = await Run(f, JsonSerializer.Serialize(new { cwd = f.Root, prompt = "dependencies of `Service`", sharpdepsDisabled = true, trusted = true }));
        Assert.Empty(disabled.Output);
        Assert.Contains("disabled", disabled.Status);
        using var untrustedInput = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { cwd = f.Root, prompt = "dependencies of `Service`", trusted = true })));
        var untrustedOutput = new StringWriter(); var untrustedError = new StringWriter();
        Assert.Equal(0, await CliPromptContext.RunAsync(["--root", f.Root, "--index", target], untrustedInput, untrustedOutput, untrustedError));
        Assert.Empty(untrustedOutput.ToString());
        Assert.Contains("source-not-authorized", untrustedError.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualSavedIndexInjectsSourceAndMissingSymbolFallsBack(bool multipleScopes)
    {
        using var fixture = new Fixture(true, multipleScopes);
        var result = await Run(fixture, Payload(fixture));
        Assert.Equal(0, result.Exit);
        Assert.Contains("\"state\":\"attempted\"", result.Status);
        Assert.Contains("\"state\":\"injected\"", result.Status);
        using var outer = JsonDocument.Parse(result.Output);
        var evidence = Evidence(outer.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!);
        Assert.Contains("public class Service", evidence);
        Assert.StartsWith("SharpDeps discover generation=", evidence, StringComparison.Ordinal);
        Assert.Contains(multipleScopes ? "groups=2 omittedGroups=0" : "groups=1 omittedGroups=0", evidence);
        Assert.Contains("Partial", evidence);
        Assert.Contains("Freshness=", evidence);
        Assert.True(Encoding.UTF8.GetByteCount(result.Output) <= CliPromptContext.OutputBytes);
        var missing = await Run(fixture, Payload(fixture, "Explain dependencies of `DoesNotExist`"));
        Assert.Empty(missing.Output);
        File.WriteAllText(fixture.IndexPath, "invalid database");
        var broken = await Run(fixture, Payload(fixture));
        Assert.Empty(broken.Output);
        Assert.Contains("exploration-failed", broken.Status);
        using var failureStatus = JsonDocument.Parse(broken.Status.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
        Assert.NotEqual(0, failureStatus.RootElement.GetProperty("productExitCode").GetInt32());
    }

    [Fact]
    public async Task DuplicateKeysAndInvalidUtf8AreRejectedBeforeExploration()
    {
        using var f = new Fixture(false);
        var duplicate = "{\"cwd\":" + JsonSerializer.Serialize(f.Root) + ",\"prompt\":\"dependencies of `Service`\",\"prompt\":\"other\"}";
        foreach (var bytes in new[] { Encoding.UTF8.GetBytes(duplicate), new byte[] { 0xc3, 0x28 } })
        {
            using var input = new MemoryStream(bytes);
            var output = new StringWriter(); var error = new StringWriter();
            Assert.Equal(0, await CliPromptContext.RunAsync(["--trusted", "--root", f.Root, "--index", f.IndexPath], input, output, error));
            Assert.Empty(output.ToString());
            Assert.DoesNotContain("\"state\":\"attempted\"", error.ToString());
            Assert.Contains("invalid", error.ToString());
        }
    }

    [Theory]
    [InlineData("10", true)]
    [InlineData("30000", true)]
    [InlineData("9", false)]
    [InlineData("30001", false)]
    public async Task BothBudgetBoundsAreValidatedBeforeMissingIndex(string budget, bool valid)
    {
        using var f = new Fixture(false);
        foreach (var option in new[] { "--prepare-max-milliseconds", "--max-milliseconds" })
        {
            var result = await Run(f, Payload(f), option, budget);
            Assert.Equal(0, result.Exit);
            Assert.Empty(result.Output);
            Assert.Contains(valid ? "index-unavailable" : "input-or-exploration-invalid", result.Status);
        }
    }

    [Fact]
    public async Task CallerCancellationWinsOverDisabledAndMissingIndex()
    {
        using var f = new Fixture(false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        foreach (var extra in new[] { Array.Empty<string>(), new[] { "--disabled" } })
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(Payload(f)));
            var output = new StringWriter(); var error = new StringWriter();
            Assert.Equal(130, await CliPromptContext.RunAsync(["--trusted", "--root", f.Root, "--index", f.IndexPath, .. extra], input, output, error, cancelled.Token));
            Assert.Empty(output.ToString());
            Assert.Contains("cancelled", error.ToString());
            Assert.DoesNotContain("\"state\":\"attempted\"", error.ToString());
        }
    }

    [Fact]
    public async Task CooperativeStdinDeadlineFallsBackWithoutBlockingPrompt()
    {
        using var f = new Fixture(false);
        using var input = new WaitingInput();
        var output = new StringWriter(); var error = new StringWriter();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.Equal(0, await CliPromptContext.RunAsync(["--trusted", "--root", f.Root, "--index", f.IndexPath], input, output, error, safety.Token));
        Assert.True(input.Cancelled);
        Assert.Empty(output.ToString());
        Assert.Contains("input-deadline", error.ToString());
    }

    private sealed class WaitingInput : MemoryStream
    {
        public bool Cancelled { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return 0;
        }
    }

    [Fact]
    public void DiscoveryRequiresSuccessfulTopLevelHeaderNotSourceImpersonation()
    {
        const string header = "SharpDeps discover generation=1 snapshot=x outcome=source-returned groups=2 omittedGroups=0\n";
        const string body = "SharpDeps explore generation=1 coverage=Partial Freshness=unverified\nOutcome=source-returned source=returned range=complete-saved-declaration\nsource text\n";
        var wrapped = CliPromptContext.Wrap(header + body);
        Assert.NotNull(wrapped);
        using var outer = JsonDocument.Parse(wrapped);
        Assert.Equal(header + body, Evidence(outer.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!));
        Assert.Null(CliPromptContext.Wrap(header.Replace("outcome=source-returned", "outcome=failed") + body + header));
        Assert.Null(CliPromptContext.Wrap(header.Replace("outcome=source-returned", "outcome=source-unavailable") + body));
        Assert.Null(CliPromptContext.Wrap("source text\n" + header + body));
        Assert.Null(CliPromptContext.Wrap(header + body + new string('x', CliPromptContext.OutputBytes)));
    }

    [Fact]
    public void CandidatePriorityDeduplicatesAndReportsTruncation()
    {
        var selected = CliPromptContext.SelectCandidates("DbContext calls saveChanges() and `Service` via src/Thing.cs; SaveChangesAsync calls saveChanges() and OtherType");
        Assert.NotNull(selected);
        Assert.Equal("Service src/Thing.cs saveChanges", selected.Query);
        Assert.Equal(3, selected.OmittedCandidates);
        const string text = "SharpDeps discover generation=1 snapshot=x outcome=source-returned groups=1 omittedGroups=0\nsource \"quoted\"\r\n第二行\n";
        using var json = JsonDocument.Parse(CliPromptContext.Wrap(text, selected.OmittedCandidates)!);
        var context = json.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        Assert.Contains("3 additional names omitted", context);
        Assert.Equal(text, Evidence(context));
        Assert.Contains("\r\n第二行\n", context);
    }

    [Fact]
    public async Task OmittedCandidateCountIsReportedWithoutPromptOrNames()
    {
        using var f = new Fixture(false);
        var result = await Run(f, Payload(f, "Explain dependencies of `One` `Two` `Three` `Four`"));
        Assert.Empty(result.Output);
        using var status = JsonDocument.Parse(result.Status);
        Assert.Equal(1, status.RootElement.GetProperty("omittedCandidates").GetInt32());
        Assert.DoesNotContain("One", result.Status);
        Assert.DoesNotContain("Four", result.Status);
    }

    private static string Evidence(string context)
    {
        const string separator = "\n--- SharpDeps saved evidence ---\n";
        var index = context.IndexOf(separator, StringComparison.Ordinal);
        Assert.True(index >= 0);
        return context[(index + separator.Length)..];
    }

    private static string Payload(Fixture f, string prompt = "Explain dependencies of `Service`") => JsonSerializer.Serialize(new { prompt, cwd = f.Root });
    private static async Task<(int Exit, string Output, string Status)> Run(Fixture f, string input, params string[] extra)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var output = new StringWriter(); var status = new StringWriter();
        var exit = await CliPromptContext.RunAsync(["--root", f.Root, "--index", f.IndexPath, "--trusted", .. extra], stream, output, status);
        return (exit, output.ToString(), status.ToString());
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Base = OperatingSystem.IsMacOS() ? "/private/tmp/sharpdeps-prompt-context" : Path.Combine(Path.GetTempPath(), "sharpdeps-prompt-context");
        public string Root { get; } = Path.Combine(Base, Guid.NewGuid().ToString("N"));
        public string IndexPath => Path.Combine(Root, ".sharpdeps/index.sqlite");
        public Fixture(bool seed, bool multipleScopes = false)
        {
            CliPaths.RejectLinks(Base);
            Directory.CreateDirectory(Root);
            if (!seed) return;
            var uuid = CliWorkspaceState.Open(Root, create: true).WorkspaceUuid;
            const string text = "public class Service {}\n";
            var bytes = Encoding.UTF8.GetBytes(text);
            File.WriteAllBytes(Path.Combine(Root, "Service.cs"), bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var project = HarnessIdentity.ProjectId(uuid, "App.csproj");
            var variant = HarnessIdentity.VariantId(uuid, project, "net10.0", "Debug");
            var workspace = HarnessIdentity.WorkspaceId(uuid);
            var symbol = HarnessIdentity.LogicalSymbolId(uuid, project, "type", "T:Service");
            var location = new HarnessLocation("src", "v1", hash, new(0, text.Length));
            var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, 1, HarnessGraphContract.IdentityVersion, workspace, "prompt-fixture", 1,
                HarnessCoverage.Partial, [new(variant, project, "net10.0", "Debug", null, null, null)],
                [new(workspace, HarnessNodeKind.Workspace, "Fixture", null, null), new(project, HarnessNodeKind.Project, "App", workspace, null),
                    new(symbol, HarnessNodeKind.Type, "Service", project, location)],
                [new(HarnessIdentity.SymbolOccurrenceId(uuid, symbol, variant), symbol, variant, location, [location])], [], [], null);
            if (multipleScopes)
            {
                var release = HarnessIdentity.VariantId(uuid, project, "net10.0", "Release");
                graph = graph with {
                    Variants = [.. graph.Variants, new(release, project, "net10.0", "Release", null, null, null)],
                    SymbolOccurrences = [.. graph.SymbolOccurrences, new(HarnessIdentity.SymbolOccurrenceId(uuid, symbol, release), symbol, release, location, [location])]
                };
            }
            var snapshot = new IndexSnapshot(graph, [new("src", "Service.cs", "code", hash, bytes.Length, text.Length)], [], [],
                [new IndexSearchText(symbol, "Service", "", "", "", "Service.cs")]);
            using var writer = IndexWriter.Open(Root, IndexPath, uuid, true);
            writer.Commit(IndexStage.Create(snapshot));
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Base) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture escaped temporary root.");
            Directory.Delete(Root, true);
        }
    }
}
