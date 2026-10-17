namespace SharpDeps.Query;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed class QueryCursor
{
    private sealed record State(string Workspace, string Snapshot, long Generation, string Filter, int Offset);
    private static readonly byte[] ProcessKey = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] key;
    public QueryCursor(byte[]? key)
    {
        if (key is not null && key.Length != 32) throw new ArgumentException("Cursor keys must contain 32 bytes.");
        this.key = (key ?? ProcessKey).ToArray();
    }
    public string Encode(QuerySnapshot snapshot, string filter, int offset)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new State(snapshot.WorkspaceId, SnapshotKey(snapshot.Id),
            snapshot.Generation, filter, offset));
        return Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(HMACSHA256.HashData(key, bytes));
    }
    public (int Offset, string? Error) Decode(string cursor, QuerySnapshot snapshot, string filter)
    {
        try
        {
            if (cursor.Length > 4096) return (0, "INVALID_CURSOR");
            var parts = cursor.Split('.');
            if (parts.Length != 2) return (0, "INVALID_CURSOR");
            var bytes = Convert.FromBase64String(parts[0]);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(parts[1]), HMACSHA256.HashData(key, bytes)))
                return (0, "INVALID_CURSOR");
            var state = JsonSerializer.Deserialize<State>(bytes);
            if (state is null || state.Offset < 0) return (0, "INVALID_CURSOR");
            if (state.Workspace != snapshot.WorkspaceId || state.Snapshot != SnapshotKey(snapshot.Id) || state.Generation != snapshot.Generation)
                return (0, "CURSOR_SNAPSHOT_EXPIRED");
            return state.Filter != filter ? (0, "CURSOR_FILTER_CHANGED") : (state.Offset, null);
        }
        catch (Exception error) when (error is FormatException or JsonException) { return (0, "INVALID_CURSOR"); }
    }
    private static string SnapshotKey(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    public static string Fingerprint(QueryRequest request) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request with { RequestId = "", Cursor = null }, QueryJson.Options))));
}
