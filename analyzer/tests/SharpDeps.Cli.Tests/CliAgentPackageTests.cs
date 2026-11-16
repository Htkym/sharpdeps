namespace SharpDeps.Cli.Tests;

using System.Text;
using System.Text.Json.Nodes;
using SharpDeps.Cli;
using Xunit;

public sealed class CliAgentPackageTests
{
    [Theory]
    [InlineData("portable", "plugin.json")]
    [InlineData("claude-code", ".claude-plugin/plugin.json")]
    [InlineData("opencode-v2", "opencode.config.json")]
    public void RoutingOnlyPreviewAndExportAreInert(string client, string manifest)
    {
        using var f = new Fixture();
        var preview = CliAgentPackage.Run(f.Options(client, "--dry-run"));
        Assert.Equal("preview", preview.Status);
        Assert.False(Directory.Exists(f.Output));
        Assert.Contains(manifest, preview.Files.Keys);
        if (client == "opencode-v2")
            Assert.DoesNotContain(preview.Files.Keys, path => path.StartsWith("skills/", StringComparison.Ordinal));
        else Assert.Contains("skills/sharpdeps-routing/SKILL.md", preview.Files.Keys);
        Assert.DoesNotContain(preview.Files.Keys, path => path is "mcp.json" or ".mcp.json");
        Assert.Contains("before reconstructing", preview.Files["routing.md"]);
        Assert.Contains("user opt-out", preview.Files["routing.md"]);
        Assert.Contains("A known file does not remove the need", preview.Files["routing.md"]);
        Assert.Contains("advertised tool schema supports responseMode=evidence", preview.Files["routing.md"]);
        Assert.DoesNotContain("the file/location is known", preview.Files["routing.md"]);
        Assert.DoesNotContain("AGENTS.md", preview.Files.Keys);
        var exported = CliAgentPackage.Run(f.Options(client));
        Assert.Equal("exported", exported.Status);
        Assert.Equal("unchanged", CliAgentPackage.Run(f.Options(client)).Status);
        Assert.All(preview.Files, pair => Assert.Equal(pair.Value, File.ReadAllText(Path.Combine(f.Output, pair.Key))));
        Assert.Equal("user instructions", File.ReadAllText(f.Instructions));
        if (client == "portable")
        {
            var json = JsonNode.Parse(preview.Files[manifest])!;
            Assert.Equal("https://agent-plugins.org/schemas/1.0.0/plugin.schema.json", (string?)json["$schema"]);
            Assert.Equal("sharpdeps-routing", (string?)json["name"]);
        }
        if (client == "opencode-v2")
        {
            var json = JsonNode.Parse(preview.Files[manifest])!;
            Assert.Null(json["mcp"]);
            Assert.Null(json["skills"]);
            Assert.False(Directory.Exists(Path.Combine(f.Output, "skills")));
            Assert.Equal(Path.Combine(f.Output, "routing.md"), (string?)json["instructions"]![0]);
            Assert.Single(json["instructions"]!.AsArray());
        }
    }

    [Theory]
    [InlineData("portable", false)]
    [InlineData("portable", true)]
    [InlineData("claude-code", false)]
    [InlineData("claude-code", true)]
    [InlineData("opencode-v2", false)]
    [InlineData("opencode-v2", true)]
    public void McpBundlingAndSourceTrustAreSeparateExplicitChoices(string client, bool trusted)
    {
        using var f = new Fixture();
        var command = client == "portable" ? "sharpdeps-mcp" : f.Command;
        var extras = new List<string> { "--mcp-command", command, "--index", "saved.sqlite", "--dry-run" };
        if (trusted) extras.Add("--trusted");
        var result = CliAgentPackage.Run(f.Options(client, extras.ToArray()));
        var file = client switch { "portable" => "mcp.json", "claude-code" => ".mcp.json", _ => "opencode.config.json" };
        var json = JsonNode.Parse(result.Files[file])!;
        JsonArray args;
        if (client == "opencode-v2")
        {
            var server = json["mcp"]!["servers"]!["sharpdeps"]!;
            Assert.False((bool)server["disabled"]!);
            Assert.False((bool)server["codemode"]!);
            Assert.Null(server["enabled"]);
            args = server["command"]!.AsArray();
            Assert.Equal(f.Command, (string?)args[0]);
        }
        else
        {
            var server = json["mcpServers"]!["sharpdeps"]!;
            Assert.Equal("stdio", (string?)server["type"]);
            Assert.Equal(command, (string?)server["command"]);
            args = server["args"]!.AsArray();
            if (client == "portable") Assert.Equal("https://agent-plugins.org/schemas/1.0.0/mcp.schema.json", (string?)json["$schema"]);
        }
        var values = args.Select(node => (string?)node).ToArray();
        Assert.Contains(f.Root, values);
        Assert.Contains(Path.Combine(f.Root, "saved.sqlite"), values);
        Assert.Equal(trusted, values.Contains("--trusted"));
        Assert.DoesNotContain("--allow-mutations", values);
    }

