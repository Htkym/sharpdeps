namespace SharpDeps.Cli.Tests;

using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpDeps.Index;
using Xunit;
using Xunit.Abstractions;

[Collection("markdown-index")]
public sealed class SdkPackageSetOracleTests(ITestOutputHelper output)
{
    [WindowsSdk403Fact]
    [Trait("Category", "Integration")]
    [Trait("ControlledSdkOracle", "true")]
    public async Task ActualSdkPackageSetMatchesIndependentFullAcrossEditsConfigurationDependencyAndFallback()
    {
        var f = new Fixture(output, "edits");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            await f.PrepareAsync(deadline.Token);
            var previous = f.Baseline;
            SdkProfileTestData.Write(f.Root, "guide.md", "# Guide\n`MultiFixture.AppApi.Call` edited.\n");
            previous = await f.PairAsync(previous, "markdown", 1, true, deadline.Token);
            SdkProfileTestData.Write(f.Root, "App/AppApi.cs", Fixture.AppSource.Replace("public int Call()", "public int Execute()", StringComparison.Ordinal));
            SdkProfileTestData.Write(f.Root, "Consumer/ConsumerApi.cs", Fixture.ConsumerSource.Replace(".Call()", ".Execute()", StringComparison.Ordinal));
            SdkProfileTestData.Write(f.Root, "guide.md", "# Guide\n`MultiFixture.AppApi.Execute` changed with C#.\n");
            previous = await f.PairAsync(previous, "mixed", 0, true, deadline.Token);
            SdkProfileTestData.Write(f.Root, "Consumer/Added.cs", "namespace MultiFixture; public class AddedFeature { public int Call() => new ConsumerApi().Call(); }\n");
            previous = await f.PairAsync(previous, "add", 0, true, deadline.Token);
            Assert.Contains(previous.Snapshot.Graph.Nodes, node => node.Name == "MultiFixture.AddedFeature");
            File.Delete(Path.Combine(f.Root, "Consumer", "Added.cs"));
            SdkProfileTestData.Write(f.Root, "App/AppApi.cs", Fixture.AppSource);
            SdkProfileTestData.Write(f.Root, "Consumer/ConsumerApi.cs", Fixture.ConsumerSource);
            SdkProfileTestData.Write(f.Root, "guide.md", "# Guide\n`MultiFixture.AppApi.Call` restored after consecutive edits.\n");
            previous = await f.PairAsync(previous, "delete-and-undo", 0, true, deadline.Token);
            Assert.DoesNotContain(previous.Snapshot.Graph.Nodes, node => node.Name == "MultiFixture.AddedFeature");
            f.SetCompilerConfiguration(true);
            previous = await f.PairAsync(previous, "configuration", 0, true, deadline.Token);
            Assert.Contains(previous.Snapshot.Graph.Nodes, node => node.Name == "MultiFixture.ConfiguredFeature");
            f.SetCompilerConfiguration(false);
            previous = await f.PairAsync(previous, "configuration-undo", 0, true, deadline.Token);
            Assert.DoesNotContain(previous.Snapshot.Graph.Nodes, node => node.Name == "MultiFixture.ConfiguredFeature");
            await f.SetBetaVersionAsync("2.0.0", deadline.Token);
            previous = await f.PairAsync(previous, "dependency", 0, true, deadline.Token);
            f.AssertActualCacheGroups();
            await f.SetBetaVersionAsync("1.0.0", deadline.Token);
            previous = await f.PairAsync(previous, "dependency-undo", 0, true, deadline.Token);
            SdkProfileTestData.Write(f.Root, "Directory.Build.targets", "<Project />\n");
            previous = await f.PairAsync(previous, "unsupported-import", 0, false, deadline.Token);
            File.Delete(Path.Combine(f.Root, "Directory.Build.targets"));
            previous = await f.PairAsync(previous, "fallback-recovery", 0, true, deadline.Token);
            SdkProfileTestData.Write(f.Root, "guide.md", "# Guide\n`MultiFixture.AppApi.Call` after recovery.\n");
            await f.PairAsync(previous, "recovered-markdown", 1, true, deadline.Token);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("Package-set edit oracle artifacts: " + f.RunRoot, error);
        }
    }

    [WindowsSdk403Fact]
    [Trait("Category", "Integration")]
    [Trait("ControlledSdkOracle", "true")]
    public async Task ActualSecondPackageMutationsRejectReuseAndPublicationWithoutLosingPreviousGeneration()
    {
        var isolation = OracleCliIsolationSpec.RequireOwnerInput(
            Environment.GetEnvironmentVariable("SHARPDEPS_FACT2_FIXTURE_ROOT"),
            Environment.GetEnvironmentVariable("SHARPDEPS_FACT2_DOTNET_HOST"),
            Environment.GetEnvironmentVariable("SHARPDEPS_FACT2_DOTNET_ROOT"));
        isolation = isolation with
        {
            OwnedStop = OracleOwnedStop.RequireBinding(
            Environment.GetEnvironmentVariable(OracleOwnedStop.PathKey),
            Environment.GetEnvironmentVariable(OracleOwnedStop.NonceKey), isolation.RunRoot)
        };
        var f = new Fixture(output, "mutation", isolation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            await f.PrepareAsync(deadline.Token);
            var reads = f.CaptureMutationInputs();
            foreach (var label in new[] { "dll-same-size-time", "metadata", "assets", "lock", "cache", "timestamp-choice" })
            {
                void Apply()
                {
                    Fixture.Restore(reads);
                    f.ApplyMutation(label, reads);
                }
                try
                {
                    await f.PairAsync(f.Baseline, label, 0, false, deadline.Token, Apply);
                    if (label == "timestamp-choice")
                        foreach (var arm in new[] { "update", "full" })
                            f.AssertTimestampRegeneration(label + "-" + arm);
                }
                finally
                {
                    Fixture.Restore(reads);
                }
            }
            await f.PairAsync(f.Baseline, "input-recovery", 1, true, deadline.Token);
            var index = f.Options with
            {
                AllowSemanticReuse = true
            };
            var old = OracleProcess.Observe(index.IndexPath, index.WorkspaceUuid, Fixture.Terms);
            Assert.Equal(f.Baseline.Snapshot.Graph.Generation, old.Snapshot.Graph.Generation);
            var metadataBytes = SdkProfileTestData.ChangeJson(File.ReadAllBytes(f.BetaMetadata), value => value["source"] = "publication-fixture");
            var cache = f.Caches["App"];
            var changes = new (string Label, string Target, OracleFileMutation Mutation)[]
            {
                ("publish-beta-dll", f.BetaDll, new(f.PackageRelative(f.BetaDll), "")
                    { PackageCache = true, Bytes = f.AlternateBeta, PreserveWriteTime = true }),
                ("publish-beta-metadata", f.BetaMetadata, new(f.PackageRelative(f.BetaMetadata), SdkProfileTestData.Utf8.GetString(metadataBytes))
                    { PackageCache = true, PreserveWriteTime = true }),
                ("publish-cache-timestamp", cache, new(Path.GetRelativePath(f.Root, cache), "")
                    { Bytes = File.ReadAllBytes(cache), LastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(f.Assets("App")).AddSeconds(-2).Ticks })
            };
            foreach (var (label, target, mutation) in changes)
            {
                Assert.Contains(reads, state => CliPaths.SamePath(state.Path, target));
                if (mutation.Bytes is { } bytes && mutation.PreserveWriteTime)
                {
                    Assert.Equal(File.ReadAllBytes(target).Length, bytes.Length);
                    Assert.False(File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes));
                }
                try
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => f.RunAsync(new(index, f.Baseline.Snapshot, Fixture.Terms)
                    {
                        BeforeCommit = mutation
                    }, label, deadline.Token));
                    using var failure = JsonDocument.Parse(File.ReadAllText(Path.Combine(f.RunRoot, label + ".result.failure.json")));
                    Assert.Equal("SOURCE_CHANGED_SINCE_STAGING", failure.RootElement.GetProperty("code").GetString());
                    Assert.Equal(typeof(CliIndexException).FullName, failure.RootElement.GetProperty("exceptionType").GetString());
                    if (mutation.Bytes is { } expectedBytes)
                        Assert.Equal(expectedBytes, File.ReadAllBytes(target));
                    else
                        Assert.Equal(mutation.Text, File.ReadAllText(target, SdkProfileTestData.Utf8));
                    if (mutation.PreserveWriteTime)
                        Assert.Equal(reads.Single(state => CliPaths.SamePath(state.Path, target)).Time, File.GetLastWriteTimeUtc(target));
                    if (mutation.LastWriteTimeUtcTicks is { } ticks)
                    {
                        Assert.Equal(ticks, File.GetLastWriteTimeUtc(target).Ticks);
                        Assert.True(File.GetLastWriteTimeUtc(target) < File.GetLastWriteTimeUtc(f.Assets("App")));
                    }
                    using var reader = IndexReader.Open(index.IndexPath, index.WorkspaceUuid);
                    Assert.Equal(old.Snapshot.Graph.Generation, reader.Snapshot().Graph.Generation);
                    Assert.Equal(old.Snapshot.Graph.SnapshotId, reader.Snapshot().Graph.SnapshotId);
                    Equal(old, OracleProcess.Observe(index.IndexPath, index.WorkspaceUuid, Fixture.Terms));
                    output.WriteLine(label + ": publication refused; old generation, snapshot, FTS and queries retained.");
                }
                finally
                {
                    Fixture.Restore(reads);
                }
            }
            var recovered = await f.RunAsync(new(index, f.Baseline.Snapshot, Fixture.Terms), "published-recovery", deadline.Token);
            var independent = await f.RunAsync(new(index with
            {
                IndexPath = Path.Combine(f.Root, ".sharpdeps", "published-recovery-full.sqlite"),
                AllowSemanticReuse = false
            },
                f.Baseline.Snapshot, Fixture.Terms), "published-recovery-full", deadline.Token);
            Assert.NotEqual(recovered.ProcessId, independent.ProcessId);
            Assert.True(recovered.Snapshot.Graph.Generation > old.Snapshot.Graph.Generation);
            Assert.NotEqual(old.Snapshot.Graph.SnapshotId, recovered.Snapshot.Graph.SnapshotId);
            Assert.Equal(1, Count(recovered, "cli.semanticGraphReused"));
            f.AssertProof(recovered, true);
            f.AssertProof(independent, true);
            Equal(independent, recovered);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("Package-set mutation oracle artifacts: " + f.RunRoot, error);
        }
    }

    [Fact]
    public void LegacyTextMutationRequestsKeepTheirOptionalDefaults()
    {
        var mutation = JsonSerializer.Deserialize<OracleFileMutation>("{\"relativePath\":\"proof.package/1.0.0/.nupkg.metadata\",\"text\":\"legacy\",\"packageCache\":true}",
            SharpDeps.Query.QueryJson.Options)!;
        Assert.Equal("legacy", mutation.Text);
        Assert.True(mutation.PackageCache);
        Assert.Null(mutation.Bytes);
        Assert.False(mutation.PreserveWriteTime);
        Assert.Null(mutation.LastWriteTimeUtcTicks);
    }

    private static void Equal(OracleObservation expected, OracleObservation actual)
    {
        var differences = FullBuildOracle.Differences(expected, actual);
        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences.Take(30)));
    }
    private static int Count(OracleObservation observation, string code)
        => observation.Snapshot.Graph.Diagnostics?.SingleOrDefault(d => d.Code == code)?.Count ?? 0;

    private sealed record InputState(string Path, byte[] Bytes, DateTime Time);
    private sealed class Fixture(ITestOutputHelper output, string name, OracleCliIsolationSpec? isolation = null)
    {
        internal const string Alpha = "Alpha.Package";
        internal const string Beta = "Beta.Package";
        internal const string AppSource = "namespace MultiFixture; public class AppApi { public int Call() => new LibApi().Call() + AlphaPackage.Api.Value + BetaPackage.Api.Value; }\n"
            + "#if PACKAGE_SET_EDIT\npublic class ConfiguredFeature { public int Evaluate() => BetaPackage.Api.Value; }\n#endif\n";
        internal const string ConsumerSource = "namespace MultiFixture; public class ConsumerApi { public int Call() => new AppApi().Call(); }\n";
        internal static readonly string[] Terms = ["LibApi", "AppApi", "ConsumerApi", "AddedFeature", "ConfiguredFeature", "BetaPackage", "Guide"];
        private static readonly string SdkPin = JsonSerializer.Serialize(new { sdk = new { version = "10.0.403", rollForward = "disable" } });
        internal string RunRoot { get; } = isolation?.RunRoot ?? SdkProfileTestData.NewRoot("sharpdeps-package-set-" + name);
        internal string Root => Path.Combine(RunRoot, "workspace");
        private string Feed => Path.Combine(RunRoot, "feed");
        private string Config => Path.Combine(RunRoot, "restore.config");
        private string PackageCache => Path.Combine(RunRoot, "packages");
        private string BetaVersion { get; set; } = "1.0.0";
        internal CliIndexOptions Options { get; private set; } = null!;
        internal OracleObservation Baseline { get; private set; } = null!;
        internal byte[] AlternateBeta { get; private set; } = [];
        internal Dictionary<string, string> Caches { get; } = new(StringComparer.Ordinal);
        internal string BetaDll => Path.Combine(PackageCache, "beta.package", BetaVersion, "lib", "net10.0", "Beta.Package.dll");
        internal string BetaMetadata => Path.Combine(PackageCache, "beta.package", BetaVersion, ".nupkg.metadata");
        internal string Assets(string owner) => Path.Combine(Root, owner, "obj", "project.assets.json");
        private string Lock(string owner) => Path.Combine(Root, owner, "packages.lock.json");
        internal string PackageRelative(string path) => Path.GetRelativePath(PackageCache, path);
        private string Project(string owner) => Path.Combine(Root, owner, owner + ".csproj");

        internal async Task PrepareAsync(CancellationToken token)
        {
            isolation?.Validate();
            if (isolation is not null)
            {
                if (Directory.Exists(RunRoot) || File.Exists(RunRoot))
                    throw new IOException("Fact2 requires its separate, previously absent fixture root: " + RunRoot);
                // Atomic creation rejects a racing entry; later alias/object identity remains an owner gate.
                OracleFixtureRoot.CreateNew(RunRoot);
                foreach (var name in new[] { "cli-home", "nuget-http", "nuget-scratch", "temp", "packages" })
                    Directory.CreateDirectory(Path.Combine(RunRoot, name));
            }
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Feed);
            File.WriteAllText(Config, new XElement("configuration", new XElement("packageSources", new XElement("clear"),
                new XElement("add", new XAttribute("key", "fixture"), new XAttribute("value", Feed)))).ToString(), SdkProfileTestData.Utf8);
            await PackAsync("alpha", Alpha, "AlphaPackage", "1.0.0", 11, Feed, token);
            await PackAsync("beta", Beta, "BetaPackage", "1.0.0", 70, Feed, token);
            await PackAsync("beta-v2", Beta, "BetaPackage", "2.0.0", 72, Feed, token);
            var alternate = Path.Combine(RunRoot, "alternate-feed");
            Directory.CreateDirectory(alternate);
            await PackAsync("beta-alternate", Beta, "BetaPackage", "1.0.0", 71, alternate, token);
            using (var archive = ZipFile.OpenRead(Path.Combine(alternate, "Beta.Package.1.0.0.nupkg")))
            {
                Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Equals(".signature.p7s", StringComparison.OrdinalIgnoreCase));
                using var input = archive.GetEntry("lib/net10.0/Beta.Package.dll")!.Open();
                using var bytes = new MemoryStream();
                input.CopyTo(bytes);
                AlternateBeta = bytes.ToArray();
            }
            WriteProject("Lib", [Alpha], []);
            WriteProject("App", [Alpha, Beta], ["Lib"]);
            WriteProject("Consumer", [], ["App"]);
            SdkProfileTestData.Write(Root, "Lib/LibApi.cs", "namespace MultiFixture; public class LibApi { public int Call() => AlphaPackage.Api.Value; }\n");
            SdkProfileTestData.Write(Root, "App/AppApi.cs", AppSource);
            SdkProfileTestData.Write(Root, "Consumer/ConsumerApi.cs", ConsumerSource);
            SdkProfileTestData.Write(Root, "guide.md", "# Guide\n`MultiFixture.ConsumerApi.Call`\n");
            SdkProfileTestData.Git(Root);
            Options = new(Root, Project("Consumer"), Path.Combine(Root, ".sharpdeps", "baseline.sqlite"),
                Guid.Parse("40322222-3333-4444-8555-666666666666"));
            await RestoreAsync(token);
            var prime = await PrimeAsync("prime", token);
            AssertActualCacheGroups();
            Baseline = await RunAsync(new(Options, prime.Snapshot, Terms), "baseline", token);
            var full = await RunAsync(new(Options with
            {
                IndexPath = Path.Combine(Root, ".sharpdeps", "baseline-full.sqlite")
            }, prime.Snapshot, Terms), "baseline-full", token);
            Assert.NotEqual(Baseline.ProcessId, full.ProcessId);
            AssertProof(Baseline, true);
            AssertProof(full, true);
            Equal(full, Baseline);
            var admission = Admission("baseline");
            foreach (var field in new[] { "noGenerated", "noPackage", "closedReferences", "fullFingerprint", "afterFingerprint", "fingerprintEqual", "stableInputs", "inputHash" })
                Assert.True(admission[field]!.GetValue<bool>(), "Baseline admission must be true: " + field);
            Assert.Equal(3, Baseline.Snapshot.Graph.Variants.Count());
            foreach (var owner in new[] { "LibApi", "AppApi", "ConsumerApi" })
                Assert.Contains(Baseline.Snapshot.Graph.Nodes, node => node.Name == "MultiFixture." + owner);
            var original = File.ReadAllBytes(BetaDll);
            Assert.Equal(original.Length, AlternateBeta.Length);
            Assert.False(original.AsSpan().SequenceEqual(AlternateBeta));
            using var pe = new PEReader(new MemoryStream(AlternateBeta));
            Assert.True(pe.HasMetadata);
            var metadata = pe.GetMetadataReader();
            Assert.Equal(Beta, metadata.GetString(metadata.GetAssemblyDefinition().Name));
            Assert.Empty(metadata.AssemblyFiles);
            output.WriteLine("Actual SDK403 N baseline admitted: two packed IDs, repeated Alpha owners, transitive Lib, separate full child matched.");
        }

        private async Task PackAsync(string label, string id, string ns, string version, int value, string feed, CancellationToken token)
        {
            var root = Path.Combine(RunRoot, "package-build", label);
            Directory.CreateDirectory(root);
            var project = Path.Combine(root, "Package.csproj");
            File.WriteAllText(project, new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"), new XElement("PropertyGroup",
                new XElement("TargetFramework", "net10.0"), new XElement("PackageId", id), new XElement("AssemblyName", id),
                new XElement("Version", version), new XElement("Authors", "SharpDeps fixture"), new XElement("Description", "Closed independent managed fixture"),
                new XElement("EnableNETAnalyzers", "false"), new XElement("DebugType", "none"),
                new XElement("UseSharedCompilation", "false"), new XElement("Deterministic", "true"))).ToString(), SdkProfileTestData.Utf8);
            File.WriteAllText(Path.Combine(root, "Api.cs"), "namespace " + ns + "; public static class Api { public const int Value = " + value + "; }\n", SdkProfileTestData.Utf8);
            await WithOwnedPinAsync(root, async () =>
            {
                await OracleProcess.ExecuteAsync(root, ["restore", project, "--configfile", Config, "--verbosity", "quiet", "-p:NuGetAudit=false"], token, PackageCache, isolation);
                await OracleProcess.ExecuteAsync(root, ["pack", project, "--no-restore", "--disable-build-servers",
                    "-p:UseSharedCompilation=false", "-nodeReuse:false", "--configuration", "Release", "--output", feed, "--verbosity", "quiet"], token, PackageCache, isolation);
            });
        }
        private void WriteProject(string owner, string[] packages, string[] references)
        {
            var xml = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"), new XElement("PropertyGroup",
                new XElement("TargetFramework", "net10.0"), new XElement("EnableNETAnalyzers", "false"), new XElement("GitRepositoryConfigurationScope", "local")),
                new XElement("ItemGroup", packages.Select(id => new XElement("PackageReference", new XAttribute("Include", id), new XAttribute("Version", id == Beta ? BetaVersion : "1.0.0"))),
                    references.Select(id => new XElement("ProjectReference", new XAttribute("Include", "../" + id + "/" + id + ".csproj")))));
            SdkProfileTestData.Write(Root, Path.GetRelativePath(Root, Project(owner)), xml.ToString());
        }
        private async Task RestoreAsync(CancellationToken token) => await WithOwnedPinAsync(Root, () => OracleProcess.ExecuteAsync(Root,
            ["restore", Project("Consumer"), "--configfile", Config, "--verbosity", "quiet", "--force", "-p:RestorePackagesWithLockFile=true", "-p:NuGetAudit=false"], token, PackageCache, isolation));
        private static async Task WithOwnedPinAsync(string root, Func<Task> action)
        {
            var path = Path.Combine(root, "global.json");
            using (var pin = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                pin.Write(SdkProfileTestData.Utf8.GetBytes(SdkPin));
            try
            {
                await action();
            }
            finally
            {
                if (!File.Exists(path) || File.ReadAllText(path) != SdkPin)
                    throw new InvalidDataException("The owned fixture SDK pin changed; it was not removed.");
                File.Delete(path);
            }
        }
        internal Task<OracleObservation> RunAsync(OracleRun run, string label, CancellationToken token)
            => OracleProcess.RunAsync(run with
            {
                ExactSdkVersion = "10.0.403"
            }, RunRoot, label, token, isolation);
        private async Task<OracleObservation> PrimeAsync(string label, CancellationToken token)
        {
            var observation = await RunAsync(new(Options with
            {
                IndexPath = Path.Combine(Root, ".sharpdeps", label + ".sqlite")
            }, null, Terms), label, token);
            foreach (var owner in new[] { "Lib", "App", "Consumer" })
                Caches[owner] = Assert.Single(Directory.EnumerateFiles(Path.Combine(Root, owner, "obj"), "*.assets.cache", SearchOption.AllDirectories));
            return observation;
        }
        internal async Task<OracleObservation> PairAsync(OracleObservation previous, string label, int graphReused, bool proof,
            CancellationToken token, Action? beforeEach = null)
        {
            beforeEach?.Invoke();
            var options = Options with
            {
                IndexPath = Path.Combine(Root, ".sharpdeps", label + "-update.sqlite"),
                AllowSemanticReuse = true
            };
            var incremental = await RunAsync(new(options, previous.Snapshot, Terms), label + "-update", token);
            Assert.Equal(graphReused, Count(incremental, "cli.semanticGraphReused"));
            AssertProof(incremental, proof);
            beforeEach?.Invoke();
            var full = await RunAsync(new(options with
            {
                IndexPath = Path.Combine(Root, ".sharpdeps", label + "-full.sqlite"),
                AllowSemanticReuse = false
            }, previous.Snapshot, Terms), label + "-full", token);
            Assert.NotEqual(incremental.ProcessId, full.ProcessId);
            AssertProof(full, proof);
            Equal(full, incremental);
            output.WriteLine(label + ": independent full difference count=0; reuse=" + graphReused + "; proof=" + proof);
            return incremental;
        }
        internal void AssertProof(OracleObservation value, bool present)
        {
            var proofs = value.Snapshot.Files.Where(file => file.Reuse is not null).ToArray();
            if (!present)
                Assert.Empty(proofs);
            else
            {
                var proof = Assert.Single(proofs).Reuse!;
                Assert.Equal(CliReuseProof.OwnerVersion, proof.OwnerVersion);
                Assert.True(CliPaths.SamePath(proof.SdkPath, SdkProfileTestData.InstalledSdk("10.0.403")));
                Assert.NotNull(proof.Projects);
            }
            Assert.Equal(0, Count(value, "cli.compilerErrors"));
        }
        private JsonObject Admission(string label)
        {
            var root = JsonNode.Parse(File.ReadAllBytes(Path.Combine(RunRoot, label + ".result.proof-diagnostic.json")))!;
            return Assert.Single(root["admissions"]!.AsArray())!.AsObject();
        }
        internal void AssertTimestampRegeneration(string label)
        {
            var admission = Admission(label);
            Assert.True(admission["fullFingerprint"]!.GetValue<bool>());
            Assert.True(admission["afterFingerprint"]!.GetValue<bool>());
            Assert.False(admission["fingerprintEqual"]!.GetValue<bool>());
            Assert.True(File.GetLastWriteTimeUtc(Caches["App"]) > File.GetLastWriteTimeUtc(Assets("App")));
        }
        internal void SetCompilerConfiguration(bool enabled)
        {
            var path = Project("App");
            var document = XDocument.Load(path);
            document.Root!.Elements("PropertyGroup").Elements("DefineConstants").Remove();
            if (enabled)
                document.Root.Element("PropertyGroup")!.Add(new XElement("DefineConstants", "PACKAGE_SET_EDIT"));
            document.Save(path);
        }
        internal async Task SetBetaVersionAsync(string version, CancellationToken token)
        {
            BetaVersion = version;
            var document = XDocument.Load(Project("App"));
            document.Root!.Elements("ItemGroup").Elements("PackageReference").Single(item => (string?)item.Attribute("Include") == Beta).SetAttributeValue("Version", version);
            document.Save(Project("App"));
            await RestoreAsync(token);
            await PrimeAsync("prime-beta-" + version, token);
        }

        internal void AssertActualCacheGroups()
        {
            foreach (var owner in new[] { "Lib", "App", "Consumer" })
            {
                var groups = CacheItems(File.ReadAllBytes(Caches[owner]));
                var packages = owner == "Lib" ? new[] { Alpha } : new[] { Alpha, Beta };
                Assert.Equal(packages.Order(StringComparer.Ordinal), groups[8].Order(StringComparer.Ordinal));
                string[] direct = owner switch
                {
                    "Lib" => new[] { Alpha + "/1.0.0" },
                    "App" => new[] { Alpha + "/1.0.0", Beta + "/" + BetaVersion },
                    _ => []
                };
                Assert.Equal(direct.Order(StringComparer.Ordinal), groups[9].Order(StringComparer.Ordinal));
                string[] projects = owner == "Consumer" ? new[] { Path.GetRelativePath(Path.GetDirectoryName(Project(owner))!, Project("Lib")) } : [];
                Assert.Equal(projects.Order(StringComparer.Ordinal), groups[15].Order(StringComparer.Ordinal));
                Assert.Equal(packages.Length, groups[2].Count);
                if (owner == "Lib")
                    Assert.DoesNotContain(groups[2], path => CliPaths.SamePath(path, BetaDll));
                else
                    Assert.Contains(groups[2], path => CliPaths.SamePath(path, BetaDll));
                Assert.True(File.GetLastWriteTimeUtc(Caches[owner]) > File.GetLastWriteTimeUtc(Assets(owner)),
                    "Actual SDK cache must be strictly newer than its assets before the admitted baseline: " + owner);
            }
        }
        private static List<string>[] CacheItems(byte[] bytes)
        {
            Assert.InRange(bytes.Length, 48, 4 * 1024 * 1024);
            using var reader = new BinaryReader(new MemoryStream(bytes), SdkProfileTestData.Utf8);
            Assert.Equal(0x41474b50, reader.ReadInt32());
            Assert.Equal(12, reader.ReadInt32());
            Assert.Equal(32, reader.ReadBytes(32).Length);
            var table = reader.ReadInt32();
            var groups = Enumerable.Range(0, 17).Select(_ => new List<string>()).ToArray();
            for (var group = 0; group < groups.Length; group++)
            {
                var count = reader.ReadInt32();
                Assert.InRange(count, 0, 1024);
                for (var i = 0; i < count; i++)
                {
                    groups[group].Add(reader.ReadString());
                    var metadata = reader.ReadInt32();
                    Assert.InRange(metadata, 0, 128);
                    reader.BaseStream.Position += 8 * metadata;
                    Assert.True(reader.BaseStream.Position <= table);
                }
            }
            Assert.Equal((long)table, reader.BaseStream.Position);
            return groups;
        }
        internal InputState[] CaptureMutationInputs()
        {
            AssertActualCacheGroups();
            using (var assets = JsonDocument.Parse(File.ReadAllBytes(Assets("App"))))
                Assert.Contains(assets.RootElement.GetProperty("targets").GetProperty("net10.0").GetProperty(Beta + "/1.0.0").GetProperty("compile").EnumerateObject(),
                    item => item.Name == "lib/net10.0/Beta.Package.dll");
            return new[] { "Lib", "App", "Consumer" }.SelectMany(owner => new[] { Assets(owner), Lock(owner), Caches[owner] })
                .Concat(new[] { BetaMetadata, BetaDll }).Select(path =>
                {
                    CliPaths.RejectLinks(path);
                    return new InputState(path, File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));
                }).ToArray();
        }
        internal static void Restore(InputState[] states)
        {
            foreach (var state in states)
            {
                File.WriteAllBytes(state.Path, state.Bytes);
                File.SetLastWriteTimeUtc(state.Path, state.Time);
                Assert.Equal(state.Time, File.GetLastWriteTimeUtc(state.Path));
            }
        }
        internal void ApplyMutation(string label, InputState[] states)
        {
            InputState State(string path) => states.Single(state => CliPaths.SamePath(state.Path, path));
            var wrongHash = Convert.ToBase64String(new byte[64]);
            string target;
            switch (label)
            {
                case "dll-same-size-time":
                    target = BetaDll;
                    File.WriteAllBytes(target, AlternateBeta);
                    File.SetLastWriteTimeUtc(target, State(target).Time);
                    Assert.Equal(State(target).Bytes.Length, File.ReadAllBytes(target).Length);
                    Assert.Equal(State(target).Time, File.GetLastWriteTimeUtc(target));
                    break;
                case "metadata":
                    target = BetaMetadata;
                    File.WriteAllBytes(target, SdkProfileTestData.ChangeJson(State(target).Bytes, node => node["contentHash"] = wrongHash));
                    break;
                case "assets":
                    target = Assets("App");
                    File.WriteAllBytes(target, SdkProfileTestData.ChangeJson(State(target).Bytes, node => node["libraries"]![Beta + "/1.0.0"]!["sha512"] = wrongHash));
                    break;
                case "lock":
                    target = Lock("App");
                    File.WriteAllBytes(target, SdkProfileTestData.ChangeJson(State(target).Bytes, node => node["dependencies"]!["net10.0"]![Beta]!["contentHash"] = wrongHash));
                    break;
                case "cache":
                    target = Caches["App"];
                    File.WriteAllBytes(target, ChangeCachePackageMetadata(State(target).Bytes));
                    File.SetLastWriteTimeUtc(target, State(target).Time);
                    Assert.True(File.GetLastWriteTimeUtc(target) > File.GetLastWriteTimeUtc(Assets("App")));
                    break;
                case "timestamp-choice":
                    target = Caches["App"];
                    Assert.True(State(target).Time > File.GetLastWriteTimeUtc(Assets("App")));
                    File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(Assets("App")).AddSeconds(-2));
                    Assert.True(File.GetLastWriteTimeUtc(target) < File.GetLastWriteTimeUtc(Assets("App")));
                    Assert.Equal(State(target).Bytes, File.ReadAllBytes(target));
                    return;
                default:
                    throw new InvalidDataException("Unknown mutation fixture: " + label);
            }
            Assert.False(State(target).Bytes.AsSpan().SequenceEqual(File.ReadAllBytes(target)), "The selected input was not mutated: " + label);
        }
        private static byte[] ChangeCachePackageMetadata(byte[] original)
        {
            var bytes = (byte[])original.Clone();
            using var reader = new BinaryReader(new MemoryStream(bytes), SdkProfileTestData.Utf8);
            var table = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40));
            reader.BaseStream.Position = table;
            var count = reader.ReadInt32();
            Assert.InRange(count, 1, 4096);
            var changed = 0;
            for (var i = 0; i < count; i++)
            {
                var value = reader.ReadString();
                if (value != "lib/net10.0/Beta.Package.dll")
                    continue;
                var replacement = SdkProfileTestData.Utf8.GetBytes("lib/net10.0/Zeta.Package.dll");
                Assert.Equal(SdkProfileTestData.Utf8.GetByteCount(value), replacement.Length);
                replacement.CopyTo(bytes.AsSpan((int)reader.BaseStream.Position - replacement.Length));
                changed++;
            }
            Assert.Equal(1, changed);
            Assert.Equal(CacheItems(original)[2], CacheItems(bytes)[2]);
            return bytes;
        }
    }
}
