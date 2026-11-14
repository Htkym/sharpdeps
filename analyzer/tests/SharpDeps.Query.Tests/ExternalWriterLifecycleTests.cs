namespace SharpDeps.Query.Tests;

using System.Diagnostics;
using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SharpDeps.Index;
using Xunit;

[Collection("SharpDeps lifecycle diagnostics")]
public sealed class ExternalWriterLifecycleTests
{
    [Fact]
    public async Task SeparateWriterKeepsActiveGenerationCoherentAndIdleHostReleasesWal()
    {
        // Deliberately fail rather than silently skip when the separately built probe is absent.
        var dll = RequiredFile("SHARPDEPS_LIFECYCLE_WRITER_DLL",
            Path.Combine(AppContext.BaseDirectory, "LifecycleWriterProbe", "SharpDeps.Lifecycle.WriterProbe.dll"));
        // The coordinated verification launcher pins DOTNET_HOST_PATH; PATH fallback supports ordinary CI.
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is null
            ? "dotnet" : RequiredFile("DOTNET_HOST_PATH");
        var configuredEvidence = Environment.GetEnvironmentVariable("SHARPDEPS_LIFECYCLE_EVIDENCE_DIR");
        if (configuredEvidence is not null && !Path.IsPathFullyQualified(configuredEvidence))
            throw new InvalidOperationException("SHARPDEPS_LIFECYCLE_EVIDENCE_DIR must be absolute.");
        var evidenceBase = Path.GetFullPath(configuredEvidence ?? Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "sharpdeps-lifecycle"));
        RejectLinks(evidenceBase);
        Directory.CreateDirectory(evidenceBase);
        var workspace = Guid.NewGuid();
        var evidence = Path.Combine(evidenceBase, "wal-" + workspace.ToString("N"));
        Directory.CreateDirectory(evidence);
        var root = Path.Combine(evidence, "writer-" + workspace.ToString("N"));
        Directory.CreateDirectory(root);
        // Parent deadline bounds child synchronous publication/checkpoint work through response waits.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var receipts = new List<object>();
        var ownerPhases = new List<object>();
        var connectionObservations = new List<object>();
        var host = new PreparedExplorationHost(); // Default session, with native support verified from metrics below.
        PreparedExplorationLease? lease = null;
        Child? child = null;
        var passed = false;
        var nativeRetainedConnectionVerified = false;
        var nativeRetentionStatus = "unavailable";
        var requireNative = Environment.GetEnvironmentVariable("SHARPDEPS_LIFECYCLE_REQUIRE_NATIVE") == "1";
        Exception? failure = null;
        try
        {
            child = new Child(dotnet, dll, root, workspace, receipts);
            child.Initialize(); // The finally block owns the process before any fallible post-start setup.
            var ready = await child.Receive("ready", deadline.Token);
            Assert.Equal(1, ready.GetProperty("generation").GetInt32());
            Assert.NotEqual(Environment.ProcessId, child.Id);
            lease = await Acquire(host, root, workspace, deadline.Token);
            AssertGeneration(lease, 1, "Before", "After");
            var published = await child.Command("publish", deadline.Token);
            Assert.Equal(2, published.GetProperty("generation").GetInt32());
            AssertGeneration(lease, 1, "Before", "After");
            var active = await child.Command("active", deadline.Token);
            Assert.Equal(1, active.GetProperty("busy").GetInt64());
            Assert.True(active.GetProperty("log").GetInt64() > 0, "Active checkpoint must observe nonzero WAL frames.");
            var done = active.GetProperty("checkpointed").GetInt64();
            Assert.InRange(done, 0L, active.GetProperty("log").GetInt64());
            lease.Dispose();
            lease = null;
            // Releasing G1 after an external commit resets its stale connection. Warm and release G2
            // before checkpointing. Establish a stable hit independently of checkpoint revalidation.
            lease = await Acquire(host, root, workspace, deadline.Token);
            AssertGeneration(lease, 2, "After", "Before");
            lease.Dispose();
            lease = null;
            var priorMetrics = MetricFiles(root);
            lease = await Acquire(host, root, workspace, deadline.Token);
            AssertGeneration(lease, 2, "After", "Before");
            var beforeStatus = ReadOwner(root, priorMetrics, "stable-before-checkpoint", ownerPhases);
            Assert.True(beforeStatus is "native-validated-reuse" or "full-validation-fallback");
            if (requireNative) Assert.Equal("native-validated-reuse", beforeStatus);
            var retainedConnection = ObserveConnection(lease.Reader);
            RecordConnection(retainedConnection, "active-before-checkpoint", connectionObservations);
            lease.Dispose();
            lease = null;
            RecordConnection(retainedConnection, "idle-before-checkpoint", connectionObservations);
            if (beforeStatus == "native-validated-reuse") Assert.Equal(ConnectionState.Open, retainedConnection.State);
            var idle = await child.Command("idle", deadline.Token);
            RecordConnection(retainedConnection, "idle-after-checkpoint", connectionObservations);
            if (beforeStatus == "native-validated-reuse") Assert.Equal(ConnectionState.Open, retainedConnection.State);
            Assert.Equal(0, idle.GetProperty("busy").GetInt64());
            Assert.Equal(0, idle.GetProperty("log").GetInt64());
            Assert.Equal(0, idle.GetProperty("checkpointed").GetInt64());
            priorMetrics = MetricFiles(root);
            lease = await Acquire(host, root, workspace, deadline.Token);
            AssertGeneration(lease, 2, "After", "Before");
            var postStatus = ReadOwner(root, priorMetrics, "after-checkpoint", ownerPhases);
            var postConnection = ObserveConnection(lease.Reader);
            RecordConnection(postConnection, "active-after-checkpoint", connectionObservations);
            if (beforeStatus == "native-validated-reuse")
            {
                Assert.Same(retainedConnection, postConnection);
                Assert.Equal(ConnectionState.Open, postConnection.State);
                Assert.True(postStatus is "native-validated-reuse" or "native-revalidated");
            }
            lease.Dispose();
            lease = null;
            priorMetrics = MetricFiles(root);
            lease = await Acquire(host, root, workspace, deadline.Token);
            AssertGeneration(lease, 2, "After", "Before");
            var finalStatus = ReadOwner(root, priorMetrics, "stable-after-checkpoint", ownerPhases);
            Assert.True(finalStatus is "native-validated-reuse" or "full-validation-fallback");
            nativeRetainedConnectionVerified = beforeStatus == "native-validated-reuse"
                && (postStatus is "native-validated-reuse" or "native-revalidated")
                && finalStatus == "native-validated-reuse";
            nativeRetentionStatus = nativeRetainedConnectionVerified ? postStatus : "full-validation-fallback";
            if (requireNative) Assert.True(nativeRetainedConnectionVerified, "Native acceptance rejects fallback at any proof barrier.");
            lease.Dispose();
            lease = null;
            await host.DisposeAsync().AsTask().WaitAsync(deadline.Token);
            await child.Command("stop", deadline.Token);
            await child.Finish(deadline.Token);
            GC.KeepAlive(host);
            passed = true;
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            try { lease?.Dispose(); }
            catch (Exception error) { passed = false; failure ??= error; }
            try { await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { passed = false; failure ??= error; }
            if (child is not null)
            {
                try { await child.DisposeAsync(); }
                catch (Exception error) { passed = false; failure ??= error; }
            }
            if (passed && configuredEvidence is null)
            {
                try { DeleteOwnedTinyRoot(root, evidence, workspace); }
                catch (Exception error) { passed = false; failure ??= error; }
            }
            // Always retain failures and all explicit-output evidence. JSON can be paired with TRX by test name/run ID.
            var receipt = new
            {
                test = nameof(SeparateWriterKeepsActiveGenerationCoherentAndIdleHostReleasesWal),
                runId = workspace, utc = DateTimeOffset.UtcNow, passed, failure = failure?.ToString(),
                root, dll, dotnet, parentPid = Environment.ProcessId,
                commands = receipts, stderr = child?.ErrorText, trailingStdout = child?.TrailingText,
                forcedCleanup = child?.ForcedCleanup ?? false,
                nativeRetainedConnectionVerified, nativeRetentionStatus, requireNative,
                ownerPhases, connectionObservations,
                connectionProofScope = "Read-only object identity and Open state at barriers; not exhaustive native HANDLE continuity proof.",
                nodeCountPerGeneration = 2, generations = 2
            };
            RejectLinks(evidence);
            using (var output = new FileStream(Path.Combine(evidence, "receipt.json"), FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(output, receipt, new JsonSerializerOptions { WriteIndented = true });
            if (!passed && failure is not null && child?.ForcedCleanup == true)
                Assert.Fail("Lifecycle row failed; forced child cleanup is not normal drain. Evidence: " + evidence + "\n" + failure);
            if (!passed && failure is not null)
                Assert.Fail("Lifecycle row failed. Evidence: " + evidence + "\n" + failure);
        }
    }

    private static Task<PreparedExplorationLease> Acquire(PreparedExplorationHost host, string root, Guid workspace, CancellationToken token)
        => host.AcquireAsync(root, Path.Combine(root, ".sharpdeps", "index.sqlite"), workspace,
            Path.Combine(root, ".sharpdeps", "metrics"), token);

    private static HashSet<string> MetricFiles(string root) => Directory.GetFiles(
        Path.Combine(root, ".sharpdeps", "metrics"), "*.jsonl", SearchOption.AllDirectories).ToHashSet(StringComparer.Ordinal);

    private static string ReadOwner(string root, HashSet<string> prior, string phase, List<object> evidence)
    {
        var path = Assert.Single(MetricFiles(root).Except(prior));
        Assert.InRange(new FileInfo(path).Length, 1L, 128 * 1024L);
        var raw = File.ReadAllText(path);
        evidence.Add(new { phase, raw }); // Preserve raw owner evidence even if classification fails.
        return VerifyRetentionMetrics(raw);
    }

    private static DbConnection ObserveConnection(IndexReader reader)
    {
        // Test-only read of an existing reference. No SQL, method invocation, mutation, or product hook.
        var field = typeof(IndexReader).GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("IndexReader.connection diagnostic field is unavailable.");
        return Assert.IsAssignableFrom<DbConnection>(field.GetValue(reader));
    }

    private static void RecordConnection(DbConnection connection, string phase, List<object> evidence)
        => evidence.Add(new { phase, objectIdentity = RuntimeHelpers.GetHashCode(connection), state = connection.State.ToString() });

    private static string VerifyRetentionMetrics(string text)
    {
        var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToArray();
        Assert.NotEmpty(rows);
        var runId = rows[0].GetProperty("runId").GetString();
        for (var index = 0; index < rows.Length; index++)
        {
            Assert.Equal(runId, rows[index].GetProperty("runId").GetString());
            Assert.Equal(index + 1, rows[index].GetProperty("sequence").GetInt64());
        }
        var payloads = rows.Select(row => row.GetProperty("payload")).ToArray();
        Assert.Equal("run.start", payloads[0].GetProperty("type").GetString());
        Assert.Equal("explore-prepare", payloads[0].GetProperty("operation").GetString());
        Assert.Equal("run.end", payloads[^1].GetProperty("type").GetString());
        Assert.Equal("completed", payloads[^1].GetProperty("outcome").GetString());
        bool Decision(string stage, string reason) => payloads.Any(payload =>
            payload.GetProperty("type").GetString() == "decision"
            && payload.GetProperty("stage").GetString() == stage
            && payload.GetProperty("reason").GetString() == reason);
        var native = Decision("reader.session", "validated-snapshot-hit") && Decision("explore.host", "generation-reused");
        var revalidated = Decision("reader.session", "validated-snapshot-miss") && Decision("explore.host", "generation-prepared");
        var fallback = Decision("reader.session", "full-validation-fallback");
        Assert.Equal(1, (native ? 1 : 0) + (revalidated ? 1 : 0) + (fallback ? 1 : 0));
        return native ? "native-validated-reuse" : revalidated ? "native-revalidated" : "full-validation-fallback";
    }

    private static void AssertGeneration(PreparedExplorationLease lease, int generation, string present, string absent)
    {
        Assert.Same(lease.Reader.Snapshot(), lease.Prepared.saved);
        Assert.Equal(generation, lease.Prepared.saved.Graph.Generation);
        Assert.Equal(present, Assert.Single(lease.Prepared.saved.Graph.Nodes, node => node.Id == "file-App").Name);
        var search = lease.Prepared.GetSearchIndex(default);
        Assert.Equal(present, search.Search["file-App"].Node.Name);
        Assert.Contains(search.Names[present], node => node.Id == "file-App");
        Assert.False(search.Names.ContainsKey(absent));
        Assert.Contains(lease.Reader.Search(present, 10), hit => hit.NodeId == "file-App");
        Assert.Empty(lease.Reader.Search(absent, 10));
    }

    private static string RequiredFile(string variable, string? fallback = null)
    {
        var value = Environment.GetEnvironmentVariable(variable) ?? fallback;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || !File.Exists(value))
            throw new InvalidOperationException(variable + " must name an existing absolute file. Build and configure the lifecycle helper before this test.");
        RejectLinks(value);
        return Path.GetFullPath(value);
    }

    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Lifecycle paths cannot contain reparse points: " + current);
    }

