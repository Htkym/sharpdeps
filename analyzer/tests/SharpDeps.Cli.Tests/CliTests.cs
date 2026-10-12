namespace SharpDeps.Cli.Tests;

using System.Security.Cryptography;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Markdown;
using SharpDeps.Index;
using SharpDeps.Query;
using Xunit;

public sealed class CliTests
{
    [Fact]
    public void MarkdownContainmentReachesCandidatesWithoutExpandingThemOrBridgingVariants()
    {
        using var fixture = new Fixture();
        using var original = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var saved = original.Snapshot();
        var primary = Assert.Single(saved.Graph.Variants);
        var alternate = HarnessIdentity.VariantId(fixture.Uuid, primary.ProjectId, "net9.0", "Debug");
        var parent = saved.Graph.Nodes.Single(n => n.Name == "Service");
        var target = HarnessIdentity.LogicalSymbolId(fixture.Uuid, primary.ProjectId, "method", "M:Service.Run");
        var tail = HarnessIdentity.LogicalSymbolId(fixture.Uuid, primary.ProjectId, "method", "M:Service.Other");
        string Occurrence(string node, string variant) => HarnessIdentity.SymbolOccurrenceId(fixture.Uuid, node, variant);
        var graph = saved.Graph with { SnapshotId = "markdown-connectivity", Generation = 2,
            Variants = [.. saved.Graph.Variants, new(alternate, primary.ProjectId, "net9.0", "Debug", null, null, null)],
            Nodes = [.. saved.Graph.Nodes, new(target, HarnessNodeKind.Member, "Run", parent.Id, parent.Location),
                new(tail, HarnessNodeKind.Member, "Other", parent.Id, parent.Location)],
            SymbolOccurrences = [.. saved.Graph.SymbolOccurrences, .. new[] { target, tail }.SelectMany(node =>
                new[] { primary.Id, alternate }.Select(variant => new HarnessSymbolOccurrence(
                    Occurrence(node, variant), node, variant, parent.Location, [parent.Location!])))],
            Edges = new[] { primary.Id, alternate }.Select(variant => new HarnessEdge("calls-" + variant,
                target, tail, Occurrence(target, variant), Occurrence(tail, variant), variant,
                "calls", HarnessCertainty.Resolved, "fixture-semantic", parent.Location)).ToArray() };
        const string raw = "---\nsharpdeps:\n  symbols:\n    - documentationId: M:Service.Run\n---\n# Guide\n\nService.Run\n";
        File.WriteAllText(Path.Combine(fixture.Root, "readme.md"), raw, new UTF8Encoding(false));
        var documentUuid = Guid.NewGuid();
        var request = new MarkdownGraphRequest(raw, fixture.Uuid, documentUuid, graph.WorkspaceId,
            "readme.md", "v1", graph.SnapshotId, graph.Generation);
        var adapter = new LithoSharpMarkdownAdapter();
        var parsed = adapter.Analyze(request, true);
        var tokens = parsed.Facts.Sections.ToDictionary(s => s.LocalKey, s => "regression:" + s.LocalKey);
        var projection = adapter.Analyze(request with { SectionTokens = tokens,
            SectionIdentityTextHash = parsed.Facts.TextHash }, true);
        graph = graph with { Nodes = graph.Nodes.Concat(projection.Graph.Nodes).DistinctBy(n => n.Id).ToArray(),
            Edges = graph.Edges.Concat(projection.Graph.Edges).ToArray(), Markdown = projection.Graph.Markdown };
        var catalog = new MarkdownSymbolCatalog(graph, new[] { primary.Id, alternate }.Select(variant =>
            new MarkdownSymbolCandidate(target, "Service.Run", "Run", "M:Service.Run", VariantId: variant)).ToArray());
        var resolution = MarkdownSymbolResolver.Resolve(projection, raw, catalog);
        var projected = MarkdownSymbolResolver.ProjectGraph(graph, resolution);
        var stamp = projected.Markdown!;
        var snapshot = saved with { Graph = projected,
            Files = [.. saved.Files, new("readme.md", "readme.md", "markdown", parsed.Facts.TextHash,
                Encoding.UTF8.GetByteCount(raw), raw.Length)],
            Documents = [new(documentUuid, "readme.md", null, parsed.Facts.TextHash, graph.WorkspaceId,
                stamp.ParserVersion, stamp.ContractVersion, stamp.ProfileId, stamp.OptionsHash,
                projection.Sections.Select(s => new IndexSectionIdentity(s.Source.LocalKey,
                    tokens[s.Source.LocalKey], null, null)).ToArray())] };
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        writer.Commit(IndexStage.Create(snapshot));
        using var reader = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var query = new QueryService(new PinnedQueryIndex(reader));
        var document = HarnessIdentity.DocumentId(fixture.Uuid, documentUuid);
        var section = Assert.Single(resolution.Mentions, m => m.Origin == MarkdownMentionOrigin.BodyText
            && m.Token == "Service.Run").OwnerNodeId;
        var budget = new QueryBudget(100, 100, 8, 10000, 100000, 400000);
        foreach (var seed in new[] { document, section })
        {
            var queryRequest = new QueryRequest("candidate", QueryKind.Impact, NodeId: seed,
                Scope: new(VariantIds: [primary.Id, alternate]), Dependents: false,
                IncludeCandidates: true, Budget: budget);
            var reply = query.Execute(queryRequest).Envelope;
            Assert.Empty(reply.Errors);
            Assert.False(reply.Truncated);
            Assert.Contains(reply.Items, i => i.Node?.Kind == HarnessNodeKind.SymbolMention);
            Assert.Contains(reply.Candidates, i => i.Node?.Id == target);
            var bindings = reply.Candidates.Where(i => i.Edge?.TargetNodeId == target).ToArray();
            Assert.Equal(seed == document ? 2 : 1, bindings.Length);
            Assert.Equal(resolution.Mentions.Where(m => m.CandidateNodeIds.Contains(target)
                && (seed == document || m.OwnerNodeId == seed)).Select(m => m.Id).Order(),
                bindings.Select(i => i.Edge!.SourceNodeId).Order());
            var binding = Assert.Single(reply.Candidates, i => i.Edge?.TargetNodeId == target
                && i.Edge.Kind == (seed == document ? "documents_symbol" : "mentions_symbol"));
            Assert.Equal(HarnessCertainty.Candidate, binding.Edge!.Certainty);
            Assert.Null(binding.Edge.VariantId);
            Assert.Null(binding.Edge.SourceOccurrenceId);
            Assert.Null(binding.Edge.TargetOccurrenceId);
            Assert.DoesNotContain(reply.Items.Concat(reply.Candidates), i => i.Edge?.Kind == "calls" || i.Node?.Id == tail);
            var strict = query.Execute(queryRequest with { IncludeCandidates = false }).Envelope;
            Assert.Contains(strict.Items, i => i.Node?.Kind == HarnessNodeKind.SymbolMention);
            Assert.Empty(strict.Candidates);
            Assert.DoesNotContain(strict.Items, i => i.Node?.Id == target);
        }
        // Only a separately complete catalog and an explicit variant may bind an occurrence.
        var scoped = MarkdownSymbolResolver.Resolve(projection, raw,
            catalog with { Graph = graph with { Coverage = HarnessCoverage.CompleteWithinScope } },
            new(VariantId: primary.Id));
        var scopedGraph = MarkdownSymbolResolver.ProjectGraph(graph, scoped);
        writer.Commit(IndexStage.Create(snapshot with { Graph = scopedGraph with { Generation = 3, SnapshotId = "scoped" } }));
        using var scopedReader = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var scopedQuery = new QueryService(new PinnedQueryIndex(scopedReader));
        var selected = new QueryRequest("selected", QueryKind.Impact, NodeId: document, Dependents: false,
            IncludeCandidates: true, Scope: new(VariantIds: [primary.Id, alternate]), Budget: budget);
        var selectedReply = scopedQuery.Execute(selected).Envelope;
        Assert.Empty(selectedReply.Errors);
        Assert.Contains(selectedReply.Items, i => i.Edge?.Id == "calls-" + primary.Id);
        Assert.DoesNotContain(selectedReply.Items.Concat(selectedReply.Candidates), i => i.Edge?.Id == "calls-" + alternate);
        var other = scopedQuery.Execute(selected with { Scope = new(VariantIds: [alternate]) }).Envelope;
        Assert.Empty(other.Errors);
        Assert.DoesNotContain(other.Items.Concat(other.Candidates), i => i.Node?.Id == target || i.Node?.Id == tail);
    }

