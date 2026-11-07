namespace SharpDeps.Index;

using SharpDeps.Analysis.Core.Diagnostics;

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
        using var metrics = PhaseMetrics.Measure("stage.create");
        using (PhaseMetrics.Measure("stage.validate-input"))
            IndexSnapshotValidator.Validate(snapshot);
        string serialized;
        using (PhaseMetrics.Measure("stage.serialize"))
            serialized = JsonSerializer.Serialize(snapshot, Json);
        if (PhaseMetrics.Enabled)
            PhaseMetrics.Count("serialized.utf8-bytes", Encoding.UTF8.GetByteCount(serialized));
        IndexSnapshot frozen;
        using (PhaseMetrics.Measure("stage.deserialize"))
            frozen = JsonSerializer.Deserialize<IndexSnapshot>(serialized, Json)!;
        using (PhaseMetrics.Measure("stage.validate-frozen"))
            IndexSnapshotValidator.Validate(frozen);
        return new IndexStage(serialized, ComputeManifestHash(frozen.Files));
    }
    internal IndexSnapshot Materialize() => JsonSerializer.Deserialize<IndexSnapshot>(serialized, Json)!;
    // Reuse is optional extraction metadata. Hash only the schema-1 input fields so older readers
    // that ignore the additive JSON property can still verify a newly published input manifest.
    internal static string ComputeManifestHash(IReadOnlyList<IndexFile> files)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            files.OrderBy(f => f.SourceId, StringComparer.Ordinal).Select(f => f with { Reuse = null }).ToArray(), Json)))).ToLowerInvariant();
}
