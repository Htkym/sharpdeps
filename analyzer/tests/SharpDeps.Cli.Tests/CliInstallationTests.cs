namespace SharpDeps.Cli.Tests;

using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

public sealed class CliInstallationTests(ITestOutputHelper output)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    [Fact]
    public async Task CreateNewInstallationIsDryRunSafeIdempotentAndRetainsUninstalledFiles()
    {
        var fixture = CreateFixture();
        var originalFiles = fixture.Files();
        var preview = await Run(fixture.Arguments("install", "--dry-run", "--request-id", "scratch-install"));
        Assert.Equal(0, preview.Exit);
        Assert.Empty(preview.Progress);
        using (var document = JsonDocument.Parse(preview.Json))
        {
            Assert.Equal("install", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("scratch-install", document.RootElement.GetProperty("requestId").GetString());
            Assert.Empty(document.RootElement.GetProperty("errors").EnumerateArray());
            var result = document.RootElement.GetProperty("result");
            Assert.True(result.GetProperty("dryRun").GetBoolean());
            AssertRegistration(result.GetProperty("configuration").GetString()!, fixture, trusted: false);
        }
        Assert.Equal(originalFiles, fixture.Files());
        var trustedPreview = CliAgentInstallation.Run(fixture.Options("install", "--dry-run", "--trusted"));
        AssertRegistration(trustedPreview.Configuration!, fixture, trusted: true);
        Assert.Equal(originalFiles, fixture.Files());

        var installed = CliAgentInstallation.Run(fixture.Options());
        Assert.Equal("installed", installed.Status);
        Assert.Null(installed.ErrorCode);
        Assert.Empty(installed.RetainedPaths);
        var config = File.ReadAllBytes(fixture.ConfigFile);
        var instructions = File.ReadAllBytes(fixture.InstructionsFile);
        AssertRegistration(Utf8.GetString(config), fixture, trusted: false);
        Assert.NotEmpty(instructions);
        var installedFiles = fixture.Files();
        var repeated = CliAgentInstallation.Run(fixture.Options());
        Assert.Equal("unchanged", repeated.Status);
        Assert.Equal(config, File.ReadAllBytes(fixture.ConfigFile));
        Assert.Equal(instructions, File.ReadAllBytes(fixture.InstructionsFile));
        Assert.Equal(installedFiles, fixture.Files());
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".sharpdeps")));

        File.Delete(fixture.Command);
        Directory.Delete(Path.GetDirectoryName(fixture.Command)!);
        var removed = CliAgentInstallation.Run(fixture.Options("uninstall"));
        Assert.Equal("uninstalled", removed.Status);
        Assert.Null(removed.ErrorCode);
        Assert.False(File.Exists(fixture.ConfigFile));
        Assert.False(File.Exists(fixture.InstructionsFile));
        Assert.Equal(2, removed.RetainedPaths.Length);
        AssertRetainedWithinFixture(removed, fixture);
        Assert.Contains(removed.RetainedPaths, path => File.ReadAllBytes(path).SequenceEqual(config));
        Assert.Contains(removed.RetainedPaths, path => File.ReadAllBytes(path).SequenceEqual(instructions));
        var retainedFiles = fixture.Files();
        Assert.Equal("unchanged", CliAgentInstallation.Run(fixture.Options("uninstall")).Status);
        Assert.Equal(retainedFiles, fixture.Files());
    }

    [Fact]
    public async Task ForeignModifiedAndIncompletePairsAreNeverAdopted()
    {
        const string secret = "foreign-config-secret-must-not-be-printed";
        var foreignConfigurations = new[]
        {
            Utf8.GetBytes("{\"mcpServers\":{\"sharpdeps\":{}},\"env\":{\"TOKEN\":\"" + secret + "\"}}"),
            Utf8.GetBytes("{"),
            Utf8.GetBytes("""{"mcpServers":{},"\u006dcpServers":{}}"""),
            Utf8.GetBytes("""{"mcpServers":{"other":{"env":{"TOKEN":"one","TOKEN":"two"}}}}"""),
            new byte[] { 0xc3, 0x28 }
        };
        foreach (var bytes in foreignConfigurations)
        {
            var fixture = CreateFixture();
            File.WriteAllBytes(fixture.ConfigFile, bytes);
            File.WriteAllText(fixture.InstructionsFile, "\uFEFFforeign instructions 検証\r\n" + secret, Utf8);
            AssertConflictPreservesFiles(fixture, fixture.Options());
            AssertConflictPreservesFiles(fixture, fixture.Options("uninstall"));
            var reply = await Run(fixture.Arguments("install", "--dry-run"));
            Assert.NotEqual(0, reply.Exit);
            Assert.DoesNotContain(secret, reply.Json + reply.Progress);
        }

        {
            var fixture = CreateFixture();
            var preview = CliAgentInstallation.Run(fixture.Options("install", "--dry-run"));
            File.WriteAllText(fixture.ConfigFile, preview.Configuration, Utf8);
            AssertConflictPreservesFiles(fixture, fixture.Options());
            Assert.False(File.Exists(fixture.InstructionsFile));
        }
        foreach (var change in new Action<InstallationFixture>[]
        {
            item => File.AppendAllText(item.ConfigFile, "\n", Utf8),
            item => File.AppendAllText(item.InstructionsFile, File.ReadAllText(item.InstructionsFile, Utf8), Utf8),
            item => File.WriteAllText(item.InstructionsFile, "<!-- modified marker -->", Utf8),
            item => File.Delete(item.ConfigFile),
            item => File.Delete(item.InstructionsFile)
        })
        {
            var fixture = CreateFixture();
            CliAgentInstallation.Run(fixture.Options());
            change(fixture);
            AssertConflictPreservesFiles(fixture, fixture.Options());
            AssertConflictPreservesFiles(fixture, fixture.Options("uninstall"));
        }
        {
            var fixture = CreateFixture();
            CliAgentInstallation.Run(fixture.Options());
            var otherRoot = Path.Combine(fixture.Base, "different-workspace");
            Directory.CreateDirectory(otherRoot);
            AssertConflictPreservesFiles(fixture, CliArguments.Parse(Replace(fixture.Arguments(), "--root", otherRoot)));
            AssertConflictPreservesFiles(fixture, CliArguments.Parse(Replace(fixture.Arguments("uninstall"), "--root", otherRoot)));
        }
    }

    [Fact]
    public async Task InstallerGrammarAndDestinationValidationPrecedeWorkspaceAccess()
    {
        var fixture = CreateFixture();
        foreach (var arguments in new[]
        {
            Replace(fixture.Arguments(), "--client", "unsupported"),
            fixture.Arguments("uninstall", "--mcp-command", fixture.Command),
            fixture.Arguments("uninstall", "--index", "different.sqlite"),
            fixture.Arguments("uninstall", "--trusted")
        })
        {
            var reply = await Run(arguments);
            Assert.Equal(2, reply.Exit);
            using var document = JsonDocument.Parse(reply.Json);
            Assert.Single(document.RootElement.GetProperty("errors").EnumerateArray());
        }
        foreach (var arguments in new[]
        {
            Replace(fixture.Arguments(), "--instructions-file", fixture.ConfigFile),
            Replace(fixture.Arguments(), "--config-file", Path.GetDirectoryName(fixture.ConfigFile)!),
            Replace(fixture.Arguments(), "--config-file", Path.Combine(fixture.Base, "missing-parent", "config.json")),
            Replace(fixture.Arguments(), "--mcp-command", "relative-command")
        }) Assert.Throws<CliFailure>(() => CliAgentInstallation.Run(CliArguments.Parse(arguments)));
        if (OperatingSystem.IsWindows())
            foreach (var path in new[] { fixture.ConfigFile + ":stream", Path.Combine(Path.GetDirectoryName(fixture.ConfigFile)!, "CON.json") })
                Assert.Throws<CliFailure>(() => CliAgentInstallation.Run(CliArguments.Parse(Replace(fixture.Arguments(), "--config-file", path))));
        var outsideIndex = Assert.Throws<CliFailure>(() => CliAgentInstallation.Run(fixture.Options("install", "--index", "../outside.sqlite")));
        Assert.Equal("PATH_OUTSIDE_ROOT", outsideIndex.Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.Base, "missing-parent")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".sharpdeps")));
        Assert.False(File.Exists(fixture.ConfigFile));
        Assert.False(File.Exists(fixture.InstructionsFile));
        var protectedFixture = CreateFixture();
        var stateDirectory = Path.Combine(protectedFixture.Root, ".sharpdeps");
        Directory.CreateDirectory(stateDirectory);
        foreach (var path in new[] { protectedFixture.Command, Path.Combine(stateDirectory, "workspace.json"), Path.Combine(stateDirectory, "index.sqlite-wal") })
            Assert.Throws<CliFailure>(() => CliAgentInstallation.Run(CliArguments.Parse(Replace(protectedFixture.Arguments(), "--config-file", path))));
        Assert.Equal("scratch command must never execute", File.ReadAllText(protectedFixture.Command, Utf8));
        Assert.Empty(Directory.GetFiles(stateDirectory));
        Assert.False(File.Exists(protectedFixture.InstructionsFile));
    }

    [Fact]
    public void LinkedDestinationsAndAncestorsLeaveTheirTargetsUntouched()
    {
        var fixture = CreateFixture();
        var target = Path.Combine(fixture.Base, "linked-target.json");
        var sentinel = Utf8.GetBytes("foreign linked target");
        File.WriteAllBytes(target, sentinel);
        var leaf = Path.Combine(fixture.Base, "linked-config.json");
        File.CreateSymbolicLink(leaf, target);
        var leafFailure = Assert.Throws<CliFailure>(() => CliAgentInstallation.Run(CliArguments.Parse(
            Replace(fixture.Arguments(), "--config-file", leaf))));
        Assert.Equal("PATH_LINK_FORBIDDEN", leafFailure.Code);
        Assert.Equal(sentinel, File.ReadAllBytes(target));
        var ancestor = Path.Combine(fixture.Base, "linked-parent");
        Directory.CreateSymbolicLink(ancestor, Path.GetDirectoryName(fixture.ConfigFile)!);
        var ancestorFailure = Assert.Throws<CliFailure>(() => CliAgentInstallation.Run(CliArguments.Parse(
            Replace(fixture.Arguments(), "--config-file", Path.Combine(ancestor, "config.json")))));
        Assert.Equal("PATH_LINK_FORBIDDEN", ancestorFailure.Code);
        Assert.False(File.Exists(fixture.ConfigFile));
        Assert.False(File.Exists(fixture.InstructionsFile));
        Assert.Equal(sentinel, File.ReadAllBytes(target));
    }

    [Fact]
    public void PublicationRacesRetainForeignTargetsAndEditedRollbackFiles()
    {
        {
            var fixture = CreateFixture();
            var foreign = Utf8.GetBytes("instructions created after preflight");
            var result = CliAgentInstallation.Run(fixture.Options(), checkpoint: (stage, path) =>
            {
                if (stage == "before-instructions-publish") File.WriteAllBytes(path, foreign);
            });
            Assert.Equal("partial", result.Status);
            Assert.True(result.ErrorCode is "INSTALLATION_CONFLICT" or "INSTALLATION_IO_FAILED");
            Assert.Equal(foreign, File.ReadAllBytes(fixture.InstructionsFile));
            Assert.False(File.Exists(fixture.ConfigFile));
            AssertRetainedWithinFixture(result, fixture);
        }
        foreach (var oversized in new[] { false, true })
        {
            var fixture = CreateFixture();
            var foreignConfig = Utf8.GetBytes("config created after preflight");
            var recreated = Utf8.GetBytes("instructions recreated before recovery");
            byte[]? edited = null;
            var result = CliAgentInstallation.Run(fixture.Options(), checkpoint: (stage, path) =>
            {
                if (stage == "before-config-publish")
                {
                    edited = oversized ? Enumerable.Repeat((byte)'e', 129 * 1024).ToArray()
                        : [.. File.ReadAllBytes(fixture.InstructionsFile), .. Utf8.GetBytes("editor change")];
                    File.WriteAllBytes(fixture.InstructionsFile, edited);
                    File.WriteAllBytes(path, foreignConfig);
                }
                if (stage == "before-restore") File.WriteAllBytes(path, recreated);
            });
            Assert.Equal("partial", result.Status);
            Assert.True(result.ErrorCode is "INSTALLATION_CONFLICT" or "INSTALLATION_IO_FAILED");
            Assert.Equal(foreignConfig, File.ReadAllBytes(fixture.ConfigFile));
            Assert.Equal(recreated, File.ReadAllBytes(fixture.InstructionsFile));
            Assert.NotNull(edited);
            var backup = Assert.Single(result.RetainedPaths.Where(path => path.EndsWith(".bak", StringComparison.Ordinal)));
            Assert.Equal(edited, File.ReadAllBytes(backup));
            AssertRetainedWithinFixture(result, fixture);
        }
    }

    [Fact]
    public void UninstallRestoresEditedDetachedBytesOrRetainsBothOnRecreation()
    {
        foreach (var recreate in new[] { false, true })
        {
            var fixture = CreateFixture();
            CliAgentInstallation.Run(fixture.Options());
            var instructions = File.ReadAllBytes(fixture.InstructionsFile);
            var edited = Utf8.GetBytes("editor bytes carried by detached file");
            var recreated = Utf8.GetBytes("a new file owns the original path");
            string? detached = null;
            var result = CliAgentInstallation.Run(fixture.Options("uninstall"), checkpoint: (stage, path) =>
            {
                if (stage == "after-config-detach")
                {
                    detached = path;
                    File.WriteAllBytes(path, edited);
                }
                if (stage == "before-restore" && recreate) File.WriteAllBytes(path, recreated);
            });
            Assert.Equal("partial", result.Status);
            Assert.Equal("INSTALLATION_CONFLICT", result.ErrorCode);
            Assert.Equal(instructions, File.ReadAllBytes(fixture.InstructionsFile));
            Assert.NotNull(detached);
            if (recreate)
            {
                Assert.Equal(recreated, File.ReadAllBytes(fixture.ConfigFile));
                Assert.Contains(detached, result.RetainedPaths);
                Assert.Equal(edited, File.ReadAllBytes(detached));
            }
            else
            {
                Assert.Equal(edited, File.ReadAllBytes(fixture.ConfigFile));
                Assert.False(File.Exists(detached));
                Assert.Empty(result.RetainedPaths);
            }
            AssertRetainedWithinFixture(result, fixture);
        }
    }

    [Fact]
    public void CancellationAfterFirstPublicationRecoversWithoutDeletingItsBytes()
    {
        var fixture = CreateFixture();
        using var cancellation = new CancellationTokenSource();
        byte[]? published = null;
        var result = CliAgentInstallation.Run(fixture.Options(), cancellation.Token, (stage, _) =>
        {
            if (stage != "before-config-publish") return;
            published = File.ReadAllBytes(fixture.InstructionsFile);
            cancellation.Cancel();
        });
        Assert.Equal("partial", result.Status);
        Assert.Equal("CANCELLED", result.ErrorCode);
        Assert.Equal("config-publish", result.FailedStage);
        Assert.False(File.Exists(fixture.ConfigFile));
        Assert.False(File.Exists(fixture.InstructionsFile));
        Assert.NotNull(published);
        Assert.NotEmpty(result.RetainedPaths);
        Assert.Contains(result.RetainedPaths, path => File.ReadAllBytes(path).SequenceEqual(published));
        AssertRetainedWithinFixture(result, fixture);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".sharpdeps")));
    }

    private static void AssertRegistration(string configuration, InstallationFixture fixture, bool trusted)
    {
        using var document = JsonDocument.Parse(configuration);
        var registration = Assert.Single(document.RootElement.GetProperty("mcpServers").EnumerateObject());
        Assert.Equal("sharpdeps", registration.Name);
        var server = registration.Value;
        Assert.Equal("stdio", server.GetProperty("type").GetString());
        Assert.Equal(fixture.Command, server.GetProperty("command").GetString());
        Assert.Equal(new[] { "explore" }, server.GetProperty("tools").EnumerateArray().Select(item => item.GetString()));
        var arguments = server.GetProperty("args").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(new[] { "--root", fixture.Root, "--index", Path.Combine(fixture.Root, ".sharpdeps", "index.sqlite") }
            .Concat(trusted ? ["--trusted"] : Array.Empty<string>()), arguments);
        Assert.DoesNotContain("--allow-mutations", arguments);
    }

    private static void AssertConflictPreservesFiles(InstallationFixture fixture, CliArguments options)
    {
        static byte[]? Read(string path)
        {
            try { return File.ReadAllBytes(path); }
            catch (FileNotFoundException) { return null; }
        }
        var config = Read(fixture.ConfigFile);
        var instructions = Read(fixture.InstructionsFile);
        var files = fixture.Files();
        Assert.Equal("INSTALLATION_CONFLICT", Assert.Throws<CliFailure>(() => CliAgentInstallation.Run(options)).Code);
        Assert.Equal(config, Read(fixture.ConfigFile));
        Assert.Equal(instructions, Read(fixture.InstructionsFile));
        Assert.Equal(files, fixture.Files());
    }

    private static void AssertRetainedWithinFixture(CliInstallationResult result, InstallationFixture fixture)
    {
        Assert.All(result.RetainedPaths, path =>
        {
            Assert.StartsWith(fixture.Base + Path.DirectorySeparatorChar, Path.GetFullPath(path),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            Assert.True(File.Exists(path));
        });
    }

    private static string[] Replace(string[] arguments, string name, string value)
    {
        var result = arguments.ToArray();
        result[Array.IndexOf(result, name) + 1] = value;
        return result;
    }

    private static async Task<(int Exit, string Json, string Progress)> Run(string[] arguments)
    {
        var output = new StringWriter();
        var progress = new StringWriter();
        var exit = await CliApplication.RunAsync(arguments, output, progress);
        return (exit, output.ToString(), progress.ToString());
    }

    // The gate archives each emitted root before any guarded cleanup, including failed assertions.
    private InstallationFixture CreateFixture() => new(output);

    private sealed class InstallationFixture
    {
        private static readonly string Parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sharpdeps-installation-tests"));
        public string Base { get; } = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Base, "workspace 検証");
        public string ConfigFile => Path.Combine(Base, "config", "mcp-config.json");
        public string InstructionsFile => Path.Combine(Base, "instructions", "AGENTS.md");
        public string Command => Path.Combine(Base, "bin", "sharpdeps mcp.exe");

        public InstallationFixture(ITestOutputHelper output)
        {
            if (OperatingSystem.IsWindows() && !string.Equals(Path.GetPathRoot(Base), @"C:\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Installer scratch validation requires a C temporary directory.");
            CliPaths.RejectLinks(Parent);
            CliPaths.RejectLinks(Base);
            output.WriteLine("Installer scratch root: {0}", Base);
            foreach (var directory in new[] { Root, Path.GetDirectoryName(ConfigFile)!, Path.GetDirectoryName(InstructionsFile)!, Path.GetDirectoryName(Command)! })
                Directory.CreateDirectory(directory);
            File.WriteAllText(Command, "scratch command must never execute", Utf8);
        }

        public string[] Arguments(string operation = "install", params string[] extra)
            => [operation, "--client", "copilot-cli", "--root", Root, "--config-file", ConfigFile,
                "--instructions-file", InstructionsFile, .. (operation == "install" ? new[] { "--mcp-command", Command } : Array.Empty<string>()), .. extra];

        public CliArguments Options(string operation = "install", params string[] extra)
            => CliArguments.Parse(Arguments(operation, extra));

        public string[] Files() => Directory.GetFiles(Base, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

    }
}
