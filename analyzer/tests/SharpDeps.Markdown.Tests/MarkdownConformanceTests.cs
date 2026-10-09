namespace SharpDeps.Markdown.Tests;

using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Markdown;
using Xunit;

public sealed class MarkdownConformanceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ActualRuntimeFactsMatchTheLithoRuntimeAndSourceConsumerGolden(int index)
    {
        using var fixtureFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "markdown-consumer-conformance-v1.json")));
        var fixture = fixtureFile.RootElement.GetProperty("fixtures")[index];
        var raw = fixture.GetProperty("rawText").GetString()!;
        var request = new MarkdownGraphRequest(raw, Guid.Parse("11111111-2222-4333-8444-555555555555"),
            Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"), "pair-scope", "same.md", null, "snapshot-1", 1);
        var projection = new LithoSharpMarkdownAdapter().Analyze(request, true);
        Assert.Equal(fixture.GetProperty("utf16Length").GetInt32(), raw.Length);
        Assert.Equal(MarkdownRuntimePin.ParserVersion, projection.Facts.ParserVersion);
        var golden = (Dictionary<string, object?>)Normalize(projection.Facts)!;
        // The old immutable oracle includes its stamp; only this root field changes with a rename.
        golden[nameof(projection.Facts.ParserVersion)] = fixtureFile.RootElement.GetProperty("parserVersion").GetString();
        var json = JsonSerializer.Serialize(golden);
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        Assert.Equal(fixture.GetProperty("lithoRuntimeSourceFactsSha256").GetString(), actual);
        Assert.Equal(MarkdownRuntimePin.ParserVersion, projection.Graph.Markdown!.ParserVersion);
        using var pinFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "markdown-runtime-pin.json")));
        Assert.Equal(MarkdownRuntimePin.CanonicalSourceHash, pinFile.RootElement.GetProperty("canonicalSourceHash").GetString());
        Assert.Equal(MarkdownRuntimePin.RuntimeAssemblySha256,
            pinFile.RootElement.GetProperty("artifacts").GetProperty("runtime").GetProperty("assemblySha256").GetString());
    }

    // The same normalized property selection/order as the accepted MD-05 artifact comparison,
    // including every nested span, source segment, diagnostic and coverage fact.
    private static object? Normalize(object? value)
    {
        if (value is null || value is string || value.GetType().IsPrimitive || value is decimal) return value;
        if (value.GetType().IsEnum) return value.ToString();
        if (value is IEnumerable sequence) return sequence.Cast<object?>().Select(Normalize).ToArray();
        var properties = value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.GetMethod is not null && p.GetIndexParameters().Length == 0
                && p.Name is not ("RawText" or "ContractVersion" or "ProfileId"))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(properties);
        return properties.ToDictionary(p => p.Name, p => Normalize(p.GetValue(value)), StringComparer.Ordinal);
    }
}
