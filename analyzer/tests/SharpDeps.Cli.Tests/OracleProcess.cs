namespace SharpDeps.Cli.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDeps.Index;
using SharpDeps.Query;

internal sealed record OracleRun(CliIndexOptions Options, IndexSnapshot? Previous, string[] Terms);
internal sealed record OracleObservation(IndexSnapshot Snapshot, Dictionary<string, IndexSearchHit[]> Fts,
    Dictionary<string, QueryEnvelope> Queries, int ProcessId);

internal static class OracleProcess
{
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadCleanupTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ExitCleanupTimeout = TimeSpan.FromSeconds(3);

    public static async Task<int> Main(string[] args)
    {
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
                UseShellExecute = false, CreateNoWindow = true,
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
                    child.Kill(entireProcessTree: true);
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
        if (args is not ["--full-oracle", var input, var output])
            return 2;
        var run = JsonSerializer.Deserialize<OracleRun>(File.ReadAllText(input), QueryJson.Options)
            ?? throw new InvalidDataException("Oracle request is missing.");
        var sequence = 0;
        Guid Allocate() => new(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{run.Options.WorkspaceUuid:N}:{run.Previous?.Graph.Generation ?? 0}:{++sequence}"))[..16]);
        var before = Environment.GetEnvironmentVariables();
        var admissions = new List<CliProofAdmission>();
        var build = await CliIndexer.BuildForPublicationAsync(run.Options, run.Previous, default, null,
            run.Previous is null ? null : Allocate, admissions.Add);
        WriteProofDiagnostic(Path.ChangeExtension(output, "proof-diagnostic.json"), run.Options.Root, before, admissions);
        using (var writer = IndexWriter.Open(run.Options.Root, run.Options.IndexPath, run.Options.WorkspaceUuid, true))
            writer.Commit(IndexStage.Create(build.Snapshot), verifySelection: build.VerifySelection);
        using var reader = IndexReader.Open(run.Options.IndexPath, run.Options.WorkspaceUuid);
        var query = new QueryService(new PinnedQueryIndex(reader));
        var budget = new QueryBudget(500, 1000, 4, 30000, 1_000_000, 4_000_000);
        var fts = new Dictionary<string, IndexSearchHit[]>(StringComparer.Ordinal);
        var queries = new Dictionary<string, QueryEnvelope>(StringComparer.Ordinal);
        foreach (var term in run.Terms)
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
        File.WriteAllText(output, JsonSerializer.Serialize(new OracleObservation(reader.Snapshot(), fts,
            queries, Environment.ProcessId), QueryJson.Options));
        return 0;
    }

    private static void WriteProofDiagnostic(string path, string root, System.Collections.IDictionary before,
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
            catch (Exception error) { return new { exceptionType = error.GetBaseException().GetType().Name }; }
        }
        string HostState(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is null) return "unset";
            var expected = name == "DOTNET_HOST_PATH"
                ? Path.Combine(dotnet, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet") : dotnet;
            return Path.IsPathFullyQualified(value)
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)), Path.TrimEndingDirectorySeparator(expected), comparison)
                ? "expected" : "mismatched";
        }
        var after = Environment.GetEnvironmentVariables();
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            sdkPath = sdk, runtimeDirectory = runtime,
            sdkInsideRuntimeRoot = sdk is not null && string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(sdk)),
                Path.Combine(dotnet, "sdk"), comparison),
            normalizedBefore = sdk is null ? false : Check("NormalizeEnvironment", before, sdk),
            normalizedAfter = sdk is null ? false : Check("NormalizeEnvironment", after, sdk),
            environmentBefore = DescribeProofEnvironment(before, sdk),
            environmentAfter = DescribeProofEnvironment(after, sdk),
            userHooks = Check("HasUserHooks"), ancestorConfiguration = Check("HasAncestorConfiguration", root),
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
            if (entry.Key is not string name || !name.Equals(cf, StringComparison.OrdinalIgnoreCase)) withoutCf.Add(entry.Key, entry.Value);
        object Normalize(System.Collections.IDictionary values)
        {
            if (sdk is null) return false;
            try
            {
                return typeof(CliReuseProof).GetMethod("NormalizeEnvironment",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [values, sdk]) is not null;
            }
            catch (Exception error) { return new { exceptionType = error.GetBaseException().GetType().Name }; }
        }
        string RegisteredState(string name, string? expected)
        {
            var value = Value(name);
            if (value is null) return "unset";
            if (value.Length == 0) return "empty";
            if (expected is null) return "unregistered";
            try
            {
                return Path.IsPathFullyQualified(value) && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)),
                    expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? "expected" : "mismatched";
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return "mismatched"; }
        }
        return new
        {
            coreFoundationPresent = Value(cf) is not null,
            unknownEnvironmentCount = environment.Keys.Cast<object>().Count(key => key is not string name || !known.Contains(name)),
            normalized = Normalize(environment), normalizedWithoutCoreFoundation = Normalize(withoutCf),
            registeredEnvironment = registered.ToDictionary(pair => pair.Key, pair => RegisteredState(pair.Key, pair.Value)),
            controls = new[] { ("MSBUILDDISABLENODEREUSE", "1"), ("MSBuildEnableWorkloadResolver", "false") }
                .ToDictionary(pair => pair.Item1, pair => Value(pair.Item1) is not { } value ? "unset" : value == pair.Item2 ? "expected" : "mismatched")
        };
    }

    internal static async Task<OracleObservation> RunAsync(OracleRun run, string directory, string label,
        CancellationToken token)
    {
        var input = Path.Combine(directory, label + ".request.json");
        var output = Path.Combine(directory, label + ".result.json");
        File.WriteAllText(input, JsonSerializer.Serialize(run, QueryJson.Options));
        await ExecuteAsync(run.Options.Root, [typeof(OracleProcess).Assembly.Location, "--full-oracle", input, output], token,
            packageRoot: Path.Combine(directory, "packages"));
        return JsonSerializer.Deserialize<OracleObservation>(File.ReadAllText(output), QueryJson.Options)
            ?? throw new InvalidDataException("Oracle result is missing.");
    }

    internal static async Task ExecuteAsync(string root, string[] arguments, CancellationToken token, string? packageRoot = null)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Keep unknown host/MSBuild properties out of the closed-input proof without changing this process.
        var inherited = start.Environment.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "PATHEXT", "SystemRoot", "windir", "ComSpec", "ProgramFiles",
            "ProgramFiles(x86)", "ProgramW6432", "TEMP", "TMP", "TMPDIR", "USERPROFILE", "HOME", "HOMEDRIVE",
            "HOMEPATH", "APPDATA", "LOCALAPPDATA", "NUGET_PACKAGES", "DOTNET_ROOT", "DOTNET_ROOT_X64",
            "DOTNET_ROOT_ARM64", "DOTNET_HOST_PATH" })
            if (inherited.TryGetValue(name, out var value) && value is not null)
                start.Environment[name] = value;
        if (packageRoot is not null)
        {
            if (!Path.IsPathFullyQualified(packageRoot))
                throw new ArgumentException("Oracle package root must be absolute.", nameof(packageRoot));
            start.Environment["NUGET_PACKAGES"] = packageRoot;
        }
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["MSBuildEnableWorkloadResolver"] = "false";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        if (arguments is ["restore", ..] && inherited.TryGetValue("DOTNET_CLI_HOME", out var cliHome) && cliHome is not null)
            start.Environment["DOTNET_CLI_HOME"] = cliHome;
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Oracle process did not start.");
        using var reads = new CancellationTokenSource();
        var stdout = process.StandardOutput.ReadToEndAsync(reads.Token);
        var stderr = process.StandardError.ReadToEndAsync(reads.Token);
        var drain = Task.WhenAll(stdout, stderr);
        try { await process.WaitForExitAsync(token); }
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
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Oracle process exited {process.ExitCode}:\n{await stdout}\n{await stderr}");
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
