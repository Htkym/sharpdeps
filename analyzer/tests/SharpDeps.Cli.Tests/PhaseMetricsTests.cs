namespace SharpDeps.Cli.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis.MSBuild;
using SharpDeps.Analysis.Roslyn;
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
            using var input = new MemoryStream([1, 2, 3]);
            using (PhaseMetrics.ObserveRead(PhaseMetrics.ReadCategory.Workspace, input))
                Assert.Equal(1, input.ReadByte());
            using var progress = SemanticLoadMetrics.TryCreate()!;
            progress.Report(ProjectLoadOperation.Evaluate, TimeSpan.FromMilliseconds(1));
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

    [Theory]
    [InlineData(0, "workspace")]
    [InlineData(1, "sdk")]
    [InlineData(2, "git")]
    [InlineData(3, "package")]
    [InlineData(4, "tool")]
    public void ReadCategoriesPreserveTheExistingOrderedProof(int categoryValue, string label)
    {
        using var fixture = new MetricsFixture();
        var category = (PhaseMetrics.ReadCategory)categoryValue;
        var inputs = Path.Combine(fixture.Directory, "inputs");
        System.IO.Directory.CreateDirectory(inputs);
        File.WriteAllBytes(Path.Combine(inputs, "first"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(inputs, "second"), [4, 5, 6, 7]);
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        CliReuseProof.AddTree(inputs, "fixture", expected, new(default), category);
        using var actual = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var repeated = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var run = PhaseMetrics.Start(Path.Combine(fixture.Directory, "metrics"), "fixture")!)
        {
            using (PhaseMetrics.Measure("proof.first"))
                CliReuseProof.AddTree(inputs, "fixture", actual, new(default), category);
            using (PhaseMetrics.Measure("proof.second"))
                CliReuseProof.AddTree(inputs, "fixture", repeated, new(default), category);
            run.Complete();
        }
        var expectedDigest = expected.GetHashAndReset();
        Assert.Equal(expectedDigest, actual.GetHashAndReset());
        Assert.Equal(expectedDigest, repeated.GetHashAndReset());
        var events = Read(Assert.Single(System.IO.Directory.GetFiles(Path.Combine(fixture.Directory, "metrics"), "*.jsonl")));
        var reads = events.Where(e => Payload(e, "read")).Select(e => e.GetProperty("payload")).ToArray();
        var phases = events.Where(e => Payload(e, "phase")).Select(e => e.GetProperty("payload")).ToArray();
        Assert.Equal(4, reads.Length);
        Assert.All(reads, read => Assert.Equal(label, read.GetProperty("category").GetString()));
        Assert.Equal(14, reads.Sum(read => read.GetProperty("bytes").GetInt64()));
        Assert.All(reads, read =>
        {
            Assert.Equal(JsonValueKind.Null, read.GetProperty("reason").ValueKind);
            var phase = Assert.Single(phases, phase => phase.GetProperty("spanId").GetInt64() == read.GetProperty("spanId").GetInt64());
            Assert.Equal(read.GetProperty("phase").GetString(), phase.GetProperty("phase").GetString());
            Assert.Equal(read.GetProperty("parentSpanId").GetInt64(), phase.GetProperty("parentSpanId").GetInt64());
        });
        Assert.Equal(4, reads.Select(read => read.GetProperty("spanId").GetInt64()).Distinct().Count());
        foreach (var pass in new[] { "proof.first", "proof.second" })
        {
            var parent = Assert.Single(phases, phase => phase.GetProperty("phase").GetString() == pass);
            var passReads = reads.Where(read => read.GetProperty("parentSpanId").GetInt64() == parent.GetProperty("spanId").GetInt64()).ToArray();
            Assert.Equal(2, passReads.Length);
            Assert.Equal(7, passReads.Sum(read => read.GetProperty("bytes").GetInt64()));
        }
        Assert.False(PhaseMetrics.Enabled);
    }
    [Fact]
    public void ReadsObserveExistingBytesDuringCancellationAndDoNotReadForDiagnostics()
    {
        using var fixture = new MetricsFixture();
        using var input = new ReadProbeStream([1, 2, 3, 4]);
        input.PositionUnavailable = true;
        Assert.Null(PhaseMetrics.ObserveRead(PhaseMetrics.ReadCategory.Workspace, input));
        Assert.Equal(0, input.PositionRequests);
        input.PositionUnavailable = false;
        using (var run = PhaseMetrics.Start(fixture.Directory, "fixture")!)
        {
            Assert.Throws<OperationCanceledException>((Action)(() =>
            {
                using var read = PhaseMetrics.ObserveRead(PhaseMetrics.ReadCategory.Workspace, input);
                Assert.Equal(1, input.ReadByte());
                Assert.Equal(2, input.ReadByte());
                throw new OperationCanceledException();
            }));
            input.PositionUnavailable = true;
            using (PhaseMetrics.ObserveRead(PhaseMetrics.ReadCategory.Git, input))
                Assert.Equal(3, input.ReadByte());
            run.Fail(cancelled: true);
        }
        Assert.Equal(3, input.ReadCalls);
        var events = Read(Assert.Single(System.IO.Directory.GetFiles(fixture.Directory, "*.jsonl")));
        var reads = events.Where(e => Payload(e, "read")).Select(e => e.GetProperty("payload")).ToArray();
        Assert.Equal(2, reads.Length);
        Assert.Equal(2, reads[0].GetProperty("bytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, reads[1].GetProperty("bytes").ValueKind);
        Assert.Equal("stream-position-unavailable", reads[1].GetProperty("reason").GetString());
        Assert.Equal("cancelled", events[^1].GetProperty("payload").GetProperty("outcome").GetString());
        Assert.False(PhaseMetrics.Enabled);
    }

    [Fact]
    public async Task MsbuildDurationsBindToTheRunWithoutAmbientCallbackContext()
    {
        using var fixture = new MetricsFixture();
        Assert.Null(SemanticLoadMetrics.TryCreate());
        using (var run = PhaseMetrics.Start(fixture.Directory, "fixture")!)
        {
            using var progress = SemanticLoadMetrics.TryCreate()!;
            Task callback;
            using (ExecutionContext.SuppressFlow())
                callback = Task.Run(() => progress.Report(ProjectLoadOperation.Evaluate, TimeSpan.FromMilliseconds(12)));
            await callback;
            progress.Report(ProjectLoadOperation.Evaluate, TimeSpan.FromMilliseconds(7));
            progress.Report(ProjectLoadOperation.Resolve, TimeSpan.FromMilliseconds(-1));
            progress.Report((ProjectLoadOperation)999, TimeSpan.FromMilliseconds(99));
            progress.Dispose();
            progress.Report(ProjectLoadOperation.Build, TimeSpan.FromMilliseconds(100));
            run.Fail(cancelled: true);
        }
        var events = Read(Assert.Single(System.IO.Directory.GetFiles(fixture.Directory, "*.jsonl")));
        var durations = events.Where(e => Payload(e, "reported-duration")).Select(e => e.GetProperty("payload")).ToArray();
        Assert.Equal(5, durations.Length);
        Assert.All(durations, value => Assert.Equal("reported-operation-elapsed", value.GetProperty("timing").GetString()));
        Assert.Equal(new[] { 12d, 7d }, durations.Where(value => value.GetProperty("operation").GetString() == "evaluate")
            .Select(value => value.GetProperty("elapsedMilliseconds").GetDouble()));
        var missing = Assert.Single(durations, value => value.GetProperty("operation").GetString() == "build");
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("elapsedMilliseconds").ValueKind);
        Assert.Equal("no-progress-event", missing.GetProperty("reason").GetString());
        Assert.Contains(durations, value => value.GetProperty("reason").GetString() == "invalid-elapsed");
        Assert.Contains(durations, value => value.GetProperty("reason").GetString() == "unsupported-progress-operation");
        Assert.DoesNotContain(events, value => Payload(value, "phase") && value.GetProperty("payload").GetProperty("phase").GetString() == "semantic.msbuild-progress");
        Assert.False(PhaseMetrics.Enabled);
    }

    private sealed class ReadProbeStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal bool PositionUnavailable { get; set; }
        internal int PositionRequests { get; private set; }
        internal int ReadCalls { get; private set; }
        public override long Position
        {
            get
            {
                PositionRequests++;
                return PositionUnavailable ? throw new NotSupportedException("private diagnostic failure") : base.Position;
            }
            set => base.Position = value;
        }
        public override int ReadByte()
        {
            ReadCalls++;
            return base.ReadByte();
        }
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
