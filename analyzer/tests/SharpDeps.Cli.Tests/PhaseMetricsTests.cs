namespace SharpDeps.Cli.Tests;

using System.Diagnostics;
using System.Text.Json;
using SharpDeps.Analysis.Core.Diagnostics;
using SharpDeps.Analysis.Roslyn.Evidence;
using SharpDeps.Index;
using Xunit;
using Xunit.Abstractions;

public sealed class PhaseMetricsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ParallelRunsKeepTheirOwnPhasesAndNeverRecordInputLabels()
    {
        using var fixture = new MetricsFixture();
        var sentinel = Path.Combine(fixture.Directory, "keep.jsonl");
        File.WriteAllText(sentinel, "keep");
        await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(async () =>
        {
            using var run = PhaseMetrics.Start(fixture.Directory, "fixture")!;
            using (PhaseMetrics.Measure("parent"))
            {
                using (PhaseMetrics.Measure("child"))
                {
                    await Task.Yield();
                    PhaseMetrics.Count("owner", index);
                    PhaseMetrics.Decision("proof", "supported");
                    PhaseMetrics.Decision("proof", @"C:\private\secret");
                }
                await Task.WhenAll(Overlap(), Overlap());
            }
            run.Complete();

            static async Task Overlap()
            {
                using var span = PhaseMetrics.Measure("overlap-child");
                await Task.Delay(5);
            }
        })));
        Assert.False(PhaseMetrics.Enabled);
        Assert.Equal("keep", File.ReadAllText(sentinel));
        var files = System.IO.Directory.GetFiles(fixture.Directory, "*.jsonl").Where(p => p != sentinel).ToArray();
        Assert.Equal(2, files.Length);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("private", text);
            var events = Read(file);
            Assert.Single(events.Select(e => e.GetProperty("runId").GetString()).Distinct());
            Assert.Equal(Enumerable.Range(1, events.Length).Select(n => (long)n), events.Select(e => e.GetProperty("sequence").GetInt64()));
            Assert.Single(events, e => Payload(e, "count"));
            var parent = Assert.Single(events, e => Payload(e, "phase") && e.GetProperty("payload").GetProperty("phase").GetString() == "parent").GetProperty("payload");
            Assert.True(parent.GetProperty("overlappingChildren").GetBoolean());
            Assert.InRange(parent.GetProperty("measuredChildExclusiveMilliseconds").GetDouble(), 0,
                parent.GetProperty("inclusiveMilliseconds").GetDouble());
            Assert.Equal("completed", events.Last().GetProperty("payload").GetProperty("outcome").GetString());
        }
    }

    [Fact]
    public void DiagnosticFailuresAndWorkspaceOutputCannotChangeProductionWork()
    {
        using var fixture = new MetricsFixture();
        var file = Path.Combine(fixture.Directory, "existing-file");
        File.WriteAllText(file, "preserved");
        Assert.Null(PhaseMetrics.Start(file, "fixture"));
        Assert.Null(PhaseMetrics.TryStart(fixture.Directory, "fixture", fixture.Directory));
        Assert.Null(PhaseMetrics.TryStart(Path.Combine(fixture.Directory, "source"), "fixture", fixture.Directory));
        Assert.Equal("preserved", File.ReadAllText(file));
        Assert.False(PhaseMetrics.Enabled);
        using (var failing = new PhaseMetrics.Run("failure-fixture", "fixture", new StreamWriter(new FailedStream())))
        {
            for (var index = 0; index < 100; index++)
                PhaseMetrics.Count("observed", index);
            failing.Complete();
        }
        Assert.False(PhaseMetrics.Enabled);
        using var run = PhaseMetrics.TryStart(Path.Combine(fixture.Directory, ".sharpdeps", "metrics"), "fixture", fixture.Directory);
        Assert.NotNull(run);
        run.Complete();
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("sdk")]
    [InlineData("package")]
    public void DiagnosticOutputNeverOverlapsAProtectedDependencyTree(string kind)
    {
        using var fixture = new MetricsFixture();
        var parent = Path.Combine(fixture.Directory, ".sharpdeps", kind);
        var dependency = Path.Combine(parent, "dependency");
        var nested = Path.Combine(dependency, "metrics");
        Assert.False(PhaseMetrics.IsOutputIsolated(dependency, fixture.Directory, [dependency]));
        Assert.False(PhaseMetrics.IsOutputIsolated(nested, fixture.Directory, [dependency]));
        Assert.False(PhaseMetrics.IsOutputIsolated(parent, fixture.Directory, [dependency]));
        Assert.True(PhaseMetrics.IsOutputIsolated(Path.Combine(parent, "dependency-sibling"), fixture.Directory, [dependency]));
        Assert.True(PhaseMetrics.IsOutputIsolated(Path.Combine(fixture.Directory, ".sharpdeps", "metrics"), fixture.Directory, [dependency]));
        Assert.False(System.IO.Directory.Exists(parent));
    }

    [Fact]
    public void ProductionStartRefusesTheActualToolPayloadBeforeCreatingAnOutput()
    {
        var workspace = AppContext.BaseDirectory;
        var directory = Path.Combine(workspace, ".sharpdeps", "metrics-refused-" + Guid.NewGuid().ToString("N"));
        Assert.False(System.IO.Directory.Exists(directory));
        Assert.Null(PhaseMetrics.TryStart(directory, "fixture", workspace));
        Assert.False(System.IO.Directory.Exists(directory));
        Assert.False(PhaseMetrics.Enabled);
    }

    [Fact]
    public void ProjectDecisionsDistinguishCleanDirtySelectionAndBudgetWithoutChangingResults()
    {
        using var fixture = new MetricsFixture();
        using (var run = PhaseMetrics.Start(fixture.Directory, "fixture")!)
        {
            var files = new[] { new IndexFile("a", "a.cs", "source", new string('a', 64), 1, 1),
                new IndexFile("b", "b.cs", "source", new string('a', 64), 1, 1) };
            var selections = new CliProjectSelection[] { new("A", "vA", "pA", ["a"], []), new("B", "vB", "pB", ["b"], []) };
            var data = JsonSerializer.SerializeToElement(new CliProjectReuse.Extraction(new([], [], [], false), [], new OperationCollectionStats(0, 0, 0, 0)));
            var proof = new IndexProjectReuse(CliProjectReuse.Version, new string('f', 64), selections.Select(s =>
                new IndexProjectExtraction(s.Key, s.VariantId, s.ProjectId, s.SourceIds, s.References, data)).ToArray());
            var snapshot = new IndexSnapshot(null!, files, [], [], []);
            var current = files.ToDictionary(f => f.SourceId);
            Assert.Null(CliProjectReuse.Select(snapshot, proof, proof.StructureHash, selections, current, default));
            current["a"] = current["a"] with { ContentHash = new string('b', 64) };
            Assert.NotNull(CliProjectReuse.Select(snapshot, proof, proof.StructureHash, selections, current, default));
            current["b"] = current["b"] with { ContentHash = new string('b', 64) };
            Assert.Null(CliProjectReuse.Select(snapshot, proof, proof.StructureHash, selections, current, default));
            Assert.Null(CliProjectReuse.Select(snapshot, proof, proof.StructureHash, selections[..1], current, default));
            var budget = new CliReuseProof.ProofBudget(default);
            Assert.Throws<IOException>(() => budget.Bytes(IndexSnapshotValidator.MaximumInputFileBytes + 1));
            run.Complete();
        }
        var events = Read(Assert.Single(System.IO.Directory.GetFiles(fixture.Directory, "*.jsonl")));
        var reasons = events.Where(e => Payload(e, "decision")).Select(e => e.GetProperty("payload").GetProperty("reason").GetString()).ToArray();
        Assert.Contains("no-dirty-project", reasons);
        Assert.Contains("partial-reuse", reasons);
        Assert.Contains("no-clean-project", reasons);
        Assert.Contains("selection-changed", reasons);
        Assert.Contains("bytes", reasons);
    }

    [Fact]
    public void MetricsDestinationRoundTripsWithoutChangingTheSemanticSelection()
    {
        using var fixture = new MetricsFixture();
        File.WriteAllText(Path.Combine(fixture.Directory, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var root = fixture.Directory;
        var index = Path.Combine(root, ".sharpdeps", "index.sqlite");
        var uuid = Guid.NewGuid();
        var baseline = CoordinatorUpdate.Parse(CliArguments.Parse(["update", "--root", root,
            "--target", "App.csproj", "--trusted"]), root, index, uuid);
        var directory = Path.Combine(root, ".sharpdeps", "metrics");
        var measured = CoordinatorUpdate.Parse(CliArguments.Parse([.. baseline.Arguments(), "--metrics-directory", directory]), root, index, uuid);
        var forwarded = CoordinatorUpdate.Parse(CliArguments.Parse(measured.Arguments()), root, index, uuid);
        Assert.Equal(directory, forwarded.Options.MetricsDirectory);
        Assert.Equal(measured.Key, forwarded.Key);
        Assert.NotEqual(baseline.Key, measured.Key);
        Assert.Equal(baseline.Options, measured.Options with { MetricsDirectory = null });
        Assert.False(System.IO.Directory.Exists(directory));
    }

    [Fact]
    public void BoundedInstrumentationOverheadSample()
    {
        using var fixture = new MetricsFixture();
        const int count = 10000;
        for (var n = 0; n < 100; n++)
            PhaseMetrics.Measure("warmup")?.Dispose();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        for (var n = 0; n < count; n++)
            PhaseMetrics.Measure("sample")?.Dispose();
        var disabled = clock.Elapsed.TotalMilliseconds;
        var disabledBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        using (var run = PhaseMetrics.Start(fixture.Directory, "overhead")!)
        {
            clock.Restart();
            for (var n = 0; n < count; n++)
                PhaseMetrics.Measure("sample")?.Dispose();
            output.WriteLine($"bounded-scopes={count}; disabled-ms={disabled:F3}; disabled-allocated-bytes={disabledBytes}; enabled-ms={clock.Elapsed.TotalMilliseconds:F3}");
            run.Complete();
        }
        Assert.InRange(disabledBytes, 0, 128);
        Assert.False(PhaseMetrics.Enabled);
    }

    private static bool Payload(JsonElement value, string type) => value.GetProperty("payload").GetProperty("type").GetString() == type;
    private static JsonElement[] Read(string path) => File.ReadLines(path).Select(line =>
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }).ToArray();

    private sealed class FailedStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("diagnostic sink unavailable");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("diagnostic sink unavailable");
    }

    private sealed class MetricsFixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "sharpdeps-metrics-" + Guid.NewGuid().ToString("N"));
        internal MetricsFixture() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
