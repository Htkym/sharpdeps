namespace SharpDeps.Cli;

using System.Diagnostics;
using System.IO.Pipes;
using SharpDeps.Index;

/// <summary>One OS writer lease from before analysis until all admitted work drains.</summary>
internal sealed class CliCoordinator : IAsyncDisposable
{
    private readonly IndexWriter writer;
    private readonly Func<CoordinatorUpdate, CancellationToken, Task<CliIndexBuild>> build;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationTokenSource admission = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly SemaphoreSlim slots = new(32);
    private readonly object gate = new();
    private readonly List<Batch> pending = [];
    private readonly HashSet<Task> sessions = [];
    private readonly Task runner;
    private readonly Task listener;
    private long desiredRevision;
    private long committedRevision;
    private CoordinatorResult? committed;
    private string state = "idle";
    private bool stopping;
    private bool accepting = true;
    private bool disposed;
    private CoordinatorWatch? watch;
    private string? watchError;
    internal CoordinatorOwner Owner { get; }
    internal Task Stopped => Task.WhenAll(listener, runner);
    // A test observer controls admission interleavings while exercising the actual IPC and writer.
    internal Action<string>? RequestAccepted { get; set; }

    private sealed class Waiter(string requestId, CancellationToken token)
    {
        internal string RequestId { get; } = requestId;
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource<CoordinatorReply> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Batch(CoordinatorUpdate update, Action<CancellationToken>? verifyWatchInputs)
    {
        internal CoordinatorUpdate Update { get; } = update;
        internal Action<CancellationToken>? VerifyWatchInputs { get; } = verifyWatchInputs;
        internal List<Waiter> Waiters { get; } = [];
        internal int Attempts { get; set; }
    }

    internal CliCoordinator(IndexWriter writer,
        Func<CoordinatorUpdate, CancellationToken, Task<CliIndexBuild>>? build = null)
    {
        this.writer = writer;
        this.build = build ?? new CoordinatorExecutor(writer).BuildAsync;
        using var process = Process.GetCurrentProcess();
        var instance = Guid.NewGuid();
        Owner = new(CoordinatorProtocol.Version, instance, writer.WorkspaceUuid, writer.CanonicalRoot,
            writer.IndexPath, "sharpdeps-" + instance.ToString("N"), process.Id, process.StartTime.ToUniversalTime().ToString("o"));
        // Prepare the actual listener before publishing its endpoint. Ownership still comes only from writer.
        var first = NewPipe();
        try { CoordinatorProtocol.Publish(Owner); }
        catch { first.Dispose(); throw; }
        runner = RunBatchesAsync();
        listener = ListenAsync(first);
    }

    internal CoordinatorReply Status(string requestId)
    {
        lock (gate) return new(CoordinatorProtocol.Version, Owner.Instance, requestId, committedRevision,
            desiredRevision, state == "idle" && watch is { NativeAvailable: false } ? "degraded" : state,
            committed, watchError);
    }

    internal void StartWatch(CoordinatorUpdate selection, TimeSpan resyncPeriod)
    {
        lock (gate)
        {
            if (!accepting || stopping || watch is not null) throw new CliFailure("COORDINATOR_BUSY", 6);
            watch = new(this, selection, resyncPeriod);
            desiredRevision = checked(desiredRevision + 1);
            state = "dirty";
            watch.Start();
        }
    }

    internal CoordinatorWatch? Watch { get { lock (gate) return watch; } }

    internal void WatchDirty(CoordinatorWatch source)
    {
        lock (gate)
        {
            if (stopping || !ReferenceEquals(watch, source)) return;
            desiredRevision = checked(desiredRevision + 1);
            if (state != "updating") state = "dirty";
        }
    }

    internal void WatchFailed(CoordinatorWatch source, string code)
    {
        lock (gate)
        {
            if (stopping || !ReferenceEquals(watch, source)) return;
            watchError = code;
            if (state != "updating") state = "degraded";
        }
    }