    [Fact]
    public void RemoveVerifiesEntirePackageAndPreservesBackup()
    {
        using var f = new Fixture();
        var exported = CliAgentPackage.Run(f.Options("portable"));
        var preview = CliAgentPackage.Run(f.Remove("--dry-run"));
        Assert.Equal("preview", preview.Status);
        Assert.True(Directory.Exists(f.Output));
        var removed = CliAgentPackage.Run(f.Remove());
        Assert.Equal("removed", removed.Status);
        Assert.False(Directory.Exists(f.Output));
        var backup = Assert.Single(removed.RetainedPaths);
        Assert.All(exported.Files, item => Assert.Equal(item.Value, File.ReadAllText(Path.Combine(backup, item.Key))));
        Assert.Equal("unchanged", CliAgentPackage.Run(f.Remove()).Status);
        Assert.Equal("user instructions", File.ReadAllText(f.Instructions));
    }

    [Theory]
    [InlineData("modified")]
    [InlineData("extra-file")]
    [InlineData("extra-directory")]
    [InlineData("missing")]
    [InlineData("unknown-receipt")]
    public void ModifiedOrForeignPackageIsNeverOverwrittenOrRemoved(string change)
    {
        using var f = new Fixture();
        CliAgentPackage.Run(f.Options("portable"));
        if (change == "modified") File.AppendAllText(Path.Combine(f.Output, "routing.md"), "user edit");
        if (change == "extra-file") File.WriteAllText(Path.Combine(f.Output, "foreign.txt"), "foreign");
        if (change == "extra-directory") Directory.CreateDirectory(Path.Combine(f.Output, "foreign"));
        if (change == "missing") File.Move(Path.Combine(f.Output, "routing.md"), Path.Combine(f.Root, "saved-routing.md"));
        if (change == "unknown-receipt")
        {
            var p = Path.Combine(f.Output, "sharpdeps-package.json");
            File.WriteAllText(p, File.ReadAllText(p).Replace("sharpdeps-agent-package/v1", "sharpdeps-agent-package/v99"));
        }
        Assert.Throws<CliFailure>(() => CliAgentPackage.Run(f.Options("portable")));
        Assert.Throws<CliFailure>(() => CliAgentPackage.Run(f.Remove()));
        Assert.True(Directory.Exists(f.Output));
    }

    [Fact]
    public void InvalidFlagsAndDestinationsAreRejectedWithoutWriting()
    {
        using var f = new Fixture();
        Assert.Throws<CliFailure>(() => CliAgentPackage.Run(f.Options("unknown")));
        Assert.Throws<CliFailure>(() => CliAgentPackage.Run(f.Options("portable", "--trusted")));
        Assert.Throws<CliFailure>(() => CliAgentPackage.Run(f.Options("portable", "--index", "saved.sqlite")));
        Assert.Throws<CliFailure>(() => CliAgentPackage.Run(f.Options("portable", "--mcp-command", "sharpdeps-mcp", "--index", "../outside.sqlite")));
        Assert.Throws<CliFailure>(() => CliAgentPackage.Run(CliArguments.Parse(["agent-export", "--client", "portable", "--root", f.Root, "--output-dir", f.Root])));
        Assert.Throws<CliFailure>(() => CliAgentPackage.Run(CliArguments.Parse(["agent-export", "--client", "portable", "--root", f.Root, "--output-dir", Path.GetDirectoryName(f.Root)!])));
        Assert.Throws<CliFailure>(() => CliArguments.Parse(["agent-export", "--scope", "global"]));
        Assert.False(Directory.Exists(f.Output));
    }

    [Fact]
    public void CancellationBeforePublicationRetainsStageAndDoesNotPublish()
    {
        using var f = new Fixture();
        using var cancel = new CancellationTokenSource();
        var result = CliAgentPackage.Run(f.Options("portable"), cancel.Token, (stage, _) =>
        {
            if (stage == "before-publish") cancel.Cancel();
        });
        Assert.Equal("CANCELLED", result.ErrorCode);
        Assert.False(Directory.Exists(f.Output));
        Assert.True(Directory.Exists(Assert.Single(result.RetainedPaths)));
    }

    [Fact]
    public void ConcurrentDestinationWinsWithoutOverwrite()
    {
        using var f = new Fixture();
        var result = CliAgentPackage.Run(f.Options("portable"), checkpoint: (stage, _) =>
        {
            if (stage != "before-publish") return;
            Directory.CreateDirectory(f.Output);
            File.WriteAllText(Path.Combine(f.Output, "foreign.txt"), "concurrent writer");
        });
        Assert.Equal("AGENT_PACKAGE_IO_FAILED", result.ErrorCode);
        Assert.Equal("concurrent writer", File.ReadAllText(Path.Combine(f.Output, "foreign.txt")));
        Assert.Single(result.RetainedPaths);
        Assert.False(File.Exists(Path.Combine(f.Output, "plugin.json")));
    }

    [Fact]
    public async Task PublicCliHelpAndPreviewDoNotRequireAnIndex()
    {
        using var f = new Fixture();
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await CliApplication.RunAsync(["agent-export", "--client", "portable", "--root", f.Root, "--output-dir", f.Output, "--dry-run"], output, error);
        Assert.Equal(0, exit);
        Assert.Contains("preview", output.ToString());
        Assert.False(Directory.Exists(f.Output));
    }

