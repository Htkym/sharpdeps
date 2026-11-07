namespace SharpDeps.Mcp.Tests;

using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

public sealed class FixedQueryReplayTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PublicProcessesKeepWholeSourceScopesEncodingsAndWireBudgets()
    {
        using var fixture = new ReplayFixture();
        var cases = new List<ReplayCase>
        {
            new("explicit-unique", "Service", ScopePolicy: "unique", Purpose: "Preserve the explicit unique contract; zero source is legitimate."),
            new("default-discovery", "Service", Purpose: "Intentional new default behavior, not an identical historical workflow."),
            new("single-member", fixture.Member),
            new("overloads", "Service.Run", ProjectId: fixture.Project, VariantId: fixture.Variant),
            new("partial-and-huge-type", "Service", ProjectId: fixture.Project, VariantId: fixture.Variant),
            new("partial-path", "Service", ProjectId: fixture.Project, VariantId: fixture.Variant, PathPrefix: "src/Service.Partial.cs"),
            new("agent-source", fixture.Member, Format: "agent"),
            new("escaped-budget", fixture.Huge, MaxChars: 2048, MaxBytes: 2048),
            new("large-budget-retry", fixture.Huge, MaxChars: 1000000, MaxBytes: 4000000),
            new("no-match", "NoSuchSavedSymbol", ScopePolicy: "unique"),
            new("unsupported-selection", fixture.Member, ScopePolicy: "discover", Equivalence: "protocol-error",
                Purpose: "CLI argument envelope and MCP InvalidParams are intentionally different public error surfaces.")
        };
        foreach (var name in new[] { "Utf8", "Utf8Bom", "Utf16Le", "Utf16Be", "Generated", "Package" })
        {
            cases.Add(new(name.ToLowerInvariant(), name, ProjectId: fixture.Project, VariantId: fixture.Variant));
        }
        var observations = await RunAsync(fixture, "matrix", cases);
        var strict = observations.Single(item => item.Id == "explicit-unique");
        Assert.Equal(0, strict.CliExit);
        Assert.False(strict.McpIsError);
        Assert.Equal("scope-choices", strict.Outcome);
        Assert.Empty(strict.Units);
        Assert.Contains("SCOPE_AMBIGUOUS", strict.CliText, StringComparison.Ordinal);
        var discovery = observations.Single(item => item.Id == "default-discovery");
        Assert.Equal("source-returned", discovery.Outcome);
        Assert.Contains("groups=4 omittedGroups=0", discovery.CliText, StringComparison.Ordinal);
        Assert.Equal(4, discovery.Units.Select(unit => (unit.ProjectId, unit.VariantId)).Distinct().Count());
        var member = observations.Single(item => item.Id == "single-member");
        Assert.Contains(member.Units, unit => unit.Text == ReplayFixture.RunInt);
        Assert.DoesNotContain(ReplayFixture.Unrelated, member.CliText, StringComparison.Ordinal);
        var overloads = observations.Single(item => item.Id == "overloads");
        Assert.Contains(overloads.Units, unit => unit.Text == ReplayFixture.RunInt);
        Assert.Contains(overloads.Units, unit => unit.Text == ReplayFixture.RunString);
        Assert.DoesNotContain(ReplayFixture.Unrelated, overloads.CliText, StringComparison.Ordinal);
        var fallback = observations.Single(item => item.Id == "partial-and-huge-type");
        Assert.NotEmpty(fallback.Units);
        Assert.Contains("OMITTED_UNITS", fallback.CliText, StringComparison.Ordinal);
        Assert.Contains(fallback.Units, unit => unit.Path == "src/Service.Partial.cs");
        Assert.All(observations.Single(item => item.Id == "partial-path").Units,
            unit => Assert.Equal("src/Service.Partial.cs", unit.Path));
        var tight = observations.Single(item => item.Id == "escaped-budget");
        Assert.Empty(tight.Units);
        Assert.Contains(tight.Outcome, new[] { "budget-limited", "failed" });
        Assert.Contains(observations.Single(item => item.Id == "large-budget-retry").Units,
            unit => unit.Text.StartsWith("public string Huge()", StringComparison.Ordinal));
        var absent = observations.Single(item => item.Id == "no-match");
        Assert.Equal("no-match", absent.Outcome);
        Assert.Empty(absent.Units);
        var unsupported = observations.Single(item => item.Id == "unsupported-selection");
        Assert.Equal(2, unsupported.CliExit);
        Assert.Null(unsupported.McpText);
        using (var response = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(unsupported.ReceiptPath)!, "mcp.response.json"))))
        {
            Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
        foreach (var name in new[] { "utf8", "utf8bom", "utf16le", "utf16be", "generated", "package" })
        {
            var observation = observations.Single(item => item.Id == name);
            var unit = Assert.Single(observation.Units);
            Assert.Contains("日本語😀", unit.Text, StringComparison.Ordinal);
            Assert.Equal(2, unit.FirstLine);
            Assert.True(unit.WholeSavedRangeVerified);
        }
        Assert.All(observations.SelectMany(item => item.Units), unit =>
        {
            Assert.True(unit.WholeSavedRangeVerified);
            Assert.NotNull(unit.Span);
            Assert.NotEmpty(unit.Selectors);
        });
    }

    [Fact]
    public async Task PublicProcessesSeparateTrustDirtySourceAndUnprovenConfiguration()
    {
        using var fixture = new ReplayFixture();
        var untrusted = Assert.Single(await RunAsync(fixture, "untrusted", [new("untrusted", fixture.Member)], trusted: false));
        Assert.Empty(untrusted.Units);
        Assert.Contains("WORKSPACE_UNTRUSTED", untrusted.CliText, StringComparison.Ordinal);
        var freshness = Assert.Single(await RunAsync(fixture, "owner-proof", [new("owner-proof-unavailable", fixture.Member,
            Freshness: "require-fresh", OwnerStatus: true)]));
        Assert.Empty(freshness.Units);
        Assert.NotEqual(0, freshness.CliExit);
        Assert.Contains("FRESHNESS_REQUIREMENT_NOT_MET", freshness.CliText, StringComparison.Ordinal);
        Assert.Contains("INPUT_INVENTORY_UNVERIFIED", freshness.CliText, StringComparison.Ordinal);
        Assert.Contains("Owner observation state=absent", freshness.CliText, StringComparison.Ordinal);
        fixture.ChangeSource();
        var changed = Assert.Single(await RunAsync(fixture, "changed", [new("changed-source", fixture.Member)]));
        Assert.Empty(changed.Units);
        Assert.Contains("SOURCE_CHANGED_SINCE_SNAPSHOT", changed.CliText, StringComparison.Ordinal);
        Assert.DoesNotContain(ReplayFixture.RunInt, changed.CliText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicProcessesCaptureColdDeadlineAndGenerationBoundaries()
    {
        using var fixture = new ReplayFixture(noise: 5000);
        var timed = Assert.Single(await RunAsync(fixture, "deadline", [new("cold-deadline", fixture.Member,
            MaxMilliseconds: 10, Equivalence: "deadline-sensitive")]));
        var cliTimedOut = timed.CliText.Contains("QUERY_TIMEOUT", StringComparison.Ordinal);
        var mcpTimedOut = timed.McpText!.Contains("QUERY_TIMEOUT", StringComparison.Ordinal);
        foreach (var text in new[] { timed.CliText, timed.McpText })
        {
            if (text.Contains("QUERY_TIMEOUT", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(ReplayFixture.RunInt, text, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains(ReplayFixture.RunInt, text, StringComparison.Ordinal);
                Assert.DoesNotContain(ReplayFixture.Unrelated, text, StringComparison.Ordinal);
            }
        }
        output.WriteLine("Cold deadline observed: CLI={0}, MCP={1}. Non-triggered deadlines are not timeout coverage.", cliTimedOut, mcpTimedOut);
        using (var receipt = JsonDocument.Parse(File.ReadAllBytes(timed.ReceiptPath)))
        {
            var resolution = receipt.RootElement.GetProperty("resolution");
            if (cliTimedOut)
            {
                Assert.Contains("QUERY_TIMEOUT", resolution.GetProperty("cliPublicErrorCodes").EnumerateArray().Select(code => code.GetString()));
            }
            if (mcpTimedOut)
            {
                Assert.Contains("QUERY_TIMEOUT", resolution.GetProperty("publicErrorCodes").EnumerateArray().Select(code => code.GetString()));
            }
        }
        fixture.Publish(2);
        var next = Assert.Single(await RunAsync(fixture, "next-generation", [new("generation-two", fixture.Member)]));
        Assert.Contains("generation=2 snapshot=fixed-query-fixture-2", next.CliText, StringComparison.Ordinal);
        Assert.DoesNotContain("generation=1", next.CliText, StringComparison.Ordinal);
        Assert.NotEmpty(next.Units);
    }

    [Fact]
    public async Task ActualMcpCancellationIsBoundedAndDoesNotReturnUnrelatedSource()
    {
        using var fixture = new ReplayFixture(noise: 5000);
        var directory = ReceiptDirectory("cancellation");
        Directory.CreateDirectory(directory);
        var input = Input(fixture, []);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var process = new ReplayProcess(fixture.Root, FixedQueryReplay.Dotnet,
            [input.McpDll, "--root", fixture.Root, "--trusted"]);
        await Send("initialize", new { protocolVersion = "2025-11-25", capabilities = new { },
            clientInfo = new { name = "replay-cancel", version = "1" } }, 1, "initialize");
        await Read("initialize");
        await Send("notifications/initialized", new { }, null, "initialized");
        await Send("tools/call", new { name = "explore", arguments = new { query = fixture.Member, format = "audit", maxMilliseconds = 0 } }, 2, "call");
        await Send("notifications/cancelled", new { requestId = 2, reason = "fixed replay cancellation" }, null, "cancel");
        await Send("ping", new { }, 3, "ping");
        var classification = "response-suppressed-before-ping; cancellation acceptance not proven";
        var cancellationAccepted = false;
        using (var reply = JsonDocument.Parse(await Read("after-cancel")))
        {
            if (reply.RootElement.GetProperty("id").GetInt32() == 2)
            {
                Classify(reply.RootElement);
                using var ping = JsonDocument.Parse(await Read("ping"));
                Assert.Equal(3, ping.RootElement.GetProperty("id").GetInt32());
            }
            else
            {
                Assert.Equal(3, reply.RootElement.GetProperty("id").GetInt32());
            }
        }
        var finished = await process.FinishAsync(deadline.Token);
        File.WriteAllBytes(Path.Combine(directory, "mcp.stderr"), finished.Stderr);
        File.WriteAllBytes(Path.Combine(directory, "mcp.trailing.stdout"), finished.Stdout);
        foreach (var line in Encoding.UTF8.GetString(finished.Stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var trailing = JsonDocument.Parse(line);
            Assert.Equal(2, trailing.RootElement.GetProperty("id").GetInt32());
            Classify(trailing.RootElement);
        }
        FixedQueryReplay.WriteJson(Path.Combine(directory, "receipt.json"), new
        {
            notificationSent = true, cancellationAccepted, classification, hostResponsiveToPing = true,
            limitation = "CLI signal cancellation is not covered. Sending a notification does not prove that cancellation won the race.", finished
        });
        FixedQueryReplay.WriteManifest(directory);
        Assert.Equal(0, finished.ExitCode);
        output.WriteLine("Cancellation receipt: {0}", directory);

        void Classify(JsonElement reply)
        {
            if (reply.TryGetProperty("error", out var error))
            {
                Assert.Equal(-32800, error.GetProperty("code").GetInt32());
                classification = "accepted-cancellation-rpc-error";
                cancellationAccepted = true;
                return;
            }
            var text = reply.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            if (text.Contains("CANCELLED", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(ReplayFixture.RunInt, text, StringComparison.Ordinal);
                classification = "accepted-cancellation-tool-result";
                cancellationAccepted = true;
            }
            else
            {
                Assert.Contains(ReplayFixture.RunInt, text, StringComparison.Ordinal);
                Assert.DoesNotContain(ReplayFixture.Unrelated, text, StringComparison.Ordinal);
                classification = "completed-before-effective-cancellation";
            }
        }
        async Task Send(string method, object parameters, int? id, string label)
        {
            var request = id is { } value
                ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id = value, method, @params = parameters })
                : JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });
            var bytes = Encoding.UTF8.GetBytes(request + "\n");
            File.WriteAllBytes(Path.Combine(directory, label + ".request.json"), bytes);
            await process.SendAsync(bytes, deadline.Token);
        }
        async Task<byte[]> Read(string label)
        {
            var bytes = await process.ReadLineAsync(deadline.Token);
            File.WriteAllBytes(Path.Combine(directory, label + ".response.json"), bytes);
            return bytes;
        }
    }

    [Fact]
    public async Task SourceOracleRejectsIncorrectProvenanceAndUnrelatedSavedDeclarations()
    {
        using var fixture = new ReplayFixture();
        var observed = Assert.Single(await RunAsync(fixture, "provenance", [new("provenance-baseline", fixture.Member)]));
        var unit = Assert.Single(observed.Units, value => value.Selectors.Contains(fixture.Member));
        Assert.Equal(ReplayFixture.RunInt, unit.Text);
        Assert.Equal(unit.SourceId, unit.ReportedSourceId);
        Assert.Equal(unit.ContentHash, unit.ReportedContentHash);
        var text = observed.McpText!;
        var member = fixture.Snapshot.Graph.SymbolOccurrences.Single(value => value.Id == fixture.Member);
        var memberNode = fixture.Snapshot.Graph.Nodes.Single(value => value.Id == member.LogicalSymbolId);
        var otherNode = fixture.Snapshot.Graph.Nodes.Single(value => value.Name == "Unrelated" && value.ParentId == memberNode.ParentId);
        var other = fixture.Snapshot.Graph.SymbolOccurrences.Single(value => value.LogicalSymbolId == otherNode.Id && value.VariantId == fixture.Variant);
        var otherSpan = other.Declarations![0].RawSpan!.Value;
        var source = File.ReadAllText(Path.Combine(fixture.Root, unit.Path));
        var otherLine = 1 + source.AsSpan(0, otherSpan.Start).Count('\n');
        AssertRejected(text.Replace("source=" + unit.SourceId, "source=not-the-saved-source", StringComparison.Ordinal));
        AssertRejected(text.Replace("saved-sha256=" + unit.ContentHash, "saved-sha256=" + new string('0', 64), StringComparison.Ordinal));
        AssertRejected(text.Replace("id=" + fixture.Member, "id=" + other.Id, StringComparison.Ordinal));
        var otherProject = fixture.Snapshot.Graph.Variants.First(value => value.ProjectId != fixture.Project).ProjectId;
        var otherVariant = fixture.Snapshot.Graph.Variants.Single(value => value.ProjectId == fixture.Project && value.Id != fixture.Variant).Id;
        var otherProjectMember = fixture.Snapshot.Graph.SymbolOccurrences.First(value => value.VariantId != fixture.Variant
            && fixture.Snapshot.Graph.Variants.Any(variant => variant.Id == value.VariantId && variant.ProjectId == otherProject)
            && value.Declarations?.Any(location => location.SourceId == unit.SourceId && location.RawSpan == unit.Span) == true);
        AssertRejected(text.Replace("Scope project=" + fixture.Project, "Scope project=" + otherProject, StringComparison.Ordinal));
        AssertRejected(text.Replace("Scope project=" + fixture.Project + " variant=" + fixture.Variant,
            "Scope project=unselected variant=unselected", StringComparison.Ordinal));
        AssertRejected(text.Replace("variant=" + fixture.Variant, "variant=" + otherVariant, StringComparison.Ordinal));
        AssertRejected(text.Replace("id=" + fixture.Member, "id=" + otherProjectMember.Id, StringComparison.Ordinal));
        var unrelated = text.Replace($"span={unit.Span!.Value.Start}:{unit.Span.Value.Length}",
            $"span={otherSpan.Start}:{otherSpan.Length}", StringComparison.Ordinal)
            .Replace($"{unit.FirstLine}\t{unit.Text}\n", $"{otherLine}\t{ReplayFixture.Unrelated}\n", StringComparison.Ordinal);
        Assert.Contains($"{otherLine}\t{ReplayFixture.Unrelated}\n", unrelated, StringComparison.Ordinal);
        AssertRejected(unrelated);

        void AssertRejected(string changed)
        {
            Assert.NotEqual(text, changed);
            Assert.Throws<InvalidDataException>(() => FixedQueryReplay.ParseUnits(changed, fixture.Snapshot,
                fixture.Root, Path.Combine(Path.GetDirectoryName(observed.ReceiptPath)!, "rejected")));
        }
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("agent")]
    public async Task DocumentSelectorsUseDocumentScopeWithoutRelaxingCodeScope(string format)
    {
        using var fixture = new ReplayFixture(documents: true);
        var observed = await RunAsync(fixture, "document-" + format,
            [new("document-only", fixture.Document, Format: format), new("section-only", fixture.Section, Format: format),
                new("document-prefer-debug", fixture.Section, Format: format, ScopePolicy: "prefer-debug"),
                new("document-project", fixture.Section, Format: format, ProjectId: fixture.Project),
                new("code-with-document", fixture.Member, Format: format)]);
        foreach (var item in observed.Where(value => value.Id is "document-only" or "section-only" or "document-prefer-debug"))
        {
            var unit = Assert.Single(item.Units);
            Assert.Equal(ReplayFixture.Markdown, unit.Text);
            Assert.Equal(format == "audit" ? null : "unselected", unit.ProjectId);
            Assert.Equal(format == "audit" ? null : "unselected", unit.VariantId);
            Assert.True(unit.WholeSavedRangeVerified);
        }
        var preferred = observed.Single(value => value.Id == "document-prefer-debug");
        Assert.Contains("decision=no-code-scope", preferred.McpText!, StringComparison.Ordinal);
        var projectOnly = observed.Single(value => value.Id == "document-project");
        var projectDocument = Assert.Single(projectOnly.Units);
        Assert.Equal(fixture.Project, projectDocument.ProjectId);
        Assert.Equal("unselected", projectDocument.VariantId);
        Assert.Equal(ReplayFixture.Markdown, projectDocument.Text);
        Assert.True(projectDocument.WholeSavedRangeVerified);
        var otherProject = fixture.Snapshot.Graph.Variants.First(value => value.ProjectId != fixture.Project).ProjectId;
        var spoofedProject = projectOnly.McpText!.Replace("Scope project=" + fixture.Project,
            "Scope project=" + otherProject, StringComparison.Ordinal);
        Assert.NotEqual(projectOnly.McpText, spoofedProject);
        Assert.Throws<InvalidDataException>(() => FixedQueryReplay.ParseUnits(spoofedProject, fixture.Snapshot,
            fixture.Root, Path.Combine(Path.GetDirectoryName(projectOnly.ReceiptPath)!, "scope-spoof-rejected")));
        var linked = observed.Single(value => value.Id == "code-with-document");
        Assert.Contains(linked.Units, unit => unit.Selectors.Contains(fixture.Member));
        var document = Assert.Single(linked.Units, unit => unit.Selectors.Contains(fixture.Section));
        Assert.Equal(fixture.Project, document.ProjectId);
        Assert.Equal(fixture.Variant, document.VariantId);
        Assert.Equal(ReplayFixture.Markdown, document.Text);
        var unlinked = fixture.Snapshot with { Graph = fixture.Snapshot.Graph with { Edges = [] } };
        Assert.Throws<InvalidDataException>(() => FixedQueryReplay.ParseUnits(linked.McpText!, unlinked,
            fixture.Root, Path.Combine(Path.GetDirectoryName(linked.ReceiptPath)!, "unlinked-rejected")));
    }

    [Theory]
    [InlineData("stdout", "run")]
    [InlineData("stderr", "run")]
    [InlineData("stdout", "finish")]
    [InlineData("stderr", "finish")]
    [InlineData("stdout", "readline")]
    [InlineData("stderr", "readline")]
    public async Task OutputLimitFaultReclaimsChildWithoutWaitingForOuterDeadline(string channel, string waitMode)
    {
        var directory = ReceiptDirectory("output-limit-" + channel);
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "child.pid");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string[] arguments = [typeof(FixedQueryReplay).Assembly.Location,
            waitMode == "readline" ? "--replay-output-limit-probe-immediate" : "--replay-output-limit-probe", channel, marker];
        if (waitMode == "run")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => ReplayProcess.RunAsync(directory,
                FixedQueryReplay.Dotnet, arguments, deadline.Token));
        }
        else
        {
            await using (var process = new ReplayProcess(directory, FixedQueryReplay.Dotnet, arguments))
            {
                if (waitMode == "finish")
                {
                    await Assert.ThrowsAsync<InvalidDataException>(() => process.FinishAsync(deadline.Token));
                }
                else
                {
                    await Assert.ThrowsAsync<InvalidDataException>(() => process.ReadLineAsync(deadline.Token));
                }
            }
        }
        Assert.False(deadline.IsCancellationRequested);
        var processId = int.Parse(File.ReadAllText(marker), System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var child = System.Diagnostics.Process.GetProcessById(processId);
            Assert.True(child.HasExited);
        }
        catch (ArgumentException)
        {
            // The owned process has already been reaped by the operating system.
        }
        FixedQueryReplay.WriteJson(Path.Combine(directory, "receipt.json"), new
        {
            channel, waitMode, processId, childReclaimed = true, captureLimitBytes = 16 * 1024 * 1024,
            attemptedBytes = 17 * 1024 * 1024, wallMilliseconds = clock.Elapsed.TotalMilliseconds,
            outerDeadlineReached = deadline.IsCancellationRequested
        });
        FixedQueryReplay.WriteManifest(directory);
        output.WriteLine("Output limit receipt: {0}", directory);
    }

    [Fact]
    public async Task ProcessExitDrainsTrailingStdoutBeforeWaiting()
    {
        var directory = ReceiptDirectory("drain-probe");
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var process = new ReplayProcess(directory, FixedQueryReplay.Dotnet,
            [typeof(FixedQueryReplay).Assembly.Location, "--replay-drain-probe"]);
        var capture = await process.FinishAsync(deadline.Token);
        Assert.Equal(0, capture.ExitCode);
        Assert.Equal(2 * 1024 * 1024, capture.Stdout.Length);
        Assert.All(capture.Stdout, value => Assert.Equal((byte)'R', value));
        Assert.Empty(capture.Stderr);
        File.WriteAllBytes(Path.Combine(directory, "probe.stdout"), capture.Stdout);
        FixedQueryReplay.WriteJson(Path.Combine(directory, "receipt.json"), new
        {
            capture.ExitCode, stdoutBytes = capture.Stdout.Length,
            stdoutSha256 = FixedQueryReplay.Hash(capture.Stdout),
            capture.WallMilliseconds, capture.CpuMilliseconds,
            requirement = "A child writing trailing stdout larger than a pipe buffer must exit without waiting for the outer deadline."
        });
        FixedQueryReplay.WriteManifest(directory);
        output.WriteLine("Drain probe receipt: {0}", directory);
    }

    private async Task<IReadOnlyList<ReplayObservation>> RunAsync(ReplayFixture fixture, string label,
        IReadOnlyList<ReplayCase> cases, bool trusted = true)
    {
        var directory = ReceiptDirectory(label);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var result = await FixedQueryReplay.RunAsync(Input(fixture, cases, trusted), directory, deadline.Token);
        output.WriteLine("Replay receipts: {0}", directory);
        return result;
    }

    private static string ReceiptDirectory(string label)
        => Path.Combine(Environment.GetEnvironmentVariable("SHARPDEPS_REPLAY_ARTIFACT_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "sharpdeps-fixed-query-receipts"), label + "-" + Guid.NewGuid().ToString("N"));

    private static ReplayInput Input(ReplayFixture fixture, IReadOnlyList<ReplayCase> cases, bool trusted = true)
    {
        var analyzer = new DirectoryInfo(AppContext.BaseDirectory);
        while (analyzer is not null && !File.Exists(Path.Combine(analyzer.FullName, "SharpDeps.Analyzer.slnx")))
        {
            analyzer = analyzer.Parent;
        }
        Assert.NotNull(analyzer);
        var configuration = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.Name;
        var cli = Path.Combine(analyzer.FullName, "src", "SharpDeps.Cli", "bin", configuration, "net10.0", "SharpDeps.Cli.dll");
        var mcp = Path.Combine(analyzer.FullName, "src", "SharpDeps.Mcp", "bin", configuration, "net10.0", "SharpDeps.Mcp.dll");
        Assert.True(File.Exists(cli), cli);
        Assert.True(File.Exists(mcp), mcp);
        return new(fixture.Root, cli, mcp, analyzer.Parent!.FullName, cases, trusted);
    }
}
