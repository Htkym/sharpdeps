namespace SharpDeps.Query.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Index;
using Xunit;

public sealed class PreparedExplorationHostTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CancellingTheFirstWaiterLeavesItsOwnerAliveAndTheNextWaiterCanReusePreparation()
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var release = Signal();
        var ambient = new AsyncLocal<string?> { Value = "caller-context" };
        string? ownerContext = null;
        CancellationToken ownerToken = default;
        var attempts = 0;
        await using var host = new PreparedExplorationHost(beforePrepare: async token =>
        {
            if (Interlocked.Increment(ref attempts) != 1) return;
            ownerToken = token;
            ownerContext = ambient.Value;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        using var caller = new CancellationTokenSource();
        var abandoned = fixture.AcquireAsync(host, caller.Token);
        await entered.Task.WaitAsync(Guard);
        var next = fixture.AcquireAsync(host);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned.WaitAsync(Guard));
        Assert.False(ownerToken.IsCancellationRequested);
        Assert.Null(ownerContext);
        Assert.False(next.IsCompleted);
        release.TrySetResult();
        ExplorationSnapshot prepared;
        using (var lease = await next.WaitAsync(Guard))
        {
            prepared = lease.Prepared;
            Assert.Same(lease.Reader.Snapshot(), prepared.saved);
            Assert.Same(prepared, lease.Pin(default).PrepareExploration(default));
        }
        var previousMetrics = Directory.GetFiles(fixture.MetricsDirectory, "*.jsonl", SearchOption.AllDirectories).ToHashSet(StringComparer.Ordinal);
        using var reused = await fixture.AcquireAsync(host).WaitAsync(Guard);
        var reuseMetrics = File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.MetricsDirectory, "*.jsonl", SearchOption.AllDirectories).Except(previousMetrics)));
        AssertReuseOrFullValidationFallback(prepared, reused.Prepared, reuseMetrics);
    }

    [Fact]
    public async Task IndependentOwnerDeadlineEndsAbandonedPreparationAndTheNextRequestCanRetry()
    {
        using var fixture = new Fixture();
        var clock = new ManualTimeProvider();
        var entered = Signal();
        var stopped = Signal();
        var attempts = 0;
        CancellationToken ownerToken = default;
        await using var host = new PreparedExplorationHost(preparationMilliseconds: 100, timeProvider: clock,
            beforePrepare: async token =>
            {
                if (Interlocked.Increment(ref attempts) != 1) return;
                ownerToken = token;
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { stopped.TrySetResult(); }
            });
        using var caller = new CancellationTokenSource();
        var abandoned = fixture.AcquireAsync(host, caller.Token);
        await entered.Task.WaitAsync(Guard);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned.WaitAsync(Guard));
        Assert.False(ownerToken.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMilliseconds(101));
        await stopped.Task.WaitAsync(Guard);
        Assert.True(ownerToken.IsCancellationRequested);
        using var recovered = await fixture.AcquireAsync(host).WaitAsync(Guard);
        Assert.Equal(fixture.WorkspaceId, recovered.Prepared.saved.Graph.WorkspaceId);
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task AnOwnerTimeoutIsReportedAsPreparationTimeoutWithoutCancellingTheCaller()
    {
        using var fixture = new Fixture();
        var clock = new ManualTimeProvider();
        var entered = Signal();
        await using var host = new PreparedExplorationHost(preparationMilliseconds: 100, timeProvider: clock,
            beforePrepare: async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        using var caller = new CancellationTokenSource();
        var pending = fixture.AcquireAsync(host, caller.Token);
        await entered.Task.WaitAsync(Guard);
        clock.Advance(TimeSpan.FromMilliseconds(101));
        var error = await Assert.ThrowsAsync<IndexStoreException>(async () => await pending.WaitAsync(Guard));
        Assert.Equal("PREPARATION_TIMEOUT", error.Code);
        Assert.False(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task AFailedOwnerIsRetriedOnlyByTheNextRequest()
    {
        using var fixture = new Fixture();
        var attempts = 0;
        await using var host = new PreparedExplorationHost(beforePrepare: token =>
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref attempts) == 1)
                throw new IndexStoreException("INDEX_INVALID_SNAPSHOT", "Injected preparation failure.");
            return Task.CompletedTask;
        });
        var error = await Assert.ThrowsAsync<IndexStoreException>(() => fixture.AcquireAsync(host).WaitAsync(Guard));
        Assert.Equal("INDEX_INVALID_SNAPSHOT", error.Code);
        Assert.Equal(1, Volatile.Read(ref attempts));
        using var recovered = await fixture.AcquireAsync(host).WaitAsync(Guard);
        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Equal(fixture.WorkspaceId, recovered.Reader.Snapshot().Graph.WorkspaceId);
    }

    [Fact]
    public async Task CancellingAQueuedSqlLeaseCannotCancelOrDisposeTheActiveLease()
    {
        using var fixture = new Fixture();
        var attempts = 0;
        await using var host = new PreparedExplorationHost(beforePrepare: _ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.CompletedTask;
        });
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fixture.AcquireAsync(host, cancelled.Token).WaitAsync(Guard));
            Assert.Equal(0, Volatile.Read(ref attempts));
        }
        var holder = await fixture.AcquireAsync(host).WaitAsync(Guard);
        try
        {
            using var queuedCaller = new CancellationTokenSource();
            var queued = fixture.AcquireAsync(host, queuedCaller.Token);
            queuedCaller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued.WaitAsync(Guard));
            Assert.Equal(1, Volatile.Read(ref attempts));
            Assert.Same(holder.Prepared.saved, holder.Reader.Snapshot());
            Assert.Contains(holder.Reader.Search("Before", 10), hit => hit.NodeId == "file-App");
        }
        finally { holder.Dispose(); }
        using var next = await fixture.AcquireAsync(host).WaitAsync(Guard);
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task CapacityAndIdleExpiryKeepActiveRootsAliveAndReleaseTheirPreparedGeneration()
    {
        using var first = new Fixture();
        using var second = new Fixture(first.Workspace);
        var clock = new ManualTimeProvider();
        await using var host = new PreparedExplorationHost(maximumEntries: 1,
            idleLifetime: TimeSpan.FromSeconds(10), timeProvider: clock);
        var active = await first.AcquireAsync(host).WaitAsync(Guard);
        var original = active.Prepared;
        try
        {
            clock.Advance(TimeSpan.FromSeconds(11));
            var busy = await Assert.ThrowsAsync<IndexStoreException>(() => second.AcquireAsync(host).WaitAsync(Guard));
            Assert.Equal("INDEX_PREPARATION_BUSY", busy.Code);
            Assert.Same(original.saved, active.Reader.Snapshot());
        }
        finally { active.Dispose(); }
        var previousMetrics = Directory.GetFiles(first.MetricsDirectory, "*.jsonl", SearchOption.AllDirectories).ToHashSet(StringComparer.Ordinal);
        using (var warm = await first.AcquireAsync(host).WaitAsync(Guard))
        {
            var reuseMetrics = File.ReadAllText(Assert.Single(Directory.GetFiles(first.MetricsDirectory, "*.jsonl", SearchOption.AllDirectories).Except(previousMetrics)));
            AssertReuseOrFullValidationFallback(original, warm.Prepared, reuseMetrics);
        }
        clock.Advance(TimeSpan.FromSeconds(11));
        using (var expired = await first.AcquireAsync(host).WaitAsync(Guard))
            Assert.NotSame(original, expired.Prepared);
        clock.Advance(TimeSpan.FromSeconds(11));
        using (var other = await second.AcquireAsync(host).WaitAsync(Guard))
        {
            Assert.Equal(second.WorkspaceId, other.Prepared.saved.Graph.WorkspaceId);
            Assert.NotSame(original, other.Prepared);
            var busy = await Assert.ThrowsAsync<IndexStoreException>(() => first.AcquireAsync(host).WaitAsync(Guard));
            Assert.Equal("INDEX_PREPARATION_BUSY", busy.Code);
        }
        clock.Advance(TimeSpan.FromSeconds(11));
        using var rebuilt = await first.AcquireAsync(host).WaitAsync(Guard);
        Assert.NotSame(original, rebuilt.Prepared);
        Assert.Equal(first.WorkspaceId, rebuilt.Prepared.saved.Graph.WorkspaceId);
    }

    [Fact]
    public async Task ShutdownCancelsOwnersAndWaitersButDrainsActiveLeasesBeforeCompleting()
    {
        using var first = new Fixture();
        using var second = new Fixture();
        var entered = Signal();
        var stopped = Signal();
        var attempts = 0;
        await using var host = new PreparedExplorationHost(maximumEntries: 2, beforePrepare: async token =>
        {
            if (Interlocked.Increment(ref attempts) == 1) return;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped.TrySetResult(); }
        });
        var active = await first.AcquireAsync(host).WaitAsync(Guard);
        try
        {
            var owner = second.AcquireAsync(host);
            await entered.Task.WaitAsync(Guard);
            var waiter = second.AcquireAsync(host);
            var shutdown = host.DisposeAsync().AsTask();
            await stopped.Task.WaitAsync(Guard);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await owner.WaitAsync(Guard));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter.WaitAsync(Guard));
            Assert.True(host.LifetimeToken.IsCancellationRequested);
            Assert.False(shutdown.IsCompleted);
            Assert.Same(active.Prepared.saved, active.Reader.Snapshot());
            active.Dispose();
            await shutdown.WaitAsync(Guard);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => first.AcquireAsync(host).WaitAsync(Guard));
        }
        finally
        {
            active.Dispose();
            await host.DisposeAsync().AsTask().WaitAsync(Guard);
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertReuseOrFullValidationFallback(ExplorationSnapshot expected, ExplorationSnapshot actual, string metrics)
    {
        if (metrics.Contains("\"reason\":\"full-validation-fallback\"", StringComparison.Ordinal))
            Assert.NotSame(expected, actual);
        else
        {
            Assert.Contains("\"reason\":\"validated-snapshot-hit\"", metrics, StringComparison.Ordinal);
            Assert.Same(expected, actual);
        }
        Assert.Equal(JsonSerializer.Serialize(expected.saved, QueryJson.Options), JsonSerializer.Serialize(actual.saved, QueryJson.Options));
    }

    private sealed class Fixture : IDisposable
    {
        private const string Source = "class App { }\n";
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private static readonly string FixtureBase = Path.GetFullPath(Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "sharpdeps-prepared-host-tests"));
        private readonly IndexWriter writer;
        internal string Root { get; } = Path.Combine(FixtureBase, Guid.NewGuid().ToString("N"));
        internal string IndexPath => Path.Combine(Root, ".sharpdeps", "index.sqlite");
        internal string MetricsDirectory => Path.Combine(Root, ".sharpdeps", "owner-metrics");
        internal Guid Workspace { get; }
        internal string WorkspaceId => HarnessIdentity.WorkspaceId(Workspace);

        internal Fixture(Guid? workspace = null)
        {
            Workspace = workspace ?? Guid.NewGuid();
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "App.cs"), Source, Utf8);
            var hash = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(Source))).ToLowerInvariant();
            var location = new HarnessLocation("App.cs", null, hash, new(0, Source.Length));
            var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
                HarnessGraphContract.IdentityVersion, WorkspaceId, "prepared-host-fixture", 1, HarnessCoverage.CompleteWithinScope,
                [], [new(WorkspaceId, HarnessNodeKind.Workspace, "Fixture", null, null),
                    new("file-App", HarnessNodeKind.SourceFile, "Before", WorkspaceId, location)], [], [], [], null);
            var snapshot = new IndexSnapshot(graph, [new("App.cs", "App.cs", "source", hash, Utf8.GetByteCount(Source), Source.Length)],
                [], [], [new("file-App", "Before", "", "", "", "App.cs")]);
            writer = IndexWriter.Open(Root, IndexPath, Workspace, isTrusted: true);
            writer.Commit(IndexStage.Create(snapshot));
        }

        internal Task<PreparedExplorationLease> AcquireAsync(PreparedExplorationHost host, CancellationToken token = default)
            => host.AcquireAsync(Root, IndexPath, Workspace, MetricsDirectory, token);

        public void Dispose()
        {
            writer.Dispose();
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(FixtureBase + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
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