    [Fact]
    public void CancelledRemovalKeepsOriginalPackage()
    {
        using var f = new Fixture();
        var original = CliAgentPackage.Run(f.Options("portable"));
        using var cancel = new CancellationTokenSource();
        var result = CliAgentPackage.Run(f.Remove(), cancel.Token, (stage, _) =>
        {
            if (stage == "before-remove") cancel.Cancel();
        });
        Assert.Equal("CANCELLED", result.ErrorCode);
        Assert.Empty(result.RetainedPaths);
        Assert.All(original.Files, pair => Assert.Equal(pair.Value, File.ReadAllText(Path.Combine(f.Output, pair.Key))));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"registration\":null}")]
    [InlineData("not-json")]
    public void MalformedReceiptCannotAuthorizeRemoval(string receipt)
    {
        using var f = new Fixture();
        CliAgentPackage.Run(f.Options("portable"));
        File.WriteAllText(Path.Combine(f.Output, "sharpdeps-package.json"), receipt);
        Assert.Throws<CliFailure>(() => CliAgentPackage.Run(f.Remove()));
        Assert.True(File.Exists(Path.Combine(f.Output, "plugin.json")));
    }

    [Fact]
    public void RelocatedPackageCannotBeRemovedUsingOldReceipt()
    {
        using var f = new Fixture();
        CliAgentPackage.Run(f.Options("portable"));
        var moved = Path.Combine(f.Root, "moved");
        Directory.Move(f.Output, moved);
        var options = CliArguments.Parse(["agent-remove", "--root", f.Root, "--output-dir", moved]);
        Assert.Throws<CliFailure>(() => CliAgentPackage.Run(options));
        Assert.True(File.Exists(Path.Combine(moved, "plugin.json")));
    }
    [Fact]
    public void LinkedOutputAndGeneratedEntriesAreRefused()
    {
        using var f = new Fixture();
        var target = Path.Combine(f.Root, "target"); Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "sentinel.txt"); File.WriteAllText(sentinel, "untouched");
        Directory.CreateSymbolicLink(f.Output, target);
        Assert.Throws<CliFailure>(() => CliAgentPackage.Run(f.Options("portable")));
        Directory.Delete(f.Output); // Remove the test link, not its target.
        CliAgentPackage.Run(f.Options("portable"));
        var routing = Path.Combine(f.Output, "routing.md"); File.Delete(routing);
        File.CreateSymbolicLink(routing, sentinel);
        Assert.Throws<CliFailure>(() => CliAgentPackage.Run(f.Remove()));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
        File.Delete(routing); // Do not leave a link for fixture cleanup.
    }
    [Theory]
    [InlineData("C:\\tools\\sharpdeps-mcp.exe")]
    [InlineData("/usr/bin/sharpdeps-mcp")]
    [InlineData("./sharpdeps-mcp")]
    [InlineData("../sharpdeps-mcp")]
    [InlineData("sharpdeps-mcp --trusted")]
    [InlineData("sharpdeps-mcp;other")]
    public void PortableRejectsPathsAndShellStringsWithoutBasenameConversion(string command)
    {
        using var f = new Fixture();
        var error = Assert.Throws<CliFailure>(() => CliAgentPackage.Run(f.Options("portable", "--mcp-command", command)));
        Assert.Equal("AGENT_PACKAGE_PORTABLE_COMMAND_REQUIRED", error.Code);
        Assert.False(Directory.Exists(f.Output));
    }

    [Fact]
    public void PortableBareCommandOwnershipRoundTripsWithoutResolvingPath()
    {
        using var f = new Fixture();
        var result = CliAgentPackage.Run(f.Options("portable", "--mcp-command", "explicit-uninstalled-tool.exe"));
        Assert.Equal("exported", result.Status);
        Assert.Contains("host PATH", result.Files["README.md"]);
        Assert.Equal("explicit-uninstalled-tool.exe", (string?)JsonNode.Parse(result.Files["mcp.json"])!["mcpServers"]!["sharpdeps"]!["command"]);
        Assert.Equal("unchanged", CliAgentPackage.Run(f.Options("portable", "--mcp-command", "explicit-uninstalled-tool.exe")).Status);
        Assert.Equal("removed", CliAgentPackage.Run(f.Remove()).Status);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sharpdeps-agent-package-tests-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Root, "package");
        public string Command => Path.Combine(Root, "mcp command.bin");
        public string Instructions => Path.Combine(Root, "AGENTS.md");
        public Fixture() { Directory.CreateDirectory(Root); File.WriteAllText(Command, "not executed"); File.WriteAllText(Instructions, "user instructions"); }
        public CliArguments Options(string client, params string[] extra) => CliArguments.Parse(new[] { "agent-export", "--client", client, "--root", Root, "--output-dir", Output }.Concat(extra).ToArray());
        public CliArguments Remove(params string[] extra) => CliArguments.Parse(new[] { "agent-remove", "--root", Root, "--output-dir", Output }.Concat(extra).ToArray());
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
