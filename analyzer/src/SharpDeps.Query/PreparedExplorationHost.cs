namespace SharpDeps.Query;

using SharpDeps.Analysis.Core.Diagnostics;
using SharpDeps.Index;

/// <summary>Owns bounded saved generations. Idle entries retain no SQLite transaction.</summary>
internal sealed class PreparedExplorationHost : IAsyncDisposable
{
    private readonly record struct Key(string Root, string Index, Guid Workspace);
    private sealed class Entry(Key key, long now)
    {
        internal readonly Key Key = key;
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IndexReadSession? Session;
        internal ExplorationSnapshot? Prepared;
        internal int References;
        internal long LastUsed = now;
        internal bool Retired;
        internal int Closing;
    }

    private sealed record OwnerResult(IndexReader Reader, ExplorationSnapshot Prepared);
    private readonly object sync = new();
    private readonly Dictionary<Key, Entry> entries = [];
    private readonly HashSet<Entry> liveEntries = [];
    private readonly CancellationTokenSource stopping = new();
    private readonly CancellationToken lifetimeToken;
    private readonly int maximumEntries;
    private readonly TimeSpan idleLifetime;
    private readonly int preparationMilliseconds;
    private readonly TimeProvider timeProvider;
    private readonly Func<CancellationToken, Task>? beforePrepare;
    private ITimer? timer;
    private bool disposed;
    private Task? shutdown;

    internal PreparedExplorationHost(int maximumEntries = 1, TimeSpan? idleLifetime = null,
        int preparationMilliseconds = 30000, TimeProvider? timeProvider = null,
        Func<CancellationToken, Task>? beforePrepare = null)
    {
        if (maximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (preparationMilliseconds is < 10 or > 30000) throw new ArgumentOutOfRangeException(nameof(preparationMilliseconds));
        this.idleLifetime = idleLifetime ?? TimeSpan.FromMinutes(5);
        if (this.idleLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleLifetime));
        this.maximumEntries = maximumEntries;
        this.preparationMilliseconds = preparationMilliseconds;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.beforePrepare = beforePrepare;
        lifetimeToken = stopping.Token;
    }

    internal CancellationToken LifetimeToken => lifetimeToken;

