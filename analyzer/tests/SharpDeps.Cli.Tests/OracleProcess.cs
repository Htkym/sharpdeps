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
        var build = run.Previous is null
            ? await CliIndexer.BuildForPublicationAsync(run.Options, null, default, null)
            : await CliIndexer.BuildForPublicationAsync(run.Options, run.Previous, default, null, Allocate);
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

    internal static async Task<OracleObservation> RunAsync(OracleRun run, string directory, string label,
        CancellationToken token)
    {
        var input = Path.Combine(directory, label + ".request.json");
        var output = Path.Combine(directory, label + ".result.json");
        File.WriteAllText(input, JsonSerializer.Serialize(run, QueryJson.Options));
        await ExecuteAsync(run.Options.Root, [typeof(OracleProcess).Assembly.Location, "--full-oracle", input, output], token);
        return JsonSerializer.Deserialize<OracleObservation>(File.ReadAllText(output), QueryJson.Options)
            ?? throw new InvalidDataException("Oracle result is missing.");
    }

    internal static async Task ExecuteAsync(string root, string[] arguments, CancellationToken token)
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
