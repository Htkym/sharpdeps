namespace SharpDeps.Index;

using System.Text.Json;
using System.Text.Json.Serialization;
using SharpDeps.Analysis.Contracts.Harness;

// The SQL projection empties legacy graph arrays; reading their object metadata again is unnecessary.
internal sealed record IndexGraphHeader(string Format, int SchemaVersion, string IdentityVersion,
    string WorkspaceId, string SnapshotId, long Generation, HarnessCoverage Coverage,
    IndexEmptyGraphArray Variants, IndexEmptyGraphArray Nodes, IndexEmptyGraphArray SymbolOccurrences, IndexEmptyGraphArray Edges,
    IndexEmptyGraphArray LegacyReferences, HarnessMarkdownIdentity? Markdown, IReadOnlyList<HarnessGraphDiagnostic>? Diagnostics = null)
{
    internal static HarnessGraphEnvelope Read(string json)
    {
        try
        {
            var header = JsonSerializer.Deserialize(json, IndexHeaderJsonContext.Default.IndexGraphHeader)
                ?? throw new JsonException("A harness graph object is required.");
            var graph = new HarnessGraphEnvelope(header.Format, header.SchemaVersion, header.IdentityVersion,
                header.WorkspaceId, header.SnapshotId, header.Generation, header.Coverage,
                [], [], [], [], [], header.Markdown, header.Diagnostics);
            HarnessGraphContract.ValidateHeader(graph);
            return graph;
        }
        // Nonempty/duplicate legacy fields retain the original typed-deserialization checks.
        catch (IndexFullGraphHeaderException) { return HarnessGraphContract.Read(json); }
        catch (JsonException) { return HarnessGraphContract.Read(json); }
        catch (ArgumentException error) { throw new JsonException("Invalid harness graph input.", error); }
        catch (OverflowException error) { throw new JsonException("Invalid harness graph range.", error); }
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(IndexGraphHeader))]
internal partial class IndexHeaderJsonContext : JsonSerializerContext;

[JsonConverter(typeof(IndexEmptyGraphArrayConverter))]
internal readonly struct IndexEmptyGraphArray { }

internal sealed class IndexFullGraphHeaderException : Exception;

internal sealed class IndexEmptyGraphArrayConverter : JsonConverter<IndexEmptyGraphArray>
{
    public override IndexEmptyGraphArray Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray || !reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            throw new IndexFullGraphHeaderException();
        return default;
    }

    public override void Write(Utf8JsonWriter writer, IndexEmptyGraphArray value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteEndArray();
    }
}
