namespace SharpDeps.QueryHost;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Index;
using SharpDeps.Query;

internal static class Program
{
    private const int MaximumRequestUtf16 = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions RequestJson = MakeRequestJson();

    public static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelled = (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancelled;
        // Stdio is the only wire. Never log source paths, raw requests, stack traces or state keys.
        using var input = new StreamReader(Console.OpenStandardInput(), StrictUtf8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: false);
        var output = new StreamWriter(Console.OpenStandardOutput(), StrictUtf8) { AutoFlush = true };
        var ready = false;
        string ErrorRequestId() => ready ? "host" : "ready";
        try { return await Run(args, input, output, stop.Token, () => ready = true); }
        catch (OperationCanceledException) { await Error(output, ErrorRequestId(), "CANCELLED"); return 130; }
        catch (HostFailure error) { await Error(output, ErrorRequestId(), error.Code); return error.ExitCode; }
        catch (IndexStoreException error) { await Error(output, ErrorRequestId(), error.Code); return 6; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { await Error(output, ErrorRequestId(), "HOST_ACCESS_DENIED"); return 5; }
        catch (Exception) { await Error(output, ErrorRequestId(), "INDEX_CORRUPT_OR_HOST_FAILURE"); return 6; }
        finally
        {
            Console.CancelKeyPress -= cancelled;
            try { output.Dispose(); }
            catch (IOException) { } // A disconnected client must not produce an unhandled stack trace.
        }
    }

    private static async Task<int> Run(string[] args, StreamReader input, TextWriter output, CancellationToken token, Action readySent)
    {
        string? rootValue = null; string? indexValue = null;
        if (args.Length is not (2 or 4)) throw new HostFailure("HOST_ARGUMENT_INVALID", 2);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (args[i + 1].Length is 0 or > 4096 || args[i + 1].Any(char.IsControl))
                throw new HostFailure("HOST_ARGUMENT_INVALID", 2);
            switch (args[i])
            {
                case "--root" when rootValue is null: rootValue = args[i + 1]; break;
                case "--index" when indexValue is null: indexValue = args[i + 1]; break;
                default: throw new HostFailure("HOST_ARGUMENT_INVALID", 2);
            }
        }
        if (rootValue is null || !Path.IsPathFullyQualified(rootValue) || Unc(rootValue))
            throw new HostFailure("HOST_ROOT_INVALID", 5);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootValue));
        if (Unc(root)) throw new HostFailure("HOST_ROOT_INVALID", 5);
        RejectLinks(root);
        if (!Directory.Exists(root)) throw new HostFailure("HOST_ROOT_INVALID", 5);
        var indexPath = InRoot(root, indexValue ?? ".sharpdeps/index.sqlite");
        foreach (var path in new[] { indexPath, indexPath + "-wal", indexPath + "-shm" }) RejectLinks(path);
        if (!File.Exists(indexPath)) throw new HostFailure("INDEX_NOT_FOUND", 3);
        var state = await ReadSavedState(InRoot(root, ".sharpdeps/workspace.json"), token);
        token.ThrowIfCancellationRequested();
        using var reader = IndexReader.Open(indexPath, state.WorkspaceUuid);
        var query = new QueryService(new PinnedQueryIndex(reader), cursorKey: Convert.FromBase64String(state.CursorKey));
        await output.WriteLineAsync(query.Execute(new("ready", QueryKind.Status), token).Json);
        readySent();
        await foreach (var line in ReadRequests(input, token))
        {
            token.ThrowIfCancellationRequested();
            var requestId = "host";
            try
            {
                using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
                RejectDuplicateProperties(json.RootElement);
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String)
                    requestId = SafeRequestId(id.GetString());
                ValidateEnumFields(json.RootElement);
                var request = JsonSerializer.Deserialize<QueryRequest>(line, RequestJson)
                    ?? throw new JsonException();
                var reply = query.Execute(request, token);
                await output.WriteLineAsync(reply.Json);
                if (token.IsCancellationRequested) return 130; // Execute already returned its cancellation envelope.
            }
            catch (Exception error) when (error is JsonException or ArgumentException or OverflowException)
            { await Error(output, requestId, "HOST_REQUEST_INVALID"); }
        }
        return 0;
    }

    private sealed record SavedState(int SchemaVersion, Guid WorkspaceUuid, string CursorKey);
    private static async Task<SavedState> ReadSavedState(string path, CancellationToken token)
    {
        RejectLinks(path);
        if (!File.Exists(path)) throw new HostFailure("WORKSPACE_STATE_MISSING", 3);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        if (input.Length is < 1 or > 4096) throw new HostFailure("WORKSPACE_STATE_INVALID", 6);
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, token);
        if (await input.ReadAsync(new byte[1], token) != 0) throw new HostFailure("WORKSPACE_STATE_INVALID", 6);
        try
        {
            var text = StrictUtf8.GetString(bytes);
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(json.RootElement);
            var saved = JsonSerializer.Deserialize<SavedState>(text, RequestJson);
            if (saved is null || saved.SchemaVersion != 1 || saved.WorkspaceUuid == Guid.Empty
                || saved.CursorKey is null || Convert.FromBase64String(saved.CursorKey).Length != 32)
                throw new JsonException();
            return saved;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or FormatException)
        { throw new HostFailure("WORKSPACE_STATE_INVALID", 6); }
    }

    private static async IAsyncEnumerable<string> ReadRequests(StreamReader input,
        [EnumeratorCancellation] CancellationToken token)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        while (true)
        {
            int read;
            try { read = await input.ReadAsync(buffer.AsMemory(), token); }
            catch (DecoderFallbackException) { throw new HostFailure("HOST_INPUT_ENCODING_INVALID", 2); }
            if (read == 0)
            {
                if (line.Length != 0) yield return Complete(line);
                yield break;
            }
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n') continue;
                Append(buffer, start, i - start);
                yield return Complete(line);
                line.Clear(); start = i + 1;
            }
            Append(buffer, start, read - start);
        }
        void Append(char[] chars, int start, int length)
        {
            if (line.Length + length > MaximumRequestUtf16) throw new HostFailure("HOST_REQUEST_TOO_LARGE", 2);
            line.Append(chars, start, length);
        }
        static string Complete(StringBuilder builder) => builder.Length > 0 && builder[^1] == '\r'
            ? builder.ToString(0, builder.Length - 1) : builder.ToString();
    }

    private static JsonSerializerOptions MakeRequestJson()
    {
        var options = new JsonSerializerOptions(QueryJson.Options) { MaxDepth = 16,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static void ValidateEnumFields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind)
            || !NamedEnum<QueryKind>(kind)) throw new JsonException();
        if (root.TryGetProperty("freshness", out var freshness) && !NamedEnum<QueryFreshnessPolicy>(freshness)) throw new JsonException();
        if (root.TryGetProperty("scope", out var scope) && scope.ValueKind != JsonValueKind.Null)
        {
            if (scope.ValueKind != JsonValueKind.Object) throw new JsonException();
            if (scope.TryGetProperty("kind", out var nodeKind) && nodeKind.ValueKind != JsonValueKind.Null
                && !NamedEnum<HarnessNodeKind>(nodeKind))
                throw new JsonException();
        }
    }

    private static bool NamedEnum<T>(JsonElement value) where T : struct, Enum
        => value.ValueKind == JsonValueKind.String && value.GetString() is { } name
            && Enum.GetNames<T>().Contains(name, StringComparer.OrdinalIgnoreCase);

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static string InRoot(string root, string value)
    {
        if (Unc(value)) throw new HostFailure("HOST_PATH_INVALID", 5);
        var full = Path.GetFullPath(value, root);
        if (Unc(full)) throw new HostFailure("HOST_PATH_INVALID", 5);
        var relative = Path.GetRelativePath(root, full);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative)) throw new HostFailure("HOST_PATH_OUTSIDE_ROOT", 5);
        RejectLinks(full);
        return full;
    }

    private static bool Unc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new HostFailure("HOST_PATH_LINK_FORBIDDEN", 5);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string SafeRequestId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 64 && !id.Any(char.IsControl) ? id : "host";
    private static async Task Error(TextWriter output, string requestId, string code)
    {
        try
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new { apiVersion = "1", requestId,
                errors = new[] { new QueryIssue(code, "Saved query host rejected the operation.") } }, QueryJson.Options));
        }
        catch (IOException) { } // The peer may already have closed stdout.
    }
    private sealed class HostFailure(string code, int exitCode) : Exception(code)
    {
        public string Code { get; } = code;
        public int ExitCode { get; } = exitCode;
    }
}
