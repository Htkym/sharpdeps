namespace SharpDeps.Cli.Tests;

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Index;
using SharpDeps.Query;
using Xunit;

[Collection("markdown-index")]
public sealed class FullBuildOracleTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SeparateFullExtractionMatchesUnchangedMarkdownAndProjectUpdatesWithTheSameLineage()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "sharpdeps-full-oracle", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(runRoot, "workspace");
        Directory.CreateDirectory(root);
        var sdkRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "sdk"));
        var sdk = Directory.EnumerateDirectories(sdkRoot).Select(Path.GetFileName)
            .Where(s => s is not null && s.StartsWith("10.", StringComparison.Ordinal) && Version.TryParse(s, out _))
            .OrderByDescending(s => Version.Parse(s!)).First()
            ?? throw new InvalidOperationException("An installed .NET 10 SDK is required by the reuse profile.");
        Write("global.json", JsonSerializer.Serialize(new { sdk = new { version = sdk, rollForward = "disable" } }));
        File.WriteAllText(Path.Combine(runRoot, "restore.config"), "<configuration><packageSources><clear /></packageSources></configuration>");
        Write("Oracle.slnx", "<Solution><Project Path=\"A/A.csproj\" /><Project Path=\"B/B.csproj\" /><Project Path=\"C/C.csproj\" /></Solution>");
        foreach (var name in new[] { "A", "B", "C" })
            Write(name + "/" + name + ".csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net10.0</TargetFramework><EnableNETAnalyzers>false</EnableNETAnalyzers>" +
                "</PropertyGroup>" + (name == "A" ? "<ItemGroup><ProjectReference Include=\"../B/B.csproj\" /></ItemGroup>" : "") + "</Project>");
        const string source = "namespace Demo;\r\npublic partial class Service { public int Run() => Shared.Value(); }\r\n";
        Write("A/Service.cs", source);
        Write("A/Partial.cs", "namespace Demo; public partial class Service { public string Label => \"日本語😀\"; }");
        Write("B/Shared.cs", "namespace Demo; public static class Shared { public static int Value() => 1; }");
        Write("C/Independent.cs", "namespace Demo; public class Independent { }");
        Write("guide.md", "# Guide\r\n\r\n`Demo.Service.Run` 日本語😀\r\n");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await OracleProcess.ExecuteAsync(root, ["restore", "Oracle.slnx", "--configfile", Path.Combine(runRoot, "restore.config"), "--verbosity", "quiet"], deadline.Token);
            File.Delete(Path.Combine(root, "global.json"));
            var options = new CliIndexOptions(root, Path.Combine(root, "Oracle.slnx"),
                Path.Combine(root, ".sharpdeps", "baseline.sqlite"), Guid.Parse("11111111-2222-4333-8444-555555555555"));
            string[] terms = ["Service", "Shared", "Guide", "日本語", "missing-term"];
            var previous = await OracleProcess.RunAsync(new(options, null, terms), runRoot, "baseline", deadline.Token);
            var proof = Assert.Single(previous.Snapshot.Files, f => f.Reuse is not null).Reuse!;
            Assert.NotNull(proof.Projects);
            Assert.Equal(sdk, Path.GetFileName(proof.SdkPath));
            Assert.Equal(3, proof.Projects.Variants.Count);
            AssertFull(previous);
            for (var step = 0; step < 5; step++)
            {
                if (step == 1)
                {
                    Write("guide.md", "# Guide\r\n\r\n`Demo.Service.Run` 日本語😀 edited.\r\n\r\n## New heading\r\nNew text.\r\n");
                    Write("new.md", "# New document\r\n`Demo.Shared`\r\n");
                }
                else if (step == 2)
                    Write("A/Service.cs", "\r\n" + source.Replace("Shared.Value()", "Shared.Missing()"));
                else if (step == 3)
                    Write("A/Added.cs", "namespace Demo; public class Added { public Service Create() => new(); }");
                else if (step == 4)
                {
                    var project = Path.Combine(root, "A", "A.csproj");
                    File.WriteAllText(project, File.ReadAllText(project).Replace("</PropertyGroup>",
                        "<DefineConstants>FEATURE</DefineConstants></PropertyGroup>"));
                }
                var incrementalOptions = options with { IndexPath = Path.Combine(root, ".sharpdeps", $"update-{step}.sqlite"), AllowSemanticReuse = true };
                var fullOptions = incrementalOptions with { IndexPath = Path.Combine(root, ".sharpdeps", $"full-{step}.sqlite"), AllowSemanticReuse = false };
                var incremental = await OracleProcess.RunAsync(new(incrementalOptions, previous.Snapshot, terms), runRoot, $"update-{step}", deadline.Token);
                var full = await OracleProcess.RunAsync(new(fullOptions, previous.Snapshot, terms), runRoot, $"full-{step}", deadline.Token);
                Assert.NotEqual(Environment.ProcessId, full.ProcessId);
                Assert.NotEqual(incremental.ProcessId, full.ProcessId);
                AssertFull(full);
                Assert.Equal(previous.Snapshot.Graph.Generation + 1, incremental.Snapshot.Graph.Generation);
                if (step < 2)
                    Assert.Equal(1, Count(incremental, "cli.semanticGraphReused"));
                else if (step is 2 or 4)
                    AssertFull(incremental);
                else
                {
                    Assert.Equal(1, Count(incremental, "cli.projectCollectorsExtracted"));
                    Assert.Equal(2, Count(incremental, "cli.projectCollectorsReused"));
                }
                if (step >= 2)
                    Assert.True(Count(incremental, "cli.compilerErrors") > 0);
                foreach (var reply in full.Queries.Values.Concat(incremental.Queries.Values))
                {
                    Assert.Empty(reply.Errors);
                    Assert.False(reply.Truncated);
                    Assert.Null(reply.NextCursor);
                }
                var differences = FullBuildOracle.Differences(full, incremental);
                Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences.Take(30)) + "\nArtifacts: " + runRoot);
                previous = incremental;
            }
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("Oracle artifacts: " + runRoot, error);
        }
        void Write(string relative, string text)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false, true));
        }
    }

    private static void AssertFull(OracleObservation observation)
    {
        Assert.Equal(3, Count(observation, "cli.projectCollectorsExtracted"));
        Assert.Equal(0, Count(observation, "cli.projectCollectorsReused"));
        Assert.Equal(0, Count(observation, "cli.semanticGraphReused"));
    }

    private static int Count(OracleObservation observation, string code)
        => observation.Snapshot.Graph.Diagnostics?.SingleOrDefault(d => d.Code == code)?.Count ?? 0;

    [Fact]
    public async Task OracleProcessReportsFailureAndHonorsCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OracleProcess.ExecuteAsync(Path.GetTempPath(),
            [typeof(OracleProcess).Assembly.Location, "--unknown"], cancelled.Token));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => OracleProcess.ExecuteAsync(Path.GetTempPath(),
            [typeof(OracleProcess).Assembly.Location, "--unknown"], default));
        Assert.Contains("exited 2", failure.Message, StringComparison.Ordinal);
        var marker = Path.Combine(Path.GetTempPath(), "oracle-process-" + Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource();
        using var readyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = OracleProcess.ExecuteAsync(Path.GetTempPath(),
            [typeof(OracleProcess).Assembly.Location, "--oracle-wait", marker], stop.Token);
        try
        {
            while (!File.Exists(marker))
            {
                if (running.IsCompleted)
                    await running;
                await Task.Delay(20, readyDeadline.Token);
            }
            var processId = int.Parse(File.ReadAllText(marker), System.Globalization.CultureInfo.InvariantCulture);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(processId));
        }
        finally
        {
            stop.Cancel();
            try { await running; }
            catch (OperationCanceledException) { }
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task OracleProcessBoundsDrainAfterParentExitWhileDescendantHoldsPipes()
    {
        var marker = Path.Combine(Path.GetTempPath(), "oracle-inherited-pipes-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = OracleProcess.ExecuteAsync(Path.GetTempPath(),
            [typeof(OracleProcess).Assembly.Location, "--oracle-exit-with-pipes", marker], deadline.Token);
        // Register before cleanup can fail, so the original runner's eventual failure is always observed.
        var observed = running.ContinueWith(completed =>
        {
            _ = completed.Exception;
        }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var failure = await Assert.ThrowsAsync<TimeoutException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("Oracle output drain", failure.Message, StringComparison.Ordinal);
            Assert.False(deadline.IsCancellationRequested);
            var processIds = JsonSerializer.Deserialize<int[]>(File.ReadAllText(marker))!;
            Assert.True(File.Exists(marker + ".ready"));
            Assert.False(IsRunning(processIds[0]));
            Assert.True(IsRunning(processIds[1]));
        }
        finally
        {
            deadline.Cancel();
            if (File.Exists(marker))
            {
                var childId = JsonSerializer.Deserialize<int[]>(File.ReadAllText(marker))![1];
                System.Diagnostics.Process? child = null;
                try
                {
                    child = System.Diagnostics.Process.GetProcessById(childId);
                }
                catch (ArgumentException)
                {
                }
                using (child)
                {
                    if (child is not null)
                    {
                        if (!child.HasExited)
                        {
                            try
                            {
                                child.Kill(entireProcessTree: true);
                            }
                            catch (InvalidOperationException) when (child.HasExited)
                            {
                            }
                        }
                        var exited = child.WaitForExitAsync();
                        _ = exited.ContinueWith(completed =>
                        {
                            _ = completed.Exception;
                        }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        await exited.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                Assert.False(IsRunning(childId));
            }
            // Killing the owned descendant also releases the pipes if this regression fails against the old runner.
            await observed.WaitAsync(TimeSpan.FromSeconds(5));
            File.Delete(marker);
            File.Delete(marker + ".tmp");
            File.Delete(marker + ".ready");
        }

        static bool IsRunning(int id)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(id);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
    [Theory]
    [InlineData("edge")]
    [InlineData("source-position")]
    [InlineData("diagnostic")]
    [InlineData("unknown-execution-diagnostic")]
    [InlineData("fts-rank")]
    public void ComparatorRejectsIntentionalSemanticAndRankingDifferences(string mutation)
    {
        var location = new HarnessLocation("source", "v1", new string('a', 64), new(0, 5));
        var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, 1, HarnessGraphContract.IdentityVersion,
            "hw_11111111222243338444555555555555", "baseline", 1, HarnessCoverage.Partial, [],
            [new("symbol", HarnessNodeKind.Type, "Service", null, location)], [],
            [new("edge", "symbol", "symbol", null, null, null, "calls", HarnessCertainty.Resolved, "semantic", location)], [], null,
            [new("cli.compilerErrors", 1), new("cli.projectCollectorsExtracted", 3)]);
        var baseline = new OracleObservation(new(graph, [], [], [], []),
            new() { ["Service"] = [new("symbol", 1, "fts")] }, new(), 1);
        var changed = baseline with { Snapshot = baseline.Snapshot with { Graph = graph with { SnapshotId = "other", Generation = 7 } }, ProcessId = 2 };
        Assert.Empty(FullBuildOracle.Differences(baseline, changed));
        changed = mutation switch
        {
            "edge" => changed with { Snapshot = changed.Snapshot with { Graph = graph with { Edges = [graph.Edges[0] with { Certainty = HarnessCertainty.Candidate }] } } },
            "source-position" => changed with { Snapshot = changed.Snapshot with { Graph = graph with { Nodes = [graph.Nodes[0] with { Location = location with { RawSpan = new(1, 5) } }] } } },
            "diagnostic" => changed with { Snapshot = changed.Snapshot with { Graph = graph with { Diagnostics = [new("cli.compilerErrors", 2)] } } },
            "unknown-execution-diagnostic" => changed with { Snapshot = changed.Snapshot with { Graph = graph with { Diagnostics = [.. graph.Diagnostics!, new("cli.projectReused.not-a-variant", 1)] } } },
            "fts-rank" => changed with { Fts = new() { ["Service"] = [new("symbol", 2, "fts")] } },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        Assert.NotEmpty(FullBuildOracle.Differences(baseline, changed));
    }
}
