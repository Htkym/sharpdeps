namespace SharpDeps.Cli;

using SharpDeps.Analysis.Core.Diagnostics;
using SharpDeps.Index;
using SharpDeps.Query;

internal sealed partial class CliArguments
{
    private static readonly HashSet<string> Symbols = new(StringComparer.Ordinal)
    { "term", "ids", "project-id", "variant", "path-prefix", "freshness", "max-results",
      "direction", "edge-kind", "prepare-max-milliseconds", "max-milliseconds", "max-chars", "max-bytes", "metrics-directory" };

    internal SymbolQueryRequest SymbolsRequest()
    {
        var operation = Command switch
        {
            "find-symbols" => SymbolQueryOperation.FindSymbols,
            "read-symbols" => SymbolQueryOperation.ReadSymbols,
            "relations" => SymbolQueryOperation.Relations,
            _ => throw new CliFailure("COMMAND_UNKNOWN", 2)
        };
        if (Many("variant") is { Count: > 1 }
            || operation != SymbolQueryOperation.Relations && (Has("direction") || Has("edge-kind")))
            throw new CliFailure("ARGUMENT_INVALID", 2);
        return ExplorationService.NormalizeSymbols(new(operation, Get("term"), Get("ids")?.Split(','),
            Get("project-id"), Get("variant"), Get("path-prefix"), ExploreRequestFactory.Freshness(Get("freshness")),
            new QueryBudget(MaxMilliseconds: Number("max-milliseconds", 1000, 10, 30000),
                MaxChars: Number("max-chars", 12000, 2048, 1000000), MaxBytes: Number("max-bytes", 48000, 2048, 4000000)))
        {
            PrepareMaxMilliseconds = ExplorePreparationMilliseconds(),
            MaxResults = Number("max-results", 16, 1, 32),
            Direction = Get("direction") ?? "outgoing",
            EdgeKinds = Many("edge-kind")
        });
    }
}

public static partial class CliApplication
{
    /// <summary>Executes one read-only typed symbol operation under shared finite preparation/query budgets.</summary>
    public static Task<(string Text, int ExitCode)> SymbolsAsync(string root, string index, bool trusted,
        SymbolQueryRequest request, string requestId = "cli", CancellationToken token = default)
        => SymbolsCoreAsync(root, index, trusted, request, null, requestId, token, null);

    internal static Task<(string Text, int ExitCode)> SymbolsWithHostAsync(string root, string index, bool trusted,
        SymbolQueryRequest request, string? metricsDirectory, PreparedExplorationHost preparationHost,
        string requestId = "cli", CancellationToken token = default)
        => SymbolsCoreAsync(root, index, trusted, request, metricsDirectory, requestId, token, preparationHost);

    private static async Task<(string Text, int ExitCode)> SymbolsCoreAsync(string root, string index, bool trusted,
        SymbolQueryRequest request, string? metricsDirectory, string requestId, CancellationToken token,
        PreparedExplorationHost? preparationHost)
    {
        PhaseMetrics.Run? metrics = null;
        ExplorationExecutionBudget? execution = null;
        using var hostedCancellation = preparationHost is null ? null
            : CancellationTokenSource.CreateLinkedTokenSource(token, preparationHost.LifetimeToken);
        try
        {
            CliArguments.ValidateValue(root);
            CliArguments.ValidateValue(index);
            CliArguments.ValidateRequestId(requestId);
            request = ExplorationService.NormalizeSymbols(request);
            var preparationRequest = ExplorationService.ToExploreRequest(request);
            execution = new(preparationRequest, hostedCancellation?.Token ?? token);
            execution.Check();
            root = CliPaths.Root(root);
            index = CliPaths.InRoot(root, index);
            foreach (var sidecar in new[] { index + "-wal", index + "-shm", index + ".writer.lock" }) CliPaths.RejectLinks(sidecar);
            if (!File.Exists(index)) throw new CliFailure("INDEX_MISSING", 3);
            metrics = PhaseMetrics.TryStart(metricsDirectory, "symbols", root);
            var state = CliWorkspaceState.Open(root, create: false);
            using var hosted = preparationHost is null ? null
                : await preparationHost.AcquireAsync(root, index, state.WorkspaceUuid, metricsDirectory, execution.Token).ConfigureAwait(false);
            using var freshReader = hosted is null ? IndexReader.Open(index, state.WorkspaceUuid, execution.Token) : null;
            var pinned = hosted is not null ? hosted.Pin(execution.Token) : new PinnedQueryIndex(freshReader!, execution.Token);
            var workspace = trusted ? new QueryWorkspace(root,
                ownerToken => VerifyQueryOwner(root, index, state.WorkspaceUuid, pinned.Snapshot, ownerToken)) : null;
            var service = new ExplorationService(pinned, workspace, trusted);
            var prepared = service.PrepareSnapshot(preparationRequest, execution.Token);
            execution.StartQuery();
            var reply = service.ExecuteSymbolsPrepared(request, prepared, execution);
            hosted?.Dispose();
            freshReader?.Dispose();
            execution.CallerToken.ThrowIfCancellationRequested();
            if (reply.Succeeded) execution.Check();
            var exit = reply.Succeeded ? 0 : ExitFor(reply.Envelope.Errors[0].Code);
            if (exit == 0) metrics?.Complete();
            else metrics?.Fail(reply.Envelope.Errors.Any(error => error.Code == "CANCELLED"));
            return (reply.Json, exit);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var cancellationCode = execution is not null
                && (error is OperationCanceledException || execution.CallerToken.IsCancellationRequested) ? execution.Code : null;
            var failure = cancellationCode is not null
                ? (Code: cancellationCode, ExitCode: cancellationCode == "CANCELLED" ? 130 : 4)
                : Failure(error);
            metrics?.Fail(failure.Code == "CANCELLED");
            return (ExplorationService.SymbolsFailure(request, failure.Code).Json, failure.ExitCode);
        }
        finally
        {
            execution?.Dispose();
            metrics?.Dispose();
        }
    }
}
