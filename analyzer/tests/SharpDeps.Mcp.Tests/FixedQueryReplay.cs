namespace SharpDeps.Mcp.Tests;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Text;
using SharpDeps.Index;
using SharpDeps.Query;

internal sealed record ReplayCase(string Id, string Query, string Format = "audit", string? ScopePolicy = null,
    string? ProjectId = null, string? VariantId = null, string? PathPrefix = null,
    string Freshness = "allow-stale", int MaxChars = 12000, int MaxBytes = 48000,
    int MaxMilliseconds = 0, bool OwnerStatus = false, string Equivalence = "exact",
    string? Purpose = null);
internal sealed record ReplayInput(string Root, string CliDll, string McpDll, string RepositoryRoot,
    IReadOnlyList<ReplayCase> Cases, bool Trusted = true, string Index = ".sharpdeps/index.sqlite", bool PhaseMetrics = false);
internal sealed record ReplayUnit(string SourceId, string Path, string ContentHash, HarnessRawSpan? Span,
    IReadOnlyList<HarnessRawSpan> PossibleSpans, int FirstLine, string Text, string RenderedNumberedText, string? ProjectId,
    string? VariantId, IReadOnlyList<string> Selectors, bool WholeSavedRangeVerified,
    string? ReportedSourceId, string? ReportedContentHash);
internal sealed record ReplayObservation(string Id, string CliText, string? McpText, int CliExit,
    bool? McpIsError, string Outcome, IReadOnlyList<ReplayUnit> Units, bool Equivalent,
    int ToolResultUtf16, int ToolResultUtf8, string ReceiptPath);

