namespace SharpDeps.Cli;

using System.Reflection;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpDeps.Index;
using SharpDeps.Query;

public static class CliApplication
{
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken token = default)
    {
        CliArguments? options = null;
        CancellationTokenSource? explorationDeadline = null;
        Stopwatch? explorationClock = null;
        try
        {
            options = CliArguments.Parse(args);
            token.ThrowIfCancellationRequested();
            if (options.Command == "help")
                return await Reply(stdout, options, new { commands = new[] { "explore", "index", "update", "serve", "coordinator-status", "status", "search", "symbol", "callers", "callees", "impact", "context", "query", "doctor", "help" },
                    root = "--root absolute-directory",
                    index = "--target root-contained.slnx|.sln|.csproj --trusted",
                    updateMode = "closed-input-project-markdown-or-full-rebuild",
                    query = "--term text | --id graph-id; --freshness allow-stale|require-fresh|refresh",
                    explore = "--term names-or-file-paths [--project-id id --variant id]; returns bounded source evidence with explicit freshness and omissions",
                    watch = "serve --trusted --watch --target root-contained.csproj|.sln|.slnx [--watch-resync-seconds 30]",
                    helpContract = "docs/contracts/cli-skill-v1.ja.md", package = $"SharpDeps.Cli@{typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]}" });
            if (options.Command == "doctor")
                return await Reply(stdout, options, new { runtime = RuntimeInformation.FrameworkDescription, sdkProbe = "not-run",
                    toolTargetFramework = "net10.0", indexing = "trusted target SDK/MSBuild required", savedQuery = "runtime-only; no restore/build/network",
                    configurationProof = "not-connected",
                    updateMode = "closed-input-project-markdown-or-full-rebuild"
                });
            // Validate request syntax before opening any stored data.
            var exploration = options.Command == "explore" ? options.ExplorationRequest() : null;
            var request = options.Command is "index" or "update" or "serve" or "coordinator-status" or "explore" ? null : options.QueryRequest();
            if (exploration is not null)
            {
                explorationClock = Stopwatch.StartNew();
                explorationDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                explorationDeadline.CancelAfter(exploration.Budget!.MaxMilliseconds);
            }
            if (options.Command is "index" or "update" or "serve" && !options.Has("trusted")) throw new CliFailure("WORKSPACE_UNTRUSTED", 5);
            var root = CliPaths.Root(options.Require("root"));
            var indexPath = CliPaths.InRoot(root, options.Get("index") ?? ".sharpdeps/index.sqlite");
            foreach (var sidecar in new[] { indexPath + "-wal", indexPath + "-shm", indexPath + ".writer.lock" }) CliPaths.RejectLinks(sidecar);
            if (options.Command is "index" or "update")
                return await IndexAsync(options, root, indexPath, stdout, stderr, token);
            if (options.Command == "serve") return await ServeAsync(options, root, indexPath, stdout, stderr, token);
            if (options.Command == "coordinator-status") return await CoordinatorStatusAsync(options, root, indexPath, stdout, token);
            var state = CliWorkspaceState.Open(root, create: false);
            if (!File.Exists(indexPath)) throw new CliFailure("INDEX_MISSING", 3);
            var queryToken = explorationDeadline?.Token ?? token;
            queryToken.ThrowIfCancellationRequested();
            using var reader = IndexReader.Open(indexPath, state.WorkspaceUuid, queryToken);
            var pinned = new PinnedQueryIndex(reader, queryToken);
            var sourceWorkspace = options.Has("trusted") ? new QueryWorkspace(root,
                ownerToken => VerifyQueryOwner(root, indexPath, state.WorkspaceUuid, pinned.Snapshot, ownerToken)) : null;
            if (exploration is not null)
            {
                if (explorationClock!.ElapsedMilliseconds >= exploration.Budget!.MaxMilliseconds)
                    throw new CliFailure("QUERY_TIMEOUT", 4);
                var explored = new ExplorationService(pinned, sourceWorkspace, options.Has("trusted"))
                    .Execute(exploration, token, queryToken);
                token.ThrowIfCancellationRequested();
                if (explorationClock.ElapsedMilliseconds >= exploration.Budget.MaxMilliseconds)
                    throw new CliFailure("QUERY_TIMEOUT", 4);
                await stdout.WriteLineAsync(explored.Text);
                return explored.Succeeded ? 0 : explored.Errors.Count == 0 ? 4 : ExitFor(explored.Errors[0].Code);
            }
            var service = new QueryService(pinned, sourceWorkspace, options.Has("trusted"), Convert.FromBase64String(state.CursorKey));
            var reply = service.Execute(request!, token);
            await stdout.WriteLineAsync(reply.Json);
            return reply.Succeeded ? 0 : ExitFor(reply.Envelope.Errors[0].Code);
        }
        catch (CliFailure error) { return await Error(stdout, options, error.Code, error.ExitCode); }
        catch (CliIndexException error) { return await Error(stdout, options, error.Code, error.ExitCode); }
        catch (IndexStoreException error) { return await Error(stdout, options, error.Code, ExitFor(error.Code)); }
        catch (OperationCanceledException)
        {
            return explorationDeadline?.IsCancellationRequested == true && !token.IsCancellationRequested
                ? await Error(stdout, options, "QUERY_TIMEOUT", 4)
                : await Error(stdout, options, "CANCELLED", 130);
        }
        catch (ArgumentException) { return await Error(stdout, options, "ARGUMENT_INVALID", 2); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return await Error(stdout, options, "ROOT_ACCESS_DENIED", 5); }
        catch (Exception) { return await Error(stdout, options, "INTERNAL_ERROR", 7); }
        finally { explorationDeadline?.Dispose(); }
    }

    private static QueryInputInventory VerifyQueryOwner(string root, string indexPath, Guid uuid,
        IndexSnapshot snapshot, CancellationToken token)
    {
        try
        {
            var owner = CoordinatorProtocol.Discover(root, indexPath, uuid);
            if (owner is null) throw new QuerySourceException("QUERY_PROOF_UNAVAILABLE");
            var reply = CoordinatorProtocol.TrySendAsync(owner, new(CoordinatorProtocol.Version, owner.Instance, uuid,
                "query-proof", "verify-query", root, indexPath, true,
                [snapshot.Graph.SnapshotId, snapshot.Graph.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)]),
                token).GetAwaiter().GetResult();
            if (reply?.ErrorCode is not null || reply?.QueryInventory is not { ConfigurationVerified: true } inventory
                || reply.Result?.SnapshotId != snapshot.Graph.SnapshotId || reply.Result.Generation != snapshot.Graph.Generation
                || reply.State != "idle" || reply.Revision != reply.DesiredRevision)
                throw new QuerySourceException("QUERY_PROOF_UNAVAILABLE");
            return inventory;
        }
        catch (Exception error) when (error is CliFailure or IOException or UnauthorizedAccessException)
        { throw new QuerySourceException("QUERY_PROOF_UNAVAILABLE"); }
    }

    // Keep target SDK/MSBuild activation out of the saved-only command path.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> IndexAsync(CliArguments options, string root, string indexPath,
        TextWriter stdout, TextWriter stderr, CancellationToken token)
    {
        // Validate the entire selection before creating workspace identity or trying IPC.
        var update = CoordinatorUpdate.Parse(options, root, indexPath, Guid.Empty);
        var timeout = options.Number("timeout-seconds", 180, 1, 300);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        operation.CancelAfter(TimeSpan.FromSeconds(timeout));
        try
        {
            // A missing identity on an existing index is never silently replaced.
            var existed = File.Exists(indexPath);
            if (options.Command == "update" && !existed) throw new CliFailure("INDEX_MISSING", 3);
            var state = CliWorkspaceState.Open(root, create: !existed);
            update = update with { Options = update.Options with { WorkspaceUuid = state.WorkspaceUuid } };
            var discovered = CoordinatorProtocol.Discover(root, indexPath, state.WorkspaceUuid);
            CoordinatorReply? reply = null;
            if (discovered is not null)
            {
                reply = await CoordinatorProtocol.TrySendAsync(discovered, new(CoordinatorProtocol.Version, discovered.Instance,
                    state.WorkspaceUuid, options.RequestId, "update", root, indexPath, true, update.Arguments()), operation.Token);
            }
            if (reply is null)
            {
                // A stale descriptor never authorizes takeover: this Open must actually acquire the OS lease.
                using var writer = IndexWriter.Open(root, indexPath, state.WorkspaceUuid, isTrusted: true);
                await using var owner = new CliCoordinator(writer);
                if (!options.Has("quiet")) await stderr.WriteLineAsync("sharpdeps: extraction started (no automatic restore)");
                try { reply = await owner.UpdateAsync(update, options.RequestId, operation.Token); }
                finally { await owner.DrainAsync(); } // client cancellation cannot terminate other admitted clients
            }
            if (reply.ErrorCode is { } code) throw new CliFailure(code, ExitFor(code));
            var result = reply.Result ?? throw new CliFailure("COORDINATOR_REPLY_INVALID", 6);
            if (!options.Has("quiet")) await stderr.WriteLineAsync($"sharpdeps: {result.UpdateMode} committed");
            return await Reply(stdout, options, new { result.WorkspaceId, result.SnapshotId, result.Generation,
                result.Coverage, result.UpdateMode, configurationProof = "unverified", coordinatorState = reply.State,
                ownerInstance = reply.Instance, revision = reply.Revision, desiredRevision = reply.DesiredRevision,
                result.Files, result.Nodes, result.Edges });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CliFailure("TIMEOUT", 4); }
    }

    private static async Task<int> ServeAsync(CliArguments options, string root, string indexPath,
        TextWriter stdout, TextWriter stderr, CancellationToken token)
    {
        // Validate target and budgets before creating workspace state or taking ownership.
        var watchUpdate = options.Has("watch") ? CoordinatorUpdate.Parse(options, root, indexPath, Guid.Empty) : null;
        var state = CliWorkspaceState.Open(root, create: !File.Exists(indexPath));
        using var writer = IndexWriter.Open(root, indexPath, state.WorkspaceUuid, isTrusted: true);
        Guid instance;
        await using (var owner = new CliCoordinator(writer))
        {
            instance = owner.Owner.Instance;
            if (watchUpdate is not null)
                owner.StartWatch(watchUpdate with { Options = watchUpdate.Options with { WorkspaceUuid = state.WorkspaceUuid } },
                    TimeSpan.FromSeconds(options.Number("watch-resync-seconds", 30, 1, 3600)));
            if (!options.Has("quiet")) await stderr.WriteLineAsync(watchUpdate is null
                ? "sharpdeps: coordinator ready (local IPC; no automatic restore)"
                : "sharpdeps: coordinator ready (saved-input watch; periodic full resync; no automatic restore)");
            var ownerStopped = owner.Stopped;
            var stopped = await Task.WhenAny(Task.Delay(Timeout.InfiniteTimeSpan, token), ownerStopped);
            if (!token.IsCancellationRequested && stopped == ownerStopped)
                throw new CliFailure("COORDINATOR_UNAVAILABLE", 6);
        }
        return await Reply(stdout, options, new { coordinatorState = "stopped", ownerInstance = instance });
    }

    private static async Task<int> CoordinatorStatusAsync(CliArguments options, string root, string indexPath,
        TextWriter stdout, CancellationToken token)
    {
        var state = CliWorkspaceState.Open(root, create: false);
        var owner = CoordinatorProtocol.Discover(root, indexPath, state.WorkspaceUuid);
        if (owner is null) return await Reply(stdout, options, new { coordinatorState = "absent", configurationProof = "unverified" });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        CoordinatorReply? reply;
        try
        {
            reply = await CoordinatorProtocol.TrySendAsync(owner, new(CoordinatorProtocol.Version, owner.Instance,
                state.WorkspaceUuid, options.RequestId, "status", root, indexPath, false, []), timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CliFailure("TIMEOUT", 4); }
        if (reply?.ErrorCode is { } code) throw new CliFailure(code, ExitFor(code));
        return await Reply(stdout, options, new { coordinatorState = reply?.State ?? "unreachable", ownerInstance = owner.Instance,
            revision = reply?.Revision, desiredRevision = reply?.DesiredRevision, result = reply?.Result,
            configurationProof = "unverified" });
    }

    internal static int ExitFor(string code) => code switch
    {
        "CANCELLED" => 130,
        "WORKSPACE_UNTRUSTED" or "INDEX_UNTRUSTED" or "ROOT_ACCESS_DENIED" or "INVALID_SOURCE_ENCODING" => 5,
        "UPDATE_REQUIRED" or "FRESHNESS_UNVERIFIED" or "FRESHNESS_REQUIREMENT_NOT_MET" or "SOURCE_CHANGED_SINCE_SNAPSHOT"
            or "INDEX_NO_SNAPSHOT" or "INDEX_NOT_FOUND" => 3,
        "SYMBOL_NOT_FOUND" => 2,
        "QUERY_TIMEOUT" or "INCOMPLETE_RESULT" or "OUTPUT_BUDGET_TOO_SMALL" => 4,
        "COORDINATOR_SUPERSEDED" => 3,
        _ when code.StartsWith("COORDINATOR_", StringComparison.Ordinal) => 6,
        _ when code.Contains("CURSOR", StringComparison.Ordinal) || code.StartsWith("INDEX_", StringComparison.Ordinal) => 6,
        _ => 4
    };

    private static async Task<int> Reply(TextWriter output, CliArguments options, object result)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new { apiVersion = "1", command = options.Command,
            requestId = options.RequestId, result, errors = Array.Empty<QueryIssue>() }, QueryJson.Options));
        return 0;
    }
    private static async Task<int> Error(TextWriter output, CliArguments? options, string code, int exitCode)
    {
        var message = code == "CLI_MARKDOWN_FACTS_INCOMPLETE"
            ? "Markdown facts contain invalid, truncated, or unsupported source structure; no new generation was published. Review the Markdown input and parser limits before retrying."
            : "See CLI contract for the recovery action.";
        await output.WriteLineAsync(JsonSerializer.Serialize(new { apiVersion = "1", command = options?.Command ?? "unknown",
            requestId = options?.RequestId ?? "cli", errors = new[] { new QueryIssue(code, message) } }, QueryJson.Options));
        return exitCode;
    }
}
