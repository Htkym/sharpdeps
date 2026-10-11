namespace SharpDeps.Cli;

/// <summary>Input hints and periodic reconciliation owned by the existing OS writer lease.</summary>
internal sealed class CoordinatorWatch(CliCoordinator owner, CoordinatorUpdate selection, TimeSpan resyncPeriod) : IAsyncDisposable
{
    private readonly CancellationTokenSource stopped = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly object nativeGate = new();
    private FileSystemWatcher? native;
    private Task worker = Task.CompletedTask;
    private long revision;
    private int nativeAvailable;
    private int ending;
    private bool notificationsEnabled = true;
    internal bool NativeAvailable => Volatile.Read(ref nativeAvailable) != 0;

    internal void Start() => worker = Task.Run(RunAsync);

    private void Changed(object sender, FileSystemEventArgs change)
    {
        if (CliIndexer.IsWatchExcluded(selection.Options.Root, change.FullPath, selection.Options.IndexPath)
            && (change is not RenamedEventArgs rename
                || CliIndexer.IsWatchExcluded(selection.Options.Root, rename.OldFullPath, selection.Options.IndexPath))) return;
        Invalidate();
    }

    private void Failed(object sender, ErrorEventArgs error)
    {
        Volatile.Write(ref nativeAvailable, 0);
        Invalidate(); // overflow and root loss have the same bounded resync path
    }

    private void Invalidate()
    {
        // Advance before taking the owner gate: transaction guards can see an event while admission is locked.
        if (Volatile.Read(ref ending) != 0) return;
        Interlocked.Increment(ref revision);
        owner.WatchDirty(this);
        lock (nativeGate)
        {
            if (Volatile.Read(ref ending) == 0 && wake.CurrentCount == 0) wake.Release();
        }
    }

    private void EnsureNative()
    {
        lock (nativeGate)
        {
            if (Volatile.Read(ref ending) != 0 || NativeAvailable || !notificationsEnabled) return;
            native?.Dispose();
            native = null;
            try
            {
                native = new FileSystemWatcher(selection.Options.Root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                native.Changed += Changed;
                native.Created += Changed;
                native.Deleted += Changed;
                native.Renamed += Changed;
                native.Error += Failed;
                native.EnableRaisingEvents = notificationsEnabled;
                Volatile.Write(ref nativeAvailable, notificationsEnabled ? 1 : 0);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                native?.Dispose();
                native = null;
                Volatile.Write(ref nativeAvailable, 0);
            }
        }
    }

    // A fault hook disables the real OS notification source; the production periodic path stays unchanged.
    internal void SetNotificationsEnabled(bool enabled)
    {
        lock (nativeGate)
        {
            notificationsEnabled = enabled;
            if (native is not null) native.EnableRaisingEvents = enabled;
            Volatile.Write(ref nativeAvailable, native is not null && enabled ? 1 : 0);
        }
    }

    private async Task RunAsync()
    {
        var full = true;
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                EnsureNative();
                try
                {
                    await Task.Delay(100, stopped.Token); // one bounded settle interval per reconciliation
                    var captured = Volatile.Read(ref revision);
                    var inputs = await CliIndexer.CaptureWatchInputsAsync(selection.Options.Root, selection.Options.IndexPath, stopped.Token)
                        ?? throw new CliFailure("CLI_INPUT_BUDGET_EXCEEDED", 4);
                    VerifyRevision(captured);
                    var update = selection with { Full = full, Options = selection.Options with { AllowSemanticReuse = !full } };
                    var reply = await owner.UpdateAsync(update, "watch", stopped.Token, operationToken =>
                    {
                        VerifyRevision(captured);
                        var current = CliIndexer.CaptureWatchInputsAsync(selection.Options.Root, selection.Options.IndexPath, operationToken)
                            .GetAwaiter().GetResult();
                        if (current != inputs) throw new CliFailure("WATCH_INPUT_CHANGED", 3);
                        VerifyRevision(captured);
                    });
                    if (reply.ErrorCode is { } code) owner.WatchFailed(this, code);
                }
                catch (Exception error) when (error is not OutOfMemoryException
                    && !(error is OperationCanceledException && stopped.IsCancellationRequested))
                { owner.WatchFailed(this, CliCoordinator.ErrorCode(error)); }

                // Even an unchanged root hash needs full extraction: ancestor config, SDK and external inputs may change.
                full = !await wake.WaitAsync(resyncPeriod, stopped.Token);
                if (full) owner.WatchDirty(this);
            }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
    }

    private void VerifyRevision(long captured)
    {
        if (Volatile.Read(ref ending) != 0 || captured != Volatile.Read(ref revision))
            throw new CliFailure("WATCH_INPUT_CHANGED", 3);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref ending, 1) != 0) { await worker; return; }
        lock (nativeGate)
        {
            stopped.Cancel();
            Interlocked.Increment(ref revision);
            native?.Dispose();
            native = null;
            Volatile.Write(ref nativeAvailable, 0);
        }
        await worker;
        lock (nativeGate)
        {
            // Delayed callbacks check ending before touching the disposed semaphore/token.
            wake.Dispose();
            stopped.Dispose();
        }
    }
}
