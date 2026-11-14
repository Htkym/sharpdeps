using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Index;

// Deliberately independent of the replay observer, xUnit, CLI entrypoint, and MCP adapter.
// Inputs: absolute dotnet host, actual CLI DLL, actual MCP DLL, new evidence directory.
if (args.Length != 4) throw new ArgumentException("dotnet-path cli-dll mcp-dll new-evidence-directory");
Check(args.All(Path.IsPathFullyQualified), "All inputs must be absolute paths");
var host = Path.GetFullPath(args[0]);
var cli = Path.GetFullPath(args[1]);
var mcp = Path.GetFullPath(args[2]);
var evidence = Path.GetFullPath(args[3]);
Check(File.Exists(host) && File.Exists(cli) && File.Exists(mcp), "Missing explicit host/product input");
foreach (var path in new[] { host, cli, mcp, evidence }) RejectLinks(path);
foreach (var payload in new[] { Path.GetDirectoryName(cli)!, Path.GetDirectoryName(mcp)!, AppContext.BaseDirectory })
    Check(!Within(evidence, payload) && !Within(payload, evidence), "Evidence and payload/helper directories must be disjoint");
Check(!Directory.Exists(evidence), "Evidence directory must be new; never overwrite receipts");
Directory.CreateDirectory(evidence);
var root = Path.Combine(evidence, "fixture");
var metrics = Path.Combine(root, ".sharpdeps", "transport-metrics");
var utf8 = new UTF8Encoding(false, true);
const string code = "public sealed class Service { public int Value => 7; }\n";
const int prepareMs = 30000, queryMs = 1000, chars = 12000, bytes = 48000;
var sourceHash = Hash(utf8.GetBytes(code));
var workspace = Guid.Parse("98dd3285-ef47-458c-9c7e-50eeebcf122b");
var project = HarnessIdentity.ProjectId(workspace, "App.csproj");
var variant = HarnessIdentity.VariantId(workspace, project, "net10.0", "Debug");
var symbol = HarnessIdentity.LogicalSymbolId(workspace, project, "type", "T:Service");
var occurrence = HarnessIdentity.SymbolOccurrenceId(workspace, symbol, variant);
Process? server = null;
Task<string>? stderr = null;
using var serverReads = new CancellationTokenSource();
Exception? primaryError = null;
var cleanupErrors = new List<string>();
bool passed = false, forced = false;
var ownerIds = new HashSet<string>(StringComparer.Ordinal);
try
{
    // Bind both complete payload trees, including their union of engine DLLs and native assets.
    var payloads = new[] { Path.GetDirectoryName(cli)!, Path.GetDirectoryName(mcp)! }.Distinct().ToArray();
    var payloadBefore = payloads.SelectMany((p, i) => Manifest(p).Select(x => new { payload = i, x.path, x.sha256 })).ToArray();
    Save("product-inputs.json", new { mode = "local-built-dll-integration-only", host, hostSha256 = FileHash(host), cli, mcp,
        payloads, files = payloadBefore, budgetContract = "split-v1", prepareMs, queryMs, chars, bytes });
    Save("helper-inputs.json", new { helper = Environment.ProcessPath, files = Manifest(AppContext.BaseDirectory) });
    var engines = payloads.SelectMany(p => Directory.GetFiles(p, "SharpDeps.*.dll")).Select(Path.GetFileName)
        .Where(n => n != "SharpDeps.Mcp.dll").Distinct().Order(StringComparer.Ordinal).ToArray();
    Check(engines.Contains("SharpDeps.Query.dll") && engines.Contains("SharpDeps.Index.dll"), "Engine union incomplete");
    foreach (var name in engines)
    {
        var hashes = payloads.Select(p => FileHash(Path.Combine(p, name!))).Distinct().ToArray();
        Check(hashes.Length == 1, "Mixed CLI/MCP engine identity: " + name);
        Check(FileHash(Path.Combine(AppContext.BaseDirectory, name!)) == hashes[0], "Helper reference differs from product: " + name);
    }
    Seed();
    var inputBefore = Inputs();
    Save("fixture-inputs.json", new { workspace, project, variant, symbol, occurrence, sourceHash,
        spanStart = 0, spanLength = code.Length - 1, code, files = inputBefore });
    server = Start(mcp, ["--root", root, "--trusted", "--metrics-directory", metrics]);
    Save("mcp-process.json", new { server.Id, startUtc = server.StartTime.ToUniversalTime(), host, mcp });
    stderr = ReadBounded(server.StandardError, serverReads.Token);
    var initialized = await Rpc(1, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "transport-smoke", version = "1" } });
    Check(initialized.GetProperty("result").GetProperty("protocolVersion").GetString() == "2025-11-25", "Protocol");
    Check(initialized.GetProperty("result").GetProperty("instructions").GetString()!.Contains("split-v1", StringComparison.Ordinal), "Budget instructions");
    await Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
    var listed = await Rpc(2, "tools/list", new { });
    var tools = listed.GetProperty("result").GetProperty("tools");
    Check(tools.GetArrayLength() == 1 && tools[0].GetProperty("name").GetString() == "explore", "Read-only tool list");
    var schema = tools[0].GetProperty("inputSchema");
    Check(!schema.GetProperty("additionalProperties").GetBoolean(), "Flat argument contract");
    Check(schema.GetProperty("properties").GetProperty("prepareMaxMilliseconds").GetProperty("minimum").GetInt32() == 10, "Preparation minimum");

    int id = 10;
    foreach (var format in new[] { "agent", "audit" })
    {
        string? first = null;
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var text = await Equivalent(++id, format, occurrence, 0);
            Check(text.Contains(code[..^1], StringComparison.Ordinal), "Expected complete saved source");
            Check(text.Contains(format == "audit" ? "span=0:" + (code.Length - 1) : "Source F1:1-1", StringComparison.Ordinal), "Expected saved span/line range");
            Check(text.Contains(sourceHash, StringComparison.Ordinal) == (format == "audit"), "Format hash visibility");
            if (first is not null) Check(first == text, "Repeated exact body changed");
            first = text;
        }
    }
    var missing = await Equivalent(++id, "audit", "NoSuchSavedSymbol", 2);
    Check(missing.Contains("SYMBOL_NOT_FOUND", StringComparison.Ordinal) && !missing.Contains(code[..^1], StringComparison.Ordinal), "Missing-symbol no-source error");

    // Deterministic pre-admission argument-validation rejection, NOT a timeout or MCP_BUSY test.
    var invalid = Arguments("agent", occurrence);
    invalid["prepareMaxMilliseconds"] = 0;
    var beforeInvalid = MetricNames();
    var rejected = await Rpc(++id, "tools/call", new { name = "explore", arguments = invalid });
    Check(rejected.GetProperty("error").GetProperty("code").GetInt32() == -32602, "Zero budget protocol rejection");
    Check(rejected.GetProperty("error").GetProperty("message").GetString()!.Contains("MCP_ARGUMENT_INVALID", StringComparison.Ordinal), "Invalid argument error code");
    Check(!rejected.GetRawText().Contains(code[..^1], StringComparison.Ordinal), "Rejected call returned source");
    Check(beforeInvalid.SetEquals(MetricNames()), "Rejected call admitted metrics");
    Capture("request-" + id);
    var invalidArguments = CliArguments("agent", occurrence);
    invalidArguments[Array.IndexOf(invalidArguments, "--prepare-max-milliseconds") + 1] = "0";
    var invalidCli = await Cli(id, invalidArguments);
    Check(invalidCli.exit == 2 && !invalidCli.text.Contains(code[..^1], StringComparison.Ordinal), "CLI invalid-budget no-source rejection");
    Check(invalidCli.text.Contains("ARGUMENT_INVALID", StringComparison.Ordinal), "CLI invalid argument error code");
    // CLI argument diagnostics and JSON-RPC protocol errors are intentionally not body-equivalent.

    server.StandardInput.Close();
    await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(server.ExitCode == 0, "MCP nonzero shutdown");
    Check((await stderr.WaitAsync(TimeSpan.FromSeconds(10))).Length == 0, "Unexpected MCP stderr");
    using (var trailingTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        Check((await ReadBounded(server.StandardOutput, trailingTimeout.Token)).Length == 0, "Unexpected trailing stdout");
    Capture("terminal");
    foreach (var owner in ownerIds)
    {
        var files = Directory.GetFiles(Path.Combine(evidence, "terminal", "owners", owner), "*.jsonl");
        Check(files.Length == 1, "Owner terminal receipt missing/ambiguous");
        Terminal(files[0], "explore-prepare", "completed");
    }
    Check(JsonSerializer.Serialize(inputBefore) == JsonSerializer.Serialize(Inputs()), "Fixture DB/WAL/workspace/source changed");
    var payloadAfter = payloads.SelectMany((p, i) => Manifest(p).Select(x => new { payload = i, x.path, x.sha256 })).ToArray();
    Check(JsonSerializer.Serialize(payloadBefore) == JsonSerializer.Serialize(payloadAfter), "Payload changed");
    using (var reader = IndexReader.Open(Path.Combine(root, ".sharpdeps", "index.sqlite"), workspace))
        Check(reader.Snapshot().Graph.Generation == 1, "Generation changed");
    passed = true;
}
catch (Exception error) { primaryError = error; }
finally
{
    // Only exact handles created here are terminated. Never scan/kill other dotnet processes.
    if (server is not null)
    {
        await Cleanup(async () =>
        {
            if (!server.HasExited) { forced = true; server.Kill(entireProcessTree: true); await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        });
        await Cleanup(async () =>
        {
            if (stderr is not null) Write("mcp.stderr.txt", await stderr.WaitAsync(TimeSpan.FromSeconds(10)));
        });
        serverReads.Cancel();
        if (stderr is not null) await Cleanup(async () => { try { await stderr; } catch (OperationCanceledException) { } });
        await Cleanup(() =>
        {
            if (!Directory.Exists(Path.Combine(evidence, "terminal"))) Capture("terminal");
            Save("mcp-exit.json", new { server.Id, exited = server.HasExited, exitCode = server.HasExited ? server.ExitCode : (int?)null, forced });
            return Task.CompletedTask;
        });
        server.Dispose();
    }
    passed = passed && !forced && cleanupErrors.Count == 0 && primaryError is null;
    try
    {
        if (primaryError is not null) Save("failure.json", new { type = primaryError.GetType().FullName, primaryError.Message });
        Save("summary.json", new { passed, forced, cleanupErrors, mode = "local-built-dll-integration-only", ownerIds });
        Save("artifact-manifest.json", Manifest(evidence));
    }
    catch (Exception error) { primaryError ??= error; }
}
if (primaryError is not null) ExceptionDispatchInfo.Capture(primaryError).Throw();
Check(passed, "Cleanup failed; see retained receipts");

async Task Cleanup(Func<Task> action)
{
    try { await action(); }
    catch (Exception error) { cleanupErrors.Add(error.GetType().Name + ": " + error.Message); }
}

async Task<string> Equivalent(int id, string format, string query, int expectedExit)
{
    var before = MetricNames();
    var response = await Rpc(id, "tools/call", new { name = "explore", arguments = Arguments(format, query) });
    Capture("request-" + id); // Immutable request-time snapshot, never replaced by terminal capture.
    var added = MetricNames().Except(before).ToArray();
    Check(added.Length == 1, "Request receipt association ambiguous");
    var receipt = Path.Combine(evidence, "request-" + id, added[0]);
    Terminal(receipt, "explore", expectedExit == 0 ? "completed" : "failed");
    var owners = File.ReadLines(receipt).Select(line => JsonDocument.Parse(line)).ToArray();
    try
    {
        var links = owners.Select(d => d.RootElement.GetProperty("payload"))
            .Where(p => p.TryGetProperty("stage", out var stage) && stage.GetString() == "explore.host-owner")
            .Select(p => p.GetProperty("reason").GetString()!).ToArray();
        Check(links.Length == 1 && links[0].Length == 32 && links[0].All(Uri.IsHexDigit), "Owner association");
        ownerIds.Add(links[0]);
        Save("request-" + id + "-association.json", new { id, run = added[0], owner = links[0] });
    }
    finally { foreach (var document in owners) document.Dispose(); }
    var result = response.GetProperty("result");
    Check(!result.TryGetProperty("structuredContent", out _), "Unexpected structured content");
    Check(result.GetProperty("isError").GetBoolean() == (expectedExit != 0), "MCP error status");
    var content = result.GetProperty("content");
    Check(content.GetArrayLength() == 1 && content[0].GetProperty("type").GetString() == "text", "Text-only result");
    var body = content[0].GetProperty("text").GetString()!;
    Check(result.GetRawText().Length <= chars && utf8.GetByteCount(result.GetRawText()) <= bytes, "Wire budget");
    var actual = await Cli(id, CliArguments(format, query));
    Check(actual.exit == expectedExit, "CLI exit");
    // Console.WriteLine framing only. No Trim/TrimEnd, line normalization, or product-text rewriting.
    Check(actual.text.EndsWith(Environment.NewLine, StringComparison.Ordinal), "CLI framing newline missing");
    Check(actual.text[..^Environment.NewLine.Length] == body, "Exact CLI/MCP body mismatch");
    Write("body-" + id + ".txt", body);
    Save("parity-" + id + ".json", new { id, format, query, expectedExit, bodySha256 = Hash(utf8.GetBytes(body)), cliFraming = Environment.NewLine });
    return body;
}
Dictionary<string, object> Arguments(string format, string query) => new()
{
    ["query"] = query, ["format"] = format, ["projectId"] = project, ["variantId"] = variant,
    ["scopePolicy"] = "unique", ["freshness"] = "allow-stale", ["prepareMaxMilliseconds"] = prepareMs,
    ["maxMilliseconds"] = queryMs, ["maxChars"] = chars, ["maxBytes"] = bytes
};
string[] CliArguments(string format, string query) => ["explore", "--root", root, "--trusted", "--format", format,
    "--project-id", project, "--variant", variant, "--scope-policy", "unique", "--freshness", "allow-stale",
    "--prepare-max-milliseconds", "30000", "--max-milliseconds", "1000", "--max-chars", "12000", "--max-bytes", "48000", "--term", query];
Process Start(string dll, IEnumerable<string> arguments)
{
    var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = utf8, StandardErrorEncoding = utf8, StandardInputEncoding = utf8, WorkingDirectory = root };
    start.ArgumentList.Add(dll);
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new IOException("Process did not start");
}
async Task<(int exit, string text)> Cli(int id, string[] arguments)
{
    Save("cli-" + id + "-arguments.json", arguments);
    using var process = Start(cli, arguments);
    using var reads = new CancellationTokenSource();
    Task<string>? stdout = null, errors = null;
    var killed = false;
    Exception? failure = null;
    (int exit, string text) result = (-1, "");
    try
    {
        stdout = ReadBounded(process.StandardOutput, reads.Token);
        errors = ReadBounded(process.StandardError, reads.Token);
        Save("cli-" + id + "-process.json", new { process.Id, startUtc = process.StartTime.ToUniversalTime(), host, cli });
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        result = (process.ExitCode, await stdout.WaitAsync(TimeSpan.FromSeconds(10)));
        Check((await errors.WaitAsync(TimeSpan.FromSeconds(10))).Length == 0, "Unexpected CLI stderr");
    }
    catch (Exception error) { failure = error; }
    finally
    {
        await Cleanup(async () =>
        {
            if (!process.HasExited) { killed = true; forced = true; process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        });
        await Cleanup(async () => { if (stdout is not null) Write("cli-" + id + ".stdout.txt", await stdout.WaitAsync(TimeSpan.FromSeconds(10))); });
        await Cleanup(async () => { if (errors is not null) Write("cli-" + id + ".stderr.txt", await errors.WaitAsync(TimeSpan.FromSeconds(10))); });
        reads.Cancel();
        foreach (var read in new[] { stdout, errors })
            if (read is not null) await Cleanup(async () => { try { await read; } catch (OperationCanceledException) { } });
        await Cleanup(() =>
        {
            Save("cli-" + id + "-exit.json", new { process.Id, exited = process.HasExited, exitCode = process.HasExited ? process.ExitCode : (int?)null, forced = killed });
            return Task.CompletedTask;
        });
    }
    if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    Check(!killed && cleanupErrors.Count == 0, "CLI cleanup failed");
    return result;
}
async Task Send(object request)
{
    var line = JsonSerializer.Serialize(request);
    File.AppendAllText(Path.Combine(evidence, "mcp.requests.jsonl"), line + "\n", utf8);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await server!.StandardInput.WriteLineAsync(line.AsMemory(), timeout.Token);
    await server.StandardInput.FlushAsync(timeout.Token);
}
async Task<JsonElement> Rpc(int id, string method, object parameters)
{
    await Send(new { jsonrpc = "2.0", id, method, @params = parameters });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var line = await ReadLineBounded(server!.StandardOutput, timeout.Token);
    Check(line is not null, "Unexpected MCP EOF");
    Write("rpc-" + id + ".json", line!);
    using var document = JsonDocument.Parse(line!);
    Check(document.RootElement.GetProperty("jsonrpc").GetString() == "2.0" && document.RootElement.GetProperty("id").GetInt32() == id, "RPC association");
    return document.RootElement.Clone();
}
void Seed()
{
    Directory.CreateDirectory(Path.Combine(root, ".sharpdeps"));
    File.WriteAllText(Path.Combine(root, "App.cs"), code, utf8);
    File.WriteAllText(Path.Combine(root, ".sharpdeps", "workspace.json"), JsonSerializer.Serialize(new
        { schemaVersion = 1, workspaceUuid = workspace, cursorKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()) }), utf8);
    var location = new HarnessLocation("App.cs", null, sourceHash, new(0, code.Length - 1));
    var ws = HarnessIdentity.WorkspaceId(workspace);
    var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
        HarnessGraphContract.IdentityVersion, ws, "transport-smoke", 1, HarnessCoverage.Partial,
        [new(variant, project, "net10.0", "Debug", null, null, null)],
        [new(ws, HarnessNodeKind.Workspace, "Workspace", null, null), new(project, HarnessNodeKind.Project, "App", ws, null),
         new(symbol, HarnessNodeKind.Type, "Service", project, null)],
        [new(occurrence, symbol, variant, location, [location])], [], [], null);
    using var writer = IndexWriter.Open(root, Path.Combine(root, ".sharpdeps", "index.sqlite"), workspace, isTrusted: true);
    writer.Commit(IndexStage.Create(new IndexSnapshot(graph,
        [new("App.cs", "App.cs", "source", sourceHash, utf8.GetByteCount(code), code.Length)], [], [],
        [new(symbol, "Service", "Service", "", "", "App.cs")])));
}
HashSet<string> MetricNames() => Directory.Exists(metrics)
    ? Directory.GetFiles(metrics, "*.jsonl").Select(Path.GetFileName).Select(n => n!).ToHashSet(StringComparer.Ordinal) : [];