    internal async Task StopWatchAsync()
    {
        CoordinatorWatch? stoppedWatch;
        lock (gate)
        {
            stoppedWatch = watch;
            watch = null;
            if (stoppedWatch is not null)
            {
                desiredRevision = checked(desiredRevision + 1);
                if (!stopping && state != "updating") state = "dirty";
                watchError = null;
            }
        }
        if (stoppedWatch is not null) await stoppedWatch.DisposeAsync();
    }

    internal async Task<CoordinatorReply> UpdateAsync(CoordinatorUpdate update, string requestId, CancellationToken clientToken,
        Action<CancellationToken>? verifyWatchInputs = null)
    {
        var waiter = new Waiter(requestId, clientToken);
        lock (gate)
        {
            if (!accepting || stopping) throw new CliFailure("COORDINATOR_STOPPING", 6);
            clientToken.ThrowIfCancellationRequested();
            if (pending.Sum(batch => batch.Waiters.Count) >= 64 || pending.Count >= 16)
                throw new CliFailure("COORDINATOR_BUSY", 6);
            // A running build is never joined by a request whose input has not been captured.
            var batch = pending.LastOrDefault();
            if (batch is null || batch.Update.Key != update.Key || !ReferenceEquals(batch.VerifyWatchInputs, verifyWatchInputs))
            {
                batch = new(update, verifyWatchInputs);
                pending.Add(batch);
                desiredRevision = checked(desiredRevision + 1);
                signal.Release();
            }
            batch.Waiters.Add(waiter);
            if (state != "updating") state = "dirty";
            RequestAccepted?.Invoke(requestId);
        }
        // This token belongs solely to this waiter; it never cancels the shared operation.
        return await waiter.Completion.Task.WaitAsync(clientToken);
    }

