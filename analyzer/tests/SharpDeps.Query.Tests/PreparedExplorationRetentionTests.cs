namespace SharpDeps.Query.Tests;

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Index;
using Xunit;
using Xunit.Abstractions;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PreparedRetentionCollection
{
    public const string Name = "SharpDeps lifecycle diagnostics";
}

// Run only this class in a dedicated Release test process. These are collectibility
// proofs for two-node fixtures, not byte ceilings or performance measurements.
[Collection(PreparedRetentionCollection.Name)]
public sealed class PreparedExplorationRetentionTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);
    private const int CollectionCycles = 3;
    private static readonly string[] Names = ["Before", "After", "Third", "Fourth"];

    [Fact]
    public void CoherentReplacementCollectsThreeObsoleteGenerationsWithRetainedControl()
        => Run(nameof(CoherentReplacementCollectsThreeObsoleteGenerationsWithRetainedControl), () =>
        {
            using var fixture = new Fixture();
            var host = new PreparedExplorationHost();
            try
            {
                List<WeakPair> obsolete = [];
                for (var generation = 1; generation < 4; generation++)
                {
                    var replacement = Replace(fixture, host, generation);
                    obsolete.Add(replacement.Old);
                    Collect();
                    AssertAlive(replacement.Old); // Collector must not defeat a genuine strong root.
                    Clear(replacement.Control);
                    Collect();
                    foreach (var old in obsolete) AssertDead(old);
                    AssertAlive(replacement.Current);
                    GC.KeepAlive(host);
                }
            }
            finally { Drain(host); }
        });

    [Fact]
    public void IdleTtlCollectsAtExactBoundaryWithoutAnotherAcquire()
        => Run(nameof(IdleTtlCollectsAtExactBoundaryWithoutAnotherAcquire), () =>
        {
            using var fixture = new Fixture();
            var clock = new ManualTimeProvider();
            var host = new PreparedExplorationHost(idleLifetime: Ttl, timeProvider: clock);
            try
            {
                var old = Warm(fixture, host);
                clock.Advance(Ttl - TimeSpan.FromTicks(1));
                Collect();
                AssertAlive(old);
                clock.Advance(TimeSpan.FromTicks(1));
                Collect();
                AssertDead(old);
                GC.KeepAlive(host);
            }
            finally { Drain(host); }
        });

    [Fact]
    public void IdleCapacityEvictionCollectsBeforeTtlWhileOtherEntryRemainsAlive()
        => Run(nameof(IdleCapacityEvictionCollectsBeforeTtlWhileOtherEntryRemainsAlive), () =>
        {
            using var first = new Fixture();
            using var second = new Fixture();
            var clock = new ManualTimeProvider();
            var host = new PreparedExplorationHost(maximumEntries: 1, idleLifetime: Ttl, timeProvider: clock);
            try
            {
                var old = Warm(first, host);
                var current = Warm(second, host); // Clock has not advanced: expiry cannot explain eviction.
                Collect();
                AssertDead(old);
                AssertAlive(current);
                GC.KeepAlive(host);
            }
            finally { Drain(host); }
        });

    [Fact]
    public void ShutdownDrainsActiveLeaseThenCollectsWithDisposedHostAlive()
        => Run(nameof(ShutdownDrainsActiveLeaseThenCollectsWithDisposedHostAlive), () =>
        {
            using var fixture = new Fixture();
            var host = new PreparedExplorationHost();
            try
            {
                var old = ShutdownWithActiveLease(fixture, host);
                Collect();
                AssertDead(old);
                Drain(host);
                Assert.Throws<ObjectDisposedException>(() => WithAcquisition(fixture, host, (pending, _) => Capture(Await(pending))));
                GC.KeepAlive(host);
            }
            finally { Drain(host); }
        });

    [Fact]
    public void FailureAfterWarmStateDrainsCollectsAndRecovers()
        => Run(nameof(FailureAfterWarmStateDrainsCollectsAndRecovers), () =>
        {
            using var fixture = new Fixture();
            var attempts = 0;
            var host = new PreparedExplorationHost(beforePrepare: _ =>
            {
                if (Interlocked.Increment(ref attempts) == 2)
                    throw new IndexStoreException("INDEX_INVALID_SNAPSHOT", "Injected failure after warm state.");
                return Task.CompletedTask;
            });
            try
            {
                var old = Warm(fixture, host);
                FailSecond(fixture, host);
                var current = Warm(fixture, host); // Acquiring the same gate proves abandoned cleanup drained.
                Assert.Equal(3, Volatile.Read(ref attempts));
                Collect();
                AssertDead(old);
                AssertAlive(current);
                GC.KeepAlive(host);
            }
            finally { Drain(host); }
        });

    [Fact]
    public void AbandonedOwnerAfterWarmStateDrainsBeforeRetirementAndCollection()
        => Run(nameof(AbandonedOwnerAfterWarmStateDrainsBeforeRetirementAndCollection), () =>
        {
            using var fixture = new Fixture();
            var clock = new ManualTimeProvider();
            var barrier = new OwnerBarrier();
            var host = new PreparedExplorationHost(idleLifetime: Ttl, timeProvider: clock,
                beforePrepare: barrier.BeforePrepare);
            try
            {
                var old = Warm(fixture, host);
                AbandonAndDrain(fixture, host, barrier);
                // No claim about a cold abandoned Prepared: old is the previously warmed G1.
                clock.Advance(Ttl);
                Collect();
                AssertDead(old);
                GC.KeepAlive(host);
            }
            finally { barrier.Release.TrySetResult(); Drain(host); }
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakPair Warm(Fixture fixture, PreparedExplorationHost host)
        => WithAcquisition(fixture, host, (pending, _) =>
        {
            var lease = Await(pending);
            Coherent(lease, 1);
            return Capture(lease);
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Replacement Replace(Fixture fixture, PreparedExplorationHost host, int generation)
        => WithAcquisition(fixture, host, (oldTask, _) =>
        {
            var old = Await(oldTask);
            Coherent(old, generation);
            var weak = Capture(old);
            var control = new StrongControl { Prepared = old.Prepared, Snapshot = old.Prepared.saved };
            fixture.Publish(generation + 1);
            Coherent(old, generation);
            Assert.Empty(old.Reader.Search(Names[generation]));
            return WithAcquisition(fixture, host, (pending, _) =>
            {
                Assert.False(pending.IsCompleted);
                old.Dispose();
                var current = Await(pending);
                Coherent(current, generation + 1);
                Assert.Empty(current.Reader.Search(Names[generation - 1]));
                return new Replacement(weak, Capture(current), control);
            });
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakPair ShutdownWithActiveLease(Fixture fixture, PreparedExplorationHost host)
        => WithAcquisition(fixture, host, (pending, _) =>
        {
            var lease = Await(pending);
            var weak = Capture(lease);
            var shutdown = host.DisposeAsync().AsTask();
            try
            {
                Assert.False(shutdown.IsCompleted);
                Coherent(lease, 1);
                lease.Dispose();
                Assert.Throws<ObjectDisposedException>(() => lease.Pin(default));
                Await(shutdown);
                return weak;
            }
            finally { Cleanup(lease.Dispose); Cleanup(() => Await(shutdown)); }
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailSecond(Fixture fixture, PreparedExplorationHost host)
    {
        var error = Assert.Throws<IndexStoreException>(() => WithAcquisition(fixture, host,
            (pending, _) => Capture(Await(pending))));
        Assert.Equal("INDEX_INVALID_SNAPSHOT", error.Code);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonAndDrain(Fixture fixture, PreparedExplorationHost host, OwnerBarrier barrier)
    {
        WithAcquisition(fixture, host, (pending, caller) =>
        {
            try
            {
                Await(barrier.Entered.Task);
                caller.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => Await(pending));
                Assert.False(barrier.OwnerToken.IsCancellationRequested);
                Assert.False(barrier.Exited.Task.IsCompleted);
            }
            finally
            {
                Cleanup(caller.Cancel);
                barrier.Release.TrySetResult();
            }
            return true;
        });
        WithAcquisition(fixture, host, (pending, _) =>
        {
            var next = Await(pending);
            Await(barrier.Exited.Task);
            Coherent(next, 1); // Same-key gate is the drain barrier, not caller cancellation.
            return true;
        });
    }

    // A timeout limits only observation. Cancel and observe the original task in
    // every exit path, disposing even a successful lease delivered after timeout.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static T WithAcquisition<T>(Fixture fixture, PreparedExplorationHost host,
        Func<Task<PreparedExplorationLease>, CancellationTokenSource, T> body)
    {
        using var caller = new CancellationTokenSource();
        var pending = fixture.AcquireAsync(host, caller.Token);
        try { return body(pending, caller); }
        finally
        {
            Cleanup(caller.Cancel);
            Cleanup(() =>
            {
                PreparedExplorationLease lease;
                try { lease = Await(pending); }
                catch when (pending.IsFaulted || pending.IsCanceled) { return; } // Observed failure/cancellation.
                catch (TimeoutException) when (pending.IsCompletedSuccessfully)
                {
                    pending.GetAwaiter().GetResult().Dispose();
                    return;
                }
                lease.Dispose();
            });
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Coherent(PreparedExplorationLease lease, int generation)
    {
        var snapshot = lease.Reader.Snapshot();
        Assert.Same(snapshot, lease.Prepared.saved);
        Assert.Equal((long)generation, snapshot.Graph.Generation);
        Assert.Equal($"retention-{generation}", snapshot.Graph.SnapshotId);
        var name = Names[generation - 1];
        Assert.Equal(name, Assert.Single(snapshot.Graph.Nodes, node => node.Id == "file-App").Name);
        Assert.Equal(name, lease.Prepared.GetSearchIndex(default).Search["file-App"].Label);
        Assert.Equal("file-App", Assert.Single(lease.Reader.Search(name)).NodeId);
        Assert.Same(lease.Prepared, lease.Pin(default).PrepareExploration(default));
    }

    private sealed record WeakPair(WeakReference<ExplorationSnapshot> Prepared, WeakReference<IndexSnapshot> Snapshot);
    private sealed class StrongControl
    {
        internal ExplorationSnapshot? Prepared;
        internal IndexSnapshot? Snapshot;
    }
    private sealed record Replacement(WeakPair Old, WeakPair Current, StrongControl Control);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakPair Capture(PreparedExplorationLease lease)
        => new(new(lease.Prepared), new(lease.Prepared.saved));
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Clear(StrongControl control) { control.Prepared = null; control.Snapshot = null; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertAlive(WeakPair weak)
    {
        Assert.True(weak.Prepared.TryGetTarget(out _), "Prepared positive/retention control was lost.");
        Assert.True(weak.Snapshot.TryGetTarget(out _), "Snapshot positive/retention control was lost.");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertDead(WeakPair weak)
    {
        Assert.False(weak.Prepared.TryGetTarget(out _), "Retired Prepared remains reachable.");
        Assert.False(weak.Snapshot.TryGetTarget(out _), "Retired Snapshot remains reachable.");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect()
    {
        for (var cycle = 0; cycle < CollectionCycles; cycle++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }
    private static T Await<T>(Task<T> task) => task.WaitAsync(Guard).GetAwaiter().GetResult();
    private static void Await(Task task) => task.WaitAsync(Guard).GetAwaiter().GetResult();
    private static void Drain(PreparedExplorationHost host) => Cleanup(() => Await(host.DisposeAsync().AsTask()));

    [ThreadStatic] private static List<Exception>? cleanupFailures;
    [ThreadStatic] private static List<string>? fixtureRoots;

    // Cleanup errors are reported by Run, never substituted for an assertion failure.
    private static void Cleanup(Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            if (cleanupFailures is null) throw;
            cleanupFailures.Add(error);
        }
    }

    private void Run(string name, Action body)
    {
        var started = DateTimeOffset.UtcNow;
        var before = Diagnostics();
        Exception? primary = null;
        Exception? receiptFailure = null;
        cleanupFailures = [];
        fixtureRoots = [];
        string? evidenceDirectory = null;
        try
        {
            try
            {
                var root = Environment.GetEnvironmentVariable("SHARPDEPS_LIFECYCLE_EVIDENCE_DIR");
                if (!string.IsNullOrWhiteSpace(root))
                {
                    ValidateAbsoluteNoReparse(root);
                    evidenceDirectory = Path.Combine(Path.GetFullPath(root), $"{name}-{Environment.ProcessId}-{Guid.NewGuid():N}");
                    if (Directory.Exists(evidenceDirectory) || File.Exists(evidenceDirectory))
                        throw new IOException("Evidence child must be new.");
                    Directory.CreateDirectory(evidenceDirectory);
                    ValidateAbsoluteNoReparse(evidenceDirectory);
                }
#if DEBUG
                throw new InvalidOperationException("Managed-retention evidence requires a dedicated Release test process.");
#else
                body();
#endif
            }
            catch (Exception error) { primary = error; }

            // Preserve failed tiny fixtures in place when explicit evidence storage was
            // accepted. Receipt paths identify them; never overwrite earlier evidence.
            var preserve = evidenceDirectory is not null && (primary is not null || cleanupFailures.Count != 0);
            foreach (var root in fixtureRoots)
                if (!preserve) Cleanup(() => Fixture.DeleteOwnedRoot(root));
            object? after = null;
            try { after = Diagnostics(); }
            catch (Exception error) { receiptFailure = error; }
            // Capture the initialized per-run collections, not nullable thread-static
            // fields that are cleared after this local function has finished.
            var receiptCleanupFailures = cleanupFailures;
            var receiptFixtureRoots = fixtureRoots;
            string Receipt(string status) => JsonSerializer.Serialize(new
            {
                suite = nameof(PreparedExplorationRetentionTests), test = name,
                processId = Environment.ProcessId, started, finished = DateTimeOffset.UtcNow,
                outcome = status, primaryFailure = primary?.ToString(),
                cleanupFailures = receiptCleanupFailures.Select(error => error.ToString()).ToArray(),
                receiptFailure = receiptFailure?.ToString(), preservedFixtureRoots = receiptFixtureRoots.Where(Directory.Exists).ToArray(),
                generationsLimit = 4, nodesPerGeneration = 2, collectionCycles = CollectionCycles,
                before, after,
                scope = "Tiny-fixture managed collectibility only. TRX is authoritative; not a byte ceiling."
            });
            var failed = primary is not null || cleanupFailures.Count != 0;
            try
            {
                if (evidenceDirectory is not null)
                {
                    ValidateAbsoluteNoReparse(evidenceDirectory);
                    using var stream = new FileStream(Path.Combine(evidenceDirectory, "receipt.json"), FileMode.CreateNew, FileAccess.Write);
                    using var writer = new StreamWriter(stream);
                    // A partially persisted file can never claim a completed pass.
                    writer.Write(Receipt(failed ? "failed" : "assertions-completed-await-trx"));
                }
            }
            catch (Exception error) { receiptFailure = receiptFailure is null ? error : new AggregateException(receiptFailure, error); }
            try { output.WriteLine(Receipt(failed || receiptFailure is not null ? "failed" : "passed-await-trx")); }
            catch (Exception error) { receiptFailure = receiptFailure is null ? error : new AggregateException(receiptFailure, error); }

            if (primary is not null)
            {
                if (cleanupFailures.Count != 0) primary.Data["LifecycleCleanupFailures"] = cleanupFailures.Select(error => error.ToString()).ToArray();
                if (receiptFailure is not null) primary.Data["LifecycleReceiptFailure"] = receiptFailure.ToString();
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            }
            if (receiptFailure is not null) cleanupFailures.Add(receiptFailure);
            if (cleanupFailures.Count != 0) throw new AggregateException("Lifecycle cleanup/evidence failed.", cleanupFailures);
        }
        finally { cleanupFailures = null; fixtureRoots = null; }
    }

    private static void ValidateAbsoluteNoReparse(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("An absolute evidence/fixture path is required.");
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent)
        {
            if (File.Exists(item.FullName)) throw new IOException("A directory path resolves to a file.");
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse points are not permitted in evidence/fixture paths.");
        }
    }
    private static object Diagnostics()
    {
        var info = GC.GetGCMemoryInfo();
        return new
        {
            captured = DateTimeOffset.UtcNow, totalMemory = GC.GetTotalMemory(false),
            totalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true),
            gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
            info.HeapSizeBytes, info.FragmentedBytes, info.TotalCommittedBytes,
            info.MemoryLoadBytes, info.HighMemoryLoadThresholdBytes, info.Index
        };
    }

    private sealed class OwnerBarrier
    {
        private int attempts;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken OwnerToken;
        internal async Task BeforePrepare(CancellationToken token)
        {
            if (Interlocked.Increment(ref attempts) != 2) return;
            OwnerToken = token;
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(token).ConfigureAwait(false); }
            finally { Exited.TrySetResult(); }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private static readonly string FixtureBase = Path.GetFullPath(Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "sharpdeps-retention-tests"));
        private readonly IndexWriter writer;
        internal string Root { get; } = Path.Combine(FixtureBase, Guid.NewGuid().ToString("N"));
        private string IndexPath => Path.Combine(Root, ".sharpdeps", "index.sqlite");
        private Guid Workspace { get; } = Guid.NewGuid();

        internal Fixture()
        {
            ValidateAbsoluteNoReparse(Root);
            Directory.CreateDirectory(Root);
            ValidateAbsoluteNoReparse(Root);
            (fixtureRoots ?? throw new InvalidOperationException("Fixture requires a lifecycle test scope.")).Add(Root);
            writer = IndexWriter.Open(Root, IndexPath, Workspace, isTrusted: true);
            try { Publish(1); }
            catch { Cleanup(writer.Dispose); throw; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Publish(int generation)
        {
            if (generation is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(generation));
            var name = Names[generation - 1];
            var source = $"class {name} {{ }}\n";
            File.WriteAllText(Path.Combine(Root, "App.cs"), source, Utf8);
            var hash = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(source))).ToLowerInvariant();
            var workspaceId = HarnessIdentity.WorkspaceId(Workspace);
            var location = new HarnessLocation("App.cs", null, hash, new(0, source.Length));
            var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
                HarnessGraphContract.IdentityVersion, workspaceId, $"retention-{generation}", generation,
                HarnessCoverage.CompleteWithinScope, [],
                [new(workspaceId, HarnessNodeKind.Workspace, "Fixture", null, null),
                    new("file-App", HarnessNodeKind.SourceFile, name, workspaceId, location)], [], [], [], null);
            var snapshot = new IndexSnapshot(graph,
                [new("App.cs", "App.cs", "source", hash, Utf8.GetByteCount(source), source.Length)],
                [], [], [new("file-App", name, "", "", "", "App.cs")]);
            writer.Commit(IndexStage.Create(snapshot));
        }

        internal Task<PreparedExplorationLease> AcquireAsync(PreparedExplorationHost host, CancellationToken token = default)
            => host.AcquireAsync(Root, IndexPath, Workspace, null, token);

        public void Dispose() => Cleanup(writer.Dispose);

        internal static void DeleteOwnedRoot(string root)
        {
            var resolved = Path.GetFullPath(root);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(resolved), FixtureBase, comparison)
                || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
                throw new IOException("Fixture cleanup target is not an exact owned GUID child.");
            ValidateAbsoluteNoReparse(resolved);
            if (!Directory.Exists(resolved)) return;
            // Bound traversal and reject links before recursive deletion. No traversal
            // through a reparse point, nor deleting unbounded unexpected contents.
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((resolved, 0));
            var entries = 0;
            long bytes = 0;
            while (pending.TryPop(out var directory))
            {
                if (directory.Depth > 8) throw new IOException("Fixture cleanup depth exceeded.");
                foreach (var entry in new DirectoryInfo(directory.Path).EnumerateFileSystemInfos())
                {
                    if (++entries > 256 || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Fixture cleanup entry limit/reparse guard rejected contents.");
                    if (entry is DirectoryInfo child) pending.Push((child.FullName, directory.Depth + 1));
                    else if (entry is FileInfo file)
                    {
                        bytes = checked(bytes + file.Length);
                        if (bytes > 32 * 1024 * 1024) throw new IOException("Fixture cleanup size exceeded 32 MiB.");
                    }
                }
            }
            Directory.Delete(resolved, recursive: true);
        }
    }
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate)
            {
                var timer = new ManualTimer(this, callback, state);
                timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }

        internal void Advance(TimeSpan elapsed)
        {
            lock (gate) ticks = checked(ticks + elapsed.Ticks);
            while (true)
            {
                ManualTimer? timer;
                lock (gate)
                {
                    timer = timers.FirstOrDefault(candidate => candidate.Due <= ticks);
                    if (timer is null) return;
                    timer.Due = timer.Period > 0 ? checked(timer.Due + timer.Period) : long.MaxValue;
                }
                timer.Callback(timer.State);
            }
        }

        private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            internal TimerCallback Callback { get; } = callback;
            internal object? State { get; } = state;
            internal long Due { get; set; } = long.MaxValue;
            internal long Period { get; private set; }
            private bool disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock.gate)
                {
                    if (disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(clock.ticks + dueTime.Ticks);
                    Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (clock.gate)
                {
                    disposed = true;
                    clock.timers.Remove(this);
                }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
