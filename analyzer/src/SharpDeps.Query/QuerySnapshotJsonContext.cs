namespace SharpDeps.Query;

using System.Text.Json.Serialization;
using SharpDeps.Index;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(IndexSnapshot))]
internal partial class QuerySnapshotJsonContext : JsonSerializerContext;