    [Fact]
    public async Task TrustAndArgumentFailuresHappenBeforeAnyWorkspaceCreation()
    {
        using var fixture = new Fixture(seed: false);
        var untrusted = await Run("index", "--root", fixture.Root, "--target", "absent.csproj");
        Assert.Equal(5, untrusted.Exit);
        Assert.Equal("WORKSPACE_UNTRUSTED", Code(untrusted.Json));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".sharpdeps")));
        var missingTerm = await Run("search", "--root", fixture.Root);
        Assert.Equal(2, missingTerm.Exit);
        var unsupported = await Run("query", "--kind", "status", "--root", fixture.Root, "--dependencies");
        Assert.Equal(2, unsupported.Exit);
        var statusScope = await Run("status", "--root", fixture.Root, "--project-id", "ignored-project");
        Assert.Equal(2, statusScope.Exit);
        Assert.Equal("ARGUMENT_UNSUPPORTED", Code(statusScope.Json));
        var network = await Run("status", "--root", "//invalid.invalid/sharpdeps");
        Assert.Equal(5, network.Exit);
        Assert.Equal("ROOT_INVALID", Code(network.Json));
        var duplicate = await Run("status", "--root", fixture.Root, "--root", fixture.Root);
        Assert.Equal(2, duplicate.Exit);
        Assert.Equal("ARGUMENT_DUPLICATE", Code(duplicate.Json));
        var badBudget = await Run("search", "--root", fixture.Root, "--term", "Service", "--max-nodes", "0");
        Assert.Equal(2, badBudget.Exit);
        var outside = await Run("status", "--root", fixture.Root, "--index", "../outside.sqlite");
        Assert.Equal(5, outside.Exit);
        Assert.Equal("PATH_OUTSIDE_ROOT", Code(outside.Json));
        var missingWatchTarget = await Run("serve", "--root", fixture.Root, "--trusted", "--watch");
        Assert.Equal(2, missingWatchTarget.Exit);
        var invalidWatchTarget = await Run("serve", "--root", fixture.Root, "--trusted", "--watch", "--target", "absent.csproj");
        Assert.Equal("TARGET_INVALID", Code(invalidWatchTarget.Json));
        var invalidWatchPeriod = await Run("serve", "--root", fixture.Root, "--trusted", "--watch", "--target", "absent.csproj",
            "--watch-resync-seconds", "0");
        Assert.Equal(2, invalidWatchPeriod.Exit);
        var selectionWithoutWatch = await Run("serve", "--root", fixture.Root, "--trusted", "--target", "absent.csproj");
        Assert.Equal("ARGUMENT_UNSUPPORTED", Code(selectionWithoutWatch.Json));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".sharpdeps")));
    }

    [Fact]
    public async Task SeparateInvocationsShareCursorKeyAndReadSavedWithoutSourceFiles()
    {
        using var fixture = new Fixture();
        var before = SHA256.HashData(File.ReadAllBytes(fixture.IndexPath));
        File.Delete(Path.Combine(fixture.Root, "Service.cs"));
        var first = await Run("search", "--root", fixture.Root, "--term", "Service", "--page-size", "1");
        Assert.Equal(0, first.Exit);
        Assert.Empty(first.Progress);
        var envelope = JsonSerializer.Deserialize<QueryEnvelope>(first.Json, QueryJson.Options)!;
        var cursor = Assert.IsType<string>(envelope.NextCursor);
        var second = await Run("search", "--root", fixture.Root, "--term", "Service", "--page-size", "1", "--cursor", cursor);
        Assert.Equal(0, second.Exit);
        var next = JsonSerializer.Deserialize<QueryEnvelope>(second.Json, QueryJson.Options)!;
        Assert.NotEqual(Assert.Single(envelope.Items).Id, Assert.Single(next.Items).Id);
        var changed = await Run("search", "--root", fixture.Root, "--term", "Other", "--page-size", "1", "--cursor", cursor);
        Assert.Equal(6, changed.Exit);
        Assert.Equal("CURSOR_FILTER_CHANGED", Code(changed.Json));
        var context = await Run("context", "--root", fixture.Root, "--id", fixture.Symbol("Service"));
        Assert.Equal(0, context.Exit);
        Assert.All(JsonSerializer.Deserialize<QueryEnvelope>(context.Json, QueryJson.Options)!.Items, item => Assert.Null(item.Snippet));
        var trusted = await Run("status", "--root", fixture.Root, "--trusted", "--freshness", "require-fresh");
        Assert.Equal(3, trusted.Exit);
        Assert.Equal("FRESHNESS_REQUIREMENT_NOT_MET", Code(trusted.Json));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.IndexPath)));
    }

    [Fact]
    public async Task CancellationAndUnsupportedStateProduceOneJsonWithoutWritingOrLeakingPaths()
    {
        using var fixture = new Fixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var output = new StringWriter();
        var errors = new StringWriter();
        Assert.Equal(130, await CliApplication.RunAsync(["status", "--root", fixture.Root], output, errors, cancelled.Token));
        Assert.Equal("CANCELLED", Code(output.ToString()));
        Assert.Empty(errors.ToString());
        File.WriteAllText(Path.Combine(fixture.Root, ".sharpdeps/workspace.json"), "{\"schemaVersion\":99}");
        var invalid = await Run("status", "--root", fixture.Root);
        Assert.Equal(6, invalid.Exit);
        Assert.Equal("WORKSPACE_STATE_INVALID", Code(invalid.Json));
        Assert.DoesNotContain(fixture.Root, invalid.Json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(invalid.Json);
        Assert.Single(document.RootElement.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task HelpAndDoctorAreMachineReadableAndDoNotProbeTargetSdk()
    {
        var help = await Run("help");
        Assert.Equal(0, help.Exit);
        Assert.Empty(help.Progress);
        using var helpDoc = JsonDocument.Parse(help.Json);
        Assert.Contains("index", helpDoc.RootElement.GetProperty("result").GetProperty("commands").EnumerateArray().Select(x => x.GetString()));
        var doctor = await Run("doctor");
        Assert.Equal(0, doctor.Exit);
        using var info = JsonDocument.Parse(doctor.Json);
        Assert.Equal("not-run", info.RootElement.GetProperty("result").GetProperty("sdkProbe").GetString());
    }

    [Fact]
    public async Task ActualIpcCoalescesPendingClientsAndCancellingOneCannotCancelSharedPublication()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        var publications = 0;
        var remoteAdmissions = 0;
        using var pinned = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var original = pinned.Snapshot();
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        await using var owner = new CliCoordinator(writer, async (_, token) =>
        {
            var count = Interlocked.Increment(ref builds);
            if (count == 1)
            {
                firstBuild.TrySetResult();
                await releaseBuild.Task.WaitAsync(token);
            }
            else
            {
                secondBuild.TrySetResult();
                await releaseSecond.Task.WaitAsync(token);
            }
            using var latest = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
            var previous = latest.Snapshot();
            return new(previous with { Graph = previous.Graph with { Generation = previous.Graph.Generation + 1,
                SnapshotId = Guid.NewGuid().ToString("N") } }, () => Interlocked.Increment(ref publications));
        });
        owner.RequestAccepted = id =>
        {
            if ((id is "cancel-client" or "surviving-client") && Interlocked.Increment(ref remoteAdmissions) == 2)
                admitted.TrySetResult();
        };
        var arguments = new[] { "update", "--root", fixture.Root, "--index", fixture.IndexPath, "--trusted", "--target", "App.csproj" };
        var update = CoordinatorUpdate.Parse(CliArguments.Parse(arguments), fixture.Root, fixture.IndexPath, fixture.Uuid);
        var initial = owner.UpdateAsync(update, "initial", deadline.Token);
        await firstBuild.Task.WaitAsync(deadline.Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var canceledClient = CoordinatorProtocol.TrySendAsync(owner.Owner, Request("cancel-client"), cancel.Token);
        var survivingClient = CoordinatorProtocol.TrySendAsync(owner.Owner, Request("surviving-client"), deadline.Token);
        await admitted.Task.WaitAsync(deadline.Token);
        Assert.Equal(2, owner.Status("status").DesiredRevision);
        releaseBuild.TrySetResult();
        await secondBuild.Task.WaitAsync(deadline.Token);
        Assert.Equal("INDEX_BUSY", Assert.Throws<IndexStoreException>(() =>
            IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true)).Code);
        var shortOwner = FinishShortOwnerAsync();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledClient);
        releaseSecond.TrySetResult();
        var survivor = Assert.IsType<CoordinatorReply>(await survivingClient);
        Assert.Null(survivor.ErrorCode);
        Assert.Equal(original.Graph.Generation + 1, survivor.Result!.Generation);
        Assert.Equal(survivor.Result.Generation, (await shortOwner).Result!.Generation);
        Assert.Equal(2, builds); // superseded extraction + one coalesced fresh extraction
        Assert.Equal(1, publications);
        Assert.Equal(original.Graph.Generation, pinned.Snapshot().Graph.Generation);
        Assert.Contains(pinned.Search("Service"), hit => hit.NodeId == fixture.Symbol("Service"));
        using var current = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        Assert.Equal(survivor.Result.Generation, current.Snapshot().Graph.Generation);
        Assert.Contains(current.Search("Service"), hit => hit.NodeId == fixture.Symbol("Service"));
        CoordinatorRequest Request(string id) => new(CoordinatorProtocol.Version, owner.Owner.Instance, fixture.Uuid,
            id, "update", fixture.Root, fixture.IndexPath, true, arguments);

        async Task<CoordinatorReply> FinishShortOwnerAsync()
        {
            var reply = await initial;
            await owner.DrainAsync();
            await owner.DisposeAsync();
            return reply;
        }
    }

    [Fact]
    public async Task IpcRejectsUntrustedAndWrongProtocolWithoutPublishingOrStealingTheLease()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        await using var owner = new CliCoordinator(writer, (_, _) => throw new InvalidOperationException("Must not extract"));
        var denied = await CoordinatorProtocol.TrySendAsync(owner.Owner, new(CoordinatorProtocol.Version, owner.Owner.Instance,
            fixture.Uuid, "denied", "update", fixture.Root, fixture.IndexPath, false, []), deadline.Token);
        Assert.Equal("WORKSPACE_UNTRUSTED", denied!.ErrorCode);
        var mismatch = await CoordinatorProtocol.TrySendAsync(owner.Owner, new(99, owner.Owner.Instance,
            fixture.Uuid, "mismatch", "status", fixture.Root, fixture.IndexPath, false, []), deadline.Token);
        Assert.Equal("COORDINATOR_PROTOCOL_MISMATCH", mismatch!.ErrorCode);
        Assert.Equal(0, owner.Status("status").DesiredRevision);
        Assert.Equal("INDEX_BUSY", Assert.Throws<IndexStoreException>(() =>
            IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true)).Code);
        Assert.Throws<CliFailure>(() => CoordinatorProtocol.Decode<CoordinatorRequest>(
            Encoding.UTF8.GetBytes("{\"protocol\":1,\"protocol\":99}")));
        Assert.Throws<CliFailure>(() => CoordinatorProtocol.Decode<CoordinatorRequest>([0xff, 0xfe]));
        using var tooLarge = new MemoryStream([0xff, 0xff, 0xff, 0x7f]);
        await Assert.ThrowsAsync<CliFailure>(() => CoordinatorProtocol.ReadAsync<CoordinatorRequest>(tooLarge, deadline.Token));
        using var reader = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        Assert.Equal(1, reader.Snapshot().Graph.Generation);
    }

    [Fact]
    public void SdkUnavailableAtPublicationRequiresOwnerRestartAndKeepsSavedGeneration()
    {
        using var fixture = new Fixture();
        var sdk = CliSdkSelection.Capture(fixture.Root);
        using var pinned = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var previous = pinned.Snapshot();
        var next = previous with { Graph = previous.Graph with { Generation = previous.Graph.Generation + 1,
            SnapshotId = Guid.NewGuid().ToString("N") } };
        var stage = IndexStage.Create(next);
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        var buildGuardCalled = false;
        var guard = CoordinatorExecutor.PublicationGuard(sdk, fixture.Root, () => buildGuardCalled = true);
        // Change SDK selection after staging, before the real transaction's publication callback.
        File.WriteAllText(Path.Combine(fixture.Root, "global.json"),
            "{\"sdk\":{\"version\":\"99.0.999\",\"rollForward\":\"disable\"}}");
        Assert.Equal("CLI_SDK_UNAVAILABLE", Assert.Throws<CliFailure>(() => CliSdkSelection.Capture(fixture.Root)).Code);
        var failure = Assert.Throws<CliFailure>(() => writer.Commit(stage, verifySelection: guard));
        Assert.Equal("COORDINATOR_SDK_RESTART_REQUIRED", failure.Code);
        Assert.Equal(6, failure.ExitCode);
        Assert.False(buildGuardCalled);
        Assert.Equal(previous.Graph.Generation, pinned.Snapshot().Graph.Generation);
        using var current = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        Assert.Equal(previous.Graph.Generation, current.Snapshot().Graph.Generation);
        Assert.Equal(previous.Graph.SnapshotId, current.Snapshot().Graph.SnapshotId);
        Assert.Equal(previous.Graph.Nodes.Select(node => node.Id), current.Snapshot().Graph.Nodes.Select(node => node.Id));
        Assert.Equal(pinned.Search("Service").Select(hit => hit.NodeId), current.Search("Service").Select(hit => hit.NodeId));
    }

    [Fact]
    public async Task ConnectionBudgetDoesNotStopOwnerOrCancelExistingSessions()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        await using var owner = new CliCoordinator(writer, (_, _) => throw new InvalidOperationException("Must not extract"));
        var connections = new List<NamedPipeClientStream>();
        try
        {
            for (var i = 0; i < 32; i++)
            {
                var pipe = new NamedPipeClientStream(".", owner.Owner.Endpoint, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                connections.Add(pipe);
                await pipe.ConnectAsync(1000, deadline.Token);
            }
            var request = new CoordinatorRequest(CoordinatorProtocol.Version, owner.Owner.Instance, fixture.Uuid,
                "within-cap", "status", fixture.Root, fixture.IndexPath, false, []);
            await CoordinatorProtocol.WriteAsync(connections[0], request, deadline.Token);
            var reply = await CoordinatorProtocol.ReadAsync<CoordinatorReply>(connections[0], deadline.Token);
            Assert.Equal("idle", reply.State);
            Assert.Null(reply.ErrorCode);
            Assert.False(owner.Stopped.IsCompleted);
            Assert.Equal("INDEX_BUSY", Assert.Throws<IndexStoreException>(() =>
                IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true)).Code);
        }
        finally { foreach (var connection in connections) connection.Dispose(); }
    }

    [Fact]
    public async Task NativeWatchUsesTheExistingLeaseAndPeriodicFullRecoversLostNotifications()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        File.WriteAllText(Path.Combine(fixture.Root, "App.csproj"), "<Project />");
        var markdown = Path.Combine(fixture.Root, "guide.markdown");
        File.WriteAllText(markdown, "# Before\n");
        File.WriteAllText(Path.Combine(fixture.Root, "view.mdx"), "# Static MDX text\n");
        using var pinned = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var original = pinned.Snapshot();
        // A custom index with an input-like suffix must never become a Markdown input or an event loop.
        var customIndex = Path.Combine(fixture.Root, "state", "index.mdx");
        using var writer = IndexWriter.Open(fixture.Root, customIndex, fixture.Uuid, true);
        writer.Commit(IndexStage.Create(original));
        var neighbor = customIndex + ".owner.json.notes.md";
        File.WriteAllText(neighbor, "# User source\n");
        Assert.False(CliIndexer.IsIndexOutput(neighbor, customIndex));
        Assert.True(CliIndexer.IsIndexOutput(customIndex + ".owner.json." + Guid.NewGuid().ToString("N") + ".tmp", customIndex));
        var beforeOwner = await CliIndexer.CaptureWatchInputsAsync(fixture.Root, customIndex, deadline.Token);
        var recoveryInput = Path.Combine(fixture.Root, "additional.dat");
        string? recoveredSnapshot = null;
        var incrementalBuilds = 0;
        await using var owner = new CliCoordinator(writer, (update, token) =>
        {
            if (!update.Full) Interlocked.Increment(ref incrementalBuilds);
            using var reader = IndexReader.Open(customIndex, fixture.Uuid);
            var prior = reader.Snapshot();
            var snapshot = prior with { Graph = prior.Graph with {
                Generation = prior.Graph.Generation + 1, SnapshotId = Guid.NewGuid().ToString("N") } };
            if (update.Full && File.Exists(recoveryInput)
                && File.ReadAllBytes(recoveryInput).SequenceEqual(new byte[] { 1, 0, 2, 0 }))
                Volatile.Write(ref recoveredSnapshot, snapshot.Graph.SnapshotId);
            return Task.FromResult(new CliIndexBuild(snapshot, () => { }));
        });
        var update = CoordinatorUpdate.Parse(CliArguments.Parse(["update", "--root", fixture.Root, "--index", customIndex,
            "--trusted", "--target", "App.csproj"]), fixture.Root, customIndex, fixture.Uuid);
        // Keep the periodic timer out of the native-event/self-output assertions.
        owner.StartWatch(update, TimeSpan.FromHours(1));
        await WaitUntilAsync(() => owner.Status("initial").Result?.Generation >= 2, deadline.Token);
        Assert.Equal("INDEX_BUSY", Assert.Throws<IndexStoreException>(() =>
            IndexWriter.Open(fixture.Root, customIndex, fixture.Uuid, true)).Code);
        Assert.Equal(beforeOwner, await CliIndexer.CaptureWatchInputsAsync(fixture.Root, customIndex, deadline.Token));
        // Prove the real native notification path by observing a non-full extraction, rather than timing a sleep.
        File.WriteAllText(markdown, "# Native edit\n");
        await WaitUntilAsync(() => Volatile.Read(ref incrementalBuilds) != 0
            && owner.Status("native").State == "idle", deadline.Token);
        await Task.Delay(250, deadline.Token); // settle duplicate OS hints from the one real input save
        var generation = owner.Status("saved").Result!.Generation;
        var temporary = customIndex + ".owner.json." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, "{}");
        File.Delete(temporary);
        await Task.Delay(200, deadline.Token);
        Assert.Equal(generation, owner.Status("no-self-loop").Result!.Generation);
        // Restart only the watch on the same owner/lease, then observe a subsequent periodic extraction.
        await owner.StopWatchAsync();
        owner.StartWatch(update, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => owner.Status("resync-started").Result?.Generation > generation
            && owner.Status("resync-started").State == "idle", deadline.Token);
        owner.Watch!.SetNotificationsEnabled(false);
        File.WriteAllBytes(recoveryInput, [1, 0, 2, 0]);
        IndexReader? recoveredReader = null;
        await WaitUntilAsync(() =>
        {
            var expected = Volatile.Read(ref recoveredSnapshot);
            if (expected is null || owner.Status("periodic").Result?.SnapshotId != expected) return false;
            var committed = IndexReader.Open(customIndex, fixture.Uuid);
            if (committed.Snapshot().Graph.SnapshotId != expected) { committed.Dispose(); return false; }
            recoveredReader = committed; // Pin this observed full commit before the next periodic stage.
            return true;
        }, deadline.Token);
        // Lost events use full extraction even when the selected Markdown manifest alone would be unchanged.
        using var current = recoveredReader!;
        Assert.Equal(original.Graph.Nodes.Select(node => node.Id), current.Snapshot().Graph.Nodes.Select(node => node.Id));
        Assert.Equal(pinned.Search("Service").Select(hit => hit.NodeId), current.Search("Service").Select(hit => hit.NodeId));
        Assert.Equal(1, pinned.Snapshot().Graph.Generation);
        await owner.StopWatchAsync();
        using var stoppedReader = IndexReader.Open(customIndex, fixture.Uuid);
        var stoppedGeneration = stoppedReader.Snapshot().Graph.Generation;
        File.WriteAllText(markdown, "# After stop\n");
        await Task.Delay(200, deadline.Token);
        using var afterStop = IndexReader.Open(customIndex, fixture.Uuid);
        Assert.Equal(stoppedGeneration, afterStop.Snapshot().Graph.Generation);
        var saved = await Run("search", "--root", fixture.Root, "--index", customIndex, "--term", "Service");
        Assert.Equal(0, saved.Exit);
        Assert.Contains("unverified", saved.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WatchHashGuardRollsBackLostEventAndStoppedStageCannotPublish()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        File.WriteAllText(Path.Combine(fixture.Root, "App.csproj"), "<Project />");
        var markdown = Path.Combine(fixture.Root, "guide.md");
        File.WriteAllText(markdown, "# Before\n");
        using var pinned = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        var original = pinned.Snapshot();
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var owner = new CliCoordinator(writer, async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(original with { Graph = original.Graph with { Generation = 2, SnapshotId = "watch-staged" } }, () => { });
        });
        var update = CoordinatorUpdate.Parse(CliArguments.Parse(["update", "--root", fixture.Root,
            "--trusted", "--target", "App.csproj"]), fixture.Root, fixture.IndexPath, fixture.Uuid);
        owner.StartWatch(update, TimeSpan.FromSeconds(30));
        await entered.Task.WaitAsync(deadline.Token);
        owner.Watch!.SetNotificationsEnabled(false);
        File.WriteAllText(markdown, "# Changed without notification\n");
        release.TrySetResult();
        await WaitUntilAsync(() => owner.Status("guard").ErrorCode == "WATCH_INPUT_CHANGED", deadline.Token);
        using (var unchanged = IndexReader.Open(fixture.IndexPath, fixture.Uuid))
        {
            Assert.Equal(original.Graph.Generation, unchanged.Snapshot().Graph.Generation);
            Assert.Equal(original.Graph.SnapshotId, unchanged.Snapshot().Graph.SnapshotId);
            Assert.Equal(pinned.Search("Service").Select(hit => hit.NodeId), unchanged.Search("Service").Select(hit => hit.NodeId));
        }
        await owner.StopWatchAsync();
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.StartWatch(update, TimeSpan.FromSeconds(30));
        await entered.Task.WaitAsync(deadline.Token);
        await owner.StopWatchAsync(); // cancels only the watch waiter, invalidating its eventual publication
        release.TrySetResult();
        await owner.DrainAsync();
        await owner.DisposeAsync();
        using var reacquired = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Uuid, true);
        using var final = IndexReader.Open(fixture.IndexPath, fixture.Uuid);
        Assert.Equal(original.Graph.Generation, final.Snapshot().Graph.Generation);
        Assert.Equal(original.Graph.Nodes.Select(node => node.Id), final.Snapshot().Graph.Nodes.Select(node => node.Id));
        Assert.Equal(pinned.Search("Service").Select(hit => hit.NodeId), final.Search("Service").Select(hit => hit.NodeId));
    }

    private static async Task WaitUntilAsync(Func<bool> ready, CancellationToken token)
    {
        while (!ready()) await Task.Delay(20, token);
    }

    private static string Code(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("errors")[0].GetProperty("code").GetString()!;
    }
    private static async Task<(int Exit, string Json, string Progress)> Run(params string[] arguments)
    {
        var output = new StringWriter(); var progress = new StringWriter();
        var exit = await CliApplication.RunAsync(arguments, output, progress);
        return (exit, output.ToString(), progress.ToString());
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Base = OperatingSystem.IsWindows()
            ? @"D:\DevData\AgentOps\work-cli-20261007\runs\sharpdeps-sd207-20261010-01\test-fixtures"
            : OperatingSystem.IsMacOS() ? "/private/tmp/sharpdeps-sd207-fixtures" : Path.Combine(Path.GetTempPath(), "sharpdeps-sd207-fixtures");
        private const string Text = "public class Service {} public class ServiceExtra {}\n";
        private readonly Guid uuid = Guid.NewGuid();
        public Guid Uuid => uuid;
        public string Root { get; } = Path.Combine(Base, Guid.NewGuid().ToString("N"));
        public string IndexPath => Path.Combine(Root, ".sharpdeps/index.sqlite");
        private string Project => HarnessIdentity.ProjectId(uuid, "App.csproj");
        private string Variant => HarnessIdentity.VariantId(uuid, Project, "net10.0", "Debug");
        public string Symbol(string name) => HarnessIdentity.LogicalSymbolId(uuid, Project, "type", "T:" + name);
        public Fixture(bool seed = true)
        {
            Directory.CreateDirectory(Root);
            if (!seed) return;
            Directory.CreateDirectory(Path.Combine(Root, ".sharpdeps"));
            File.WriteAllText(Path.Combine(Root, ".sharpdeps/workspace.json"), JsonSerializer.Serialize(new {
                schemaVersion = 1, workspaceUuid = uuid, cursorKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
            var bytes = new UTF8Encoding(false).GetBytes(Text);
            File.WriteAllBytes(Path.Combine(Root, "Service.cs"), bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var location = new HarnessLocation("src", "v1", hash, new(0, Text.Length));
            var nodes = new[] { new HarnessNode(Symbol("Service"), HarnessNodeKind.Type, "Service", Project, location),
                new HarnessNode(Symbol("ServiceExtra"), HarnessNodeKind.Type, "ServiceExtra", Project, location) };
            var workspace = HarnessIdentity.WorkspaceId(uuid);
            var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, 1, HarnessGraphContract.IdentityVersion, workspace, "cli-fixture", 1,
                HarnessCoverage.Partial, [new(Variant, Project, "net10.0", "Debug", null, null, null)],
                [new(workspace, HarnessNodeKind.Workspace, "Fixture", null, null), new(Project, HarnessNodeKind.Project, "App", workspace, null), .. nodes],
                nodes.Select(n => new HarnessSymbolOccurrence(HarnessIdentity.SymbolOccurrenceId(uuid, n.Id, Variant), n.Id, Variant, location, [location])).ToArray(),
                [], [], null);
            var snapshot = new IndexSnapshot(graph, [new("src", "Service.cs", "code", hash, bytes.Length, Text.Length)], [], [],
                nodes.Select(n => new IndexSearchText(n.Id, n.Name, "", "", "", "Service.cs")).ToArray());
            using var writer = IndexWriter.Open(Root, IndexPath, uuid, true);
            writer.Commit(IndexStage.Create(snapshot));
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Base) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped run root.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