    private static void DeleteOwnedTinyRoot(string root, string evidence, Guid workspace)
    {
        if (Path.GetFullPath(root) != Path.Combine(Path.GetFullPath(evidence), "writer-" + workspace.ToString("N")))
            throw new IOException("Cleanup root does not match the exact owned fixture.");
        RejectLinks(root);
        var pending = new Stack<string>();
        pending.Push(root);
        var count = 0;
        long bytes = 0;
        while (pending.TryPop(out var directory))
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (++count > 128 || (item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Cleanup rejected unexpected fixture entries.");
                if (item is DirectoryInfo) pending.Push(item.FullName);
                else if (item is FileInfo file && (bytes += file.Length) > 16 * 1024 * 1024)
                    throw new IOException("Cleanup rejected an unexpectedly large fixture.");
            }
        Directory.Delete(root, recursive: true);
    }

    private sealed class Child : IAsyncDisposable
    {
        private readonly Process process;
        private readonly List<object> receipts;
        private readonly CancellationTokenSource io = new();
        private Task stderr = Task.CompletedTask;
        private Task? trailing;
        private readonly PrefixCapture stderrCapture = new();
        private readonly PrefixCapture trailingCapture = new();
        private string? start;
        private bool finished;
        private int responseCharacters;
        internal int Id => process.Id;
        internal bool ForcedCleanup { get; private set; }
        internal string ErrorText => stderrCapture.Text;
        internal string TrailingText => trailingCapture.Text;

        internal Child(string dotnet, string dll, string root, Guid workspace, List<object> receipts)
        {
            this.receipts = receipts;
            var info = new ProcessStartInfo(dotnet)
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.ArgumentList.Add(dll);
            info.ArgumentList.Add(root);
            info.ArgumentList.Add(workspace.ToString("N"));
            // Last constructor operation: callers own the returned child before Initialize can fail.
            process = Process.Start(info) ?? throw new InvalidOperationException("Writer did not start.");
        }

        internal void Initialize()
        {
            stderr = ReadToEndBounded(process.StandardError, stderrCapture, io.Token);
            start = process.StartTime.ToUniversalTime().ToString("O");
        }

        internal async Task<JsonElement> Command(string command, CancellationToken token)
        {
            receipts.Add(new { direction = "request", command, utc = DateTimeOffset.UtcNow });
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { command }).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            return await Receive(command, token);
        }

        internal async Task<JsonElement> Receive(string command, CancellationToken token)
        {
            using var responseDeadline = CancellationTokenSource.CreateLinkedTokenSource(token, io.Token);
            responseDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            var line = new StringBuilder();
            var character = new char[1];
            var complete = false;
            try
            {
                while (true)
                {
                    if (stderr.IsFaulted) await stderr;
                    if (await process.StandardOutput.ReadAsync(character.AsMemory(), responseDeadline.Token) == 0)
                        throw new EndOfStreamException("Writer stdout ended before " + command);
                    if (++responseCharacters > 16 * 1024 || line.Length > 4096)
                        throw new InvalidDataException("Writer output exceeded the capture bound.");
                    if (character[0] == '\n') { complete = true; break; }
                    line.Append(character[0]);
                }
            }
            finally
            {
                receipts.Add(new { direction = "response", expected = command, raw = line.ToString(), complete, utc = DateTimeOffset.UtcNow });
            }
            using var document = JsonDocument.Parse(line.ToString());
            var result = document.RootElement.Clone();
            Assert.Equal(command, result.GetProperty("command").GetString());
            Assert.Equal(Id, result.GetProperty("pid").GetInt32());
            Assert.Equal(start, result.GetProperty("start").GetString());
            return result;
        }

        internal async Task Finish(CancellationToken token)
        {
            process.StandardInput.Close();
            trailing = ReadToEndBounded(process.StandardOutput, trailingCapture, io.Token);
            await process.WaitForExitAsync(token);
            await trailing.WaitAsync(token);
            Assert.Empty(TrailingText);
            await stderr.WaitAsync(token);
            Assert.Empty(ErrorText);
            Assert.Equal(0, process.ExitCode);
            finished = true;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Receive has returned or thrown before disposal, so no competing stdout reader remains.
                trailing ??= ReadToEndBounded(process.StandardOutput, trailingCapture, io.Token);
                if (!finished)
                {
                    // Kill only the exact Process handle started here, never PID scans or unrelated processes.
                    if (!process.HasExited)
                    {
                        ForcedCleanup = true;
                        process.Kill();
                    }
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                await stderr.WaitAsync(TimeSpan.FromSeconds(5));
                if (trailing is not null) await trailing.WaitAsync(TimeSpan.FromSeconds(5));
                if (!finished) throw new InvalidOperationException("Writer did not complete the normal stop/stdio drain protocol.");
            }
            finally
            {
                io.Cancel();
                process.Dispose();
                // Observe both pumps even when either one faults; prefixes are independent of Task success.
                try { await stderr.WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (Exception error) { receipts.Add(new { stream = "stderr", drainError = error.ToString() }); }
                if (trailing is not null)
                {
                    try { await trailing.WaitAsync(TimeSpan.FromSeconds(1)); }
                    catch (Exception error) { receipts.Add(new { stream = "trailing-stdout", drainError = error.ToString() }); }
                }
                io.Dispose();
            }
        }

        private static async Task ReadToEndBounded(TextReader reader, PrefixCapture output, CancellationToken token)
        {
            var buffer = new char[512];
            int length;
            while ((length = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
            {
                output.Append(buffer, length);
            }
        }

        private sealed class PrefixCapture
        {
            private readonly StringBuilder prefix = new();
            internal string Text { get { lock (prefix) return prefix.ToString(); } }
            internal void Append(char[] buffer, int length)
            {
                lock (prefix)
                {
                    var retained = Math.Min(length, 8192 - prefix.Length);
                    prefix.Append(buffer, 0, retained);
                    if (retained != length) throw new InvalidDataException("Writer stream exceeded 8192 characters; prefix retained.");
                }
            }
        }
    }
}
