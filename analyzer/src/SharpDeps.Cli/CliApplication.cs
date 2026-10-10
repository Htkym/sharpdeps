namespace SharpDeps.Cli;

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
                return await Reply(stdout, options, new { commands = new[] { "index", "update", "status", "search", "symbol", "callers", "callees", "impact", "context", "query", "doctor", "help" },
                    root = "--root absolute-directory", index = "--target root-contained.slnx|.sln|.csproj --trusted", updateMode = "full-rebuild",
                    query = "--term text | --id graph-id; --freshness allow-stale|require-fresh|refresh",
                    helpContract = "docs/contracts/cli-skill-v1.ja.md", package = "SharpDeps.Cli@0.2.0-preview.2 (provisional, unpublished)" });
            if (options.Command == "doctor")
                return await Reply(stdout, options, new { runtime = RuntimeInformation.FrameworkDescription, sdkProbe = "not-run",
                    toolTargetFramework = "net10.0", indexing = "trusted target SDK/MSBuild required", savedQuery = "runtime-only; no restore/build/network",
                    configurationProof = "not-connected", updateMode = "full-rebuild" });
            // Validate request syntax before opening any stored data.
            var request = options.Command is "index" or "update" ? null : options.QueryRequest();
            if (options.Command is "index" or "update" && !options.Has("trusted")) throw new CliFailure("WORKSPACE_UNTRUSTED", 5);
            var root = CliPaths.Root(options.Require("root"));
            var indexPath = CliPaths.InRoot(root, options.Get("index") ?? ".sharpdeps/index.sqlite");
            foreach (var sidecar in new[] { indexPath + "-wal", indexPath + "-shm", indexPath + ".writer.lock" }) CliPaths.RejectLinks(sidecar);
            if (options.Command is "index" or "update")
                return await IndexAsync(options, root, indexPath, stdout, stderr, token);
            var state = CliWorkspaceState.Open(root, create: false);
            if (!File.Exists(indexPath)) throw new CliFailure("INDEX_MISSING", 3);
            using var reader = IndexReader.Open(indexPath, state.WorkspaceUuid);
            var service = new QueryService(new PinnedQueryIndex(reader), options.Has("trusted") ? new QueryWorkspace(root) : null,
                options.Has("trusted"), Convert.FromBase64String(state.CursorKey));
            var reply = service.Execute(request!, token);
            await stdout.WriteLineAsync(reply.Json);
            return reply.Succeeded ? 0 : ExitFor(reply.Envelope.Errors[0].Code);
        }
        catch (CliFailure error) { return await Error(stdout, options, error.Code, error.ExitCode); }
        catch (CliIndexException error) { return await Error(stdout, options, error.Code, error.ExitCode); }
        catch (IndexStoreException error) { return await Error(stdout, options, error.Code, ExitFor(error.Code)); }
        catch (OperationCanceledException) { return await Error(stdout, options, "CANCELLED", 130); }
        catch (ArgumentException) { return await Error(stdout, options, "ARGUMENT_INVALID", 2); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return await Error(stdout, options, "ROOT_ACCESS_DENIED", 5); }
        catch (Exception) { return await Error(stdout, options, "INTERNAL_ERROR", 7); }
    }

    // Keep target SDK/MSBuild activation out of the saved-only command path.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> IndexAsync(CliArguments options, string root, string indexPath,
        TextWriter stdout, TextWriter stderr, CancellationToken token)
    {
        var target = CliPaths.InRoot(root, options.Require("target"));
        if (!File.Exists(target) || Path.GetExtension(target).ToLowerInvariant() is not (".sln" or ".slnx" or ".csproj"))
            throw new CliFailure("TARGET_INVALID", 2);
        var projects = options.Many("project")?.Select(path => CliPaths.InRoot(root, path)).ToArray();
        if (projects is not null && (projects.Distinct(StringComparer.OrdinalIgnoreCase).Count() != projects.Length
            || projects.Any(path => !File.Exists(path) || Path.GetExtension(path) != ".csproj")))
            throw new CliFailure("PROJECT_SELECTION_INVALID", 2);
        var timeout = options.Number("timeout-seconds", 180, 1, 300);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        operation.CancelAfter(TimeSpan.FromSeconds(timeout));
        try
        {
            // A missing identity on an existing index is never silently replaced.
            var existed = File.Exists(indexPath);
            if (options.Command == "update" && !existed) throw new CliFailure("INDEX_MISSING", 3);
            var state = CliWorkspaceState.Open(root, create: !existed);
            using var writer = IndexWriter.Open(root, indexPath, state.WorkspaceUuid, isTrusted: true);
            IndexSnapshot? previous = null;
            if (existed)
            {
                try
                {
                    using var reader = IndexReader.Open(indexPath, state.WorkspaceUuid);
                    previous = reader.Snapshot();
                }
                catch (IndexStoreException error) when (error.Code == "INDEX_NO_SNAPSHOT" && options.Command == "index")
                { /* A failed first extraction has no published generation; retry under this lease. */ }
            }
            if (!options.Has("quiet")) await stderr.WriteLineAsync("sharpdeps: full rebuild started (no automatic restore)");
            var snapshot = await CliIndexer.BuildAsync(new(root, target, indexPath, state.WorkspaceUuid,
                options.Get("configuration") ?? "Debug", options.Get("platform"), projects, options.Get("tfm"), timeout), previous, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            writer.Commit(IndexStage.Create(snapshot), operation.Token);
            if (!options.Has("quiet")) await stderr.WriteLineAsync("sharpdeps: full rebuild committed");
            return await Reply(stdout, options, new { workspaceId = snapshot.Graph.WorkspaceId, snapshotId = snapshot.Graph.SnapshotId,
                generation = snapshot.Graph.Generation, coverage = snapshot.Graph.Coverage, updateMode = "full-rebuild",
                configurationProof = "unverified", coordinatorState = "not-connected", files = snapshot.Files.Count,
                nodes = snapshot.Graph.Nodes.Count, edges = snapshot.Graph.Edges.Count });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CliFailure("TIMEOUT", 4); }
    }

    internal static int ExitFor(string code) => code switch
    {
        "CANCELLED" => 130,
        "WORKSPACE_UNTRUSTED" or "INDEX_UNTRUSTED" or "ROOT_ACCESS_DENIED" or "INVALID_SOURCE_ENCODING" => 5,
        "UPDATE_REQUIRED" or "FRESHNESS_UNVERIFIED" or "FRESHNESS_REQUIREMENT_NOT_MET" or "SOURCE_CHANGED_SINCE_SNAPSHOT"
            or "INDEX_NO_SNAPSHOT" or "INDEX_NOT_FOUND" => 3,
        "SYMBOL_NOT_FOUND" => 2,
        "QUERY_TIMEOUT" or "INCOMPLETE_RESULT" or "OUTPUT_BUDGET_TOO_SMALL" => 4,
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
        await output.WriteLineAsync(JsonSerializer.Serialize(new { apiVersion = "1", command = options?.Command ?? "unknown",
            requestId = options?.RequestId ?? "cli", errors = new[] { new QueryIssue(code, "See CLI contract for the recovery action.") } }, QueryJson.Options));
        return exitCode;
    }
}
