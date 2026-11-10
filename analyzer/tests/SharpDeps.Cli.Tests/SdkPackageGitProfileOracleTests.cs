namespace SharpDeps.Cli.Tests;

using System.Text.Json;
using SharpDeps.Index;
using Xunit;
using Xunit.Abstractions;

[Collection("markdown-index")]
public sealed class SdkPackageGitProfileOracleTests(ITestOutputHelper output)
{
    [WindowsSdk403Fact]
    [Trait("Category", "Integration")]
    [Trait("ControlledSdkOracle", "true")]
    public async Task Sdk403PackageAndOrdinaryGitStayClosedAcrossEditsFallbackAndPublication()
    {
        var runRoot = SdkProfileTestData.NewRoot("sharpdeps-sdk403-oracle");
        var root = Path.Combine(runRoot, "workspace");
        Directory.CreateDirectory(root);
        var packageRoot = Path.Combine(runRoot, "packages", "proof.package", "1.0.0");
        var feed = Path.Combine(runRoot, "feed");
        Directory.CreateDirectory(feed);
        var files = SdkProfileTestData.PackageFiles();
        File.WriteAllBytes(Path.Combine(feed, "proof.package.1.0.0.nupkg"), files["proof.package.1.0.0.nupkg"]);
        SdkProfileTestData.Write(root, "App.csproj", SdkProfileTestData.Project);
        SdkProfileTestData.Write(root, "Service.cs", "namespace Demo; public class Service { public int Run() => ProofPackage.PackageApi.Value; }\n");
        SdkProfileTestData.Write(root, "guide.md", "# Guide\n`Demo.Service.Run`\n");
        SdkProfileTestData.Git(root);
        var gitConfig = File.ReadAllText(Path.Combine(root, ".git", "config"));
        var fixturePin = JsonSerializer.Serialize(new { sdk = new { version = "10.0.403", rollForward = "disable" } });
        SdkProfileTestData.Write(root, "global.json", fixturePin);
        var config = Path.Combine(runRoot, "restore.config");
        File.WriteAllText(config, new System.Xml.Linq.XElement("configuration",
            new System.Xml.Linq.XElement("packageSources", new System.Xml.Linq.XElement("clear"),
                new System.Xml.Linq.XElement("add", new System.Xml.Linq.XAttribute("key", "fixture"), new System.Xml.Linq.XAttribute("value", feed)))).ToString(), SdkProfileTestData.Utf8);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            await OracleProcess.ExecuteAsync(root, ["restore", "App.csproj", "--configfile", config, "--verbosity", "quiet",
                "-p:RestorePackagesWithLockFile=true", "-p:NuGetAudit=false"], deadline.Token, packageRoot: Path.Combine(runRoot, "packages"));
            Assert.Equal(fixturePin, File.ReadAllText(Path.Combine(root, "global.json")));
            File.Delete(Path.Combine(root, "global.json"));
            var options = new CliIndexOptions(root, Path.Combine(root, "App.csproj"),
                Path.Combine(root, ".sharpdeps", "baseline.sqlite"), Guid.Parse("40311111-2222-4333-8444-555555555555"));
            string[] terms = ["Service", "PackageApi", "Guide", "Execute", "AddedFeature"];
            Task<OracleObservation> RunExact(OracleRun run, string label) => OracleProcess.RunAsync(
                run with { ExactSdkVersion = "10.0.403" }, runRoot, label, deadline.Token);
            // Let the audited SDK targets materialize their actual design-time cache before the positive proof.
            await RunExact(new(options with { IndexPath = Path.Combine(root, ".sharpdeps", "cache-prime.sqlite") }, null, terms), "cache-prime");
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(root, "obj"), "*.assets.cache", SearchOption.AllDirectories));
            var previous = await RunExact(new(options, null, terms), "baseline");
            var baselineProof = Assert.Single(previous.Snapshot.Files, f => f.Reuse is not null).Reuse!;
            Assert.True(CliPaths.SamePath(baselineProof.SdkPath, SdkProfileTestData.InstalledSdk("10.0.403")),
                "Expected the actual child SDK 10.0.403 proof, got: " + baselineProof.SdkPath);
            Assert.Equal(0, Count(previous, "cli.compilerErrors"));
            output.WriteLine("Baseline proof captured after exact child SDK registration and owned pin removal: " + baselineProof.SdkPath);
            var assetsPath = Path.Combine(root, "obj", "project.assets.json");
            var lockPath = Path.Combine(root, "packages.lock.json");
            var metadataPath = Path.Combine(packageRoot, ".nupkg.metadata");
            var dllPath = Path.Combine(packageRoot, SdkProfileTestData.Dll.Replace('/', Path.DirectorySeparatorChar));
            var originals = new[] { assetsPath, lockPath, metadataPath, dllPath }.ToDictionary(p => p, File.ReadAllBytes);
            var assetsStamp = File.GetLastWriteTimeUtc(assetsPath);
            for (var step = 0; step < 8; step++)
            {
                switch (step)
                {
                    case 0:
                        SdkProfileTestData.Write(root, "guide.md", "# Guide\n`Demo.Service.Run` edited.\n");
                        break;
                    case 1:
                        SdkProfileTestData.Write(root, "Service.cs", "namespace Demo; public class Service { public int Execute() => ProofPackage.PackageApi.Value + 1; }\n");
                        SdkProfileTestData.Write(root, "guide.md", "# Guide\n`Demo.Service.Execute` edited together.\n");
                        break;
                    case 2:
                        SdkProfileTestData.Write(root, "Added.cs", "namespace Demo; public class AddedFeature { public int Call(Service service) => service.Execute(); }\n");
                        SdkProfileTestData.Write(root, "guide.md", "# Guide\n`Demo.AddedFeature.Call` added with its source.\n");
                        break;
                    case 3:
                        File.Delete(Path.Combine(root, "Added.cs"));
                        SdkProfileTestData.Write(root, "guide.md", "# Guide\n`Demo.Service.Execute` after source removal.\n");
                        break;
                    case 4:
                        var head = Path.Combine(root, ".git", "refs", "heads", "main"); var stamp = File.GetLastWriteTimeUtc(head);
                        SdkProfileTestData.Write(root, ".git/refs/heads/main", new string('2', 40) + "\n"); File.SetLastWriteTimeUtc(head, stamp);
                        SdkProfileTestData.Write(root, ".gitignore", "*.ignored.cs\n");
                        SdkProfileTestData.Write(root, ".git/info/exclude", "*.untracked.cs\n");
                        break;
                    case 5:
                        // Each artifact has isolated validator regressions; combine their rejection with a real compiler oracle here.
                        File.WriteAllBytes(dllPath, SdkProfileTestData.PackageFiles(9)[SdkProfileTestData.Dll]);
                        File.WriteAllBytes(metadataPath, SdkProfileTestData.ChangeJson(originals[metadataPath], value => value["version"] = 1));
                        File.WriteAllBytes(lockPath, SdkProfileTestData.ChangeJson(originals[lockPath], value =>
                            value["dependencies"]!["net10.0"]![SdkProfileTestData.Id]!["contentHash"] = Convert.ToBase64String(new byte[64])));
                        File.WriteAllBytes(assetsPath, SdkProfileTestData.ChangeJson(originals[assetsPath], value =>
                            value["project"]!["restore"]!["packagesPath"] = Path.Combine(runRoot, "unselected-packages")));
                        File.SetLastWriteTimeUtc(assetsPath, assetsStamp);
                        break;
                    case 6:
                        foreach (var (path, bytes) in originals)
                            File.WriteAllBytes(path, bytes);
                        File.SetLastWriteTimeUtc(assetsPath, assetsStamp);
                        SdkProfileTestData.Write(root, "fixture.gitconfig", "[user]\nname = Fixture\n");
                        SdkProfileTestData.Write(root, ".git/config", gitConfig + "[include]\npath = ../fixture.gitconfig\n");
                        break;
                    case 7:
                        SdkProfileTestData.Write(root, ".git/config", gitConfig);
                        break;
                }
                var update = options with { IndexPath = Path.Combine(root, ".sharpdeps", $"update-{step}.sqlite"), AllowSemanticReuse = true };
                var incremental = await RunExact(new(update, previous.Snapshot, terms), $"update-{step}");
                var full = await RunExact(new(update with
                { IndexPath = Path.Combine(root, ".sharpdeps", $"full-{step}.sqlite"), AllowSemanticReuse = false }, previous.Snapshot, terms), $"full-{step}");
                Assert.NotEqual(incremental.ProcessId, full.ProcessId);
                Assert.Equal(step == 0 ? 1 : 0, Count(incremental, "cli.semanticGraphReused"));
                Assert.Equal(step is not (5 or 6), incremental.Snapshot.Files.Any(f => f.Reuse is not null));
                Assert.Equal(0, Count(incremental, "cli.compilerErrors"));
                if (step == 2)
                    Assert.Contains(incremental.Snapshot.Graph.Nodes, n => n.Name == "Demo.AddedFeature");
                if (step == 3)
                    Assert.DoesNotContain(incremental.Snapshot.Graph.Nodes, n => n.Name == "Demo.AddedFeature");
                Equal(full, incremental);
                output.WriteLine($"Step {step}: package/Git full oracle matched; proof={incremental.Snapshot.Files.Any(f => f.Reuse is not null)}.");
                previous = incremental;
            }
            var rollback = options with { IndexPath = Path.Combine(root, ".sharpdeps", "update-7.sqlite"), AllowSemanticReuse = true };
            foreach (var packageMutation in new[] { false, true })
            {
                var relative = packageMutation ? "proof.package/1.0.0/.nupkg.metadata" : ".git/refs/heads/main";
                var target = packageMutation ? metadataPath : CliPaths.InRoot(root, relative);
                var original = File.ReadAllBytes(target);
                var label = packageMutation ? "publication-package" : "publication-git";
                try
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => RunExact(new(rollback, previous.Snapshot, terms)
                    {
                        BeforeCommit = new(relative, packageMutation ? "{}" : new string('3', 40) + "\n") { PackageCache = packageMutation }
                    }, label));
                    using var failure = JsonDocument.Parse(File.ReadAllText(Path.Combine(runRoot, label + ".result.failure.json")));
                    Assert.Equal("SOURCE_CHANGED_SINCE_STAGING", failure.RootElement.GetProperty("code").GetString());
                    Assert.Equal(typeof(CliIndexException).FullName, failure.RootElement.GetProperty("exceptionType").GetString());
                    using var reader = IndexReader.Open(rollback.IndexPath, rollback.WorkspaceUuid);
                    Assert.Equal(previous.Snapshot.Graph.Generation, reader.Snapshot().Graph.Generation);
                    Assert.Equal(previous.Snapshot.Graph.SnapshotId, reader.Snapshot().Graph.SnapshotId);
                    output.WriteLine($"{label}: SOURCE_CHANGED_SINCE_STAGING; previous generation and snapshot retained.");
                }
                finally
                {
                    File.WriteAllBytes(target, original);
                }
            }
            var recovered = await RunExact(new(rollback, previous.Snapshot, terms), "recovered");
            var recoveredFull = await RunExact(new(rollback with
            { IndexPath = Path.Combine(root, ".sharpdeps", "recovered-full.sqlite"), AllowSemanticReuse = false }, previous.Snapshot, terms), "recovered-full");
            Assert.Equal(1, Count(recovered, "cli.semanticGraphReused"));
            Equal(recoveredFull, recovered);
            output.WriteLine("Recovery: independent full oracle matched; semantic graph reused.");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("SDK403 package/Git oracle artifacts: " + runRoot, error);
        }
        finally
        {
            var pin = Path.Combine(root, "global.json");
            if (File.Exists(pin) && File.ReadAllText(pin) == fixturePin)
                File.Delete(pin);
        }
        output.WriteLine("SDK403 package/Git oracle artifacts: " + runRoot);
    }

    private static void Equal(OracleObservation full, OracleObservation incremental)
    {
        var differences = FullBuildOracle.Differences(full, incremental);
        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences.Take(30)));
    }

    private static int Count(OracleObservation observation, string code)
        => observation.Snapshot.Graph.Diagnostics?.SingleOrDefault(d => d.Code == code)?.Count ?? 0;
}

internal sealed class WindowsSdk403FactAttribute : FactAttribute
{
    public WindowsSdk403FactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "The exact SDK 10.0.403 package/Git proof requires Windows; this platform is unverified.";
        else if (!Directory.Exists(SdkProfileTestData.InstalledSdk("10.0.403")))
            Skip = "The audited SDK 10.0.403 payload is not installed; the profile is unverified.";
    }
}