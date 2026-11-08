namespace SharpDeps.Cli;

using SharpDeps.Analysis.Core.Diagnostics;

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
        PhaseMetrics.Run? metrics = null;
        try
        {
            options = CliArguments.Parse(args);
            token.ThrowIfCancellationRequested();
            if (options.Command == "help")
                return await Reply(stdout, options, new { commands = new[] { "explore", "index", "update", "serve", "coordinator-status", "status", "search", "symbol", "callers", "callees", "impact", "context", "query", "doctor", "help", "install", "uninstall" },
                    root = "--root absolute-directory; optional only for explore, which uses the absolute current directory without ancestor search",
                    index = "--target root-contained.slnx|.sln|.csproj --trusted [--timeout-seconds 0 for exclusive execution without a deadline]",
                    updateMode = "closed-input-project-markdown-or-full-rebuild",
                    query = "--term text | --id graph-id; --freshness allow-stale|require-fresh|refresh; --max-milliseconds 0 disables the computation deadline",
                    explore = "names-or-file-paths | --term names-or-file-paths [--query-kind symbol|http-get-literal --project-id id --variant id --format audit|agent --scope-policy discover|unique|prefer-debug --path-to occurrence-id --owner-status]; ordinary names/paths default to discover unless project or variant is explicit; exact saved IDs, path-to and http-get-literal default to unique and reject discover; http-get-literal matches the exact saved declaration fragment, including empty text, not an effective URL; path-to is Symbol-only and requires an exact source occurrence term; owner-status observes an existing coordinator without proving freshness; defaults to audit and returns bounded source evidence with explicit freshness and omissions",
                    watch = "serve --trusted --watch --target root-contained.csproj|.sln|.slnx [--watch-resync-seconds 30]",
                    installation = "install|uninstall --client copilot-cli --root absolute-directory --config-file absolute-new-file --instructions-file absolute-new-file [--dry-run]; install also requires --mcp-command absolute-existing-file; existing files must be the exact unchanged owned pair; uninstall retains adjacent backups",
                    helpContract = "docs/contracts/cli-skill-v1.ja.md", package = $"SharpDeps.Cli@{typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]}" });
            if (options.Command == "doctor")
                return await Reply(stdout, options, new { runtime = RuntimeInformation.FrameworkDescription, sdkProbe = "not-run",
                    toolTargetFramework = "net10.0", indexing = "trusted target SDK/MSBuild required", savedQuery = "runtime-only; no restore/build/network",
                    configurationProof = "not-connected",
                    updateMode = "closed-input-project-markdown-or-full-rebuild"
                });
            if (options.Command is "install" or "uninstall")
            {
                var installation = CliAgentInstallation.Run(options, token);
                var issues = installation.ErrorCode is { } code
                    ? new[] { new QueryIssue(code, "Setup is incomplete; preserve retained paths and review both destinations before retrying.") }
                    : Array.Empty<QueryIssue>();
                await Reply(stdout, options, installation, issues);
                return installation.ErrorCode is null ? 0 : installation.ErrorCode == "CANCELLED" ? 130 : 5;
            }
            // Validate request syntax before opening any stored data.
            var exploration = options.Command == "explore" ? options.ExplorationRequest() : null;
            if (exploration is not null)
            {
                var result = await ExploreWithMetricsAsync(options.Get("root") ?? Path.GetFullPath(Environment.CurrentDirectory), options.Get("index") ?? ".sharpdeps/index.sqlite",
                    options.Has("trusted"), exploration, options.Get("metrics-directory"), options.RequestId, token);
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
            metrics = PhaseMetrics.TryStart(options.Get("metrics-directory"), "query", root);
            var state = CliWorkspaceState.Open(root, create: false);
            if (!File.Exists(indexPath)) throw new CliFailure("INDEX_MISSING", 3);
            var queryToken = token;
            queryToken.ThrowIfCancellationRequested();
            using var reader = IndexReader.Open(indexPath, state.WorkspaceUuid, queryToken);
            var pinned = new PinnedQueryIndex(reader, queryToken, root);
            var sourceWorkspace = options.Has("trusted") ? new QueryWorkspace(root,
                ownerToken => VerifyQueryOwner(root, indexPath, state.WorkspaceUuid, pinned.Snapshot, ownerToken)) : null;
            var service = new QueryService(pinned, sourceWorkspace, options.Has("trusted"), Convert.FromBase64String(state.CursorKey));
            var reply = service.Execute(request!, token);
            if (reply.Succeeded)
                metrics?.Complete();
            else
                metrics?.Fail(reply.Envelope.Errors.Any(error => error.Code == "CANCELLED"));
            await stdout.WriteLineAsync(reply.Json);
            return reply.Succeeded ? 0 : ExitFor(reply.Envelope.Errors[0].Code);
        }
        catch (Exception error)
        {
            var failure = Failure(error);
            metrics?.Fail(failure.Code == "CANCELLED");
            return await Error(stdout, options, failure.Code, failure.ExitCode);
        }
        finally
        {
            metrics?.Dispose();
        }
    }

    /// <summary>Executes saved exploration with a fresh reader and returns text without the CLI transport newline.</summary>
    public static Task<(string Text, int ExitCode)> ExploreAsync(string root, string index, bool trusted,
        ExploreRequest request, string requestId = "cli", CancellationToken token = default)
        => ExploreWithMetricsAsync(root, index, trusted, request, null, requestId, token);

    internal static Task<(string Text, int ExitCode)> ExploreWithMetricsAsync(string root, string index, bool trusted,
        ExploreRequest request, string? metricsDirectory, string requestId = "cli", CancellationToken token = default)
    {
        PhaseMetrics.Run? metrics = null;
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
            if (budget.MaxMilliseconds != 0) deadline.CancelAfter(budget.MaxMilliseconds);
            root = CliPaths.Root(root);
            metrics = PhaseMetrics.TryStart(metricsDirectory, "explore", root);
            index = CliPaths.InRoot(root, index);
            foreach (var sidecar in new[] { index + "-wal", index + "-shm", index + ".writer.lock" }) CliPaths.RejectLinks(sidecar);
            if (!File.Exists(index)) throw new CliFailure("INDEX_MISSING", 3);
            var state = CliWorkspaceState.Open(root, create: false);
            var queryToken = deadline.Token;
            queryToken.ThrowIfCancellationRequested();
            using var reader = IndexReader.Open(index, state.WorkspaceUuid, queryToken);
            var pinned = new PinnedQueryIndex(reader, queryToken, root);
            var sourceWorkspace = trusted ? new QueryWorkspace(root,
                ownerToken => VerifyQueryOwner(root, index, state.WorkspaceUuid, pinned.Snapshot, ownerToken)) : null;
            if (budget.MaxMilliseconds != 0 && clock.ElapsedMilliseconds >= budget.MaxMilliseconds) throw new CliFailure("QUERY_TIMEOUT", 4);
            var service = new ExplorationService(pinned, sourceWorkspace, trusted)
            {
                OwnerStatusProvider = request.ObserveOwner
                    ? ownerToken => ObserveQueryOwner(root, index, state.WorkspaceUuid, ownerToken) : null
            };
            var explored = service.Execute(request, token, queryToken);
            token.ThrowIfCancellationRequested();
            if (budget.MaxMilliseconds != 0 && clock.ElapsedMilliseconds >= budget.MaxMilliseconds) throw new CliFailure("QUERY_TIMEOUT", 4);
            var exit = explored.Succeeded ? 0 : explored.Errors.Count == 0 ? 4 : ExitFor(explored.Errors[0].Code);
            if (exit == 0)
                metrics?.Complete();
            else
                metrics?.Fail(explored.Errors.Any(error => error.Code == "CANCELLED"));
            return Task.FromResult((explored.Text, exit));
        }
        catch (Exception error)
        {
            var failure = Failure(error, deadline?.IsCancellationRequested == true && !token.IsCancellationRequested);
            metrics?.Fail(failure.Code == "CANCELLED");
            var recovery = failure.Code is "INDEX_MISSING" or "WORKSPACE_STATE_MISSING"
                ? $"No usable saved index at '{index}' for selected root '{root}'. Select an existing --root/--index, or create an index with an explicitly authorized trusted index command."
                : null;
            return Task.FromResult((ErrorText(hostParsed ? "explore" : "unknown", hostParsed ? requestId : "cli", failure.Code, recovery), failure.ExitCode));
        }
        finally
        {
            deadline?.Dispose();
            metrics?.Dispose();
        }
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
        var timeout = update.Options.TimeoutSeconds;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (timeout != 0) operation.CancelAfter(TimeSpan.FromSeconds(timeout));
        try
        {
            // A missing identity on an existing index is never silently replaced.
            var existed = File.Exists(indexPath);
            if (options.Command == "update" && !existed) throw new CliFailure("INDEX_MISSING", 3);
            var state = CliWorkspaceState.Open(root, create: !existed);
            update = update with { Options = update.Options with { WorkspaceUuid = state.WorkspaceUuid } };
            if (timeout == 0) return await IndexExclusiveAsync(options, update, stdout, stderr, token);
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> IndexExclusiveAsync(CliArguments options, CoordinatorUpdate update,
        TextWriter stdout, TextWriter stderr, CancellationToken token)
    {
        var selection = update.Options;
        using var metrics = PhaseMetrics.TryStart(selection.MetricsDirectory, "index.exclusive", selection.Root);
        using var writer = IndexWriter.Open(selection.Root, selection.IndexPath, selection.WorkspaceUuid, isTrusted: true);
        if (!options.Has("quiet")) await stderr.WriteLineAsync("sharpdeps: exclusive extraction started (no deadline; no automatic restore)");
        var built = await new CoordinatorExecutor(writer).BuildExclusiveAsync(update, token);
        token.ThrowIfCancellationRequested();
        var stage = IndexStage.Create(built.Snapshot);
        token.ThrowIfCancellationRequested();
        writer.Commit(stage, token, built.VerifySelection);
        metrics?.Complete();
        var snapshot = built.Snapshot;
        var mode = snapshot.Graph.Diagnostics?.Any(d => d.Code == "cli.semanticGraphReused" && d.Count == 1) == true
            ? "markdown-incremental" : snapshot.Graph.Diagnostics?.Any(d => d.Code == "cli.projectCollectorsReused" && d.Count > 0) == true
                ? "project-incremental" : "full-rebuild";
        if (!options.Has("quiet")) await stderr.WriteLineAsync($"sharpdeps: {mode} committed");
        return await Reply(stdout, options, new { snapshot.Graph.WorkspaceId, snapshot.Graph.SnapshotId,
            snapshot.Graph.Generation, snapshot.Graph.Coverage, updateMode = mode, configurationProof = "unverified",
            coordinatorState = "exclusive", files = snapshot.Files.Count, nodes = snapshot.Graph.Nodes.Count,
            edges = snapshot.Graph.Edges.Count });
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
        "SYMBOL_NOT_FOUND" or "HTTP_GET_DECLARATION_NOT_FOUND" => 2,
        "QUERY_TIMEOUT" or "INCOMPLETE_RESULT" or "OUTPUT_BUDGET_TOO_SMALL" => 4,
        "COORDINATOR_SUPERSEDED" => 3,
        _ when code.StartsWith("COORDINATOR_", StringComparison.Ordinal) => 6,
        _ when code.Contains("CURSOR", StringComparison.Ordinal) || code.StartsWith("INDEX_", StringComparison.Ordinal) => 6,
        _ => 4
    };

    private static async Task<int> Reply(TextWriter output, CliArguments options, object result, QueryIssue[]? issues = null)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new { apiVersion = "1", command = options.Command,
            requestId = options.RequestId, result, errors = issues ?? Array.Empty<QueryIssue>() }, QueryJson.Options));
        return 0;
    }
    private static async Task<int> Error(TextWriter output, CliArguments? options, string code, int exitCode)
    {
        await output.WriteLineAsync(ErrorText(options?.Command ?? "unknown", options?.RequestId ?? "cli", code));
        return exitCode;
    }

    private static string ErrorText(string command, string requestId, string code, string? recovery = null)
    {
        var message = recovery ?? (code == "CLI_MARKDOWN_FACTS_INCOMPLETE"
            ? "Markdown facts contain invalid, truncated, or unsupported source structure; no new generation was published. Review the Markdown input and parser limits before retrying."
            : "See CLI contract for the recovery action.");
        return JsonSerializer.Serialize(new { apiVersion = "1", command,
            requestId, errors = new[] { new QueryIssue(code, message) } }, QueryJson.Options);
    }
}
