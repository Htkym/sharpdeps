namespace SharpDeps.Cli;

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpDeps.Query;

// Dedicated local IPC. No stdio, shell command, source text, or cursor key crosses this boundary.
internal sealed record CoordinatorOwner(int Protocol, Guid Instance, Guid WorkspaceUuid,
    string Root, string IndexPath, string Endpoint, int ProcessId, string ProcessStartUtc);
internal sealed record CoordinatorRequest(int Protocol, Guid Instance, Guid WorkspaceUuid,
    string RequestId, string Operation, string Root, string IndexPath, bool Trusted, string[] Arguments);
internal sealed record CoordinatorResult(string WorkspaceId, string SnapshotId, long Generation,
    string Coverage, string UpdateMode, int Files, int Nodes, int Edges);
internal sealed record CoordinatorReply(int Protocol, Guid Instance, string RequestId, long Revision,
    long DesiredRevision, string State, CoordinatorResult? Result, string? ErrorCode)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QueryInputInventory? QueryInventory { get; init; }
}

internal static class CoordinatorProtocol
{
    internal const int Version = 1;
    internal const int MaximumFrameBytes = 64 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumFrameBytes) throw new CliFailure("COORDINATOR_FRAME_INVALID", 6);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 2 or > MaximumFrameBytes) throw new CliFailure("COORDINATOR_FRAME_INVALID", 6);
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token);
        return Decode<T>(bytes);
    }

    internal static T Decode<T>(byte[] bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            // JsonDocument validates UTF-8 and the recursive check rejects duplicate keys.
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            Check(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Json)
                ?? throw new CliFailure("COORDINATOR_FRAME_INVALID", 6);
        }
        catch (JsonException) { throw new CliFailure("COORDINATOR_FRAME_INVALID", 6); }
        catch (DecoderFallbackException) { throw new CliFailure("COORDINATOR_FRAME_INVALID", 6); }

        static void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new CliFailure("COORDINATOR_FRAME_INVALID", 6);
                    Check(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Check(item);
        }
    }

    internal static CoordinatorOwner? Discover(string root, string indexPath, Guid uuid)
    {
        var path = MetadataPath(indexPath);
        CliPaths.RejectLinks(path);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is < 2 or > 8192) throw new CliFailure("COORDINATOR_OWNER_INVALID", 6);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        var owner = Decode<CoordinatorOwner>(bytes);
        if (owner.Protocol != Version) throw new CliFailure("COORDINATOR_PROTOCOL_MISMATCH", 6);
        if (owner.Instance == Guid.Empty || owner.WorkspaceUuid != uuid || !CliPaths.SamePath(owner.Root, root) || !CliPaths.SamePath(owner.IndexPath, indexPath)
            || owner.Endpoint != "sharpdeps-" + owner.Instance.ToString("N") || owner.ProcessId < 1
            || !DateTimeOffset.TryParse(owner.ProcessStartUtc, out _))
            throw new CliFailure("COORDINATOR_OWNER_INVALID", 6);
        return owner;
    }

    internal static string MetadataPath(string indexPath) => indexPath + ".owner.json";

    internal static void Publish(CoordinatorOwner owner)
    {
        var path = MetadataPath(owner.IndexPath);
        CliPaths.RejectLinks(path);
        var temporary = path + "." + owner.Instance.ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, owner, Json);
                stream.Flush(true);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            CliPaths.RejectLinks(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task<CoordinatorReply?> TrySendAsync(CoordinatorOwner owner,
        CoordinatorRequest request, CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", owner.Endpoint, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(750, token); }
        catch (TimeoutException) { return null; } // Metadata alone does not prove whether the lease is free.
        try
        {
            await WriteAsync(pipe, request, token);
            var reply = await ReadAsync<CoordinatorReply>(pipe, token);
            if (reply.Protocol != Version || reply.Instance != owner.Instance || reply.RequestId != request.RequestId
                || reply.Revision < 0 || reply.DesiredRevision < reply.Revision
                || reply.State is not ("idle" or "updating" or "dirty" or "degraded" or "stopping")
                || (reply.Result is null && reply.ErrorCode is null && request.Operation != "status"))
                throw new CliFailure("COORDINATOR_REPLY_INVALID", 6);
            return reply;
        }
        catch (IOException) when (!token.IsCancellationRequested)
        { throw new CliFailure("COORDINATOR_RESULT_UNKNOWN", 6); }
    }
}
