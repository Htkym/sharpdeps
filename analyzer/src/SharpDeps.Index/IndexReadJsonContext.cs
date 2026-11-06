namespace SharpDeps.Index;

using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IndexSnapshot))]
internal partial class IndexReadJsonContext : JsonSerializerContext;
