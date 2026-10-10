namespace SharpDeps.Index;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Serialized outside the write transaction so caller mutations cannot change staged facts.</summary>
public sealed class IndexStage
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string serialized;
    private IndexStage(string serialized, string manifestHash) { this.serialized = serialized; ManifestHash = manifestHash; }
    public string ManifestHash { get; }
    public static IndexStage Create(IndexSnapshot snapshot)
    {
        IndexSnapshotValidator.Validate(snapshot);
        var serialized = JsonSerializer.Serialize(snapshot, Json);
        var frozen = JsonSerializer.Deserialize<IndexSnapshot>(serialized, Json)!;
        IndexSnapshotValidator.Validate(frozen);
        return new IndexStage(serialized, ComputeManifestHash(frozen.Files));
    }
    internal IndexSnapshot Materialize() => JsonSerializer.Deserialize<IndexSnapshot>(serialized, Json)!;
    internal static string ComputeManifestHash(IReadOnlyList<IndexFile> files)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            files.OrderBy(f => f.SourceId, StringComparer.Ordinal).ToArray(), Json)))).ToLowerInvariant();
}
