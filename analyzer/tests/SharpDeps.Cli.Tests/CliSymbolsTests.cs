namespace SharpDeps.Cli.Tests;

using System.Text.Json;
using SharpDeps.Query;
using Xunit;

public sealed partial class CliTests
{
    [Fact]
    public async Task SymbolCommandsKeepExactSelectionAndCanonicalApiParity()
    {
        using var fixture = new Fixture();
        var found = await Run(["find-symbols", "--root", fixture.Root, "--term", "Service", "--max-milliseconds", "30000"]);
        Assert.Equal(0, found.Exit);
        var envelope = JsonSerializer.Deserialize<SymbolQueryEnvelope>(found.Json, QueryJson.Options)!;
        var item = Assert.Single(envelope.Items, item => item.Name == "Service");
        Assert.All(envelope.Items.SelectMany(item => item.Declarations), declaration => Assert.Null(declaration.Text));
        var request = new SymbolQueryRequest(SymbolQueryOperation.ReadSymbols, Selectors: [item.Selector],
            ProjectId: item.ProjectId, VariantId: item.VariantId, Budget: new QueryBudget(MaxMilliseconds: 30000, MaxChars: 12000, MaxBytes: 48000));
        var direct = await CliApplication.SymbolsAsync(fixture.Root, fixture.IndexPath, true, request);
        var cli = await Run(["read-symbols", "--root", fixture.Root, "--trusted", "--ids", item.Selector,
            "--project-id", item.ProjectId!, "--variant", item.VariantId!, "--max-milliseconds", "30000"]);
        Assert.Equal(0, cli.Exit);
        Assert.Equal(direct.Text, cli.Json.TrimEnd('\r', '\n'));
        var read = JsonSerializer.Deserialize<SymbolQueryEnvelope>(cli.Json, QueryJson.Options)!;
        Assert.Contains(read.Items.SelectMany(item => item.Declarations), declaration => declaration.Text?.Contains("public class Service", StringComparison.Ordinal) == true && declaration.CompleteSavedRange);
        var relations = await Run(["relations", "--root", fixture.Root, "--ids", item.Selector,
            "--project-id", item.ProjectId!, "--variant", item.VariantId!, "--direction", "incoming", "--max-milliseconds", "30000"]);
        Assert.Equal(0, relations.Exit);
        var linked = JsonSerializer.Deserialize<SymbolQueryEnvelope>(relations.Json, QueryJson.Options)!;
        Assert.Empty(Assert.Single(linked.Items).Relations);
    }

    [Theory]
    [InlineData("find-symbols", "--cursor", "untrusted")]
    [InlineData("find-symbols", "--direction", "incoming")]
    [InlineData("find-symbols", "--max-milliseconds", "0")]
    [InlineData("find-symbols", "--max-results", "33")]
    [InlineData("find-symbols", "--prepare-max-milliseconds", "0")]
    public async Task SymbolCommandRejectsInvalidContractBeforeWorkspaceAccess(string command, string option, string value)
    {
        var result = await Run([command, "--root", Path.Combine(Path.GetTempPath(), "missing-typed-symbol-index"), "--term", "Service", option, value]);
        Assert.Equal(2, result.Exit);
        Assert.DoesNotContain("INDEX_MISSING", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SymbolCommandMissingIndexDoesNotEchoLocalPaths()
    {
        using var fixture = new Fixture(seed: false);
        var result = await Run(["find-symbols", "--root", fixture.Root, "--term", "Service"]);
        Assert.Equal(3, result.Exit);
        Assert.Contains("INDEX_MISSING", result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, result.Json, StringComparison.Ordinal);
    }
}