    private async Task RunBatchesAsync()
    {
        try
        {
            while (true)
            {
                await signal.WaitAsync(lifetime.Token);
                await Task.Delay(40, lifetime.Token); // bounded admission window, not a filesystem watcher
                Batch batch;
                long captured;
                lock (gate)
                {
                    if (pending.Count == 0) continue;
                    batch = pending[0];
                    pending.RemoveAt(0);
                    captured = desiredRevision;
                    state = "updating";
                }
                if (batch.Waiters.All(waiter => waiter.Token.IsCancellationRequested))
                {
                    lock (gate)
                    {
                        state = pending.Count == 0 && desiredRevision == committedRevision ? "idle" : "dirty";
                        Complete(batch, captured, null, "CANCELLED");
                    }
                    continue;
                }
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                operation.CancelAfter(TimeSpan.FromSeconds(300));
                try
                {
                    var built = await build(batch.Update, operation.Token);
                    CoordinatorResult result;
                    lock (gate)
                    {
                        if (stopping) throw new CliFailure("COORDINATOR_STOPPING", 6);
                        if (captured != desiredRevision) throw new CliFailure("COORDINATOR_SUPERSEDED", 3);
                        operation.Token.ThrowIfCancellationRequested();
                        // Keep revision admission locked through the entire transaction/publication.
                        writer.Commit(IndexStage.Create(built.Snapshot), operation.Token, () =>
                        {
                            batch.VerifyWatchInputs?.Invoke(operation.Token);
                            built.VerifySelection();
                            batch.VerifyWatchInputs?.Invoke(operation.Token);
                        });
                        var snapshot = built.Snapshot;
                        result = new(snapshot.Graph.WorkspaceId, snapshot.Graph.SnapshotId, snapshot.Graph.Generation,
                            snapshot.Graph.Coverage.ToString(), snapshot.Graph.Diagnostics?.Any(d =>
                                d.Code == "cli.semanticGraphReused" && d.Count == 1) == true ? "markdown-incremental"
                                : snapshot.Graph.Diagnostics?.Any(d => d.Code == "cli.projectCollectorsReused" && d.Count > 0) == true
                                    ? "project-incremental" : "full-rebuild",
                            snapshot.Files.Count, snapshot.Graph.Nodes.Count, snapshot.Graph.Edges.Count);
                        committed = result;
                        committedRevision = captured;
                        if (batch.VerifyWatchInputs is not null) watchError = null;
                        state = pending.Count == 0 ? "idle" : "dirty";
                        Complete(batch, captured, result, null);
                    }
                }
                catch (Exception error)
                {
                    var code = ErrorCode(error, lifetime.IsCancellationRequested);
                    lock (gate)
                    {
                        // Guard failures and known newer input get one bounded fresh extraction.
                        if (!stopping && batch.VerifyWatchInputs is null
                            && (code is "COORDINATOR_SUPERSEDED" or "SOURCE_CHANGED_SINCE_STAGING")
                            && batch.Attempts++ == 0 && batch.Waiters.Any(waiter => !waiter.Token.IsCancellationRequested)
                            && pending.Count < 16)
                        {
                            var matching = pending.FirstOrDefault(queued => queued.Update.Key == batch.Update.Key
                                && ReferenceEquals(queued.VerifyWatchInputs, batch.VerifyWatchInputs));
                            if (matching is null)
                            {
                                pending.Add(batch);
                                desiredRevision = checked(desiredRevision + 1);
                            }
                            else
                            {
                                matching.Waiters.AddRange(batch.Waiters);
                                matching.Attempts = Math.Max(matching.Attempts, batch.Attempts);
                            }
                            state = "dirty";
                            signal.Release();
                        }
                        else
                        {
                            state = stopping ? "stopping" : "degraded";
                            Complete(batch, captured, null, code);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lock (gate)
            {
                foreach (var batch in pending) Complete(batch, desiredRevision, null, "COORDINATOR_STOPPING");
                pending.Clear();
            }
        }
    }

    private void Complete(Batch batch, long revision, CoordinatorResult? result, string? error)
    {
        foreach (var waiter in batch.Waiters)
            waiter.Completion.TrySetResult(new(CoordinatorProtocol.Version, Owner.Instance, waiter.RequestId,
                revision, desiredRevision, state, result, error));
    }

    internal static string ErrorCode(Exception error, bool stopped = false) => error switch
    {
        CliFailure failure => failure.Code,
        CliIndexException failure => failure.Code,
        IndexStoreException failure => failure.Code,
        OperationCanceledException => stopped ? "COORDINATOR_STOPPING" : "TIMEOUT",
        IOException or UnauthorizedAccessException => "ROOT_ACCESS_DENIED",
        _ => "INTERNAL_ERROR"
    };

    private NamedPipeServerStream NewPipe() => new(Owner.Endpoint, PipeDirection.InOut, 33,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenAsync(NamedPipeServerStream first)
    {
        NamedPipeServerStream? pipe = first;
        try
        {
            while (!admission.IsCancellationRequested)
            {
                await slots.WaitAsync(admission.Token);
                try { await pipe!.WaitForConnectionAsync(admission.Token); }
                catch { slots.Release(); throw; }
                var connected = pipe;
                pipe = null;
                var task = SessionAsync(connected);
                lock (gate) sessions.Add(task);
                _ = task.ContinueWith(completed => { lock (gate) sessions.Remove(completed); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                pipe = NewPipe();
            }
        }
        catch (OperationCanceledException) when (admission.IsCancellationRequested) { }
        catch (IOException) { lock (gate) state = "degraded"; lifetime.Cancel(); }
        finally { pipe?.Dispose(); }
    }

    private async Task SessionAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var session = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            // Includes handshake + queue wait. Client arguments cannot extend this hard owner-side limit.
            session.CancelAfter(TimeSpan.FromSeconds(305));
            Task? disconnect = null;
            CoordinatorRequest? request = null;
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                handshake.CancelAfter(TimeSpan.FromSeconds(3));
                request = await CoordinatorProtocol.ReadAsync<CoordinatorRequest>(pipe, handshake.Token);
                Validate(request);
                CoordinatorReply reply;
                if (request.Operation == "status") reply = Status(request.RequestId);
                else
                {
                    var arguments = CliArguments.Parse(request.Arguments);
                    if (!CliPaths.SamePath(arguments.Require("root"), Owner.Root) || (arguments.Get("index") is { } index
                        && !CliPaths.SamePath(CliPaths.InRoot(Owner.Root, index), Owner.IndexPath)))
                        throw new CliFailure("COORDINATOR_REQUEST_INVALID", 6);
                    var update = CoordinatorUpdate.Parse(arguments, Owner.Root, Owner.IndexPath, Owner.WorkspaceUuid);
                    disconnect = WatchDisconnectAsync(pipe, session);
                    reply = await UpdateAsync(update, request.RequestId, session.Token);
                }
                await CoordinatorProtocol.WriteAsync(pipe, reply, session.Token);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (request is not null && !session.IsCancellationRequested)
                {
                    try
                    {
                        var status = Status(request.RequestId);
                        await CoordinatorProtocol.WriteAsync(pipe, status with { Result = null,
                            ErrorCode = ErrorCode(error, lifetime.IsCancellationRequested) }, session.Token);
                    }
                    catch (Exception writeError) when (writeError is IOException or OperationCanceledException or ObjectDisposedException) { }
                }
            }
            finally
            {
                session.Cancel();
                if (disconnect is not null) await disconnect;
                slots.Release();
            }
        }
    }

    private void Validate(CoordinatorRequest request)
    {
        if (request.Protocol != CoordinatorProtocol.Version) throw new CliFailure("COORDINATOR_PROTOCOL_MISMATCH", 6);
        if (request.Instance != Owner.Instance || request.WorkspaceUuid != Owner.WorkspaceUuid
            || !CliPaths.SamePath(request.Root, Owner.Root) || !CliPaths.SamePath(request.IndexPath, Owner.IndexPath)
            || string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 64 || request.RequestId.Any(char.IsControl)
            || request.Operation is not ("status" or "update") || request.Arguments is null
            || request.Arguments.Length > 200 || request.Arguments.Any(argument => argument is null || argument.Length > 8192))
            throw new CliFailure("COORDINATOR_REQUEST_INVALID", 6);
        if (request.Operation == "update" && !request.Trusted) throw new CliFailure("WORKSPACE_UNTRUSTED", 5);
        if (request.Operation == "status" && request.Arguments.Length != 0) throw new CliFailure("COORDINATOR_REQUEST_INVALID", 6);
    }

    private static async Task WatchDisconnectAsync(Stream pipe, CancellationTokenSource session)
    {
        try
        {
            // One request per connection. EOF/cancel and unexpected extra bytes cancel only this waiter.
            await pipe.ReadExactlyAsync(new byte[1], session.Token);
            session.Cancel();
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        { if (!session.IsCancellationRequested) session.Cancel(); }
    }

    internal async Task DrainAsync()
    {
        await StopWatchAsync();
        // Close admission while preserving already admitted work, including other clients.
        lock (gate) accepting = false;
        admission.Cancel();
        await listener;
        Task[] admitted;
        lock (gate) admitted = sessions.ToArray();
        await Task.WhenAll(admitted); // response writes complete before the shared lifetime is cancelled
        while (true)
        {
            lock (gate)
            {
                if (pending.Count == 0 && state != "updating") { stopping = true; state = "stopping"; break; }
            }
            await Task.Delay(20, lifetime.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            accepting = false;
            stopping = true;
            state = "stopping";
        }
        lifetime.Cancel();
        admission.Cancel();
        await StopWatchAsync();
        await listener;
        await runner;
        Task[] admitted;
        lock (gate) admitted = sessions.ToArray();
        await Task.WhenAll(admitted);
        // Keep a stale bounded descriptor for diagnosis. Only a newly acquired OS lease can replace it.
        writer.Dispose();
        lifetime.Dispose();
        admission.Dispose();
        signal.Dispose();
        slots.Dispose();
    }
}
