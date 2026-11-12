namespace SharpDeps.Cli.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDeps.Index;
using SharpDeps.Query;

internal sealed record OracleFileMutation(string RelativePath, string Text)
{
    public bool PackageCache
    {
        get; init;
    }
    public byte[]? Bytes
    {
        get; init;
    }
    public bool PreserveWriteTime
    {
        get; init;
    }
    public long? LastWriteTimeUtcTicks
    {
        get; init;
    }
}
internal sealed record OracleRun(CliIndexOptions Options, IndexSnapshot? Previous, string[] Terms)
{
    public OracleFileMutation? BeforeCommit
    {
        get; init;
    }
    public string? ExactSdkVersion
    {
        get; init;
    }
}
internal sealed record OracleObservation(IndexSnapshot Snapshot, Dictionary<string, IndexSearchHit[]> Fts,
    Dictionary<string, QueryEnvelope> Queries, int ProcessId);

internal static class OracleProcess
{
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadCleanupTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ExitCleanupTimeout = TimeSpan.FromSeconds(3);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunMainAsync(args);
        }
        catch (Exception error) when (args is ["--full-oracle", _, _] && error is CliIndexException or IndexStoreException)
        {
            var code = error is CliIndexException cli ? cli.Code : ((IndexStoreException)error).Code;
            File.WriteAllText(Path.ChangeExtension(args[2], "failure.json"), JsonSerializer.Serialize(new
            {
                Code = code,
                ExceptionType = error.GetType().FullName,
                error.Message
            }, QueryJson.Options));
            throw;
        }
    }

    private static void RegisterExactOracleSdk(OracleRun run)
    {
        if (run.ExactSdkVersion is not { } version)
            return;
        if (version != "10.0.403")
            throw new InvalidDataException("oracle-sdk-request-unsupported: " + version);
        var expected = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
            "..", "..", "..", "sdk", version));
        if (!Directory.Exists(expected))
            throw new InvalidDataException("oracle-sdk-unavailable: " + expected);
        var pin = CliPaths.InRoot(run.Options.Root, "global.json");
        var text = JsonSerializer.Serialize(new
        {
            sdk = new
            {
                version,
                rollForward = "disable"
            }
        });
        using (var stream = new FileStream(pin, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(text);
            stream.Write(bytes);
        }
        try
        {
            if (!SharpDeps.Analysis.Roslyn.SemanticEnvironment.TryRegister(run.Options.Root, out var reason))
                throw new InvalidDataException("oracle-sdk-registration-failed: " + reason);
            var actual = SharpDeps.Analysis.Roslyn.SemanticEnvironment.Describe(run.Options.Root).MsBuildPath;
            if (actual is null || !CliPaths.SamePath(actual, expected))
                throw new InvalidDataException("oracle-sdk-selection-mismatch: actual=" + (actual ?? "<null>") + "; expected=" + expected);
        }
        finally
        {
            if (File.Exists(pin))
            {
                if (File.ReadAllText(pin) != text)
                    throw new InvalidDataException("oracle-sdk-pin-changed: " + pin);
                File.Delete(pin);
            }
        }
    }
    private static async Task<int> RunMainAsync(string[] args)
    {
        if (args is ["--oracle-owned-stop-leaf", var exitMode] && exitMode is "exit0" or "exit7")
        {
            await Console.Out.WriteAsync("leaf stdout");
            await Console.Error.WriteAsync("leaf stderr");
            return exitMode == "exit0" ? 0 : 7;
        }
        if (args is ["--oracle-owned-stop-canary", var canaryMarker])
        {
            File.WriteAllText(canaryMarker + ".ready", "ready");
            await Task.Delay(TimeSpan.FromSeconds(60));
            File.WriteAllText(canaryMarker + ".natural-exit", "exit0");
            return 0;
        }
        if (args is ["--oracle-owned-stop-requester", var stopMode, var stopMarker])
        {
            var binding = OracleOwnedStop.RequireBinding(Environment.GetEnvironmentVariable(OracleOwnedStop.PathKey),
                Environment.GetEnvironmentVariable(OracleOwnedStop.NonceKey));
            File.WriteAllText(stopMarker + ".before", "ready");
            if (stopMode is "validstop" or "cancelstop" or "precancelstop" or "normal-launch" or "ordinary-reject")
            {
                bool positive = stopMode is "normal-launch" or "ordinary-reject";
                string[] childArguments = positive
                    ? [typeof(OracleProcess).Assembly.Location, "--oracle-owned-stop-leaf", stopMode == "normal-launch" ? "exit0" : "exit7"]
                    : [typeof(OracleProcess).Assembly.Location,
                        stopMode == "validstop" ? "--oracle-exit-with-pipes" : "--oracle-wait", stopMarker + ".child"];
                var childStart = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
                {
                    WorkingDirectory = Path.GetDirectoryName(stopMarker)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                var inherited = childStart.Environment.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                var planned = OracleChildEnvironmentPlanner.Create(inherited, OracleChildCommand.ManagedDll);
                childStart.Environment.Clear();
                foreach (var pair in planned)
                    childStart.Environment[pair.Key] = pair.Value;
                foreach (string argument in childArguments)
                    childStart.ArgumentList.Add(argument);
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(stopMode == "cancelstop" ? 3 : 6));
                if (stopMode == "precancelstop")
                    stop.Cancel();
                try
                {
                    bool rejected = false;
                    try
                    {
                        await ExecuteOwnedAsync(childStart, childArguments, stop.Token, binding);
                    }
                    catch (InvalidOperationException error) when (stopMode == "ordinary-reject"
                        && error.Message.StartsWith("Oracle process exited 7:\n", StringComparison.Ordinal))
                    {
                        rejected = true;
                    }
                    if (positive)
                    {
                        if (stopMode == "ordinary-reject" && !rejected)
                            throw new InvalidOperationException("Expected ordinary terminal/drained exit7 rejection.");
                        File.WriteAllText(stopMarker + (rejected ? ".ordinary-rejected" : ".normal-return"), "terminal-and-drained");
                        return 0;
                    }
                }
                finally
                {
                    if (!positive)
                        File.WriteAllText(stopMarker + ".fixture-restore", "unsafe unwind");
                }
                File.WriteAllText(stopMarker + ".after-request", "unsafe return");
                return 3;
            }
            switch (stopMode)
            {
                case "normal":
                    return 0;
                // Validation-induced publisher failure exercises the same catch-and-wait path without changing ACLs.
                case "publishfailure":
                    await OracleOwnedStop.RequestAndWaitAsync(binding, "invalid-reason");
                    break;
                case "invalidrequest":
                    string stale = binding.Nonce == new string('A', 64) ? new string('B', 64) : new string('A', 64);
                    await OracleOwnedStop.RequestAndWaitAsync(binding with
                    {
                        Nonce = stale
                    }, "child-uncertain");
                    break;
                default:
                    return 2;
            }
            File.WriteAllText(stopMarker + ".after-request", "unsafe return");
            return 3;
        }
        if (args is ["--oracle-hold-pipes", var heldMarker])
        {
            using var outputPipe = Console.OpenStandardOutput();
            using var errorPipe = Console.OpenStandardError();
            await outputPipe.WriteAsync("held stdout"u8.ToArray());
            await errorPipe.WriteAsync("held stderr"u8.ToArray());
            File.WriteAllText(heldMarker + ".ready", "ready");
            // Fail-safe if the test runner itself dies before its finally can reclaim this process.
            await Task.Delay(TimeSpan.FromSeconds(30));
            return 0;
        }
        if (args is ["--oracle-exit-with-pipes", var inheritedMarker])
        {
            var childStart = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // Force explicit standard handles on Windows; stdout/stderr deliberately remain inherited.
                RedirectStandardInput = true
            };
            foreach (var argument in new[] { typeof(OracleProcess).Assembly.Location, "--oracle-hold-pipes", inheritedMarker })
                childStart.ArgumentList.Add(argument);
            using var child = Process.Start(childStart) ?? throw new InvalidOperationException("Pipe holder did not start.");
            File.WriteAllText(inheritedMarker + ".tmp", JsonSerializer.Serialize(new[] { Environment.ProcessId, child.Id }));
            File.Move(inheritedMarker + ".tmp", inheritedMarker);
            child.StandardInput.Close();
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                while (!File.Exists(inheritedMarker + ".ready"))
                {
                    if (child.HasExited)
                        throw new InvalidOperationException("Pipe holder exited before readiness.");
                    await Task.Delay(20, ready.Token);
                }
            }
            catch
            {
                if (!child.HasExited)
                    child.Kill();
                await WaitForExitBoundedAsync(child);
                throw;
            }
            return 0;
        }
        if (args is ["--oracle-wait", var marker])
        {
            await Console.Out.WriteAsync(new string('x', 100_000));
            await Console.Error.WriteAsync(new string('x', 100_000));
            File.WriteAllText(marker + ".tmp", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(marker + ".tmp", marker);
            await Task.Delay(Timeout.Infinite);
        }
        // CoreFoundation can add this startup metadata after the parent filters the child environment.
        // Keep the controlled fixtures closed; subsequent production proof checks remain unchanged.
        if (OperatingSystem.IsMacOS()
            && (args is ["--semantic-encoding-oracle", _] or ["--full-oracle", _, _]))
            Environment.SetEnvironmentVariable("__CF_USER_TEXT_ENCODING", null);
        if (args is ["--semantic-encoding-oracle", var encodingRoot])
            return await SemanticEncodingCliTests.RunFixtureAsync(encodingRoot);
        if (args is not ["--full-oracle", var input, var output])
            return 2;
        var run = JsonSerializer.Deserialize<OracleRun>(File.ReadAllText(input), QueryJson.Options)
            ?? throw new InvalidDataException("Oracle request is missing.");
        RegisterExactOracleSdk(run);
        var sequence = 0;
        Guid Allocate() => new(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{run.Options.WorkspaceUuid:N}:{run.Previous?.Graph.Generation ?? 0}:{++sequence}"))[..16]);
        var before = Environment.GetEnvironmentVariables();
        var admissions = new List<CliProofAdmission>();
        var build = await CliIndexer.BuildForPublicationAsync(run.Options, run.Previous, default, null,
            run.Previous is null ? null : Allocate, admissions.Add);
        WriteProofDiagnostic(Path.ChangeExtension(output, "proof-diagnostic.json"), run.Options.Root, before, admissions);
        if (run.BeforeCommit is { } mutation)
        {
            var mutationRoot = run.Options.Root;
            if (mutation.PackageCache)
            {
                mutationRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, "packages");
                if (!CliPaths.SamePath(mutationRoot, Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? ""))
                    throw new InvalidDataException("Oracle package mutation must stay in its private package cache.");
            }
            var target = CliPaths.InRoot(mutationRoot, mutation.RelativePath);
            var writeTime = mutation.PreserveWriteTime ? File.GetLastWriteTimeUtc(target) : (DateTime?)null;
            if (mutation.Bytes is { } bytes)
                File.WriteAllBytes(target, bytes);
            else
                File.WriteAllText(target, mutation.Text, new UTF8Encoding(false, true));
            if (mutation.LastWriteTimeUtcTicks is { } ticks)
                File.SetLastWriteTimeUtc(target, new DateTime(ticks, DateTimeKind.Utc));
            else if (writeTime is { } originalTime)
                File.SetLastWriteTimeUtc(target, originalTime);
        }
        using (var writer = IndexWriter.Open(run.Options.Root, run.Options.IndexPath, run.Options.WorkspaceUuid, true))
            writer.Commit(IndexStage.Create(build.Snapshot), verifySelection: build.VerifySelection);
        File.WriteAllText(output, JsonSerializer.Serialize(Observe(run.Options.IndexPath, run.Options.WorkspaceUuid, run.Terms), QueryJson.Options));
        return 0;
    }

    internal static OracleObservation Observe(string indexPath, Guid workspaceUuid, string[] terms)
    {
        using var reader = IndexReader.Open(indexPath, workspaceUuid);
        var query = new QueryService(new PinnedQueryIndex(reader));
        var budget = new QueryBudget(500, 1000, 4, 30000, 1_000_000, 4_000_000);
        var fts = new Dictionary<string, IndexSearchHit[]>(StringComparer.Ordinal);
        var queries = new Dictionary<string, QueryEnvelope>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            fts.Add(term, reader.Search(term, 500).ToArray());
            queries.Add("search:" + term, query.Execute(new("oracle", QueryKind.Search, Term: term,
                PageSize: 500, Budget: budget)).Envelope);
        }
        foreach (var node in reader.Snapshot().Graph.Nodes.Where(n => n.Kind is
            SharpDeps.Analysis.Contracts.Harness.HarnessNodeKind.Type or
            SharpDeps.Analysis.Contracts.Harness.HarnessNodeKind.Member))
            queries.Add("symbol:" + node.Id, query.Execute(new("oracle", QueryKind.Symbol, NodeId: node.Id,
                IncludeCandidates: true, PageSize: 500, Budget: budget)).Envelope);
        return new(reader.Snapshot(), fts, queries, Environment.ProcessId);
    }

    internal static void WriteProofDiagnostic(string path, string root, System.Collections.IDictionary before,
        IReadOnlyList<CliProofAdmission> admissions)
    {
        var sdk = SharpDeps.Analysis.Roslyn.SemanticEnvironment.Describe(root).MsBuildPath;
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var dotnet = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        object Check(string method, params object?[] args)
        {
            try
            {
                var member = typeof(CliReuseProof).GetMethod(method,
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
                var result = member.Invoke(null, args);
                return method == "NormalizeEnvironment" ? result is not null : result!;
            }
            catch (Exception error)
            {
                return new
                {
                    exceptionType = error.GetBaseException().GetType().Name
                };
            }
        }
        string HostState(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is null)
                return "unset";
            var expected = name == "DOTNET_HOST_PATH"
                ? Path.Combine(dotnet, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet") : dotnet;
            return Path.IsPathFullyQualified(value)
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)), Path.TrimEndingDirectorySeparator(expected), comparison)
                ? "expected" : "mismatched";
        }
        var after = Environment.GetEnvironmentVariables();
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            sdkPath = sdk,
            runtimeDirectory = runtime,
            sdkInsideRuntimeRoot = sdk is not null && string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(sdk)),
                Path.Combine(dotnet, "sdk"), comparison),
            normalizedBefore = sdk is null ? false : Check("NormalizeEnvironment", before, sdk),
            normalizedAfter = sdk is null ? false : Check("NormalizeEnvironment", after, sdk),
            environmentBefore = DescribeProofEnvironment(before, sdk),
            environmentAfter = DescribeProofEnvironment(after, sdk),
            userHooks = Check("HasUserHooks"),
            ancestorConfiguration = Check("HasAncestorConfiguration", root),
            hostEnvironment = new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", "DOTNET_HOST_PATH" }
                .ToDictionary(name => name, HostState),
            admissions
        }, QueryJson.Options));
    }

    internal static object DescribeProofEnvironment(System.Collections.IDictionary environment, string? sdk)
    {
        const string cf = "__CF_USER_TEXT_ENCODING";
        var known = new HashSet<string>((HashSet<string>)typeof(CliReuseProof).GetField("PlainEnvironment",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!, StringComparer.OrdinalIgnoreCase);
        var registered = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBUILD_EXE_PATH"] = sdk is null ? null : Path.Combine(sdk, "MSBuild.dll"),
            ["MSBuildSDKsPath"] = sdk is null ? null : Path.Combine(sdk, "Sdks"),
            ["MSBuildExtensionsPath"] = sdk
        };
        known.UnionWith(registered.Keys);
        known.UnionWith(["MSBUILDDISABLENODEREUSE", "MSBuildEnableWorkloadResolver", "DOTNET_ROOT",
            "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", "DOTNET_HOST_PATH"]);
        var withoutCf = new System.Collections.Hashtable(StringComparer.Ordinal);
        string? Value(string name) => environment.Cast<System.Collections.DictionaryEntry>()
            .FirstOrDefault(entry => entry.Key is string key && key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value as string;
        foreach (System.Collections.DictionaryEntry entry in environment)
            if (entry.Key is not string name || !name.Equals(cf, StringComparison.OrdinalIgnoreCase))
                withoutCf.Add(entry.Key, entry.Value);
        object Normalize(System.Collections.IDictionary values)
        {
            if (sdk is null)
                return false;
            try
            {
                return typeof(CliReuseProof).GetMethod("NormalizeEnvironment",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [values, sdk]) is not null;
            }
            catch (Exception error)
            {
                return new
                {
                    exceptionType = error.GetBaseException().GetType().Name
                };
            }
        }
        string RegisteredState(string name, string? expected)
        {
            var value = Value(name);
            if (value is null)
                return "unset";
            if (value.Length == 0)
                return "empty";
            if (expected is null)
                return "unregistered";
            try
            {
                return Path.IsPathFullyQualified(value) && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)),
                    expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? "expected" : "mismatched";
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return "mismatched";
            }
        }
        return new
        {
            coreFoundationPresent = Value(cf) is not null,
            unknownEnvironmentCount = environment.Keys.Cast<object>().Count(key => key is not string name || !known.Contains(name)),
            normalized = Normalize(environment),
            normalizedWithoutCoreFoundation = Normalize(withoutCf),
            registeredEnvironment = registered.ToDictionary(pair => pair.Key, pair => RegisteredState(pair.Key, pair.Value)),
            controls = new[] { ("MSBUILDDISABLENODEREUSE", "1"), ("MSBuildEnableWorkloadResolver", "false") }
                .ToDictionary(pair => pair.Item1, pair => Value(pair.Item1) is not { } value ? "unset" : value == pair.Item2 ? "expected" : "mismatched")
        };
    }

    internal static Task<OracleObservation> RunAsync(OracleRun run, string directory, string label,
        CancellationToken token) => RunAsync(run, directory, label, token, isolation: null);

    internal static async Task<OracleObservation> RunAsync(OracleRun run, string directory, string label,
        CancellationToken token, OracleCliIsolationSpec? isolation)
    {
        var input = Path.Combine(directory, label + ".request.json");
        var output = Path.Combine(directory, label + ".result.json");
        var arguments = new[] { typeof(OracleProcess).Assembly.Location, "--full-oracle", input, output };
        var packageRoot = Path.Combine(directory, "packages");
        isolation?.ValidateLaunch(isolation.DotnetHostPath, run.Options.Root, arguments, packageRoot, typeof(OracleProcess).Assembly.Location);
        File.WriteAllText(input, JsonSerializer.Serialize(run, QueryJson.Options));
        await ExecuteAsync(run.Options.Root, arguments, token, packageRoot, isolation);
        return JsonSerializer.Deserialize<OracleObservation>(File.ReadAllText(output), QueryJson.Options)
            ?? throw new InvalidDataException("Oracle result is missing.");
    }

    internal static Task ExecuteAsync(string root, string[] arguments, CancellationToken token, string? packageRoot = null)
        => ExecuteAsync(root, arguments, token, packageRoot, isolation: null);

    internal static async Task ExecuteAsync(string root, string[] arguments, CancellationToken token, string? packageRoot,
        OracleCliIsolationSpec? isolation)
    {
        if (isolation is null)
            token.ThrowIfCancellationRequested();
        var hostPath = isolation?.DotnetHostPath ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var command = isolation is null ? OracleChildEnvironmentPlanner.Classify(arguments)
            : isolation.ValidateLaunch(hostPath, root, arguments, packageRoot, typeof(OracleProcess).Assembly.Location);
        var start = new ProcessStartInfo(hostPath)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Keep unknown host/MSBuild properties out of the closed-input proof without changing this process.
        var inherited = start.Environment.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var planned = OracleChildEnvironmentPlanner.Create(inherited, command, packageRoot, isolation);
        start.Environment.Clear();
        foreach (var pair in planned)
            start.Environment[pair.Key] = pair.Value;
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        if (isolation is not null)
        {
            var binding = OracleOwnedStop.RequireBinding(isolation.OwnedStop?.RequestPath, isolation.OwnedStop?.Nonce, isolation.RunRoot);
            await ExecuteOwnedAsync(start, arguments, token, binding);
            return;
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Oracle process did not start.");
        using var reads = new CancellationTokenSource();
        var stdout = process.StandardOutput.ReadToEndAsync(reads.Token);
        var stderr = process.StandardError.ReadToEndAsync(reads.Token);
        var drain = Task.WhenAll(stdout, stderr);
        try
        {
            var processArtifact = arguments switch
            {
                [_, "--semantic-encoding-oracle", var directory] => Path.Combine(directory, "oracle.process.json"),
                [_, "--full-oracle", _, var processArtifactPath] => Path.ChangeExtension(processArtifactPath, "process.json"),
                _ => null
            };
            if (processArtifact is not null)
                File.WriteAllText(processArtifact, JsonSerializer.Serialize(new
                {
                    processId = process.Id,
                    startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    executable = start.FileName,
                    arguments
                }, QueryJson.Options));
            await process.WaitForExitAsync(token);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                    }
                    await WaitForExitBoundedAsync(process);
                }
            }
            finally
            {
                try
                {
                    await drain.WaitAsync(OutputDrainTimeout, token);
                }
                catch (Exception error) when (error is TimeoutException or OperationCanceledException)
                {
                    // A normally exited parent can leave descendants holding both inherited pipe handles.
                    // Observe eventual read failures even if closing the handles cannot finish the reads promptly.
                    ObserveFault(drain);
                    reads.Cancel();
                    try
                    {
                        process.StandardOutput.Dispose();
                    }
                    finally
                    {
                        process.StandardError.Dispose();
                    }
                    try
                    {
                        await drain.WaitAsync(ReadCleanupTimeout);
                    }
                    catch (Exception cleanup) when (cleanup is OperationCanceledException or IOException
                        or ObjectDisposedException or TimeoutException)
                    {
                    }
                    token.ThrowIfCancellationRequested();
                    if (error is TimeoutException)
                        throw new TimeoutException("Oracle output drain exceeded its two-second limit after process exit.", error);
                    throw;
                }
            }
        }
        if (arguments is [_, "--full-oracle", _, var artifact])
        {
            File.WriteAllText(Path.ChangeExtension(artifact, "stdout.log"), await stdout);
            File.WriteAllText(Path.ChangeExtension(artifact, "stderr.log"), await stderr);
        }
        if (arguments is [_, "--semantic-encoding-oracle", var encodingDirectory])
        {
            File.WriteAllText(Path.Combine(encodingDirectory, "process.stdout.log"), await stdout);
            File.WriteAllText(Path.Combine(encodingDirectory, "process.stderr.log"), await stderr);
        }
        if (process.ExitCode != 0)
        {
            var failure = $"Oracle process exited {process.ExitCode}:\n{await stdout}\n{await stderr}";
            if (arguments is [_, "--full-oracle", _, var output])
                File.WriteAllText(Path.ChangeExtension(output, "failure.log"), failure);
            throw new InvalidOperationException(failure);
        }
    }

    private static async Task ExecuteOwnedAsync(ProcessStartInfo start, string[] arguments, CancellationToken token,
        OracleOwnedStopBinding binding)
    {
        using var process = new Process { StartInfo = start };
        using var reads = new CancellationTokenSource();
        Task<string> stdout, stderr;
        bool terminal = false, drained = false;
        int exitCode;
        try
        {
            token.ThrowIfCancellationRequested();
            // Every operation after the start attempt is inside the owned uncertainty boundary.
            if (!process.Start())
                throw new InvalidOperationException("Owned oracle process did not start.");
            stdout = process.StandardOutput.ReadToEndAsync(reads.Token);
            stderr = process.StandardError.ReadToEndAsync(reads.Token);
            var drain = Task.WhenAll(stdout, stderr);
            ObserveFault(drain);
            if (arguments is [_, "--full-oracle", _, var artifact])
                File.WriteAllText(Path.ChangeExtension(artifact, "process.json"), JsonSerializer.Serialize(new
                {
                    processId = process.Id,
                    startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    executable = start.FileName,
                    arguments
                }, QueryJson.Options));
            await process.WaitForExitAsync(token);
            terminal = true;
            await drain.WaitAsync(OutputDrainTimeout, token);
            drained = true;
            exitCode = process.ExitCode;
            token.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            await OracleOwnedStop.RequestAndWaitAsync(binding, error is OperationCanceledException ? "cancelled" : "child-uncertain");
            throw;
        }
        if (!OracleOwnedStop.TerminalAndDrained(terminal, drained))
            await OracleOwnedStop.RequestAndWaitAsync(binding, "child-uncertain");
        if (arguments is [_, "--full-oracle", _, var output])
        {
            File.WriteAllText(Path.ChangeExtension(output, "stdout.log"), await stdout);
            File.WriteAllText(Path.ChangeExtension(output, "stderr.log"), await stderr);
        }
        // A fully terminal, drained nonzero is an ordinary rejection, including the three SOURCE_CHANGED cases.
        if (exitCode != 0)
        {
            var failure = $"Oracle process exited {exitCode}:\n{await stdout}\n{await stderr}";
            if (arguments is [_, "--full-oracle", _, var result])
                File.WriteAllText(Path.ChangeExtension(result, "failure.log"), failure);
            throw new InvalidOperationException(failure);
        }
    }

    private static async Task WaitForExitBoundedAsync(Process process)
    {
        var exited = process.WaitForExitAsync();
        ObserveFault(exited);
        await exited.WaitAsync(ExitCleanupTimeout);
    }

    private static void ObserveFault(Task task)
        => _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
        }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
