using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Index;

// Test-only executable. No discovery, arbitrary SQL, arbitrary paths, or product hooks.
// This token bounds control reads only. Synchronous Publish/checkpoint calls are bounded by the
// parent's response/process deadline; SQLite checkpoint also has a positive provider timeout.
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
try
{
    if (args.Length != 3 || !Guid.TryParseExact(args[1], "N", out var workspace)
        || !Guid.TryParseExact(args[2], "N", out var launchId) || launchId == Guid.Empty)
        throw new ArgumentException("Expected an existing empty fixture root, workspace GUID (N), and nonempty launch GUID (N).");
    var root = Path.GetFullPath(args[0]);
    if (!Path.IsPathFullyQualified(args[0]) || !Directory.Exists(root)
        || Path.GetFileName(root) != "writer-" + workspace.ToString("N")
        || Directory.EnumerateFileSystemEntries(root).Any())
        throw new ArgumentException("Root must be a new empty writer-{workspace:N} directory.");
    RejectLinks(root);
    var index = Path.Combine(root, ".sharpdeps", "index.sqlite");
    using var identity = Process.GetCurrentProcess();
    var start = identity.StartTime.ToUniversalTime().ToString("O");
    using (var writer = IndexWriter.Open(root, index, workspace, isTrusted: true))
    {
        Publish(writer, root, workspace, 1, "Before");
        await Reply(new { command = "ready", pid = Environment.ProcessId, launchId, start, generation = 1 });
        using var checkpoint = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = index, Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Private,
            Pooling = false, DefaultTimeout = 1
        }.ToString());
        checkpoint.Open();
        using (var settings = checkpoint.CreateCommand())
        {
            settings.CommandTimeout = 1;
            settings.CommandText = "PRAGMA busy_timeout=0; PRAGMA wal_autocheckpoint=0;";
            settings.ExecuteNonQuery();
        }
        // Ordered commands prevent a checkpoint or publication from being mistaken for another phase.
        foreach (var expected in new[] { "publish", "active", "idle", "stop" })
        {
            var line = await ReadLine(Console.In, deadline.Token);
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.GetProperty("command").GetString() != expected)
                throw new InvalidDataException("Unexpected protocol command; expected " + expected);
            RejectLinks(root);
            RejectTree(root);
            if (expected == "publish")
            {
                Publish(writer, root, workspace, 2, "After");
                await Reply(new { command = expected, pid = Environment.ProcessId, launchId, start, generation = 2 });
            }
            else if (expected != "stop")
            {
                using var command = checkpoint.CreateCommand();
                command.CommandTimeout = 1; // Positive provider retry bound, in addition to the parent deadline.
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using var rows = command.ExecuteReader();
                if (!rows.Read()) throw new InvalidDataException("Missing checkpoint tuple.");
                var busy = rows.GetInt64(0);
                var log = rows.GetInt64(1);
                var checkpointed = rows.GetInt64(2);
                if (rows.Read()) throw new InvalidDataException("Extra checkpoint tuple.");
                await Reply(new { command = expected, pid = Environment.ProcessId, launchId, start, busy, log, checkpointed });
            }
        }
    }
    // Stop acknowledgment is deliberately after writer/connection disposal.
    await Reply(new { command = "stop", pid = Environment.ProcessId, launchId, start });
    return 0;
}
catch (Exception error)
{
    await Console.Error.WriteLineAsync(error.GetType().Name + ": " + error.Message[..Math.Min(error.Message.Length, 2048)]);
    return 1;
}

static async Task Reply(object value)
{
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));
    await Console.Out.FlushAsync();
}

static async Task<string> ReadLine(TextReader reader, CancellationToken token)
{
    var line = new StringBuilder();
    var character = new char[1];
    while (await reader.ReadAsync(character.AsMemory(), token) != 0)
    {
        if (character[0] == '\n') return line.ToString();
        if (line.Length >= 1024) throw new InvalidDataException("Command exceeds 1024 characters.");
        line.Append(character[0]);
    }
    throw new EndOfStreamException("Control input ended before stop.");
}

static void RejectLinks(string path)
{
    for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Fixture paths cannot contain reparse points.");
}

static void RejectTree(string root)
{
    var pending = new Stack<string>();
    pending.Push(root);
    var count = 0;
    long bytes = 0;
    while (pending.TryPop(out var directory))
    foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
    {
        if (++count > 128 || (item.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Fixture contains unexpected entries or a reparse point.");
        if (item is DirectoryInfo) pending.Push(item.FullName);
        else if (item is FileInfo file && (bytes += file.Length) > 16 * 1024 * 1024)
            throw new IOException("Fixture exceeds the tiny-fixture byte budget.");
    }
}

static void Publish(IndexWriter writer, string root, Guid workspace, int generation, string name)
{
    // Separate source paths keep the retained G1 manifest valid while G2 is published.
    var path = name + ".cs";
    var source = "class " + name + " { }\n";
    var bytes = new UTF8Encoding(false, true).GetBytes(source);
    File.WriteAllBytes(Path.Combine(root, path), bytes);
    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    var id = HarnessIdentity.WorkspaceId(workspace);
    var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
        HarnessGraphContract.IdentityVersion, id, "writer-fixture-" + generation, generation,
        HarnessCoverage.CompleteWithinScope, [],
        [new(id, HarnessNodeKind.Workspace, "Fixture", null, null),
         new("file-App", HarnessNodeKind.SourceFile, name, id, new(path, null, hash, new(0, source.Length)))],
        [], [], [], null);
    writer.Commit(IndexStage.Create(new IndexSnapshot(graph,
        [new(path, path, "source", hash, bytes.Length, source.Length)], [], [],
        [new("file-App", name, "", "", name + " body", path)])));
}
