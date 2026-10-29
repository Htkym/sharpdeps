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
                    explore = "--term names-or-file-paths [--project-id id --variant id --format audit|agent --scope-policy unique|prefer-debug --path-to occurrence-id --owner-status]; path-to requires an exact source occurrence term; owner-status observes an existing coordinator without proving freshness; defaults to audit and returns bounded source evidence with explicit freshness and omissions",
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
            if (exploration is not null)
            {
                var result = await ExploreAsync(options.Require("root"), options.Get("index") ?? ".sharpdeps/index.sqlite",
                    options.Has("trusted"), exploration, options.RequestId, token);
                await stdout.WriteLineAsync(result.Text);
                return result.ExitCode;
            }
            var request = options.Command is "index" or "update" or "serve" or "coordinator-status" ? null : options.QueryRequest();
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
            var queryToken = token;
            queryToken.ThrowIfCancellationRequested();
            using var reader = IndexReader.Open(indexPath, state.WorkspaceUuid, queryToken);
            var pinned = new PinnedQueryIndex(reader, queryToken);
            var sourceWorkspace = options.Has("trusted") ? new QueryWorkspace(root,
                ownerToken => VerifyQueryOwner(root, indexPath, state.WorkspaceUuid, pinned.Snapshot, ownerToken)) : null;
            var service = new QueryService(pinned, sourceWorkspace, options.Has("trusted"), Convert.FromBase64String(state.CursorKey));
            var reply = service.Execute(request!, token);
            await stdout.WriteLineAsync(reply.Json);
            return reply.Succeeded ? 0 : ExitFor(reply.Envelope.Errors[0].Code);
        }
        catch (Exception error)
        {
            var failure = Failure(error);
            return await Error(stdout, options, failure.Code, failure.ExitCode);
        }
    }

    /// <summary>Executes saved exploration with a fresh reader and returns text without the CLI transport newline.</summary>
    public static Task<(string Text, int ExitCode)> ExploreAsync(string root, string index, bool trusted,
        ExploreRequest request, string requestId = "cli", CancellationToken token = default)
    {
        CancellationTokenSource? deadline = null;
        var hostParsed = false;
        try
        {
            CliArguments.ValidateValue(root);
            CliArguments.ValidateValue(index);
            CliArguments.ValidateValue(requestId);
            CliArguments.ValidateRequestId(requestId);
            hostParsed = true;
            token.ThrowIfCancellationRequested();
            request = ExploreRequestFactory.Validate(request);
            var budget = request.Budget!;
            var clock = Stopwatch.StartNew();
            deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(budget.MaxMilliseconds);
            root = CliPaths.Root(root);
            index = CliPaths.InRoot(root, index);
            foreach (var sidecar in new[] { index + "-wal", index + "-shm", index + ".writer.lock" }) CliPaths.RejectLinks(sidecar);
            var state = CliWorkspaceState.Open(root, create: false);
            if (!File.Exists(index)) throw new CliFailure("INDEX_MISSING", 3);
            var queryToken = deadline.Token;
            queryToken.ThrowIfCancellationRequested();
            using var reader = IndexReader.Open(index, state.WorkspaceUuid, queryToken);
            var pinned = new PinnedQueryIndex(reader, queryToken);
            var sourceWorkspace = trusted ? new QueryWorkspace(root,
                ownerToken => VerifyQueryOwner(root, index, state.WorkspaceUuid, pinned.Snapshot, ownerToken)) : null;
            if (clock.ElapsedMilliseconds >= budget.MaxMilliseconds) throw new CliFailure("QUERY_TIMEOUT", 4);
            var service = new ExplorationService(pinned, sourceWorkspace, trusted)
            {
                OwnerStatusProvider = request.ObserveOwner
                    ? ownerToken => ObserveQueryOwner(root, index, state.WorkspaceUuid, ownerToken) : null
            };
            var explored = service.Execute(request, token, queryToken);
            token.ThrowIfCancellationRequested();
            if (clock.ElapsedMilliseconds >= budget.MaxMilliseconds) throw new CliFailure("QUERY_TIMEOUT", 4);
            var exit = explored.Succeeded ? 0 : explored.Errors.Count == 0 ? 4 : ExitFor(explored.Errors[0].Code);
            return Task.FromResult((explored.Text, exit));
        }
        catch (Exception error)
        {
            var failure = Failure(error, deadline?.IsCancellationRequested == true && !token.IsCancellationRequested);
            return Task.FromResult((ErrorText(hostParsed ? "explore" : "unknown", hostParsed ? requestId : "cli", failure.Code), failure.ExitCode));
        }
        finally { deadline?.Dispose(); }
    }

    private static (string Code, int ExitCode) Failure(Exception error, bool timedOut = false) => error switch
    {
        CliFailure failure => (failure.Code, failure.ExitCode),
        CliIndexException failure => (failure.Code, failure.ExitCode),
        IndexStoreException failure => (failure.Code, ExitFor(failure.Code)),
        OperationCanceledException when timedOut => ("QUERY_TIMEOUT", 4),
        OperationCanceledException => ("CANCELLED", 130),
        ArgumentException => ("ARGUMENT_INVALID", 2),
        IOException or UnauthorizedAccessException => ("ROOT_ACCESS_DENIED", 5),
        _ => ("INTERNAL_ERROR", 7)
    };

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

    private static ExploreOwnerObservation ObserveQueryOwner(string root, string indexPath, Guid uuid,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(200));
        try
        {
            var owner = CoordinatorProtocol.Discover(root, indexPath, uuid);
            timeout.Token.ThrowIfCancellationRequested();
            if (owner is null) return new("absent");
            var reply = CoordinatorProtocol.TrySendAsync(owner, new(CoordinatorProtocol.Version, owner.Instance, uuid,
                "owner-status", "status", root, indexPath, false, []), timeout.Token).GetAwaiter().GetResult();
            timeout.Token.ThrowIfCancellationRequested();
            if (reply is null || reply.ErrorCode is not null) return new("unavailable");
            return new(reply.State, reply.Result?.WorkspaceId, reply.Result?.SnapshotId, reply.Result?.Generation,
                reply.Revision, reply.DesiredRevision);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new("unavailable"); }
        catch (Exception error) when (error is CliFailure or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new("unavailable"); }
        finally { token.ThrowIfCancellationRequested(); }
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
        await output.WriteLineAsync(ErrorText(options?.Command ?? "unknown", options?.RequestId ?? "cli", code));
        return exitCode;
    }

    private static string ErrorText(string command, string requestId, string code)
    {
        var message = code == "CLI_MARKDOWN_FACTS_INCOMPLETE"
            ? "Markdown facts contain invalid, truncated, or unsupported source structure; no new generation was published. Review the Markdown input and parser limits before retrying."
            : "See CLI contract for the recovery action.";
        return JsonSerializer.Serialize(new { apiVersion = "1", command,
            requestId, errors = new[] { new QueryIssue(code, message) } }, QueryJson.Options);
    }
}
