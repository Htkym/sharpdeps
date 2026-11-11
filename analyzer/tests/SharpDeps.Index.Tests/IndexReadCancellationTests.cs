namespace SharpDeps.Index.Tests;

using System.Collections;
using System.Security.Cryptography;
using System.Text;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using Xunit;

public sealed class IndexReadCancellationTests
{
    [Fact]
    public void CancelledOpenCachedSnapshotAndSearchKeepTheCommittedGenerationIntact()
    {
        using var fixture = new Fixture();
        using var writer = IndexWriter.Open(fixture.Root, fixture.IndexPath, fixture.Workspace, true);
        writer.Commit(IndexStage.Create(fixture.Snapshot));
        var database = ReadShared(fixture.IndexPath);
        var wal = ReadShared(fixture.IndexPath + "-wal");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
        {
            using var unexpected = IndexReader.Open(fixture.IndexPath, fixture.Workspace, cancellation.Token);
        });
        using var live = new CancellationTokenSource();
        using (var reader = IndexReader.Open(fixture.IndexPath, fixture.Workspace, live.Token))
        {
            Assert.Equal(1, reader.Snapshot(live.Token).Graph.Generation);
            Assert.Equal("file-App", Assert.Single(reader.Search("Before", token: live.Token)).NodeId);
            live.Cancel();
            Assert.Throws<OperationCanceledException>(() => reader.Snapshot(live.Token));
            Assert.Throws<OperationCanceledException>(() => reader.Search("Before", token: live.Token));
            Assert.Equal(1, reader.Snapshot().Graph.Generation);
            Assert.Equal("file-App", Assert.Single(reader.Search("Before")).NodeId);
        }
        Assert.Equal(database, ReadShared(fixture.IndexPath));
        Assert.Equal(wal, ReadShared(fixture.IndexPath + "-wal"));
        using var reopened = IndexReader.Open(fixture.IndexPath, fixture.Workspace);
        Assert.Equal("cancel-fixture", reopened.Snapshot().Graph.SnapshotId);
    }

    [Fact]
    public void VectorizedControlValidationMatchesEveryUtf16CodeUnit()
    {
        var prefix = new string('a', 40);
        var suffix = new string('z', 40);
        for (var code = 0; code <= char.MaxValue; code++)
        {
            var value = prefix + (char)code + suffix;
            Assert.Equal(char.IsControl((char)code), IndexSnapshotValidator.ContainsControlCharacters(value.AsSpan()));
        }
        Assert.False(IndexSnapshotValidator.ContainsControlCharacters(ReadOnlySpan<char>.Empty));
    }

    [Fact]
    public void CancellationDuringCollectionValidationIsNotReportedAsInvalidStorage()
    {
        var baseline = Fixture.CreateSnapshot(Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        var nodes = new CancellingList<HarnessNode>(baseline.Graph.Nodes, cancellation);
        var snapshot = baseline with { Graph = baseline.Graph with { Nodes = nodes } };
        Assert.Throws<OperationCanceledException>(() => IndexSnapshotValidator.Validate(snapshot, cancellation.Token));
        Assert.Equal(2, nodes.Visited);
        IndexSnapshotValidator.Validate(baseline);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(256)]
    public void RowBatchCancellationCannotPublishPartialResults(int count)
    {
        using var cancellation = new CancellationTokenSource();
        var row = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new HarnessNode("id", HarnessNodeKind.SourceFile, "name", null, null), IndexReadJsonContext.Default.Options);
        var rows = new CancellingRows(Enumerable.Repeat(row, count).ToArray(), cancellation);
        var info = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<HarnessNode>)IndexReadJsonContext.Default.GetTypeInfo(typeof(HarnessNode))!;
        Assert.Throws<OperationCanceledException>(() => IndexReader.DeserializeRows(rows, info, cancellation.Token));
        Assert.Equal(count, IndexReader.DeserializeRows(Enumerable.Repeat(row, count).ToArray(), info, default).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParallelRowsPreserveOrderAndFirstCorruption(bool nullFirst)
    {
        var values = Enumerable.Range(0, 256).Select(i => new HarnessNode("id-" + i, HarnessNodeKind.SourceFile, "name-" + i, null, null)).ToArray();
        var rows = values.Select(node => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node, IndexReadJsonContext.Default.Options)).ToArray();
        var info = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<HarnessNode>)IndexReadJsonContext.Default.GetTypeInfo(typeof(HarnessNode))!;
        Assert.Equal(values, IndexReader.DeserializeRows(rows, info, default));
        rows[7] = System.Text.Encoding.UTF8.GetBytes(nullFirst ? "null" : "{");
        rows[8] = System.Text.Encoding.UTF8.GetBytes(nullFirst ? "{" : "null");
        if (nullFirst)
            Assert.Equal("INDEX_INVALID_SNAPSHOT", Assert.Throws<IndexStoreException>(() => IndexReader.DeserializeRows(rows, info, default)).Code);
        else Assert.Throws<System.Text.Json.JsonException>(() => IndexReader.DeserializeRows(rows, info, default));
    }

    private sealed class CancellingRows(byte[][] values, CancellationTokenSource cancellation) : IReadOnlyList<byte[]>
    {
        private int visited;
        public int Count => values.Length;
        public byte[] this[int index]
        {
            get
            {
                if (Interlocked.Increment(ref visited) == 2) cancellation.Cancel();
                return values[index];
            }
        }
        public IEnumerator<byte[]> GetEnumerator() => ((IEnumerable<byte[]>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancellingList<T>(IReadOnlyList<T> values, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => values.Count;
        public T this[int index] => values[index];
        public int Visited { get; private set; }
        public IEnumerator<T> GetEnumerator()
        {
            foreach (var value in values)
            {
                if (++Visited == 2) cancellation.Cancel();
                yield return value;
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static byte[] ReadShared(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        input.CopyTo(bytes);
        return bytes.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string FixtureBase = Path.GetFullPath(Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "sharpdeps-index-read-cancellation"));
        private const string Source = "class App { }\n";
        private static readonly UTF8Encoding Utf8 = new(false, true);
        public string Root { get; } = Path.Combine(FixtureBase, Guid.NewGuid().ToString("N"));
        public string IndexPath => Path.Combine(Root, ".sharpdeps", "index.sqlite");
        public Guid Workspace { get; } = Guid.NewGuid();
        public IndexSnapshot Snapshot => CreateSnapshot(Workspace);
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "App.cs"), Source, Utf8);
        }
        public static IndexSnapshot CreateSnapshot(Guid workspace)
        {
            var workspaceId = HarnessIdentity.WorkspaceId(workspace);
            var hash = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(Source))).ToLowerInvariant();
            var location = new HarnessLocation("App.cs", null, hash, new(0, Source.Length));
            var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
                HarnessGraphContract.IdentityVersion, workspaceId, "cancel-fixture", 1, HarnessCoverage.CompleteWithinScope,
                [], [new(workspaceId, HarnessNodeKind.Workspace, "Fixture", null, null),
                    new("file-App", HarnessNodeKind.SourceFile, "Before", workspaceId, location)], [], [], [], null);
            return new(graph, [new("App.cs", "App.cs", "source", hash, Utf8.GetByteCount(Source), Source.Length)],
                [], [], [new("file-App", "Before", "", "", "", "App.cs")]);
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(FixtureBase + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