void Capture(string name)
{
    var destination = Path.Combine(evidence, name);
    Check(!Directory.Exists(destination), "Receipt destination exists");
    Directory.CreateDirectory(destination);
    if (!Directory.Exists(metrics)) return;
    long total = 0;
    foreach (var file in SafeFiles(metrics).Where(p => p.EndsWith(".jsonl", StringComparison.Ordinal)))
    {
        Check((total += new FileInfo(file).Length) <= 16 * 1024 * 1024, "Metrics capture size limit");
        var target = Path.Combine(destination, Path.GetRelativePath(metrics, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        var buffer = new byte[8192];
        int read;
        long copied = 0;
        while ((read = input.Read(buffer)) > 0)
        {
            Check((copied += read) <= 1024 * 1024, "Metrics file size limit");
            output.Write(buffer, 0, read);
        }
    }
}
void Terminal(string path, string operation, string outcome)
{
    var content = File.ReadAllBytes(path);
    Check(content.Length > 0 && content[^1] == '\n', "Truncated terminal receipt");
    var lines = File.ReadAllLines(path);
    for (int i = 0; i < lines.Length; i++)
    {
        using var record = JsonDocument.Parse(lines[i]);
        Check(record.RootElement.GetProperty("sequence").GetInt32() == i + 1 && record.RootElement.GetProperty("runId").GetString() == Path.GetFileNameWithoutExtension(path), "Receipt sequence/run identity");
        var payload = record.RootElement.GetProperty("payload");
        if (i == 0) Check(payload.GetProperty("type").GetString() == "run.start" && payload.GetProperty("operation").GetString() == operation, "Receipt operation");
        if (i == lines.Length - 1) Check(payload.GetProperty("type").GetString() == "run.end" && payload.GetProperty("outcome").GetString() == outcome, "Receipt terminal outcome mismatch");
    }
}
object[] Inputs() => new[] { "App.cs", ".sharpdeps/workspace.json", ".sharpdeps/index.sqlite", ".sharpdeps/index.sqlite-wal" }
    .Select(p => (object)new { path = p, sha256 = File.Exists(Path.Combine(root, p)) ? FileHash(Path.Combine(root, p)) : null }).ToArray();
FileIdentity[] Manifest(string directory) => SafeFiles(directory)
    .OrderBy(p => p, StringComparer.Ordinal).Select(p => new FileIdentity(Path.GetRelativePath(directory, p), FileHash(p))).ToArray();
void Save(string name, object value) => Write(name, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
void Write(string name, string value)
{
    using var stream = new FileStream(Path.Combine(evidence, name), FileMode.CreateNew, FileAccess.Write);
    stream.Write(utf8.GetBytes(value));
}
static string FileHash(string path)
{
    RejectLinks(path);
    Check(new FileInfo(path).Length <= 1024L * 1024 * 1024, "Hash input exceeds 1 GiB");
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}
static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
static bool Within(string path, string root) => Path.GetFullPath(path).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase)
    || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
static void RejectLinks(string path)
{
    for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        if (File.Exists(current) || Directory.Exists(current))
            Check((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Linked input/output rejected: " + current);
}
static List<string> SafeFiles(string root)
{
    RejectLinks(root);
    var result = new List<string>();
    var pending = new Stack<(string path, int depth)>();
    pending.Push((root, 0));
    int entries = 0;
    while (pending.TryPop(out var directory))
        foreach (var path in Directory.EnumerateFileSystemEntries(directory.path))
        {
            Check(++entries <= 10000 && directory.depth < 32, "File traversal limit");
            var attributes = File.GetAttributes(path);
            Check((attributes & FileAttributes.ReparsePoint) == 0, "Linked payload/receipt entry");
            if ((attributes & FileAttributes.Directory) != 0) pending.Push((path, directory.depth + 1));
            else result.Add(path);
        }
    return result;
}
static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
{
    var text = new StringBuilder();
    var buffer = new char[4096];
    int read;
    while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
    {
        Check(text.Length + read <= 1024 * 1024, "Process output exceeds 1 MiB characters");
        text.Append(buffer, 0, read);
    }
    return text.ToString();
}
static async Task<string?> ReadLineBounded(StreamReader reader, CancellationToken token)
{
    var text = new StringBuilder();
    var character = new char[1];
    while (await reader.ReadAsync(character.AsMemory(), token) != 0)
    {
        if (character[0] == '\n') return text.ToString();
        Check(text.Length < 128 * 1024, "RPC line exceeds 128 KiB characters");
        text.Append(character[0]);
    }
    return text.Length == 0 ? null : throw new InvalidDataException("Truncated RPC line");
}
record FileIdentity(string path, string sha256);
