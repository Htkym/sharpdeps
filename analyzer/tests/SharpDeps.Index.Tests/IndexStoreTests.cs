namespace SharpDeps.Index.Tests;

using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using Xunit;

public sealed class IndexStoreTests
{
    private const string HttpGetCode = "namespace App; partial class Service { partial void Before(); [HttpGet(\" /café/{id} \"), HttpGet(\"\"), HttpGet] partial void Before() { } }\n";

    [Fact]
    public void HttpGetDeclarationRoundTripsThroughSqliteWithLiteralProvenanceAndLegacyNull()
    {
        using var fixture = new StoreFixture(code: HttpGetCode);
        var snapshot = HttpGetSnapshot(fixture);
        var attribute = snapshot.Graph.Edges[0];
        var saved = attribute.HttpGetDeclaration!;
        var emptyStart = HttpGetCode.IndexOf("HttpGet(\"\")", StringComparison.Ordinal);
        var omittedStart = HttpGetCode.LastIndexOf("HttpGet", StringComparison.Ordinal);
        var empty = new HarnessHttpGetDeclaration(HarnessHttpGetDeclarationState.Saved, "",
            new(HttpGetCode.IndexOf("\"\"", StringComparison.Ordinal), 2));
        var omitted = new HarnessHttpGetDeclaration(HarnessHttpGetDeclarationState.Omitted, OmissionReason: "no-template");
        snapshot = snapshot with { Graph = snapshot.Graph with { Edges =
        [
            attribute with { Id = "http-get-saved" },
            attribute with { Id = "http-get-empty", Evidence = attribute.Evidence! with { RawSpan = new(emptyStart, 7) }, HttpGetDeclaration = empty },
            attribute with { Id = "http-get-omitted", Evidence = attribute.Evidence! with { RawSpan = new(omittedStart, 7) }, HttpGetDeclaration = omitted },
            attribute with { Id = "legacy-attribute", HttpGetDeclaration = null }
        ] } };
        using (var writer = fixture.Writer()) writer.Commit(IndexStage.Create(snapshot));
        using var reader = fixture.Reader();
        var edges = reader.Snapshot().Graph.Edges.ToDictionary(edge => edge.Id, StringComparer.Ordinal);
        Assert.Equal(saved, edges["http-get-saved"].HttpGetDeclaration);
        Assert.Equal(" /café/{id} ", edges["http-get-saved"].HttpGetDeclaration!.Template);
        Assert.Equal(empty, edges["http-get-empty"].HttpGetDeclaration);
        Assert.Equal(omitted, edges["http-get-omitted"].HttpGetDeclaration);
        Assert.Null(edges["legacy-attribute"].HttpGetDeclaration);
        Assert.All(edges.Values, edge =>
        {
            Assert.Equal(attribute.SourceNodeId, edge.SourceNodeId);
            Assert.Equal(attribute.SourceOccurrenceId, edge.SourceOccurrenceId);
            Assert.Equal(attribute.TargetNodeId, edge.TargetNodeId);
            Assert.Null(edge.TargetOccurrenceId);
            Assert.Equal(attribute.VariantId, edge.VariantId);
            Assert.Equal(attribute.Evidence!.ContentHash, edge.Evidence!.ContentHash);
        });
        var occurrence = Assert.Single(reader.Snapshot().Graph.SymbolOccurrences);
        var literal = saved.TemplateSpan!.Value;
        Assert.True(literal.Start > occurrence.Location!.RawSpan!.Value.End);
        Assert.Contains(occurrence.Declarations!, location => location.SourceId == attribute.Evidence!.SourceId
            && location.RawSpan is { } range && range.Start <= attribute.Evidence.RawSpan!.Value.Start
            && range.End >= literal.End);
    }