    internal async Task<PreparedExplorationLease> AcquireAsync(string root, string indexPath, Guid workspaceUuid,
        string? metricsDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = new Key(Canonical(root), Canonical(indexPath), workspaceUuid);
        var entry = Admit(key);
        var gateOwned = false;
        Task<OwnerResult>? work = null;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, lifetimeToken);
        try
        {
            using (PhaseMetrics.Measure("explore.host-wait"))
                await entry.Gate.WaitAsync(wait.Token).ConfigureAwait(false);
            gateOwned = true;
            wait.Token.ThrowIfCancellationRequested();
            // Owner traces have their own clock and never inherit a request's ambient scope.
            var ownerId = Guid.NewGuid().ToString("N");
            PhaseMetrics.Decision("explore.host-owner", ownerId);
            work = Detached(() => Task.Run(() => PrepareAsync(entry, metricsDirectory, ownerId)));
            OwnerResult ready;
            using (PhaseMetrics.Measure("explore.host-prepare-wait"))
                ready = await work.WaitAsync(wait.Token).ConfigureAwait(false);
            wait.Token.ThrowIfCancellationRequested();
            return new PreparedExplorationLease(ready.Reader, ready.Prepared, () => Release(entry));
        }
        catch
        {
            if (gateOwned && work is not null)
            {
                // The caller leaves immediately. Exactly this continuation owns any eventual lease.
                _ = Detached(() => Task.Run(() => ReleaseAbandonedAsync(work, entry)));
            }
            else if (gateOwned) Release(entry);
            else DropReference(entry);
            throw;
        }
    }

    private async Task<OwnerResult> PrepareAsync(Entry entry, string? metricsDirectory, string ownerId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(preparationMilliseconds), timeProvider);
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, lifetimeToken);
        var ownerDirectory = metricsDirectory is null ? null : Path.Combine(metricsDirectory, "owners", ownerId);
        using var metrics = PhaseMetrics.TryStart(ownerDirectory, "explore-prepare", entry.Key.Root);
        PhaseMetrics.Decision("explore.host-owner", ownerId);
        IndexReader? reader = null;
        try
        {
            if (beforePrepare is not null) await beforePrepare(owner.Token).ConfigureAwait(false);
            owner.Token.ThrowIfCancellationRequested();
            entry.Session ??= new IndexReadSession(entry.Key.Index, entry.Key.Workspace);
            reader = entry.Session.Acquire(owner.Token);
            var snapshot = reader.Snapshot(owner.Token);
            var prepared = entry.Prepared;
            if (prepared is null || !ReferenceEquals(prepared.saved, snapshot))
            {
                using var standard = PhaseMetrics.Measure("explore.prepare-standard");
                prepared = new ExplorationSnapshot(snapshot, owner.Token,
                    reader.ValidatedNodes, reader.ValidatedOccurrences, reader.ValidatedFiles);
                _ = prepared.GetSearchIndex(owner.Token);
                owner.Token.ThrowIfCancellationRequested();
                entry.Prepared = prepared;
                PhaseMetrics.Decision("explore.host", "generation-prepared");
            }
            else PhaseMetrics.Decision("explore.host", "generation-reused");
            owner.Token.ThrowIfCancellationRequested();
            metrics?.Complete();
            return new(reader, prepared);
        }
        catch (Exception error)
        {
            metrics?.Fail(error is OperationCanceledException);
            try { reader?.Dispose(); }
            finally
            {
                entry.Prepared = null;
                var failed = entry.Session;
                entry.Session = null;
                failed?.Dispose();
            }
            if (error is OperationCanceledException && timeout.IsCancellationRequested && !lifetimeToken.IsCancellationRequested)
                throw new IndexStoreException("PREPARATION_TIMEOUT", "The independent saved-generation preparation deadline expired.");
            throw;
        }
    }

    private async Task ReleaseAbandonedAsync(Task<OwnerResult> work, Entry entry)
    {
        try
        {
            var ready = await work.ConfigureAwait(false);
            ready.Reader.Dispose();
        }
        catch { /* Preparation records its failure; abandoned callers receive no evidence. */ }
        finally { Release(entry); }
    }

    private Entry Admit(Key key)
    {
        List<Entry> retired = [];
        Entry result;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var now = timeProvider.GetTimestamp();
            foreach (var entry in entries.Values.ToArray())
                if (entry.References == 0 && timeProvider.GetElapsedTime(entry.LastUsed, now) >= idleLifetime)
                    Retire(entry, retired);
            if (!entries.TryGetValue(key, out result!))
            {
                if (entries.Count >= maximumEntries)
                {
                    var idle = entries.Values.Where(entry => entry.References == 0).OrderBy(entry => entry.LastUsed).FirstOrDefault();
                    if (idle is not null) Retire(idle, retired);
                }
                if (entries.Count >= maximumEntries)
                    result = null!;
                else
                {
                    result = new(key, now);
                    entries.Add(key, result);
                    liveEntries.Add(result);
                }
            }
            if (result is not null)
            {
                result.References++;
                timer ??= Detached(() => timeProvider.CreateTimer(_ => ExpireIdle(), null,
                    Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
            }
            ArmTimer();
        }
        foreach (var entry in retired) Close(entry);
        if (result is null) throw new IndexStoreException("INDEX_PREPARATION_BUSY", "The prepared-index capacity is occupied by active requests.");
        return result;
    }

    private void Retire(Entry entry, List<Entry> retired)
    {
        entries.Remove(entry.Key);
        entry.Retired = true;
        retired.Add(entry);
    }

    private void ExpireIdle()
    {
        List<Entry> retired = [];
        lock (sync)
        {
            if (disposed) return;
            var now = timeProvider.GetTimestamp();
            foreach (var entry in entries.Values.ToArray())
                if (entry.References == 0 && timeProvider.GetElapsedTime(entry.LastUsed, now) >= idleLifetime)
                    Retire(entry, retired);
            ArmTimer();
        }
        foreach (var entry in retired) Close(entry);
    }

    private void Release(Entry entry)
    {
        entry.Gate.Release();
        DropReference(entry);
    }

    private void DropReference(Entry entry)
    {
        bool close;
        lock (sync)
        {
            entry.References--;
            entry.LastUsed = timeProvider.GetTimestamp();
            close = entry.Retired && entry.References == 0;
            ArmTimer();
        }
        if (close) Close(entry);
    }

    private void Close(Entry entry)
    {
        if (Interlocked.Exchange(ref entry.Closing, 1) != 0) return;
        try
        {
            entry.Prepared = null;
            entry.Session?.Dispose();
            entry.Session = null;
            entry.Gate.Dispose();
            entry.Closed.TrySetResult();
        }
        catch (Exception error) { entry.Closed.TrySetException(error); }
        finally { lock (sync) liveEntries.Remove(entry); }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (shutdown is not null) return new(shutdown);
            disposed = true;
            var remaining = liveEntries.ToArray();
            foreach (var entry in remaining) entry.Retired = true;
            entries.Clear();
            shutdown = Detached(() => Task.Run(() => StopAndDrainAsync(remaining)));
            return new(shutdown);
        }
    }

    private async Task StopAndDrainAsync(Entry[] remaining)
    {
        if (timer is not null) await timer.DisposeAsync().ConfigureAwait(false);
        stopping.Cancel();
        foreach (var entry in remaining)
            if (Volatile.Read(ref entry.References) == 0) Close(entry);
        await Task.WhenAll(remaining.Select(entry => entry.Closed.Task)).ConfigureAwait(false);
        stopping.Dispose();
    }

    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // Called under sync. Only idle entries have an expiry; no polling or rooted idle-empty timer.
    private void ArmTimer()
    {
        if (disposed || timer is null) return;
        var due = Timeout.InfiniteTimeSpan;
        var now = timeProvider.GetTimestamp();
        foreach (var entry in entries.Values)
        {
            if (entry.References != 0) continue;
            var remaining = idleLifetime - timeProvider.GetElapsedTime(entry.LastUsed, now);
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            if (due == Timeout.InfiniteTimeSpan || remaining < due) due = remaining;
        }
        timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private static T Detached<T>(Func<T> create)
    {
        if (ExecutionContext.IsFlowSuppressed()) return create();
        using (ExecutionContext.SuppressFlow()) return create();
    }
}

internal sealed class PreparedExplorationLease(IndexReader reader, ExplorationSnapshot prepared, Action release) : IDisposable
{
    private int disposed;
    internal IndexReader Reader { get; } = reader;
    internal ExplorationSnapshot Prepared { get; } = prepared;
    internal PinnedQueryIndex Pin(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return new(Reader, Prepared, token);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { Reader.Dispose(); }
        finally { release(); }
    }
}
