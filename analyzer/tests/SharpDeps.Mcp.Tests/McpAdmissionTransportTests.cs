namespace SharpDeps.Mcp.Tests;

using System.Collections;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SharpDeps.Index;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Instrumented in-process SDK transport acceptance, not an exact shipped-executable smoke.
/// No adapter field or semaphore is replaced/mutated. A real SQL read lease stalls preparation.
/// No child process is launched; all streams, SDK work and preparation owners belong to this test.
/// </summary>
public sealed class McpAdmissionTransportTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FifthWireCallIsBusyOnlyAfterFourAdapterSlotsAreOccupiedAndThenRecovers()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        ReplayFixture? fixture = null;
        McpQueryAdapter? adapter = null;
        SemaphoreSlim? slots = null;
        NamedPipeServerStream? serverPipe = null;
        NamedPipeClientStream? clientPipe = null;
        BoundedMcpInput? input = null, replies = null;
        StreamReader? reader = null;
        StreamWriter? writer = null;
        StreamServerTransport? transport = null;
        McpServer? server = null;
        Task? run = null, connected = null;
        IndexReader? held = null;
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        var completed = false;
        var adapterDisposed = false;
        var watch = Stopwatch.StartNew();
        try
        {
            fixture = new ReplayFixture();
            adapter = new McpQueryAdapter(new(fixture.Root, Trusted: true));
            slots = Assert.IsType<SemaphoreSlim>(Field(adapter, "slots"));
            var pipeName = "sharpdeps-admission-" + Guid.NewGuid().ToString("N");
            serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            connected = serverPipe.WaitForConnectionAsync(deadline.Token);
            await clientPipe.ConnectAsync(deadline.Token);
            await connected;
            input = new BoundedMcpInput(serverPipe);
            // Both protocol directions are byte-bounded, including malformed response lines.
            replies = new BoundedMcpInput(clientPipe);
            reader = new StreamReader(replies, new UTF8Encoding(false, true), false, 1024, leaveOpen: true);
            writer = new StreamWriter(clientPipe, new UTF8Encoding(false), 1024, leaveOpen: true)
                { AutoFlush = true, NewLine = "\n" };
            var fourEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = 0;
            transport = new StreamServerTransport(input, serverPipe, "sharpdeps-admission-test");
            server = McpServer.Create(transport, new McpServerOptions
            {
                ServerInfo = new() { Name = "sharpdeps-admission-test", Version = "1" },
                ProtocolVersion = "2025-11-25",
                Capabilities = new() { Tools = new() { ListChanged = false } },
                Handlers = new()
                {
                    ListToolsHandler = (_, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return ValueTask.FromResult(new ListToolsResult { Tools = adapter.ListTools() });
                    },
                    CallToolHandler = (request, token) =>
                    {
                        var call = adapter.CallAsync(request.Params, token);
                        // One warmup plus four real calls have entered the adapter's async path.
                        if (Interlocked.Increment(ref entered) == 5) fourEntered.TrySetResult();
                        return call;
                    }
                }
            });
            run = server.RunAsync(stop.Token);
            await Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"admission-fixture","version":"1"}}}""");
            var initialized = await Reply(1);
            Assert.Equal("2025-11-25", initialized.GetProperty("result").GetProperty("protocolVersion").GetString());
            await Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await Call(2);
            var expectedBody = Success(await Reply(2));
            Assert.Contains(ReplayFixture.RunInt, expectedBody, StringComparison.Ordinal);
            Assert.Equal(4, slots.CurrentCount);

            // Warmup has finished, so the sole entry is stable and its SQL session is idle.
            // Reflection observes existing fields and calls the ordinary lease API, never gate.Wait/Release.
            var preparation = Field(adapter, "preparation");
            var entries = Assert.IsAssignableFrom<IDictionary>(Field(preparation, "entries"));
            var entry = Assert.Single(entries.Values.Cast<object>());
            var session = Field(entry, "Session");
            var acquire = session.GetType().GetMethod("Acquire", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("IndexReadSession.Acquire");
            held = Assert.IsType<IndexReader>(acquire.Invoke(session, [deadline.Token]));
            Assert.NotNull(held.Snapshot(deadline.Token));
            Assert.Equal(4, slots.CurrentCount);

            for (var id = 10; id < 14; id++) await Call(id);
            await fourEntered.Task.WaitAsync(Guard, deadline.Token);
            Assert.Equal(0, slots.CurrentCount);
            // The lease is still held, so admitted calls cannot finish before this fifth wire call.
            Receipt("four-admitted", new { availableSlots = slots.CurrentCount, heldSqlLease = true,
                requestIds = new[] { 10, 11, 12, 13 } });
            await Call(14);
            var busy = await Reply(14);
            Assert.False(busy.TryGetProperty("result", out _)); // No source/tool payload on admission rejection.
            var error = busy.GetProperty("error");
            Assert.Equal(-32603, error.GetProperty("code").GetInt32());
            Assert.Equal("MCP_BUSY", error.GetProperty("message").GetString());
            Assert.Equal(0, slots.CurrentCount);
            Assert.NotNull(held.Snapshot(deadline.Token));
            Receipt("fifth-rejected", new { requestId = 14, code = -32603, message = "MCP_BUSY", availableSlots = 0 });

            held.Dispose();
            held = null;
            var pending = new HashSet<int> { 10, 11, 12, 13 };
            for (var count = 0; count < 4; count++)
            {
                var reply = await ReadReply();
                Assert.True(pending.Remove(reply.GetProperty("id").GetInt32()), "Unexpected or duplicate admitted response.");
                Assert.Equal(expectedBody, Success(reply));
            }
            Assert.Empty(pending);
            await Until(() => slots.CurrentCount == 4);
            await Call(15);
            Assert.Equal(expectedBody, Success(await Reply(15)));
            await Until(() => slots.CurrentCount == 4);
            Receipt("recovered", new { requestId = 15, availableSlots = 4, admittedSucceeded = 4 });
            completed = true;
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            // One failed disposal must not skip cancellation or the remaining owned resources.
            Cleanup("sql-lease", () => held?.Dispose());
            Cleanup("stop", stop.Cancel);
            Cleanup("deadline", deadline.Cancel);
            if (connected is not null) await CleanupAsync("pipe-connect", async () =>
            {
                try { await connected; }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            });
            if (run is not null) await CleanupAsync("server-run", async () =>
            {
                try { await run; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            });
            if (server is not null) await CleanupAsync("server", () => server.DisposeAsync().AsTask());
            if (transport is not null) await CleanupAsync("transport", () => transport.DisposeAsync().AsTask());
            if (adapter is not null) await CleanupAsync("adapter", async () =>
            {
                await adapter.DisposeAsync();
                adapterDisposed = true;
            });
            if (writer is not null) await CleanupAsync("writer", () => writer.DisposeAsync().AsTask());
            Cleanup("reader", () => reader?.Dispose());
            Cleanup("reply-input", () => replies?.Dispose());
            Cleanup("server-input", () => input?.Dispose());
            Cleanup("client-pipe", () => clientPipe?.Dispose());
            Cleanup("server-pipe", () => serverPipe?.Dispose());
            if (slots is not null) Cleanup("admission-drain", () => Assert.Equal(4, slots.CurrentCount));
            Cleanup("fixture", () => fixture?.Dispose());
            // These receipts are observations, not a passed verdict. The paired TRX is authoritative.
            Cleanup("receipt", () => Receipt("cleanup", new { serverTaskCompleted = run?.IsCompleted, availableSlots = slots?.CurrentCount,
                adapterDisposed, ownedChildProcesses = 0, acceptanceCompleted = completed,
                bodyFailure = primaryFailure?.GetType().FullName,
                cleanupFailures = cleanupFailures.Select(error => new { resource = error.Message,
                    type = error.InnerException?.GetType().FullName, detail = error.InnerException?.Message }).ToArray() }));
            if (primaryFailure is null && cleanupFailures.Count != 0)
                throw new AggregateException("Admission fixture cleanup failed.", cleanupFailures);
        }

        void Cleanup(string name, Action action)
        {
            try { action(); }
            catch (Exception error) { cleanupFailures.Add(new InvalidOperationException(name, error)); }
        }
        async Task CleanupAsync(string name, Func<Task> action)
        {
            Task? pending = null;
            try
            {
                pending = action();
                await pending.WaitAsync(Guard);
            }
            catch (Exception error)
            {
                cleanupFailures.Add(new InvalidOperationException(name, error));
                // A timeout cannot forcibly terminate SDK disposal; observe any eventual failure.
                if (pending is not null)
                    _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        async Task Send(string json)
        {
            Assert.True(Encoding.UTF8.GetByteCount(json) < BoundedMcpInput.MaximumMessageBytes);
            await writer!.WriteLineAsync(json.AsMemory(), deadline.Token);
        }
        Task Call(int id) => Send(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id, method = "tools/call",
            @params = new { name = "explore", arguments = new { query = fixture!.Member,
                prepareMaxMilliseconds = 30000, maxMilliseconds = 1000, maxChars = 4096, maxBytes = 16384 } }
        }));
        async Task<JsonElement> ReadReply()
        {
            // This fixture enables no server logging/subscriptions, so every incoming line must be a reply.
            using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            readDeadline.CancelAfter(Guard);
            // Await the actual cancelled read, not an abandoned WaitAsync proxy.
            var line = await reader!.ReadLineAsync(readDeadline.Token);
            Assert.NotNull(line);
            using var document = JsonDocument.Parse(line);
            Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
            Assert.True(document.RootElement.TryGetProperty("id", out _));
            return document.RootElement.Clone();
        }
        async Task<JsonElement> Reply(int id)
        {
            var reply = await ReadReply();
            Assert.Equal(id, reply.GetProperty("id").GetInt32());
            return reply;
        }
        async Task Until(Func<bool> condition)
        {
            var started = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(started.Elapsed < Guard, "Admission/drain observation exceeded its hard guard.");
                await Task.Delay(5, deadline.Token);
            }
        }
        void Receipt(string stage, object evidence) => output.WriteLine(JsonSerializer.Serialize(new
        {
            schema = "sharpdeps-mcp-admission-transport/1", stage, elapsedMilliseconds = watch.ElapsedMilliseconds,
            transport = "in-process-named-pipe-sdk-stream", exactShippedExecutable = false, evidence
        }));
    }

    private static object Field(object owner, string name) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(owner)
        ?? throw new MissingFieldException(owner.GetType().FullName, name);

    private static string Success(JsonElement response)
    {
        Assert.False(response.TryGetProperty("error", out _), response.ToString());
        var result = response.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(), response.ToString());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        return content.GetProperty("text").GetString() ?? throw new InvalidDataException("Missing tool text.");
    }
}