    [Fact]
    public void StagingRejectsPartialUnsafeOrForeignHttpGetDeclarationMetadata()
    {
        using var fixture = new StoreFixture(code: HttpGetCode);
        var snapshot = HttpGetSnapshot(fixture);
        var attribute = snapshot.Graph.Edges[0];
        var saved = attribute.HttpGetDeclaration!;
        foreach (var declaration in new[]
        {
            saved with { Template = null },
            saved with { TemplateSpan = null },
            saved with { OmissionReason = "no-template" },
            saved with { Template = "unsafe\ntext" },
            saved with { Template = "\uD800" },
            saved with { Template = new string('x', HarnessHttpGetDeclaration.MaximumTemplateUtf16 + 1) },
            saved with { TemplateSpan = new(saved.TemplateSpan!.Value.Start, 0) },
            saved with { TemplateSpan = new(attribute.Evidence!.RawSpan!.Value.Start, 1) },
            saved with { TemplateSpan = new(HttpGetCode.Length - 2, 1) },
            saved with { TemplateSpan = new(HttpGetCode.Length, 1) },
            saved with { TemplateSpan = new(saved.TemplateSpan!.Value.Start, HarnessHttpGetDeclaration.MaximumLiteralSpanUtf16 + 1) },
            saved with { State = (HarnessHttpGetDeclarationState)123 },
            new(HarnessHttpGetDeclarationState.Omitted, Template: "partial", OmissionReason: "nonliteral-template"),
            new(HarnessHttpGetDeclarationState.Omitted, TemplateSpan: new(0, 1), OmissionReason: "semantic-unavailable"),
            new(HarnessHttpGetDeclarationState.Omitted),
            new(HarnessHttpGetDeclarationState.Omitted, OmissionReason: "unknown-reason")
        }) Reject(attribute with { HttpGetDeclaration = declaration });
        var foreign = snapshot.Graph.Nodes.Single(node => node.Kind == HarnessNodeKind.Document).Location!;
        foreach (var edge in new[]
        {
            attribute with { Kind = "calls" },
            attribute with { Certainty = HarnessCertainty.Candidate },
            attribute with { Producer = "roslyn-operation" },
            attribute with { SourceOccurrenceId = null },
            attribute with { TargetNodeId = fixture.MemberId },
            attribute with { Evidence = null },
            attribute with { Evidence = attribute.Evidence! with { RawSpan = null } },
            attribute with { Evidence = attribute.Evidence! with { RawSpan = new(0, 0) } },
            attribute with { Evidence = attribute.Evidence! with { ContentHash = null } },
            attribute with { Evidence = attribute.Evidence! with { SourceVersion = "other-version" } },
            attribute with { Evidence = foreign with { RawSpan = new(0, 1) },
                HttpGetDeclaration = saved with { TemplateSpan = new(2, 1) } }
        }) Reject(edge);
        var maximum = saved with { Template = new string('x', HarnessHttpGetDeclaration.MaximumTemplateUtf16) };
        var atLimit = Enumerable.Range(0, HarnessHttpGetDeclaration.MaximumSnapshotTemplateUtf16 / maximum.Template!.Length)
            .Select(i => attribute with { Id = "http-get-" + i, HttpGetDeclaration = maximum }).ToArray();
        _ = IndexStage.Create(snapshot with { Graph = snapshot.Graph with { Edges = atLimit } });
        AssertCode("INDEX_INVALID_SNAPSHOT", () => IndexStage.Create(snapshot with { Graph = snapshot.Graph with
        { Edges = atLimit.Append(attribute with { Id = "http-get-over-budget", HttpGetDeclaration = saved with { Template = "x" } }).ToArray() } }));
        Assert.False(File.Exists(fixture.IndexPath));

        void Reject(HarnessEdge edge) => AssertCode("INDEX_INVALID_SNAPSHOT",
            () => IndexStage.Create(snapshot with { Graph = snapshot.Graph with { Edges = [edge] } }));
    }

    [Fact]
    public void ExtensionBindingMetadataRoundTripsThroughSqliteWithoutRequiringItOnLegacyCalls()
    {
        using var fixture = new StoreFixture();
        var snapshot = fixture.Snapshot(1, "Before");
        var saved = new HarnessExtensionBinding(HarnessExtensionBindingState.Saved,
            "global::Demo.Receivér", "global::Demo.Extensions.Project<global::System.String>(global::Demo.Receivér)");
        var omitted = new HarnessExtensionBinding(HarnessExtensionBindingState.Omitted, OmissionReason: "metadata-budget");
        var call = ExtensionCall(fixture, snapshot, saved);
        snapshot = snapshot with { Graph = snapshot.Graph with { Edges =
        [
            call with { Id = "extension-saved" },
            call with { Id = "extension-omitted", ExtensionBinding = omitted },
            call with { Id = "legacy-call", ExtensionBinding = null }
        ] } };
        using (var writer = fixture.Writer()) writer.Commit(IndexStage.Create(snapshot));
        using var reader = fixture.Reader();
        var edges = reader.Snapshot().Graph.Edges.ToDictionary(edge => edge.Id, StringComparer.Ordinal);
        Assert.Equal(saved, edges["extension-saved"].ExtensionBinding);
        Assert.Equal(omitted, edges["extension-omitted"].ExtensionBinding);
        Assert.Null(edges["legacy-call"].ExtensionBinding);
        Assert.All(edges.Values, edge =>
        {
            Assert.Equal(call.SourceOccurrenceId, edge.SourceOccurrenceId);
            Assert.Equal(call.TargetOccurrenceId, edge.TargetOccurrenceId);
            Assert.Equal(call.Evidence, edge.Evidence);
        });
    }

    [Fact]
    public void StagingRejectsPartialUnsafeOrUnboundExtensionBindingMetadata()
    {
        using var fixture = new StoreFixture();
        var snapshot = fixture.Snapshot(1, "Before");
        var saved = new HarnessExtensionBinding(HarnessExtensionBindingState.Saved, "Receiver", "Extensions.Project<Argument>(Receiver)");
        var call = ExtensionCall(fixture, snapshot, saved);
        var invalidBindings = new[]
        {
            saved with { ReceiverTypeDisplay = null },
            saved with { ConstructedMethodDisplay = null },
            saved with { OmissionReason = "metadata-budget" },
            saved with { ReceiverTypeDisplay = new string('x', HarnessExtensionBinding.MaximumReceiverDisplayUtf16 + 1) },
            saved with { ConstructedMethodDisplay = new string('x', HarnessExtensionBinding.MaximumMethodDisplayUtf16 + 1) },
            saved with { ReceiverTypeDisplay = "Receiver\nforged-output" },
            saved with { ConstructedMethodDisplay = "Method\0forged-output" },
            saved with { ReceiverTypeDisplay = "\uD800" },
            saved with { State = (HarnessExtensionBindingState)123 },
            new(HarnessExtensionBindingState.Omitted, ReceiverTypeDisplay: "partial", OmissionReason: "unsupported-type"),
            new(HarnessExtensionBindingState.Omitted),
            new(HarnessExtensionBindingState.Omitted, OmissionReason: "unknown-reason")
        };
        foreach (var binding in invalidBindings)
            Reject(call with { ExtensionBinding = binding });
        foreach (var edge in new[]
        {
            call with { Kind = "references_member" },
            call with { Certainty = HarnessCertainty.Candidate },
            call with { Producer = "other-producer" },
            call with { SourceOccurrenceId = null },
            call with { TargetOccurrenceId = null },
            call with { Evidence = null },
            call with { Evidence = call.Evidence! with { RawSpan = null } },
            call with { Evidence = call.Evidence! with { ContentHash = null } }
        }) Reject(edge);
        Assert.False(File.Exists(fixture.IndexPath));

        void Reject(HarnessEdge edge) => AssertCode("INDEX_INVALID_SNAPSHOT",
            () => IndexStage.Create(snapshot with { Graph = snapshot.Graph with { Edges = [edge] } }));
    }

