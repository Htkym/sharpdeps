namespace SharpDeps.Cli.Tests;

using System.Text.Json;
using SharpDeps.Cli;
using Xunit;

public sealed class CliMutationEntryTests
{
    [Fact]
    public async Task InputEofCancelsThisCallerBeforeWorkspaceMutation()
    {
        using var input = new MemoryStream();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = await CliMutationEntry.RunAsync(["update", "--root", Path.GetTempPath(), "--trusted"],
            input, output, error);
        Assert.Equal(130, result);
        using var reply = JsonDocument.Parse(output.ToString());
        Assert.Equal("CANCELLED", reply.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task NonMutationCommandCannotUsePrivateEntry()
    {
        using var input = new MemoryStream();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await CliMutationEntry.RunAsync(["serve"], input, output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("VSCODE_MUTATION_ARGUMENT_INVALID", error.ToString());
    }
}