internal static class FixedQueryReplay
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    internal static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<int> Main(string[] args)
    {
        if (args is [var mode, var channel, var marker]
            && (mode is "--replay-output-limit-probe" or "--replay-output-limit-probe-immediate")
            && channel is "stdout" or "stderr")
        {
            if (mode == "--replay-output-limit-probe")
            {
                await Console.In.ReadToEndAsync();
            }
            File.WriteAllText(marker, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            using var output = channel == "stdout" ? Console.OpenStandardOutput() : Console.OpenStandardError();
            var bytes = new byte[17 * 1024 * 1024];
            await output.WriteAsync(bytes);
            await output.FlushAsync();
            return 0;
        }
        if (args is ["--replay-drain-probe"])
        {
            await Console.In.ReadToEndAsync();
            using var output = Console.OpenStandardOutput();
            var bytes = new byte[2 * 1024 * 1024];
            Array.Fill(bytes, (byte)'R');
            await output.WriteAsync(bytes);
            await output.FlushAsync();
            return 0;
        }
        if (args is not ["--fixed-query-replay", var inputPath, var outputPath])
        {
            return 2;
        }
        try
        {
            var input = JsonSerializer.Deserialize<ReplayInput>(File.ReadAllText(inputPath), Json)
                ?? throw new InvalidDataException("Replay manifest is missing.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await RunAsync(input, outputPath, deadline.Token);
            return 0;
        }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync(error.ToString());
            return 1;
        }
    }

    internal static async Task<IReadOnlyList<ReplayObservation>> RunAsync(ReplayInput input, string directory,
        CancellationToken token)
    {
        if (Directory.Exists(directory))
        {
            throw new IOException("Use a new receipt directory; existing receipts are never overwritten.");
        }
        Directory.CreateDirectory(directory);
        try
        {
            return await RunCoreAsync(input, directory, token);
        }
        catch (Exception error)
        {
            WriteJson(Path.Combine(directory, "failure.json"), new { error = error.ToString() });
            WriteManifest(directory);
            throw;
        }
    }

    private static async Task<IReadOnlyList<ReplayObservation>> RunCoreAsync(ReplayInput input, string directory,
        CancellationToken token)
    {
        WriteJson(Path.Combine(directory, "input.json"), input);
        var indexPath = Path.GetFullPath(Path.Combine(input.Root, input.Index));
        using var state = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(input.Root, ".sharpdeps", "workspace.json")));
        var uuid = state.RootElement.GetProperty("workspaceUuid").GetGuid();
        IndexSnapshot snapshot;
        using (var reader = IndexReader.Open(indexPath, uuid, token))
        {
            snapshot = reader.Snapshot();
        }
        var originalIndexHash = Hash(File.ReadAllBytes(indexPath));
        var snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, QueryJson.Options);
        File.WriteAllBytes(Path.Combine(directory, "snapshot.json"), snapshotBytes);
        var git = await ReplayProcess.RunAsync(input.RepositoryRoot, "git", ["rev-parse", "HEAD", "HEAD^{tree}"], token);
        var status = await ReplayProcess.RunAsync(input.RepositoryRoot, "git", ["status", "--porcelain=v1"], token);
        Require(git.ExitCode == 0 && status.ExitCode == 0, "Repository identity could not be verified.");
        File.WriteAllBytes(Path.Combine(directory, "source-git.txt"), git.Stdout);
        File.WriteAllBytes(Path.Combine(directory, "source-status.txt"), status.Stdout);
        var sdk = await ReplayProcess.RunAsync(Environment.GetEnvironmentVariable("SHARPDEPS_REPLAY_SDK_HOME")
            ?? input.RepositoryRoot, Dotnet, ["--version"], token);
        Require(sdk.ExitCode == 0, "SDK identity could not be captured.");
        File.WriteAllBytes(Path.Combine(directory, "sdk-version.stdout"), sdk.Stdout);
        var hosts = new[] { input.CliDll, input.McpDll };
        var payloads = hosts.Select(path => new
        {
            entrypoint = Path.GetFullPath(path),
            files = Directory.GetFiles(Path.GetDirectoryName(path)!, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).Select(file => new
                {
                    path = Path.GetRelativePath(Path.GetDirectoryName(path)!, file),
                    bytes = new FileInfo(file).Length,
                    sha256 = Hash(File.ReadAllBytes(file))
                }).ToArray()
        }).ToArray();
        var requiredEngine = new[] { "SharpDeps.Cli.dll", "SharpDeps.Index.dll", "SharpDeps.Query.dll",
            "SharpDeps.Analysis.Contracts.dll", "SharpDeps.Analysis.Core.dll" };
        var sharedEngine = requiredEngine.Concat(payloads.SelectMany(payload => payload.files)
                .Select(file => file.path).Where(path => Path.GetFileName(path) == path
                    && path.StartsWith("SharpDeps.Analysis.", StringComparison.Ordinal) && path.EndsWith(".dll", StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(name =>
            {
                var cliFile = payloads[0].files.SingleOrDefault(file => file.path == name);
                var mcpFile = payloads[1].files.SingleOrDefault(file => file.path == name);
                return new
                {
                    name,
                    cliBytes = cliFile?.bytes,
                    mcpBytes = mcpFile?.bytes,
                    cliSha256 = cliFile?.sha256,
                    mcpSha256 = mcpFile?.sha256,
                    status = cliFile is null || mcpFile is null ? "missing"
                        : cliFile.bytes == mcpFile.bytes && cliFile.sha256 == mcpFile.sha256 ? "identical" : "different"
                };
            }).ToArray();
        WriteJson(Path.Combine(directory, "identity.json"), new
        {
            repositoryRoot = input.RepositoryRoot,
            commitAndTree = Utf8.GetString(git.Stdout).Trim().Split('\n'),
            dirtyStatusSha256 = Hash(status.Stdout),
            sourceIsClean = status.Stdout.Length == 0,
            snapshot = new
            {
                snapshot.Graph.WorkspaceId,
                snapshot.Graph.SnapshotId,
                snapshot.Graph.Generation,
                snapshot.Graph.Coverage,
                canonicalPayloadSha256 = Hash(snapshotBytes),
                indexPath,
                databaseSha256 = originalIndexHash
            },
            sourceManifest = snapshot.Files,
            variants = snapshot.Graph.Variants,
            workspaceStateSha256 = Hash(File.ReadAllBytes(Path.Combine(input.Root, ".sharpdeps", "workspace.json"))),
            sdk = Utf8.GetString(sdk.Stdout).Trim(),
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            processors = Environment.ProcessorCount,
            payloads,
            sharedEngine = new { status = sharedEngine.All(file => file.status == "identical") ? "identical" : "rejected", files = sharedEngine },
            settings = new[] { "global.json", "NuGet.Config", "analyzer/Directory.Build.props", "analyzer/Directory.Packages.props" }
                .Select(name => Path.Combine(input.RepositoryRoot, name)).Where(File.Exists)
                .Select(path => new { path, sha256 = Hash(File.ReadAllBytes(path)) }).ToArray(),
            limits = new[] { "No model trial, tokens, cost or additional Read behavior is measured.",
                "Per-phase timings require explicit opt-in; unreported candidate counts remain null; process CPU includes all host work.",
                "Agent spans are supplied only when uniquely proven by saved declarations; ambiguous candidates are retained.",
                "A source commit plus payload hashes identifies this replay, not an unbound historical binary or index." }
        });
        Require(sharedEngine.All(file => file.status == "identical"),
            "CLI/MCP shared engine DLLs are missing or different; identity.json retains the payload comparison. No query host was started.");
        var metricsRoot = input.PhaseMetrics ? Path.GetFullPath(Path.Combine(input.Root, ".sharpdeps", "replay-phase-" + Guid.NewGuid().ToString("N"))) : null;
        var mcpMetrics = new ReplayPhaseMetrics(metricsRoot is null ? null : Path.Combine(metricsRoot, "mcp"));
        var hostArguments = new List<string> { input.McpDll, "--root", input.Root, "--index", input.Index };
        if (mcpMetrics.DirectoryPath is { } mcpMetricsDirectory)
        {
            hostArguments.AddRange(["--metrics-directory", mcpMetricsDirectory]);
        }
        if (input.Trusted)
        {
            hostArguments.Add("--trusted");
        }
        WriteJson(Path.Combine(directory, "mcp-argv.json"), new { executable = Dotnet, arguments = hostArguments });
        await using var mcp = new ReplayProcess(input.Root, Dotnet, hostArguments);
        var sequence = 0;
        await RpcAsync("initialize", new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "sharpdeps-fixed-query-replay", version = "1" }
        }, "initialize");
        await NotifyAsync("notifications/initialized", new { }, "initialized");
        var listed = await RpcAsync("tools/list", new { }, "tools-list");
        using (var tools = JsonDocument.Parse(listed.Bytes))
        {
            Require(tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Any(tool => tool.GetProperty("name").GetString() == "explore"), "MCP explore schema was not listed.");
        }
        var observations = new List<ReplayObservation>();
        var exploreRequestOrdinal = 0;
        foreach (var item in input.Cases)
        {
            exploreRequestOrdinal++;
            Require(Regex.IsMatch(item.Id, "^[a-z0-9][a-z0-9-]{0,79}$", RegexOptions.CultureInvariant)
                && input.Cases.Count(value => value.Id == item.Id) == 1, "Case IDs must be unique safe filenames.");
            Require(item.Equivalence is "exact" or "protocol-error" or "deadline-sensitive", "Unknown equivalence rule.");
            var caseDirectory = Path.Combine(directory, item.Id);
            Directory.CreateDirectory(caseDirectory);
            var cliMetrics = new ReplayPhaseMetrics(metricsRoot is null ? null : Path.Combine(metricsRoot, item.Id, "cli"));
            var arguments = Arguments(input, item);
            if (cliMetrics.DirectoryPath is { } cliMetricsDirectory)
            {
                arguments.AddRange(["--metrics-directory", cliMetricsDirectory]);
            }
            WriteJson(Path.Combine(caseDirectory, "cli-argv.json"), new { executable = Dotnet, arguments });
            cliMetrics.BeginRequest();
            ReplayCapture cli;
            try
            {
                cli = await ReplayProcess.RunAsync(input.Root, Dotnet, arguments, token);
            }
            catch
            {
                cliMetrics.Capture(caseDirectory, "cli", token.IsCancellationRequested ? "cancelled" : "capture-aborted");
                throw;
            }
            File.WriteAllBytes(Path.Combine(caseDirectory, "cli.stdout"), cli.Stdout);
            File.WriteAllBytes(Path.Combine(caseDirectory, "cli.stderr"), cli.Stderr);
            var cliPhases = cliMetrics.Capture(caseDirectory, "cli", RequestOutcome(cli.Stdout, cli.ExitCode != 0));
            mcpMetrics.BeginRequest();
            (byte[] Bytes, double WallMilliseconds, double? CpuMilliseconds) reply;
            try
            {
                reply = await RpcAsync("tools/call", new { name = "explore", arguments = ToolArguments(item) }, item.Id + "/mcp");
            }
            catch
            {
                mcpMetrics.Capture(caseDirectory, "mcp", token.IsCancellationRequested ? "cancelled" : "capture-aborted");
                throw;
            }
            using var rpc = JsonDocument.Parse(reply.Bytes);
            var root = rpc.RootElement;
            string? mcpText = null;
            bool? isError = null;
            string? wire = null;
            ReplayPhaseCapture mcpPhases;
            try
            {
                if (root.TryGetProperty("result", out var result))
                {
                    Require(!result.TryGetProperty("structuredContent", out _), "Explore unexpectedly duplicated source in structuredContent.");
                    isError = result.GetProperty("isError").GetBoolean();
                    var content = result.GetProperty("content");
                    Require(content.GetArrayLength() == 1 && content[0].GetProperty("type").GetString() == "text", "Unexpected explore content.");
                    mcpText = content[0].GetProperty("text").GetString()!;
                    wire = result.GetRawText();
                }
                else
                {
                    Require(root.TryGetProperty("error", out _), "JSON-RPC response has neither result nor error.");
                }
                mcpPhases = mcpMetrics.Capture(caseDirectory, "mcp", root.TryGetProperty("error", out var rpcError)
                    ? rpcError.GetProperty("code").GetInt32() switch { -32602 => "rejected", -32800 => "cancelled", _ => "failed" }
                    : RequestOutcome(mcpText ?? "", isError == true));
            }
            catch
            {
                mcpMetrics.Capture(caseDirectory, "mcp", "response-invalid");
                throw;
            }
            var cliText = Utf8.GetString(cli.Stdout);
            Require(cliText.EndsWith(Environment.NewLine, StringComparison.Ordinal), "CLI omitted its transport newline.");
            cliText = cliText[..^Environment.NewLine.Length];
            var equivalent = cliText == mcpText && (cli.ExitCode != 0) == isError;
            var outcome = Outcome(mcpText ?? cliText);
            var units = ParseUnits(mcpText ?? "", snapshot, input.Root, caseDirectory);
            var cliUnits = equivalent ? units : ParseUnits(cliText, snapshot, input.Root, caseDirectory, "cli-");
            var receiptPath = Path.Combine(caseDirectory, "receipt.json");
            WriteJson(receiptPath, new
            {
                requested = item,
                effective = new
                {
                    item.Format,
                    scopePolicy = item.ScopePolicy ?? (mcpText?.StartsWith("SharpDeps discover ", StringComparison.Ordinal) == true
                        ? "discover" : mcpText?.StartsWith("SharpDeps explore ", StringComparison.Ordinal) == true ? "unique" : null),
                    item.ProjectId,
                    item.VariantId,
                    item.PathPrefix,
                    item.Freshness,
                    item.MaxChars,
                    item.MaxBytes,
                    item.MaxMilliseconds,
                    maxNodes = 60,
                    maxEdges = 120,
                    maxDepth = 2,
                    hostTrusted = input.Trusted
                },
                snapshot = new { snapshot.Graph.SnapshotId, snapshot.Graph.Generation, payloadSha256 = Hash(snapshotBytes) },
                cli = new
                {
                    cli.ExitCode,
                    cli.ProcessId,
                    processState = "fresh",
                    exploreRequestOrdinal = 1,
                    osCacheState = "unknown",
                    cli.WallMilliseconds,
                    cli.CpuMilliseconds,
                    stdoutSha256 = Hash(cli.Stdout),
                    stderrSha256 = Hash(cli.Stderr),
                    stdoutBytes = cli.Stdout.Length
                },
                mcp = new
                {
                    processId = mcp.Id,
                    processState = exploreRequestOrdinal == 1 ? "fresh" : "reused",
                    exploreRequestOrdinal,
                    osCacheState = "unknown",
                    isError,
                    rpcError = root.TryGetProperty("error", out var error) ? error : (JsonElement?)null,
                    reply.WallMilliseconds,
                    reply.CpuMilliseconds,
                    responseSha256 = Hash(reply.Bytes),
                    responseBytes = reply.Bytes.Length,
                    toolResultUtf16 = wire?.Length,
                    toolResultUtf8 = wire is null ? (int?)null : Utf8.GetByteCount(wire),
                    rpcEnvelopeAndLineDelimiterBytes = wire is null ? (int?)null : reply.Bytes.Length - Utf8.GetByteCount(wire)
                },
                resolution = new
                {
                    outcome,
                    equivalent,
                    candidateCount = (int?)null,
                    publicErrorCodes = PublicErrorCodes(mcpText ?? cliText),
                    cliPublicErrorCodes = PublicErrorCodes(cliText),
                    cliDeadlineObserved = item.MaxMilliseconds > 0 ? cliText.Contains("QUERY_TIMEOUT", StringComparison.Ordinal) : (bool?)null,
                    mcpDeadlineObserved = item.MaxMilliseconds > 0 ? mcpText?.Contains("QUERY_TIMEOUT", StringComparison.Ordinal) : null,
                    matchingScopes = CaptureNumber(mcpText, @"\((\d+) matching scopes"),
                    selectedGroups = CaptureNumber(mcpText, @"\bgroups=(\d+)"),
                    omittedGroups = CaptureNumber(mcpText, @"\bomittedGroups=(\d+)"),
                    scopeDecision = Lines(mcpText, "Scope ", "Scope policy=", "Choose project/variant", "Alternative scope"),
                    omissionsAndErrors = Lines(mcpText ?? cliText, "Error ", "Limits/status:", "Omitted saved units=", "Omission details", "Unverified:", "Dirty sources=")
                },
                source = new
                {
                    unitCount = units.Count,
                    fileCount = units.Select(unit => unit.SourceId).Distinct().Count(),
                    completeRangeCount = units.Count(unit => unit.WholeSavedRangeVerified),
                    utf16 = units.All(unit => unit.Span is not null) ? units.Sum(unit => unit.Text.Length) : (int?)null,
                    utf8 = units.All(unit => unit.Span is not null) ? units.Sum(unit => Utf8.GetByteCount(unit.Text)) : (int?)null,
                    units,
                    cliUnits
                },
                displayed = new
                {
                    utf16 = mcpText?.Length,
                    utf8 = mcpText is null ? (int?)null : Utf8.GetByteCount(mcpText),
                    sourceDefinition = "Numbered source body lines exactly as displayed, excluding source headers.",
                    sourceUtf16 = units.Sum(unit => unit.RenderedNumberedText.Length),
                    sourceUtf8 = units.Sum(unit => Utf8.GetByteCount(unit.RenderedNumberedText)),
                    nonSourceUtf16 = mcpText is null ? (int?)null : mcpText.Length - units.Sum(unit => unit.RenderedNumberedText.Length),
                    nonSourceUtf8 = mcpText is null ? (int?)null : Utf8.GetByteCount(mcpText) - units.Sum(unit => Utf8.GetByteCount(unit.RenderedNumberedText))
                },
                phaseMilliseconds = new { cli = cliPhases.PhaseMilliseconds, mcp = mcpPhases.PhaseMilliseconds },
                phaseMetrics = new
                {
                    definition = ReplayPhaseMetrics.Definition,
                    groups = ReplayPhaseMetrics.Groups,
                    cli = cliPhases,
                    mcp = mcpPhases
                },
                modelUsage = (object?)null
            });
            observations.Add(new(item.Id, cliText, mcpText, cli.ExitCode, isError, outcome, units,
                equivalent, wire?.Length ?? 0, wire is null ? 0 : Utf8.GetByteCount(wire), receiptPath));
            if (item.Equivalence == "exact")
            {
                Require(equivalent, $"CLI/MCP differ for {item.Id}; raw outputs and receipt were retained.");
            }
            if (item.Equivalence == "protocol-error")
            {
                Require(cli.ExitCode == 2 && root.TryGetProperty("error", out var protocolError)
                    && protocolError.GetProperty("code").GetInt32() == -32602, "Expected CLI argument error and MCP InvalidParams.");
            }
            if (item.Equivalence == "deadline-sensitive")
            {
                Require(item.MaxMilliseconds > 0 && (equivalent || cliText.Contains("QUERY_TIMEOUT", StringComparison.Ordinal)
                    || mcpText?.Contains("QUERY_TIMEOUT", StringComparison.Ordinal) == true), "Unexpected deadline comparison difference.");
            }
            if (wire is not null)
            {
                Require(wire.Length <= item.MaxChars && Utf8.GetByteCount(wire) <= item.MaxBytes,
                    $"Actual tool-result JSON exceeded the requested budget for {item.Id}.");
            }
        }
        var host = await mcp.FinishAsync(token);
        mcpMetrics.CaptureOutsideRequest(directory);
        File.WriteAllBytes(Path.Combine(directory, "mcp.stderr"), host.Stderr);
        File.WriteAllBytes(Path.Combine(directory, "mcp.trailing.stdout"), host.Stdout);
        Require(host.ExitCode == 0 && host.Stderr.Length == 0 && host.Stdout.Length == 0, "MCP failed or emitted unexpected trailing output.");
        Require(Hash(File.ReadAllBytes(indexPath)) == originalIndexHash, "Read-only replay changed the index database.");
        WriteJson(Path.Combine(directory, "summary.json"), new
        {
            status = "completed",
            cases = observations.Select(item => new
            { item.Id, item.Outcome, item.CliExit, item.McpIsError, item.Equivalent, unitCount = item.Units.Count }),
            mcpHost = host,
            indexUnchanged = true
        });
        WriteManifest(directory);
        return observations;

        async Task<(byte[] Bytes, double WallMilliseconds, double? CpuMilliseconds)> RpcAsync(string method, object parameters, string label)
        {
            var id = ++sequence;
            var request = Utf8.GetBytes(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }) + "\n");
            File.WriteAllBytes(Path.Combine(directory, label + ".request.json"), request);
            var clock = Stopwatch.StartNew();
            var cpu = mcp.CpuMilliseconds;
            await mcp.SendAsync(request, token);
            var bytes = await mcp.ReadLineAsync(token);
            File.WriteAllBytes(Path.Combine(directory, label + ".response.json"), bytes);
            using var response = JsonDocument.Parse(bytes);
            Require(response.RootElement.GetProperty("jsonrpc").GetString() == "2.0"
                && response.RootElement.GetProperty("id").GetInt32() == id, "Unexpected JSON-RPC stdout or response identity.");
            return (bytes, clock.Elapsed.TotalMilliseconds, mcp.CpuMilliseconds - cpu);
        }
        async Task NotifyAsync(string method, object parameters, string label)
        {
            var bytes = Utf8.GetBytes(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }) + "\n");
            File.WriteAllBytes(Path.Combine(directory, label + ".request.json"), bytes);
            await mcp.SendAsync(bytes, token);
        }
    }

    private static List<string> Arguments(ReplayInput input, ReplayCase item)
    {
        var args = new List<string> { input.CliDll, "explore", "--root", input.Root, "--index", input.Index,
            "--format", item.Format, "--freshness", item.Freshness,
            "--max-chars", item.MaxChars.ToString(CultureInfo.InvariantCulture),
            "--max-bytes", item.MaxBytes.ToString(CultureInfo.InvariantCulture),
            "--max-milliseconds", item.MaxMilliseconds.ToString(CultureInfo.InvariantCulture) };
        if (input.Trusted)
        {
            args.Add("--trusted");
        }
        foreach (var pair in new[] { ("--scope-policy", item.ScopePolicy), ("--project-id", item.ProjectId),
            ("--variant", item.VariantId), ("--path-prefix", item.PathPrefix) })
        {
            if (pair.Item2 is not null)
            {
                args.AddRange([pair.Item1, pair.Item2]);
            }
        }
        if (item.OwnerStatus)
        {
            args.Add("--owner-status");
        }
        args.AddRange(["--term", item.Query]);
        return args;
    }

    private static Dictionary<string, object> ToolArguments(ReplayCase item)
    {
        var args = new Dictionary<string, object>
        {
            ["query"] = item.Query,
            ["format"] = item.Format,
            ["freshness"] = item.Freshness,
            ["maxChars"] = item.MaxChars,
            ["maxBytes"] = item.MaxBytes,
            ["maxMilliseconds"] = item.MaxMilliseconds,
            ["ownerStatus"] = item.OwnerStatus
        };
        foreach (var pair in new[] { ("scopePolicy", item.ScopePolicy), ("projectId", item.ProjectId),
            ("variantId", item.VariantId), ("pathPrefix", item.PathPrefix) })
        {
            if (pair.Item2 is not null)
            {
                args.Add(pair.Item1, pair.Item2);
            }
        }
        return args;
    }

    internal static List<ReplayUnit> ParseUnits(string text, IndexSnapshot snapshot, string root, string directory, string artifactPrefix = "")
    {
        var units = new List<ReplayUnit>();
        foreach (var group in Regex.Split(text, @"(?m)(?=^SharpDeps explore )"))
        {
            var scope = Regex.Match(group, @"(?m)^Scope project=(\S+) variant=(\S+)");
            var project = scope.Success ? scope.Groups[1].Value : null;
            var variant = scope.Success ? scope.Groups[2].Value : null;
            var aliases = Regex.Matches(group, @"(?m)^(F\d+) (.+?)(?: source=(\S+) saved-sha256=([a-f0-9]{64}))?\r?$")
                .ToDictionary(match => match.Groups[1].Value, match => (Path: match.Groups[2].Value.TrimEnd('\r'),
                    SourceId: match.Groups[3].Success ? match.Groups[3].Value : null,
                    Hash: match.Groups[4].Success ? match.Groups[4].Value : null), StringComparer.Ordinal);
            var selectors = Regex.Matches(group, @"(?m)^(@\d+) [^\r\n]*? (?:id|selector)=(h\w+_[a-f0-9]+)")
                .GroupBy(match => match.Groups[1].Value).ToDictionary(values => values.Key, values => values.Last().Groups[2].Value);
            var lines = group.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var header = Regex.Match(lines[index].TrimEnd('\r'), @"^Source (F\d+)(?::(\d+)-(\d+))? (@[\d,@]+) (?:span=(\d+):(\d+)|path=(.*?))(?: role=.*)?$");
                if (!header.Success)
                {
                    continue;
                }
                Require(aliases.TryGetValue(header.Groups[1].Value, out var reported), "Returned source alias is missing.");
                var path = header.Groups[7].Success ? header.Groups[7].Value : reported.Path;
                Require(path == reported.Path, "Returned source path differs from its file alias.");
                var file = snapshot.Files.Single(value => value.RelativePath == path);
                if (header.Groups[5].Success)
                {
                    Require(reported.SourceId == file.SourceId && reported.Hash == file.ContentHash,
                        "Displayed audit source identity or saved hash differs from the snapshot.");
                }
                var savedCodeScope = snapshot.Graph.Variants.Any(value => value.Id == variant && value.ProjectId == project);
                var documentOnlyScope = scope.Success ? project == "unselected" && variant == "unselected"
                    : !Regex.IsMatch(group, @"(?m)^Scope (?!policy=)");
                var savedProjectScope = variant == "unselected" && snapshot.Graph.Variants.Any(value => value.ProjectId == project);
                var unitAliases = header.Groups[4].Value.Split(',');
                Require(unitAliases.All(selectors.ContainsKey), "Returned source has an unknown selector alias.");
                var unitSelectors = unitAliases.Select(alias => selectors[alias]).Distinct(StringComparer.Ordinal).ToArray();
                Require(unitSelectors.Length > 0, "Returned source has no saved selector.");
                var bytes = QueryWorkspace.IsStored(file)
                    ? Utf8.GetBytes(QueryWorkspace.IsPackage(file)
                        ? snapshot.StoredPackageBodies.Single(body => body.SourceId == file.SourceId).Body
                        : snapshot.StoredGeneratedBodies.Single(body => body.SourceId == file.SourceId).Body)
                    : new QueryWorkspace(root).Read(file);
                Require(Hash(bytes) == file.ContentHash, "Returned source does not match its saved hash.");
                var source = SourceTextEncoding.Decode(bytes);
                var rendered = new StringBuilder();
                var numbered = new StringBuilder();
                var firstLine = 0;
                while (index + 1 < lines.Length && Regex.Match(lines[index + 1], @"^(\d+)\t(.*)$") is { Success: true } body)
                {
                    var line = int.Parse(body.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (firstLine == 0)
                    {
                        firstLine = line;
                    }
                    Require(line == firstLine + rendered.ToString().Count(character => character == '\n'), "Source line numbers are not contiguous.");
                    rendered.Append(body.Groups[2].Value).Append('\n');
                    numbered.Append(lines[index + 1]).Append('\n');
                    index++;
                }
                HarnessRawSpan[] SavedSpans(string selector)
                {
                    string? ProjectOf(string nodeId)
                    {
                        for (var count = 0; count < snapshot.Graph.Nodes.Count; count++)
                        {
                            var node = snapshot.Graph.Nodes.SingleOrDefault(value => value.Id == nodeId);
                            if (node is null)
                                return null;
                            if (node.Kind == HarnessNodeKind.Project)
                                return node.Id;
                            if (node.ParentId is null)
                                return null;
                            nodeId = node.ParentId;
                        }
                        return null;
                    }
                    bool IsDocument(HarnessNode node) => node.Kind is HarnessNodeKind.Document or HarnessNodeKind.Section
                        or HarnessNodeKind.FrontMatter or HarnessNodeKind.CodeFence or HarnessNodeKind.LinkTarget or HarnessNodeKind.SymbolMention;
                    HarnessNode DocumentSection(HarnessNode node)
                    {
                        for (var count = 0; count < snapshot.Graph.Nodes.Count; count++)
                        {
                            if (node.Kind is HarnessNodeKind.Document or HarnessNodeKind.Section || node.ParentId is null)
                                return node;
                            node = snapshot.Graph.Nodes.Single(value => value.Id == node.ParentId);
                        }
                        throw new InvalidDataException("Saved document ancestry is cyclic.");
                    }
                    bool DocumentAllowed(HarnessNode node)
                    {
                        if (documentOnlyScope)
                            return true;
                        if (!savedCodeScope && !savedProjectScope)
                            return false;
                        var section = DocumentSection(node).Id;
                        return snapshot.Graph.Edges.Any(edge =>
                        {
                            if (!savedProjectScope && edge.VariantId is not null && edge.VariantId != variant)
                                return false;
                            var from = snapshot.Graph.Nodes.Single(value => value.Id == edge.SourceNodeId);
                            var to = snapshot.Graph.Nodes.Single(value => value.Id == edge.TargetNodeId);
                            HarnessNode code;
                            string? occurrenceId;
                            if (IsDocument(from) && DocumentSection(from).Id == section && !IsDocument(to))
                            {
                                code = to;
                                occurrenceId = edge.TargetOccurrenceId;
                            }
                            else if (IsDocument(to) && DocumentSection(to).Id == section && !IsDocument(from))
                            {
                                code = from;
                                occurrenceId = edge.SourceOccurrenceId;
                            }
                            else
                                return false;
                            return ProjectOf(code.Id) == project && (savedProjectScope || edge.VariantId == variant
                                || occurrenceId is not null && snapshot.Graph.SymbolOccurrences.Any(value => value.Id == occurrenceId && value.VariantId == variant));
                        });
                    }
                    var occurrences = snapshot.Graph.SymbolOccurrences.Where(occurrence =>
                        occurrence.Id == selector || occurrence.LogicalSymbolId == selector).ToArray();
                    var locations = occurrences.Length > 0
                        ? occurrences.Where(occurrence => savedCodeScope && occurrence.VariantId == variant && ProjectOf(occurrence.LogicalSymbolId) == project)
                            .SelectMany(occurrence => occurrence.Declarations
                                ?? (occurrence.Location is null ? [] : [occurrence.Location]))
                        : snapshot.Graph.Nodes.Where(node => node.Id == selector && (IsDocument(node) ? DocumentAllowed(node)
                            : savedCodeScope && ProjectOf(node.Id) == project))
                            .Select(node => node.Location).OfType<HarnessLocation>();
                    return locations.Where(location => location.SourceId == file.SourceId && location.ContentHash == file.ContentHash
                        && location.RawSpan is not null).Select(location => location.RawSpan!.Value).Distinct().ToArray();
                }
                var selectorSpans = unitSelectors.Select(SavedSpans).ToArray();
                Require(selectorSpans.All(spans => spans.Length > 0), "Returned source selector has no matching saved declaration.");
                var savedSpans = selectorSpans.SelectMany(spans => spans).Distinct().ToArray();
                HarnessRawSpan? span = header.Groups[5].Success ? new(int.Parse(header.Groups[5].Value, CultureInfo.InvariantCulture),
                    int.Parse(header.Groups[6].Value, CultureInfo.InvariantCulture)) : null;
                var candidates = span is { } exact ? new[] { exact } : savedSpans.Where(candidate =>
                    1 + source.AsSpan(0, candidate.Start).Count('\n') == firstLine
                    && RenderSource(source.Substring(candidate.Start, candidate.Length)) == rendered.ToString()).ToArray();
                if (span is null && candidates.Length == 1)
                {
                    span = candidates[0];
                }
                Require(candidates.Length > 0, "Returned source cannot be mapped to a complete saved range.");
                var expected = span is { } known ? source.Substring(known.Start, known.Length) : rendered.ToString();
                Require(RenderSource(expected) == rendered.ToString(), "Returned numbered source differs from the exact source span.");
                var whole = span is { } selected && Covers(savedSpans, selected)
                    && selectorSpans.All(spans => spans.Any(value => value.Start >= selected.Start && value.End <= selected.End));
                if (span is not null)
                {
                    Require(whole, "Returned source clipped a saved declaration boundary.");
                    Require(firstLine == 1 + source.AsSpan(0, span.Value.Start).Count('\n'), "Source first line differs from its raw UTF-16 span.");
                }
                Directory.CreateDirectory(Path.Combine(directory, "sources"));
                File.WriteAllBytes(Path.Combine(directory, "sources", file.ContentHash + ".bin"), bytes);
                File.WriteAllText(Path.Combine(directory, "sources", artifactPrefix + "unit-" + units.Count + ".txt"), expected, Utf8);
                units.Add(new(file.SourceId, path, file.ContentHash, span, candidates, firstLine, expected, numbered.ToString(), project, variant,
                    unitSelectors, whole, reported.SourceId, reported.Hash));
            }
        }
        return units;
    }

    private static bool Covers(IEnumerable<HarnessRawSpan> spans, HarnessRawSpan target)
    {
        var end = target.Start;
        foreach (var span in spans.Where(span => span.Start >= target.Start && span.End <= target.End).OrderBy(span => span.Start))
        {
            if (span.Start > end)
            {
                return false;
            }
            end = Math.Max(end, span.End);
        }
        return end == target.End;
    }

    private static string RequestOutcome(byte[] bytes, bool failed)
    {
        try
        {
            return RequestOutcome(Utf8.GetString(bytes), failed);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        {
            return "response-invalid";
        }
    }
    private static string RequestOutcome(string text, bool failed)
    {
        var errors = PublicErrorCodes(text);
        return errors.Contains("QUERY_TIMEOUT", StringComparer.Ordinal) ? "timeout"
            : errors.Contains("CANCELLED", StringComparer.Ordinal) ? "cancelled"
            : errors.Contains("ARGUMENT_INVALID", StringComparer.Ordinal) ? "rejected"
            : failed ? "failed" : "completed";
    }
    private static string RenderSource(string text) => text.EndsWith('\n') ? text : text + "\n";
    private static string[] PublicErrorCodes(string text)
    {
        if (text.TrimStart().StartsWith('{'))
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("errors", out var errors))
            {
                return errors.EnumerateArray().Select(error => error.GetProperty("code").GetString()!).ToArray();
            }
        }
        return Regex.Matches(text, @"(?m)^Error ([A-Z_]+):").Select(match => match.Groups[1].Value).ToArray();
    }
    private static string Outcome(string text)
    {
        var match = Regex.Match(text, @"(?:[Oo]utcome=)([a-z-]+)");
        return match.Success ? match.Groups[1].Value : "failed";
    }
    private static int? CaptureNumber(string? text, string pattern)
    {
        var match = Regex.Match(text ?? "", pattern);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }
    private static string[] Lines(string? text, params string[] prefixes)
        => (text ?? "").Split('\n').Select(line => line.TrimEnd('\r'))
            .Where(line => prefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal))).ToArray();
    internal static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json), Utf8);
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static void WriteManifest(string directory) => WriteJson(Path.Combine(directory, "artifact-manifest.json"),
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path) != "artifact-manifest.json")
            .Order(StringComparer.Ordinal).Select(path => new
            {
                path = Path.GetRelativePath(directory, path),
                bytes = new FileInfo(path).Length,
                sha256 = Hash(File.ReadAllBytes(path))
            }).ToArray());
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