    [Fact]
    public void HeaderProjectionStillRejectsIncompleteStoredGraphCollections()
    {
        using var fixture = new StoreFixture();
        using (var writer = fixture.Writer()) writer.Commit(IndexStage.Create(fixture.Snapshot(1, "Before")));
        using (var connection = IndexDatabase.Open(fixture.IndexPath, readOnly: false))
            IndexDatabase.Execute(connection, null, "UPDATE generations SET graph_json=json_set(graph_json,'$.nodes',NULL)");
        Assert.Throws<System.Text.Json.JsonException>(() => { using var reader = fixture.Reader(); });
    }

    [Fact]
    public void FilesystemRootWorkspaceCommitsAnOwnedInput()
    {
        using var fixture = new StoreFixture();
        var root = Path.GetPathRoot(fixture.Root)!;
        var relative = Path.GetRelativePath(root, fixture.CodePath).Replace('\\', '/');
        var bytes = File.ReadAllBytes(fixture.CodePath);
        var workspace = HarnessIdentity.WorkspaceId(fixture.Workspace);
        var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
            HarnessGraphContract.IdentityVersion, workspace, "root-workspace", 1, HarnessCoverage.Partial,
            [], [new(workspace, HarnessNodeKind.Workspace, "Workspace", null, null)], [], [], [], null);
        var snapshot = new IndexSnapshot(graph, [new("owned-input", relative, "code",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length, StoreFixture.Code.Length)], [], [], []);
        using (var writer = IndexWriter.Open(root, fixture.IndexPath, fixture.Workspace, isTrusted: true))
            writer.Commit(IndexStage.Create(snapshot));
        using var reader = fixture.Reader();
        Assert.Equal(relative, Assert.Single(reader.Snapshot().Files).RelativePath);
    }

    [Fact]
    public void FrozenStagingSurvivesProducerMutationReopenAndBackupWithDurableEvidence()
    {
        using var fixture = new StoreFixture();
        var snapshot = fixture.Snapshot(1, "Before");
        var inputManifestHash = IndexStage.Create(snapshot).ManifestHash;
        var reuseCatalog = new[] { new IndexReuseSymbol(fixture.MemberId, "App.Service.Before", "Before",
            "M:App.Service.Before", "src/App.csproj", null, snapshot.Graph.Variants[0].Id, fixture.CodeLocation.SourceId) };
        var reuse = new IndexReuseProof("test-owner/1", new string('a', 64), new string('b', 64),
            new string('c', 64), new string('d', 64), "unused-sdk-path", reuseCatalog);
        snapshot = snapshot with { Files = snapshot.Files.Select((file, i) => i == 0 ? file with { Reuse = reuse } : file).ToArray() };
        var mutableNodes = snapshot.Graph.Nodes.ToArray();
        var mutableSections = snapshot.Documents[0].Sections.ToArray();
        var mutableSearch = snapshot.SearchText.ToArray();
        snapshot = snapshot with
        {
            Graph = snapshot.Graph with { Nodes = mutableNodes },
            Documents = [snapshot.Documents[0] with { Sections = mutableSections }],
            SearchText = mutableSearch
        };
        var stage = IndexStage.Create(snapshot);
        Assert.Equal(inputManifestHash, stage.ManifestHash); // Optional optimization metadata does not change schema-1 input hashes.
        reuseCatalog[0] = reuseCatalog[0] with { FullName = "mutated-after-staging" };
        mutableNodes[0] = mutableNodes[0] with { Name = "mutated-after-staging" };
        mutableSections[0] = mutableSections[0] with { Token = "mutated-token" };
        mutableSearch[0] = mutableSearch[0] with { Name = "mutated-search" };
        using (var writer = fixture.Writer())
        {
            writer.Commit(stage);
            writer.BackupTo(fixture.BackupPath, isTrusted: true);
        }

        // Reopening has no dependency on the producer's objects or an in-memory identity cache.
        using var reader = fixture.Reader();
        var saved = reader.Snapshot();
        Assert.Equal(HarnessIdentity.WorkspaceId(fixture.Workspace), saved.Graph.WorkspaceId);
        Assert.Equal(stage.ManifestHash, reader.ManifestHash);
        Assert.Equal("App.Service.Before", Assert.Single(Assert.Single(saved.Files, f => f.Reuse is not null).Reuse!.Catalog).FullName);
        Assert.Equal("Workspace", Assert.Single(saved.Graph.Nodes, n => n.Kind == HarnessNodeKind.Workspace).Name);
        var document = Assert.Single(saved.Documents);
        Assert.Equal(fixture.Document, document.DocumentUuid);
        Assert.Equal("persisted:intro", Assert.Single(document.Sections).Token);
        Assert.Contains(saved.Graph.Nodes, n => n.Id == fixture.SectionId);
        Assert.Equal(snapshot.Aliases[0], Assert.Single(saved.Aliases));
        var mention = Assert.Single(saved.Graph.Nodes, n => n.Kind == HarnessNodeKind.SymbolMention);
        var evidence = Assert.Single(saved.Graph.Edges);
        Assert.Equal(mention.Id, evidence.SourceNodeId);
        Assert.Equal(fixture.MemberId, evidence.TargetNodeId);
        Assert.Equal(HarnessCertainty.Candidate, evidence.Certainty);
        Assert.Equal(snapshot.Graph.Edges[0].Evidence, evidence.Evidence);
        Assert.Equal("explicit-metadata", evidence.Origin);
        Assert.Equal(fixture.MemberId, Assert.Single(reader.Search("Before")).NodeId);
        Assert.Empty(reader.Search("mutated-search"));
        using var backup = IndexReader.Open(fixture.BackupPath, fixture.Workspace);
        Assert.Equal(saved.Graph.SnapshotId, backup.Snapshot().Graph.SnapshotId);
        Assert.Equal(stage.ManifestHash, backup.ManifestHash);
        Assert.Equal(fixture.SectionId, Assert.Single(backup.Search("設定")).NodeId);
    }

    [Fact]
    public void AnOpenReaderKeepsOneGenerationWhileNewReadersObserveCommittedRowsAndFts()
    {
        using var fixture = new StoreFixture();
        using var writer = fixture.Writer();
        writer.Commit(IndexStage.Create(fixture.Snapshot(1, "Before")));
        using var oldReader = fixture.Reader();
        Assert.Equal(1, oldReader.Snapshot().Graph.Generation);
        writer.Commit(IndexStage.Create(fixture.Snapshot(2, "After")));

        Assert.Equal(1, oldReader.Snapshot().Graph.Generation);
        Assert.Equal(fixture.MemberId, Assert.Single(oldReader.Search("Before")).NodeId);
        Assert.Empty(oldReader.Search("After"));
        using var current = fixture.Reader();
        var saved = current.Snapshot();
        Assert.Equal(2, saved.Graph.Generation);
        Assert.Equal("snapshot-2", saved.Graph.SnapshotId);
        Assert.Equal("After", Assert.Single(saved.Graph.Nodes, n => n.Id == fixture.MemberId).Name);
        Assert.Equal(fixture.MemberId, Assert.Single(current.Search("After")).NodeId);
        Assert.Empty(current.Search("Before"));
    }

    [Fact]
    public void CancellationAndChangedSourceAtThePublishBoundaryRollbackRowsFtsAndGeneration()
    {
        using var fixture = new StoreFixture();
        using var writer = fixture.Writer();
        var baseline = IndexStage.Create(fixture.Snapshot(1, "Before"));
        writer.Commit(baseline);
        var pending = IndexStage.Create(fixture.Snapshot(2, "After"));
        using var cancellation = new CancellationTokenSource();
        writer.BeforePublish = cancellation.Cancel;
        Assert.Throws<OperationCanceledException>(() => writer.Commit(pending, cancellation.Token));
        AssertBaseline();

        writer.BeforePublish = () => File.WriteAllText(fixture.CodePath, "changed after FTS insert", StoreFixture.Utf8);
        AssertCode("SOURCE_CHANGED_SINCE_STAGING", () => writer.Commit(pending));
        AssertBaseline();
        writer.BeforePublish = null;
        File.WriteAllText(fixture.CodePath, StoreFixture.Code, StoreFixture.Utf8);
        // Added inputs are not in the staged manifest. The extraction owner's selection guard must
        // still roll back the already-inserted rows/FTS before a new generation becomes visible.
        var lateInput = Path.Combine(fixture.Root, "Late.cs");
        writer.BeforePublish = () => File.WriteAllText(lateInput, "public class Late {}", StoreFixture.Utf8);
        AssertCode("SOURCE_CHANGED_SINCE_STAGING", () => writer.Commit(pending, verifySelection: () =>
        {
            if (File.Exists(lateInput))
                throw new IndexStoreException("SOURCE_CHANGED_SINCE_STAGING", "Input membership changed.");
        }));
        AssertBaseline();
        writer.BeforePublish = null;
        File.Delete(lateInput);
        writer.Commit(pending); // Failed publication does not poison the writer or reserve generation 2.
        using var committed = fixture.Reader();
        Assert.Equal(2, committed.Snapshot().Graph.Generation);
        Assert.Equal(fixture.MemberId, Assert.Single(committed.Search("After")).NodeId);

        void AssertBaseline()
        {
            using var reader = fixture.Reader();
            Assert.Equal("snapshot-1", reader.Snapshot().Graph.SnapshotId);
            Assert.Equal(baseline.ManifestHash, reader.ManifestHash);
            Assert.Equal(fixture.MemberId, Assert.Single(reader.Search("Before")).NodeId);
            Assert.Empty(reader.Search("After"));
        }
    }

    [Fact]
    public void SavedReadsAndTrustOrSchemaRejectionsLeaveExistingSourceAndDatabaseIntact()
    {
        using var fixture = new StoreFixture();
        var beforeUntrusted = fixture.FileNames();
        AssertCode("INDEX_NOT_FOUND", () => { using var unexpected = fixture.Reader(); });
        AssertCode("INDEX_UNTRUSTED", () =>
        {
            using var unexpected = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Workspace, isTrusted: false);
        });
        Assert.Equal(beforeUntrusted, fixture.FileNames());
        using (var writer = fixture.Writer())
        {
            writer.Commit(IndexStage.Create(fixture.Snapshot(1, "Before")));
            AssertCode("INDEX_UNTRUSTED", () => writer.BackupTo(fixture.BackupPath, isTrusted: false));
            Assert.False(File.Exists(fixture.BackupPath));
        }
        var database = File.ReadAllBytes(fixture.IndexPath);
        var source = File.ReadAllBytes(fixture.CodePath);
        var names = fixture.FileNames();
        using (var reader = fixture.Reader())
        {
            Assert.Equal(1, reader.Snapshot().Graph.Generation);
            Assert.NotEmpty(reader.Search("設定"));
            Assert.NotEmpty(reader.ManifestHash);
        }
        Assert.Equal(database, File.ReadAllBytes(fixture.IndexPath));
        Assert.Equal(source, File.ReadAllBytes(fixture.CodePath));
        Assert.Equal(names, fixture.FileNames());
        AssertCode("INDEX_WORKSPACE_MISMATCH", () =>
        {
            using var unexpected = IndexReader.Open(fixture.IndexPath, Guid.NewGuid());
        });

        // A future writer sets SQLite user_version; checkpoint it so the read-only header probe sees it.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.IndexPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=99; PRAGMA wal_checkpoint(FULL);";
            command.ExecuteNonQuery();
        }
        var futureDatabase = File.ReadAllBytes(fixture.IndexPath);
        Assert.Equal(99, BinaryPrimitives.ReadInt32BigEndian(futureDatabase.AsSpan(60, 4)));
        var futureNames = fixture.FileNames();
        AssertCode("INDEX_SCHEMA_UNSUPPORTED", () => { using var unexpected = fixture.Reader(); });
        AssertCode("INDEX_SCHEMA_UNSUPPORTED", () => { using var unexpected = fixture.Writer(); });
        Assert.Equal(futureDatabase, File.ReadAllBytes(fixture.IndexPath));
        Assert.Equal(source, File.ReadAllBytes(fixture.CodePath));
        Assert.Equal(futureNames, fixture.FileNames());
    }

    [Fact]
    public void MissingSharedMemoryRejectsWithoutRecoveringAnUncheckpointedFutureSchema()
    {
        using var fixture = new StoreFixture();
        using (var writer = fixture.Writer())
            writer.Commit(IndexStage.Create(fixture.Snapshot(1, "Before")));
        using (var pinnedReader = fixture.Reader())
        {
            using var futureWriter = IndexDatabase.Open(fixture.IndexPath, readOnly: false);
            IndexDatabase.Execute(futureWriter, null, "PRAGMA user_version=99;");
            Assert.Equal(1, pinnedReader.Snapshot().Graph.Generation);
        }
        // The pinned reader prevented close-time checkpoint. Its readonly close preserves the WAL.
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(File.ReadAllBytes(fixture.IndexPath).AsSpan(60, 4)));
        File.Delete(fixture.IndexPath + "-shm");
        var database = File.ReadAllBytes(fixture.IndexPath);
        var wal = File.ReadAllBytes(fixture.IndexPath + "-wal");
        AssertCode("INDEX_RECOVERY_REQUIRED", () => { using var unexpected = fixture.Writer(); });
        AssertCode("INDEX_RECOVERY_REQUIRED", () => { using var unexpected = fixture.Reader(); });
        Assert.False(File.Exists(fixture.IndexPath + "-shm"));
        Assert.Equal(database, File.ReadAllBytes(fixture.IndexPath));
        Assert.Equal(wal, File.ReadAllBytes(fixture.IndexPath + "-wal"));
    }

    [Fact]
    public void SearchFindsJapaneseSubstringsAndRanksNamesAndSignaturesAheadOfBodyText()
    {
        using var fixture = new StoreFixture();
        var signatureOnly = HarnessIdentity.LogicalSymbolId(fixture.Workspace, fixture.ProjectId, "method", "M:App.UseService");
        var snapshot = RankedSnapshot(fixture, 1);
        using (var writer = fixture.Writer()) writer.Commit(IndexStage.Create(snapshot));
        using var reader = fixture.Reader();
        Assert.Equal(fixture.SectionId, Assert.Single(reader.Search("定の手")).NodeId);
        var hits = reader.Search("Service");
        Assert.Equal(new[] { fixture.MemberId, signatureOnly, fixture.SectionId }, hits.Select(h => h.NodeId));
        Assert.Equal(fixture.MemberId, Assert.Single(reader.Search("Service", limit: 1)).NodeId);
        Assert.Equal(signatureOnly, Assert.Single(reader.Search("Configure")).NodeId);
        Assert.Empty(reader.Search("no-such-name"));
    }

    [Fact]
    public void IdenticalGenerationRanksDoNotDependOnHistoryAndPinnedReadersKeepTheSameScores()
    {
        using var fresh = new StoreFixture();
        using var longHistory = new StoreFixture(fresh.Workspace, fresh.Document);
        using var shortHistory = new StoreFixture(fresh.Workspace, fresh.Document);
        using var freshWriter = fresh.Writer();
        using var longWriter = longHistory.Writer();
        using var shortWriter = shortHistory.Writer();
        for (var generation = 1; generation <= 4; generation++)
        {
            var historical = longHistory.Snapshot(generation, "Archived");
            longWriter.Commit(IndexStage.Create(historical with
            {
                SearchText = historical.SearchText.Select(row => row with
                { Body = string.Join(' ', Enumerable.Repeat("Service", 1024)) }).ToArray()
            }));
        }
        shortWriter.Commit(IndexStage.Create(shortHistory.Snapshot(1, "Unrelated")));
        // The same graph IDs and current search corpus are stored after zero, four and one older generations.
        var current = RankedSnapshot(fresh, 10);
        freshWriter.Commit(IndexStage.Create(current));
        longWriter.Commit(IndexStage.Create(current));
        shortWriter.Commit(IndexStage.Create(current));
        using var baseline = fresh.Reader();
        var expected = baseline.Search("Service").ToArray();
        Assert.Equal(new[] { fresh.MemberId,
            HarnessIdentity.LogicalSymbolId(fresh.Workspace, fresh.ProjectId, "method", "M:App.UseService"), fresh.SectionId },
            expected.Select(hit => hit.NodeId));
        Assert.All(expected, hit =>
        {
            Assert.Equal("fts5-unicode61", hit.MatchMethod);
            Assert.True(double.IsFinite(hit.Rank) && hit.Rank < 0);
        });
        using var pinned = longHistory.Reader();
        using var sameGenerationNewReader = longHistory.Reader();
        using var otherHistory = shortHistory.Reader();
        Assert.Equal(expected, pinned.Search("Service").ToArray());
        Assert.Equal(expected, sameGenerationNewReader.Search("Service").ToArray());
        Assert.Equal(expected, otherHistory.Search("Service").ToArray());
        Assert.Equal(10, pinned.Snapshot().Graph.Generation);
        Assert.Equal(10, sameGenerationNewReader.Snapshot().Graph.Generation);

        // Adding another corpus cannot change the score/order of either pinned generation-10 reader.
        longWriter.Commit(IndexStage.Create(RankedSnapshot(longHistory, 11)));
        using var latest = longHistory.Reader();
        Assert.Equal(11, latest.Snapshot().Graph.Generation);
        Assert.Equal(expected, latest.Search("Service").ToArray());
        Assert.Equal(expected, pinned.Search("Service").ToArray());
        Assert.Equal(expected, sameGenerationNewReader.Search("Service").ToArray());
    }

    private static IndexSnapshot RankedSnapshot(StoreFixture fixture, long generation)
    {
        var snapshot = fixture.Snapshot(generation, "Service");
        var signatureOnly = HarnessIdentity.LogicalSymbolId(fixture.Workspace, fixture.ProjectId, "method", "M:App.UseService");
        return snapshot with
        {
            Graph = snapshot.Graph with { Nodes = snapshot.Graph.Nodes.Concat(
                [new HarnessNode(signatureOnly, HarnessNodeKind.Member, "Use", fixture.ProjectId, fixture.CodeLocation,
                    "App.Service Configure()")]).ToArray() },
            SearchText = [snapshot.SearchText[0], snapshot.SearchText[1] with { Body = "Service の設定の手順を説明する。" },
                new(signatureOnly, "Use", "App.Service Configure()", "", "", "src/App.cs")]
        };
    }

    [Fact]
    public void StagingRejectsDanglingReferencesInvalidIdentityHashesAndOutOfRangeEvidence()
    {
        using var fixture = new StoreFixture();
        var snapshot = fixture.Snapshot(1, "Before");
        var graph = snapshot.Graph;
        var invalid = new[]
        {
            snapshot with { Graph = graph with { Edges = [graph.Edges[0] with { TargetNodeId = "missing-target" }] } },
            snapshot with { Graph = graph with { SymbolOccurrences = [graph.SymbolOccurrences[0] with { VariantId = "missing-variant" }] } },
            snapshot with { Graph = graph with { Nodes = graph.Nodes.Select(n => n.Id == fixture.MemberId
                ? n with { Location = fixture.CodeLocation with { ContentHash = new string('0', 64) } } : n).ToArray() } },
            snapshot with { Graph = graph with { Edges = [graph.Edges[0] with { Evidence = graph.Edges[0].Evidence! with
                { RawSpan = new(0, StoreFixture.Markdown.Length + 1) } }] } },
            snapshot with { Documents = [snapshot.Documents[0] with { Sections = [snapshot.Documents[0].Sections[0] with { Token = "other-token" }] }] },
            snapshot with { Files = [snapshot.Files[0] with { ContentHash = "not-sha256" }, snapshot.Files[1]] },
            snapshot with { SearchText = [snapshot.SearchText[0] with { NodeId = "missing-search-node" }] }
        };
        foreach (var rejected in invalid)
            AssertCode("INDEX_INVALID_SNAPSHOT", () => IndexStage.Create(rejected));
        Assert.False(File.Exists(fixture.IndexPath)); // Pure staging never creates a database.
    }

    [Fact]
    public async Task WriterLockIsExclusiveAcrossProcessesAndReleasedWhenItsOwnerIsTerminated()
    {
        using var fixture = new StoreFixture();
        var probe = Path.Combine(AppContext.BaseDirectory, "WriterLockProbe", "WriterLockProbe.dll");
        Assert.True(File.Exists(probe), "The build must copy the writer-lock probe beside the test assembly.");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add(fixture.Root);
        start.ArgumentList.Add(fixture.IndexPath);
        start.ArgumentList.Add(fixture.Workspace.ToString("D"));
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            Assert.Equal("writer-ready", await process.StandardOutput.ReadLineAsync(timeout.Token));
            AssertCode("INDEX_BUSY", () => { using var unexpected = fixture.Writer(); });
            process.Kill(entireProcessTree: false); // Only the child PID this test started is owned.
            await process.WaitForExitAsync(timeout.Token);
            // TerminateProcess is asynchronous; process exit can precede final kernel handle cleanup.
            // The live-owner assertion above must still fail immediately. Recovery waits only for that owned lease.
            IndexWriter recoveredWriter;
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try { recoveredWriter = fixture.Writer(); break; }
                catch (IndexStoreException error) when (error.Code == "INDEX_BUSY")
                { await Task.Delay(25, timeout.Token); }
            }
            using var recovered = recoveredWriter;
            recovered.Commit(IndexStage.Create(fixture.Snapshot(1, "Recovered")));
            using var reader = fixture.Reader();
            Assert.Equal(fixture.MemberId, Assert.Single(reader.Search("Recovered")).NodeId);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Writer-lock probe failure before fixture cleanup: " + error);
            throw;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanupTimeout.Token);
            }
        }
    }

    private static IndexSnapshot HttpGetSnapshot(StoreFixture fixture)
    {
        var snapshot = fixture.Snapshot(1, "Before");
        var target = HarnessIdentity.ExternalSymbolId(fixture.Workspace,
            "Microsoft.AspNetCore.Mvc.Core, Version=10.0.0.0, Culture=neutral, PublicKeyToken=adb9793829ddae60",
            "type", "T:Microsoft.AspNetCore.Mvc.HttpGetAttribute");
        var nameStart = HttpGetCode.IndexOf("HttpGet", StringComparison.Ordinal);
        var literalStart = HttpGetCode.IndexOf('"');
        var primaryStart = HttpGetCode.IndexOf("partial void", StringComparison.Ordinal);
        var primary = fixture.CodeLocation with { RawSpan = new(primaryStart, HttpGetCode.IndexOf(';', primaryStart) + 1 - primaryStart) };
        var start = HttpGetCode.IndexOf('[');
        var end = HttpGetCode.LastIndexOf('}', HttpGetCode.LastIndexOf('}') - 1) + 1;
        var declaration = fixture.CodeLocation with { RawSpan = new(start, end - start) };
        var occurrence = snapshot.Graph.SymbolOccurrences[0] with { Location = primary, Declarations = [primary, declaration] };
        var attribute = snapshot.Graph.Edges[0] with
        {
            SourceNodeId = fixture.MemberId, SourceOccurrenceId = occurrence.Id, TargetNodeId = target, TargetOccurrenceId = null,
            Kind = "attribute", Certainty = HarnessCertainty.Resolved, Producer = "roslyn-declaration", Origin = "userSource",
            Evidence = fixture.CodeLocation with { RawSpan = new(nameStart, 7) },
            HttpGetDeclaration = new(HarnessHttpGetDeclarationState.Saved, " /café/{id} ", new(literalStart, "\" /café/{id} \"".Length))
        };
        return snapshot with { Graph = snapshot.Graph with
        {
            Nodes = snapshot.Graph.Nodes.Append(new(target, HarnessNodeKind.ExternalSymbol, "HttpGetAttribute", null, null)).ToArray(),
            SymbolOccurrences = [occurrence], Edges = [attribute]
        } };
    }

    private static HarnessEdge ExtensionCall(StoreFixture fixture, IndexSnapshot snapshot, HarnessExtensionBinding binding)
        => snapshot.Graph.Edges[0] with
        {
            SourceNodeId = fixture.MemberId, SourceOccurrenceId = snapshot.Graph.SymbolOccurrences[0].Id,
            Kind = "calls", Certainty = HarnessCertainty.Resolved, Producer = "roslyn-operation",
            Evidence = fixture.CodeLocation, Origin = "userSource", ExtensionBinding = binding
        };

    private static void AssertCode(string expected, Action action)
        => Assert.Equal(expected, Assert.Throws<IndexStoreException>(action).Code);

    private sealed class StoreFixture : IDisposable
    {
        public static readonly UTF8Encoding Utf8 = new(false, true);
        private static readonly string FixtureBase = OperatingSystem.IsWindows()
            ? @"D:\DevData\AgentOps\work-cli-20261007\runs\sharpdeps-sd205-20261010-01\fixtures-revision01"
            : Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "sharpdeps-sd205-fixtures-revision01");
        public const string Code = "namespace App; class Service { public void Before() { } }\n";
        private readonly string _code;
        public const string Markdown = "# 設定\r\n\r\n😀 Service の設定の手順を説明する。\r\n";
        public Guid Workspace { get; }
        public Guid Document { get; }
        public string Root { get; } = Path.GetFullPath(Path.Combine(FixtureBase, Guid.NewGuid().ToString("N")));
        public string IndexPath => Path.Combine(Root, ".sharpdeps", "index.sqlite");
        public string BackupPath => Path.Combine(Root, "backup", "index.sqlite");
        public string CodePath => Path.Combine(Root, "src", "App.cs");
        public string ProjectId => HarnessIdentity.ProjectId(Workspace, "src/App.csproj");
        public string MemberId => HarnessIdentity.LogicalSymbolId(Workspace, ProjectId, "method", "M:App.Service.Before");
        public string DocumentId => HarnessIdentity.DocumentId(Workspace, Document);
        public string SectionId => HarnessIdentity.SectionId(Workspace, DocumentId, "persisted:intro");
        public HarnessLocation CodeLocation => new("src/App.cs", "code-v1", Hash(_code), new(0, _code.Length));

        public StoreFixture(Guid? workspace = null, Guid? document = null, string? code = null)
        {
            _code = code ?? Code;
            Workspace = workspace ?? Guid.NewGuid();
            Document = document ?? Guid.NewGuid();
            Directory.CreateDirectory(Path.GetDirectoryName(CodePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
            File.WriteAllText(CodePath, _code, Utf8);
            File.WriteAllText(Path.Combine(Root, "readme.md"), Markdown, Utf8);
        }

        public IndexWriter Writer() => IndexWriter.Open(Root, IndexPath, Workspace, isTrusted: true);
        public IndexReader Reader() => IndexReader.Open(IndexPath, Workspace);
        public string[] FileNames() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(Root, p)).Order(StringComparer.Ordinal).ToArray();

        public IndexSnapshot Snapshot(long generation, string memberName)
        {
            var workspaceId = HarnessIdentity.WorkspaceId(Workspace);
            var variant = HarnessIdentity.VariantId(Workspace, ProjectId, "net10.0", "Debug");
            var occurrence = HarnessIdentity.SymbolOccurrenceId(Workspace, MemberId, variant);
            var markdownLocation = new HarnessLocation("readme.md", "markdown-v1", Hash(Markdown), new(0, Markdown.Length));
            var evidenceStart = Markdown.IndexOf("Service", StringComparison.Ordinal);
            var evidence = markdownLocation with { RawSpan = new(evidenceStart, "Service".Length) };
            var mentionId = "hmdf_" + Hash("mention");
            var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
                HarnessGraphContract.IdentityVersion, workspaceId, "snapshot-" + generation, generation,
                HarnessCoverage.Partial, [new(variant, ProjectId, "net10.0", "Debug", null, null, Hash(_code))],
                [new(workspaceId, HarnessNodeKind.Workspace, "Workspace", null, null),
                    new(ProjectId, HarnessNodeKind.Project, "App", workspaceId, null),
                    new(MemberId, HarnessNodeKind.Member, memberName, ProjectId, CodeLocation, memberName + "()"),
                    new(DocumentId, HarnessNodeKind.Document, "readme.md", workspaceId, markdownLocation),
                    new(SectionId, HarnessNodeKind.Section, "設定", DocumentId, markdownLocation),
                    new(mentionId, HarnessNodeKind.SymbolMention, "Service", SectionId, evidence)],
                [new(occurrence, MemberId, variant, CodeLocation, [CodeLocation])],
                [new("hmdf_" + Hash("edge"), mentionId, MemberId, null, occurrence, variant,
                    "mentions_symbol", HarnessCertainty.Candidate, "sharpdeps-markdown/1", evidence, "explicit-metadata")],
                [new(2, "legacy-member", MemberId, occurrence)],
                new("2.0.0-preview.3", Hash("fixed-source"), "2.0.0-preview.3", "1.0.0", "lithosharp-markdown/1", Hash("options")),
                [new("fixture-candidate", 1)]);
            return new(graph,
                [Manifest("src/App.cs", "code", _code), Manifest("readme.md", "markdown", Markdown)],
                [new(Document, "readme.md", "guide", Hash(Markdown), "scope", "2.0.0-preview.3", "1.0.0",
                    "lithosharp-markdown/1", Hash("options"), [new("section-local", "persisted:intro", "1:設定", Hash(Markdown))])],
                [new("verified-move", "retired-section", SectionId, "verified-owner-move", HarnessCertainty.Resolved)],
                [new(MemberId, memberName, memberName + "()", "", "", "src/App.cs"),
                    new(SectionId, "", "", "設定", "設定の手順を説明する。", "readme.md")]);
        }

        private static IndexFile Manifest(string path, string kind, string text)
            => new(path, path, kind, Hash(text), Utf8.GetByteCount(text), text.Length);
        private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(text))).ToLowerInvariant();

        public void Dispose()
        {
            // Delete only this fixture's verified, unique directory under the named run.
            var allowed = Path.GetFullPath(FixtureBase) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(Root);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!resolved.StartsWith(allowed, comparison))
                throw new InvalidOperationException("Fixture cleanup escaped the designated run directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
