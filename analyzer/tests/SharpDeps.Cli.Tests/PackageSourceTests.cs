namespace SharpDeps.Cli.Tests;

using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Roslyn.Symbols;
using SharpDeps.Analysis.Roslyn;
using SharpDeps.Index;
using SharpDeps.Query;
using Xunit;

public sealed class PackageSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task IndexerRegistersPackageSourcesAndPublicationRechecksAssetsAndPackageEvidence(bool bom)
    {
        using var fixture = new PackageFixture(bom);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await fixture.RestoreProject(deadline.Token);
        var workspace = Guid.NewGuid();
        var index = Path.Combine(fixture.Root, ".sharpdeps", "index.sqlite");
        var options = new CliIndexOptions(fixture.Root, fixture.ProjectPath, index, workspace, AllowSemanticReuse: true);
        using var writer = IndexWriter.Open(fixture.Root, index, workspace, true);
        var first = await CliIndexer.BuildForPublicationAsync(options, null, deadline.Token);
        var package = Assert.Single(first.Snapshot.Files, QueryWorkspace.IsPackage);
        Assert.Contains("PackageType", Assert.Single(first.Snapshot.StoredPackageBodies).Body, StringComparison.Ordinal);
        Assert.All(first.Snapshot.Files, f => Assert.Null(f.Reuse));
        var type = Assert.Single(first.Snapshot.Graph.Nodes, n => n.Name == "PackageType");
        var occurrence = Assert.Single(first.Snapshot.Graph.SymbolOccurrences,
            o => o.LogicalSymbolId == type.Id && o.Location?.SourceId == package.SourceId);
        writer.Commit(IndexStage.Create(first.Snapshot), deadline.Token, first.VerifySelection);
        var savedBody = Assert.Single(first.Snapshot.StoredPackageBodies).Body;
        var location = Assert.IsType<HarnessLocation>(occurrence.Location);
        var span = Assert.IsType<HarnessRawSpan>(location.RawSpan);
        Assert.Equal(location, Assert.Single(occurrence.Declarations!));
        Assert.Equal(bom ? 1 : 0, span.Start);
        Assert.StartsWith("public class PackageType", savedBody.Substring(span.Start, span.Length));
        var httpGet = Assert.Single(first.Snapshot.Graph.Edges,
            e => e.Evidence?.SourceId == package.SourceId && e.HttpGetDeclaration?.State == HarnessHttpGetDeclarationState.Saved);
        Assert.Equal("items/{id}", httpGet.HttpGetDeclaration!.Template);
        var templateSpan = Assert.IsType<HarnessRawSpan>(httpGet.HttpGetDeclaration!.TemplateSpan);
        Assert.Equal("\"items/{id}\"", savedBody.Substring(templateSpan.Start, templateSpan.Length));
        var workspaceReader = new QueryWorkspace(fixture.Root);
        Assert.Equal(savedBody, Encoding.UTF8.GetString(workspaceReader.ReadStored(package, savedBody)));

        var pending = await CliIndexer.BuildForPublicationAsync(options, first.Snapshot, deadline.Token);
        var assetsPath = Path.Combine(fixture.Root, "obj", "project.assets.json");
        var originalAssets = File.ReadAllText(assetsPath);
        File.AppendAllText(assetsPath, "\n");
        var changed = Assert.Throws<CliIndexException>(() => writer.Commit(IndexStage.Create(pending.Snapshot),
            deadline.Token, pending.VerifySelection));
        Assert.Equal("SOURCE_CHANGED_SINCE_STAGING", changed.Code);
        File.WriteAllText(assetsPath, originalAssets);
        File.WriteAllText(fixture.SourcePath, PackageFixture.Text + "// changed after staging");
        changed = Assert.Throws<CliIndexException>(() => writer.Commit(IndexStage.Create(pending.Snapshot),
            deadline.Token, pending.VerifySelection));
        Assert.Equal("SOURCE_CHANGED_SINCE_STAGING", changed.Code);
        await Assert.ThrowsAsync<CliIndexException>(() => CliIndexer.BuildAsync(options, first.Snapshot, deadline.Token));
        using var reader = IndexReader.Open(index, workspace);
        Assert.Equal(1, reader.Snapshot().Graph.Generation);
        Assert.Equal(first.Snapshot.StoredPackageBodies, reader.Snapshot().StoredPackageBodies);
        Assert.Equal(httpGet.HttpGetDeclaration,
            Assert.Single(reader.Snapshot().Graph.Edges, e => e.Id == httpGet.Id).HttpGetDeclaration);
    }

    [Fact]
    public void SignedRestorePackageUsesNuGetContentHashRatherThanRawArchiveHash()
    {
        using var fixture = new PackageFixture();
        var saved = Path.Combine(AppContext.BaseDirectory, "signed-package-fixture");
        var originalAssets = JsonNode.Parse(File.ReadAllText(Path.Combine(saved, "assets.json")))!;
        var library = Assert.Single(originalAssets["libraries"]!.AsObject(),
            p => p.Key.StartsWith("Microsoft.NET.Test.Sdk/", StringComparison.Ordinal));
        var identity = library.Key.Split('/');
        var id = identity[0].ToLowerInvariant();
        var version = identity[1];
        var packagePath = id + "/" + version;
        var directory = Path.Combine(fixture.Cache, id, version);
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, id + "." + version + ".nupkg");
        File.Copy(Assert.Single(Directory.GetFiles(saved, "*.nupkg")), archive);
        File.Copy(Path.Combine(saved, "metadata.json"), Path.Combine(directory, ".nupkg.metadata"));
        var expectedHash = library.Value!["sha512"]!.GetValue<string>();
        Assert.NotEqual(expectedHash, Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(archive))));
        using var zip = ZipFile.OpenRead(archive);
        Assert.NotNull(zip.GetEntry(".signature.p7s"));
        var entry = Assert.Single(zip.Entries, e => e.FullName.EndsWith("/Microsoft.NET.Test.Sdk.Program.cs", StringComparison.Ordinal));
        var source = Path.Combine(directory, entry.FullName);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        entry.ExtractToFile(source);
        var compilationText = File.ReadAllText(source, Encoding.UTF8);
        fixture.Assets["libraries"] = new JsonObject { [library.Key] = library.Value.DeepClone() };
        fixture.Assets["targets"] = new JsonObject { ["net10.0"] = new JsonObject
            { [library.Key] = new JsonObject { ["type"] = "package" } } };
        fixture.SaveAssets();
        var binding = CliPackageSource.Bind(fixture.ProjectPath, "net10.0", source, compilationText);
        Assert.Equal(id, binding.PackageId);
        Assert.Equal(compilationText, binding.Text[binding.CompilationOffset..]);
        Assert.Equal(expectedHash, JsonNode.Parse(File.ReadAllText(Path.Combine(directory, ".nupkg.metadata")))!["contentHash"]!.GetValue<string>());
    }

    [Fact]
    public void RestoreBoundTreeHasStableIdentityAndStoredBodyWithoutAdoptingOtherAssets()
    {
        using var fixture = new PackageFixture();
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.PackageDirectory, ".nupkg.metadata")))!;
        File.WriteAllText(Path.Combine(fixture.Root, "packages.lock.json"), new JsonObject
        {
            ["dependencies"] = new JsonObject
            {
                ["net10.0"] = new JsonObject { ["Test.Package"] = new JsonObject
                    { ["resolved"] = "1.0.0", ["contentHash"] = metadata["contentHash"]!.DeepClone() } },
                ["net8.0"] = new JsonObject { ["Test.Package"] = new JsonObject
                    { ["resolved"] = "2.0.0", ["contentHash"] = "another-version" } },
                ["net9.0"] = new JsonObject { ["Test.Package"] = new JsonObject
                    { ["resolved"] = "3.0.0" } }
            }
        }.ToJsonString());
        var info = fixture.Bind();
        var workspace = Guid.NewGuid();
        var rootId = Identity.WorkspaceRootId(fixture.Root);
        var project = HarnessIdentity.ProjectId(workspace, "App.csproj");
        var variant = HarnessIdentity.VariantId(workspace, project, "net10.0", "Debug");
        SourceDocumentRegistry Registry() => new(rootId, fixture.Root, HarnessIdentity.WorkspaceId(workspace));
        SharpDeps.Analysis.Contracts.SourceDocument Register(SourceDocumentRegistry registry, string key, string stable)
            => registry.RegisterPackage(key, "App", stable, info.PackageId, info.Version, info.RelativePath,
                fixture.SourcePath, info.ContentHash, info.ByteLength);
        var forward = Registry();
        var first = Register(forward, "absolute-original|net10.0", variant);
        var other = Register(forward, "other", "other-stable-variant");
        var relocatedRoot = fixture.Root + "-relocated";
        var reverse = new SourceDocumentRegistry(Identity.WorkspaceRootId(relocatedRoot), relocatedRoot,
            HarnessIdentity.WorkspaceId(workspace));
        Assert.Equal(other, Register(reverse, "other", "other-stable-variant"));
        Assert.Equal(first, Register(reverse, "relocated-absolute|net10.0", variant));
        Assert.Equal(first, forward.ResolveTreeDocument("absolute-original|net10.0", fixture.Tree));
        Assert.Equal("packageSource", first.Origin);
        Assert.Equal(2, forward.Documents.Count);

        var id = HarnessIdentity.WorkspaceId(workspace);
        var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
            HarnessGraphContract.IdentityVersion, id, "package-positive", 1, HarnessCoverage.Partial,
            [], [new(id, HarnessNodeKind.Workspace, "Workspace", null, null)], [], [], [], null);
        var input = new IndexFile(first.Id, first.RelativePath, IndexFileKinds.PackageSource,
            info.ContentHash, info.ByteLength, info.Text.Length);
        var snapshot = new IndexSnapshot(graph, [input], [], [], [], PackageBodies: [new(first.Id, info.Text)]);
        var index = Path.Combine(fixture.Root, ".sharpdeps", "index.sqlite");
        using (var writer = IndexWriter.Open(fixture.Root, index, workspace, true))
        {
            Assert.Equal(3, writer.SchemaVersion);
            writer.Commit(IndexStage.Create(snapshot));
        }
        // Neither verification nor stored reads may depend on the extracted package cache.
        File.Delete(fixture.SourcePath);
        using var reader = IndexReader.Open(index, workspace);
        var saved = reader.Snapshot();
        Assert.Equal(Assert.Single(snapshot.StoredPackageBodies), Assert.Single(saved.StoredPackageBodies));
        var query = new QueryWorkspace(fixture.Root, _ => new([], true));
        var freshness = query.Verify(saved);
        Assert.Equal("unverified", freshness.State);
        Assert.Empty(freshness.DirtyPaths);
        Assert.Equal(["PACKAGE_INPUT_UNVERIFIED:" + first.RelativePath], freshness.Unverified);
        Assert.Equal(info.Text, Encoding.UTF8.GetString(query.ReadStored(input, saved.StoredPackageBodies[0].Body)));
    }

    [Theory]
    [InlineData("net10.0", "2.0.0")]
    [InlineData("net10.0", "1.0.0")]
    [InlineData("net8.0", "1.0.0")]
    [InlineData("net8.0", "not-a-version")]
    public void MatchingLockEntriesWithoutHashesCannotBypassProvenance(string framework, string version)
    {
        using var fixture = new PackageFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "packages.lock.json"), new JsonObject
        {
            ["dependencies"] = new JsonObject { [framework] = new JsonObject
                { ["Test.Package"] = new JsonObject { ["resolved"] = version } } }
        }.ToJsonString());
        var error = Assert.Throws<PackageDocumentRegistrationException>(() => fixture.Bind());
        Assert.Equal(PackageDocumentRegistrationException.ProvenanceUnidentified, error.Code);
    }

    [Theory]
    [InlineData("net10.0")]
    [InlineData("net8.0")]
    public void MatchingLockEntriesRequireResolvedVersionEvenWithValidHash(string framework)
    {
        using var fixture = new PackageFixture();
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.PackageDirectory, ".nupkg.metadata")))!;
        File.WriteAllText(Path.Combine(fixture.Root, "packages.lock.json"), new JsonObject
        {
            ["dependencies"] = new JsonObject { [framework] = new JsonObject
                { ["Test.Package"] = new JsonObject { ["contentHash"] = metadata["contentHash"]!.DeepClone() } } }
        }.ToJsonString());
        var error = Assert.Throws<PackageDocumentRegistrationException>(() => fixture.Bind());
        Assert.Equal(PackageDocumentRegistrationException.ProvenanceUnidentified, error.Code);
    }

    [Theory]
    [InlineData("named")]
    [InlineData("relative")]
    [InlineData("absolute")]
    public void LockFileSelectionUsesNuGetPrecedenceAndHashesSelectedEvidence(string selection)
    {
        using var fixture = new PackageFixture();
        var named = Path.Combine(fixture.Root, "packages.App.lock.json");
        var selected = selection == "named" ? named : Path.Combine(fixture.Root, "locks", "custom.lock.json");
        fixture.WriteLock(Path.Combine(fixture.Root, "packages.lock.json"), validHash: false);
        if (selection != "named")
        {
            fixture.WriteLock(named, validHash: false);
            fixture.Assets["project"]!["restore"]!["restoreLockProperties"] = new JsonObject
            {
                ["restorePackagesWithLockFile"] = "true", ["restoreLockedMode"] = true,
                ["nuGetLockFilePath"] = selection == "absolute" ? selected : "locks/custom.lock.json"
            };
            fixture.SaveAssets();
        }
        fixture.WriteLock(selected);
        var first = fixture.Bind();
        File.AppendAllText(selected, "\n");
        Assert.NotEqual(first.EvidenceHash, fixture.Bind().EvidenceHash);
    }

    [Theory]
    [InlineData("custom-missing")]
    [InlineData("custom-hash")]
    [InlineData("required-missing")]
    [InlineData("outside")]
    public void RequiredLockEvidenceCannotFallBackOrEscapeTheProject(string failure)
    {
        using var fixture = new PackageFixture();
        var properties = new JsonObject
        {
            ["restorePackagesWithLockFile"] = failure == "required-missing" ? "true" : "false",
            ["nuGetLockFilePath"] = ""
        };
        if (failure != "required-missing")
        {
            fixture.WriteLock(Path.Combine(fixture.Root, "packages.lock.json"));
            fixture.WriteLock(Path.Combine(fixture.Root, "packages.App.lock.json"));
            properties["nuGetLockFilePath"] = failure == "outside" ? "../outside.lock.json" : "locks/custom.lock.json";
            if (failure == "custom-hash")
                fixture.WriteLock(Path.Combine(fixture.Root, "locks", "custom.lock.json"), validHash: false);
            if (failure == "outside")
                fixture.WriteLock(Path.GetFullPath(Path.Combine(fixture.Root, "..", "outside.lock.json")));
        }
        fixture.Assets["project"]!["restore"]!["restoreLockProperties"] = properties;
        fixture.SaveAssets();
        var error = Assert.Throws<PackageDocumentRegistrationException>(() => fixture.Bind());
        Assert.Equal(PackageDocumentRegistrationException.ProvenanceUnidentified, error.Code);
    }

    [Theory]
    [InlineData("missing-entry")]
    [InlineData("extracted-mismatch")]
    [InlineData("tree-mismatch")]
    [InlineData("escape")]
    [InlineData("metadata-hash")]
    [InlineData("assets-hash-missing")]
    [InlineData("assets-hash-invalid")]
    [InlineData("archive-and-metadata-changed")]
    [InlineData("lock-hash")]
    [InlineData("unconfirmed-boundary")]
    [InlineData("wrong-framework")]
    [InlineData("linked-cache")]
    [InlineData("linked-package")]
    [InlineData("linked-file")]
    public void IncompleteOrInconsistentEvidenceRejectsTheCompilationTree(string corruption)
    {
        using var fixture = new PackageFixture();
        var text = PackageFixture.Text;
        switch (corruption)
        {
            case "assets-hash-missing": fixture.Assets["libraries"]!["test.package/1.0.0"]!.AsObject().Remove("sha512"); fixture.SaveAssets(); break;
            case "assets-hash-invalid": fixture.Assets["libraries"]!["test.package/1.0.0"]!["sha512"] = "bad"; fixture.SaveAssets(); break;
            case "archive-and-metadata-changed":
                text += "// replaced package";
                fixture.WriteArchive("build/Source.cs", text);
                File.WriteAllText(fixture.SourcePath, text, new UTF8Encoding(false));
                break;
            case "missing-entry":
                fixture.WriteArchive("not-the-source.cs", text);
                fixture.Assets["libraries"]!["test.package/1.0.0"]!["sha512"] =
                    JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.PackageDirectory, ".nupkg.metadata")))!["contentHash"]!.DeepClone();
                fixture.SaveAssets();
                break;
            case "extracted-mismatch": File.WriteAllText(fixture.SourcePath, text + "//changed"); break;
            case "tree-mismatch": text += "//changed"; break;
            case "escape": fixture.Assets["libraries"]!["test.package/1.0.0"]!["files"] = new JsonArray("../build/Source.cs"); fixture.SaveAssets(); break;
            case "metadata-hash": File.WriteAllText(Path.Combine(fixture.PackageDirectory, ".nupkg.metadata"), "{\"contentHash\":\"bad\"}"); break;
            case "lock-hash": fixture.WriteLock(Path.Combine(fixture.Root, "packages.lock.json"), validHash: false); break;
            case "unconfirmed-boundary": File.Delete(fixture.PropsPath); break;
            case "wrong-framework": fixture.Assets["targets"] = new JsonObject { ["net8.0"] = new JsonObject() }; fixture.SaveAssets(); break;
            case "linked-cache":
                Directory.Move(fixture.Cache, fixture.Cache + "-target");
                Directory.CreateSymbolicLink(fixture.Cache, fixture.Cache + "-target");
                break;
            case "linked-package":
                Directory.Move(fixture.PackageDirectory, fixture.PackageDirectory + "-target");
                Directory.CreateSymbolicLink(fixture.PackageDirectory, fixture.PackageDirectory + "-target");
                break;
            case "linked-file":
                File.Move(fixture.SourcePath, fixture.SourcePath + ".target");
                File.CreateSymbolicLink(fixture.SourcePath, fixture.SourcePath + ".target");
                break;
        }
        var error = Assert.Throws<PackageDocumentRegistrationException>(() => fixture.Bind(text));
        Assert.Equal(PackageDocumentRegistrationException.ProvenanceUnidentified, error.Code);
        Assert.Contains("CliPackageSource.Bind", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Cache, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegistrationRejectsConflictingBodiesAndClaimsWithoutLeakingCachePaths()
    {
        using var fixture = new PackageFixture();
        var info = fixture.Bind();
        var registry = new SourceDocumentRegistry("root", fixture.Root);
        registry.RegisterPackage("variant", "App", "stable", info.PackageId, info.Version, info.RelativePath,
            fixture.SourcePath, info.ContentHash, info.ByteLength);
        var conflict = Assert.Throws<PackageDocumentRegistrationException>(() => registry.RegisterPackage(
            "variant", "App", "stable", info.PackageId, info.Version, info.RelativePath,
            fixture.SourcePath, new string('a', 64), info.ByteLength));
        Assert.Equal(PackageDocumentRegistrationException.BodyConflict, conflict.Code);
        var ambiguous = Assert.Throws<PackageDocumentRegistrationException>(() => registry.RegisterPackage(
            "variant", "App", "stable", "another.package", info.Version, info.RelativePath,
            fixture.SourcePath, info.ContentHash, info.ByteLength));
        Assert.Equal(PackageDocumentRegistrationException.IdentityAmbiguous, ambiguous.Code);
        Assert.DoesNotContain(fixture.Cache, ambiguous.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PackageFixture : IDisposable
    {
        public const string Text = "public class PackageType { [Microsoft.AspNetCore.Mvc.HttpGet(\"items/{id}\")] public void Get() { } } // café 😀\n";
        private readonly string directory = Path.Combine(Path.GetTempPath(), "sharpdeps-package-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(directory, "workspace");
        public string Cache => Path.Combine(directory, "cache");
        public string ProjectPath => Path.Combine(Root, "App.csproj");
        public string PackageDirectory => Path.Combine(Cache, "test.package", "1.0.0");
        public string SourcePath => Path.Combine(PackageDirectory, "build", "Source.cs");
        public string PropsPath => Path.Combine(Root, "obj", "App.csproj.nuget.g.props");
        public JsonObject Assets { get; }
        public Microsoft.CodeAnalysis.SyntaxTree Tree => CSharpSyntaxTree.ParseText(Text, path: SourcePath);

        public PackageFixture(bool bom = false)
        {
            Directory.CreateDirectory(Path.Combine(Root, "obj"));
            Directory.CreateDirectory(Path.GetDirectoryName(SourcePath)!);
            File.WriteAllText(ProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                + "<ItemGroup><FrameworkReference Include=\"Microsoft.AspNetCore.App\" /></ItemGroup></Project>");
            Assert.True(SemanticEnvironment.TryRegister(Root, out var reason), reason);
            File.WriteAllText(SourcePath, Text, new UTF8Encoding(bom));
            WriteArchive("build/Source.cs", Text, bom);
            new XDocument(new XElement("Project", new XElement("PropertyGroup", new XElement("NuGetPackageRoot", Cache))))
                .Save(PropsPath);
            Assets = new JsonObject
            {
                ["project"] = new JsonObject { ["restore"] = new JsonObject { ["projectPath"] = ProjectPath, ["packagesPath"] = Cache } },
                ["targets"] = new JsonObject { ["net10.0"] = new JsonObject { ["test.package/1.0.0"] = new JsonObject { ["type"] = "package" } } },
                ["libraries"] = new JsonObject { ["test.package/1.0.0"] = new JsonObject
                {
                    ["type"] = "package", ["path"] = "test.package/1.0.0",
                    ["sha512"] = JsonNode.Parse(File.ReadAllText(Path.Combine(PackageDirectory, ".nupkg.metadata")))!["contentHash"]!.DeepClone(),
                    // Not a tree: missing, escaping and otherwise invalid extra entries must not be read or adopted.
                    ["files"] = new JsonArray("build/Source.cs", "missing.cs", "../never-read.cs")
                } }
            };
            SaveAssets();
        }

        public CliPackageSource.Binding Bind(string? text = null)
            => CliPackageSource.Bind(ProjectPath, "net10.0", Tree.FilePath, text ?? Tree.GetText().ToString());

        public void SaveAssets() => File.WriteAllText(Path.Combine(Root, "obj", "project.assets.json"), Assets.ToJsonString());

        public void WriteLock(string path, bool validHash = true)
        {
            var hash = validHash
                ? JsonNode.Parse(File.ReadAllText(Path.Combine(PackageDirectory, ".nupkg.metadata")))!["contentHash"]!.GetValue<string>()
                : "bad";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new JsonObject
            {
                ["dependencies"] = new JsonObject { ["net10.0"] = new JsonObject
                    { ["Test.Package"] = new JsonObject { ["resolved"] = "1.0.0", ["contentHash"] = hash } } }
            }.ToJsonString());
        }

        public async Task RestoreProject(CancellationToken token)
        {
            File.Delete(Path.Combine(Root, "obj", "project.assets.json"));
            var project = XDocument.Load(ProjectPath);
            project.Root!.Add(new XElement("ItemGroup", new XElement("Compile", new XAttribute("Include", SourcePath))));
            project.Save(ProjectPath);
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("restore"); start.ArgumentList.Add(ProjectPath);
            start.Environment["NUGET_PACKAGES"] = Cache;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            Assert.True(process.ExitCode == 0, await output + await error);
            var restored = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "obj", "project.assets.json")))!;
            restored["libraries"]!["test.package/1.0.0"] = Assets["libraries"]!["test.package/1.0.0"]!.DeepClone();
            restored["targets"]!["net10.0"]!["test.package/1.0.0"] = Assets["targets"]!["net10.0"]!["test.package/1.0.0"]!.DeepClone();
            File.WriteAllText(Path.Combine(Root, "obj", "project.assets.json"), restored.ToJsonString());
        }

        public void WriteArchive(string entryPath, string text, bool bom = false)
        {
            var path = Path.Combine(PackageDirectory, "test.package.1.0.0.nupkg");
            using (var stream = new FileStream(path, FileMode.Create))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry(entryPath).Open(), new UTF8Encoding(bom))) writer.Write(text);
            var hash = Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(path)));
            File.WriteAllText(Path.Combine(PackageDirectory, ".nupkg.metadata"), JsonSerializer.Serialize(new { contentHash = hash }));
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
