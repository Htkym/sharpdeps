namespace SharpDeps.Cli.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Index;
using SharpDeps.Query;
using Xunit;

public sealed class CliTests
{
    [Fact]
    public async Task TrustAndArgumentFailuresHappenBeforeAnyWorkspaceCreation()
    {
        using var fixture = new Fixture(seed: false);
        var untrusted = await Run("index", "--root", fixture.Root, "--target", "absent.csproj");
        Assert.Equal(5, untrusted.Exit);
        Assert.Equal("WORKSPACE_UNTRUSTED", Code(untrusted.Json));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".sharpdeps")));
        var missingTerm = await Run("search", "--root", fixture.Root);
        Assert.Equal(2, missingTerm.Exit);
        var unsupported = await Run("query", "--kind", "status", "--root", fixture.Root, "--dependencies");
        Assert.Equal(2, unsupported.Exit);
        var statusScope = await Run("status", "--root", fixture.Root, "--project-id", "ignored-project");
        Assert.Equal(2, statusScope.Exit);
        Assert.Equal("ARGUMENT_UNSUPPORTED", Code(statusScope.Json));
        var network = await Run("status", "--root", "//invalid.invalid/sharpdeps");
        Assert.Equal(5, network.Exit);
        Assert.Equal("ROOT_INVALID", Code(network.Json));
        var duplicate = await Run("status", "--root", fixture.Root, "--root", fixture.Root);
        Assert.Equal(2, duplicate.Exit);
        Assert.Equal("ARGUMENT_DUPLICATE", Code(duplicate.Json));
        var badBudget = await Run("search", "--root", fixture.Root, "--term", "Service", "--max-nodes", "0");
        Assert.Equal(2, badBudget.Exit);
        var outside = await Run("status", "--root", fixture.Root, "--index", "../outside.sqlite");
        Assert.Equal(5, outside.Exit);
        Assert.Equal("PATH_OUTSIDE_ROOT", Code(outside.Json));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".sharpdeps")));
    }

    [Fact]
    public async Task SeparateInvocationsShareCursorKeyAndReadSavedWithoutSourceFiles()
    {
        using var fixture = new Fixture();
        var before = SHA256.HashData(File.ReadAllBytes(fixture.IndexPath));
        File.Delete(Path.Combine(fixture.Root, "Service.cs"));
        var first = await Run("search", "--root", fixture.Root, "--term", "Service", "--page-size", "1");
        Assert.Equal(0, first.Exit);
        Assert.Empty(first.Progress);
        var envelope = JsonSerializer.Deserialize<QueryEnvelope>(first.Json, QueryJson.Options)!;
        var cursor = Assert.IsType<string>(envelope.NextCursor);
        var second = await Run("search", "--root", fixture.Root, "--term", "Service", "--page-size", "1", "--cursor", cursor);
        Assert.Equal(0, second.Exit);
        var next = JsonSerializer.Deserialize<QueryEnvelope>(second.Json, QueryJson.Options)!;
        Assert.NotEqual(Assert.Single(envelope.Items).Id, Assert.Single(next.Items).Id);
        var changed = await Run("search", "--root", fixture.Root, "--term", "Other", "--page-size", "1", "--cursor", cursor);
        Assert.Equal(6, changed.Exit);
        Assert.Equal("CURSOR_FILTER_CHANGED", Code(changed.Json));
        var context = await Run("context", "--root", fixture.Root, "--id", fixture.Symbol("Service"));
        Assert.Equal(0, context.Exit);
        Assert.All(JsonSerializer.Deserialize<QueryEnvelope>(context.Json, QueryJson.Options)!.Items, item => Assert.Null(item.Snippet));
        var trusted = await Run("status", "--root", fixture.Root, "--trusted", "--freshness", "require-fresh");
        Assert.Equal(3, trusted.Exit);
        Assert.Equal("FRESHNESS_REQUIREMENT_NOT_MET", Code(trusted.Json));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.IndexPath)));
    }

    [Fact]
    public async Task CancellationAndUnsupportedStateProduceOneJsonWithoutWritingOrLeakingPaths()
    {
        using var fixture = new Fixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var output = new StringWriter();
        var errors = new StringWriter();
        Assert.Equal(130, await CliApplication.RunAsync(["status", "--root", fixture.Root], output, errors, cancelled.Token));
        Assert.Equal("CANCELLED", Code(output.ToString()));
        Assert.Empty(errors.ToString());
        File.WriteAllText(Path.Combine(fixture.Root, ".sharpdeps/workspace.json"), "{\"schemaVersion\":99}");
        var invalid = await Run("status", "--root", fixture.Root);
        Assert.Equal(6, invalid.Exit);
        Assert.Equal("WORKSPACE_STATE_INVALID", Code(invalid.Json));
        Assert.DoesNotContain(fixture.Root, invalid.Json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(invalid.Json);
        Assert.Single(document.RootElement.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task HelpAndDoctorAreMachineReadableAndDoNotProbeTargetSdk()
    {
        var help = await Run("help");
        Assert.Equal(0, help.Exit);
        Assert.Empty(help.Progress);
        using var helpDoc = JsonDocument.Parse(help.Json);
        Assert.Contains("index", helpDoc.RootElement.GetProperty("result").GetProperty("commands").EnumerateArray().Select(x => x.GetString()));
        var doctor = await Run("doctor");
        Assert.Equal(0, doctor.Exit);
        using var info = JsonDocument.Parse(doctor.Json);
        Assert.Equal("not-run", info.RootElement.GetProperty("result").GetProperty("sdkProbe").GetString());
    }

    private static string Code(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("errors")[0].GetProperty("code").GetString()!;
    }
    private static async Task<(int Exit, string Json, string Progress)> Run(params string[] arguments)
    {
        var output = new StringWriter(); var progress = new StringWriter();
        var exit = await CliApplication.RunAsync(arguments, output, progress);
        return (exit, output.ToString(), progress.ToString());
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Base = OperatingSystem.IsWindows()
            ? @"D:\DevData\AgentOps\work-cli-20261007\runs\sharpdeps-sd207-20261010-01\test-fixtures"
            : OperatingSystem.IsMacOS() ? "/private/tmp/sharpdeps-sd207-fixtures" : Path.Combine(Path.GetTempPath(), "sharpdeps-sd207-fixtures");
        private const string Text = "public class Service {} public class ServiceExtra {}\n";
        private readonly Guid uuid = Guid.NewGuid();
        public string Root { get; } = Path.Combine(Base, Guid.NewGuid().ToString("N"));
        public string IndexPath => Path.Combine(Root, ".sharpdeps/index.sqlite");
        private string Project => HarnessIdentity.ProjectId(uuid, "App.csproj");
        private string Variant => HarnessIdentity.VariantId(uuid, Project, "net10.0", "Debug");
        public string Symbol(string name) => HarnessIdentity.LogicalSymbolId(uuid, Project, "type", "T:" + name);
        public Fixture(bool seed = true)
        {
            Directory.CreateDirectory(Root);
            if (!seed) return;
            Directory.CreateDirectory(Path.Combine(Root, ".sharpdeps"));
            File.WriteAllText(Path.Combine(Root, ".sharpdeps/workspace.json"), JsonSerializer.Serialize(new {
                schemaVersion = 1, workspaceUuid = uuid, cursorKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
            var bytes = new UTF8Encoding(false).GetBytes(Text);
            File.WriteAllBytes(Path.Combine(Root, "Service.cs"), bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var location = new HarnessLocation("src", "v1", hash, new(0, Text.Length));
            var nodes = new[] { new HarnessNode(Symbol("Service"), HarnessNodeKind.Type, "Service", Project, location),
                new HarnessNode(Symbol("ServiceExtra"), HarnessNodeKind.Type, "ServiceExtra", Project, location) };
            var workspace = HarnessIdentity.WorkspaceId(uuid);
            var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, 1, HarnessGraphContract.IdentityVersion, workspace, "cli-fixture", 1,
                HarnessCoverage.Partial, [new(Variant, Project, "net10.0", "Debug", null, null, null)],
                [new(workspace, HarnessNodeKind.Workspace, "Fixture", null, null), new(Project, HarnessNodeKind.Project, "App", workspace, null), .. nodes],
                nodes.Select(n => new HarnessSymbolOccurrence(HarnessIdentity.SymbolOccurrenceId(uuid, n.Id, Variant), n.Id, Variant, location, [location])).ToArray(),
                [], [], null);
            var snapshot = new IndexSnapshot(graph, [new("src", "Service.cs", "code", hash, bytes.Length, Text.Length)], [], [],
                nodes.Select(n => new IndexSearchText(n.Id, n.Name, "", "", "", "Service.cs")).ToArray());
            using var writer = IndexWriter.Open(Root, IndexPath, uuid, true);
            writer.Commit(IndexStage.Create(snapshot));
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Base) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped run root.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
