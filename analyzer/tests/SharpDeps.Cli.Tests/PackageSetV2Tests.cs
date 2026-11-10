namespace SharpDeps.Cli.Tests;

using System.Buffers.Binary;
using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

[Collection("markdown-index")]
public sealed class PackageSetV2Tests
{
    [Theory]
    [InlineData(8, 32, 128, 512, true)]
    [InlineData(9, 32, 128, 512, false)]
    [InlineData(8, 33, 128, 512, false)]
    [InlineData(8, 32, 129, 512, false)]
    [InlineData(8, 32, 128, 513, false)]
    [InlineData(1, 2, 1, 0, true)]
    [InlineData(0, 2, 1, 0, false)]
    [InlineData(1, 1, 1, 0, false)]
    public void AdmissionCountsAreExplicit(int identities, int declarations, int owners, int edges, bool expected)
        => Assert.Equal(expected, SdkProfileTestData.Boolean("PackageSetCounts", identities, declarations, owners, edges));

    [WindowsSdk403Fact]
    public void ProductionPlainProjectsAdmitsTwoIdsRepeatedIdAndReachableOwnersWithoutMaterializedCache()
    {
        foreach (var shape in new[] { 0, 1, 2 })
        {
            var f = new Fixture();
            f.AddPackage("First.Package");
            if (shape != 1)
                f.AddPackage("Second.Package");
            f.AddProject("First", shape == 1 ? ["First.Package"] : ["First.Package", "Second.Package"]);
            if (shape > 0)
                f.AddProject("Second", ["First.Package"]);
            if (shape == 2)
            {
                f.AddProject("Middle", [], ["First"]);
                f.AddProject("Consumer", [], ["Middle", "Second"]);
            }
            f.WriteArtifacts();
            var old = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            try
            {
                Environment.SetEnvironmentVariable("NUGET_PACKAGES", f.Cache);
                object?[] arguments = [f.Root, f.Sdk, new CliReuseProof.ProofBudget(default), false, null, null];
                Assert.True((bool)SdkProfileTestData.Invoke("PlainProjects", arguments)!);
                Assert.NotNull(arguments[5]);
                SdkProfileTestData.Git(f.Root);
                arguments = [f.Root, f.Sdk, new CliReuseProof.ProofBudget(default), true, null, null];
                Assert.True((bool)SdkProfileTestData.Invoke("PlainProjects", arguments)!);
                Assert.True((bool)arguments[4]!);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NUGET_PACKAGES", old);
            }
        }
    }

    [WindowsSdk403Fact]
    public void ProductionZeroAndSingletonKeepTheirRoutesAndGit()
    {
        foreach (var count in new[] { 0, 1 })
        {
            var f = new Fixture();
            if (count == 1)
                f.AddPackage("First.Package");
            f.AddProject("First", count == 0 ? [] : ["First.Package"]);
            f.WriteArtifacts();
            SdkProfileTestData.Git(f.Root);
            var old = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            try
            {
                Environment.SetEnvironmentVariable("NUGET_PACKAGES", f.Cache);
                object?[] arguments = [f.Root, f.Sdk, new CliReuseProof.ProofBudget(default), true, null, null];
                Assert.True((bool)SdkProfileTestData.Invoke("PlainProjects", arguments)!);
                Assert.True((bool)arguments[4]!);
                if (count == 0)
                    Assert.Null(arguments[5]);
                else
                {
                    Assert.NotNull(arguments[5]);
                    Assert.Null(Property(arguments[5]!, "RootSet"));
                    Assert.NotNull(Property(arguments[5]!, "Root"));
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NUGET_PACKAGES", old);
            }
        }
    }

    [WindowsSdk403Fact]
    public void ProductionAdmissionRejectsConflictsCyclesAndEscapingLiteralReferences()
    {
        foreach (var mutation in new[] { "version", "conflicting-version", "case", "duplicate", "cycle", "escape", "extra-package-edge" })
        {
            var f = Pair();
            if (mutation == "cycle")
            {
                f.AddProject("Second", [], ["First"]);
                var xml = XDocument.Load(f.Projects[0].Path);
                xml.Root!.Add(new XElement("ItemGroup", new XElement("ProjectReference", new XAttribute("Include", "../Second/Second.csproj"))));
                xml.Save(f.Projects[0].Path);
                f.WriteArtifacts();
            }
            else if (mutation == "extra-package-edge")
            {
                var assets = f.Assets(f.Projects[0]);
                assets = SdkProfileTestData.ChangeJson(assets, node => node["targets"]!["net10.0"]![f.Packages[0].Key]!["dependencies"] = new JsonObject { [f.Packages[1].Id] = "1.0.0" });
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(f.Projects[0].Path)!, "obj", "project.assets.json"), assets);
            }
            else
            {
                var xml = XDocument.Load(f.Projects[0].Path);
                var item = xml.Root!.Elements("ItemGroup").Elements("PackageReference").First();
                if (mutation == "version")
                    item.SetAttributeValue("Version", "[1.0.0, )");
                if (mutation == "conflicting-version")
                {
                    f.AddProject("Second", ["First.Package"]);
                    f.WriteArtifacts();
                    item.SetAttributeValue("Version", "2.0.0");
                }
                if (mutation == "case")
                {
                    f.AddProject("Second", ["First.Package"]);
                    f.WriteArtifacts();
                    item.SetAttributeValue("Include", "first.package");
                }
                if (mutation == "duplicate")
                    item.Parent!.Add(new XElement(item));
                if (mutation == "escape")
                {
                    File.WriteAllText(Path.Combine(f.RunRoot, "External.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
                    xml.Root.Add(new XElement("ItemGroup", new XElement("ProjectReference", new XAttribute("Include", "../../External.csproj"))));
                }
                xml.Save(f.Projects[0].Path);
            }
            var old = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            try
            {
                Environment.SetEnvironmentVariable("NUGET_PACKAGES", f.Cache);
                Assert.False(SdkProfileTestData.Boolean("PlainProjects", f.Root, f.Sdk, new CliReuseProof.ProofBudget(default), false, null, null));
            }
            finally
            {
                Environment.SetEnvironmentVariable("NUGET_PACKAGES", old);
            }
        }
    }

    [Fact]
    public void IncrementalEnumerationStopsBeforeAddingOverBudgetAndSecondPassSharesTheCap()
    {
        var budget = Nested("PackageSetInputBudget", new CliReuseProof.ProofBudget(default));
        var yielded = 0;
        IEnumerable<string> Entries()
        {
            for (var i = 0; i < 9000; i++)
            {
                yielded++;
                yield return "entry-" + i;
            }
        }
        ThrowsIo(() => Instance(budget, "CollectEntries", Entries()));
        Assert.Equal(8193, yielded);
        budget = Nested("PackageSetInputBudget", new CliReuseProof.ProofBudget(default));
        Assert.Equal(4096, ((string[])Instance(budget, "CollectEntries", Enumerable.Range(0, 4096).Select(i => "first-" + i))!).Length);
        Assert.Equal(4096, ((string[])Instance(budget, "CollectEntries", Enumerable.Range(0, 4096).Select(i => "second-" + i))!).Length);
        ThrowsIo(() => Instance(budget, "ReserveEntries", 1));
        var directory = SdkProfileTestData.NewRoot("sharpdeps-package-directory-bound");
        File.WriteAllText(Path.Combine(directory, "one"), "one");
        budget = Nested("PackageSetInputBudget", new CliReuseProof.ProofBudget(default));
        Instance(budget, "ReserveEntries", 8192);
        var captured = budget;
        ThrowsIo(() => Instance(captured, "Entries", directory));
    }

    [Fact]
    public void RetainedExpandedAndCumulativeBudgetsRejectBeforeReadingOrAllocatingNextFile()
    {
        var pass = Nested("PackageSetPassBudget");
        Instance(pass, "ReserveFile", 63L * 1024 * 1024);
        var root = SdkProfileTestData.NewRoot("sharpdeps-package-remaining");
        var path = Path.Combine(root, "next.bin");
        using (var file = File.Create(path))
            file.SetLength(2L * 1024 * 1024);
        var budget = Nested("PackageSetInputBudget", new CliReuseProof.ProofBudget(default));
        ThrowsIo(() => Instance(budget, "Read", path, pass, SharpDeps.Analysis.Core.Diagnostics.PhaseMetrics.ReadCategory.Package, 64L * 1024 * 1024));
        Assert.Equal(0L, Field(budget, "_bytes"));
        Instance(pass, "ReserveFile", 1024L * 1024);
        var fullPass = pass;
        ThrowsIo(() => Instance(fullPass, "ReserveFile", 1L));
        pass = Nested("PackageSetPassBudget");
        for (var i = 0; i < 2048; i++)
            Instance(pass, "ReserveFile", 0L);
        var retained = pass;
        ThrowsIo(() => Instance(retained, "ReserveFile", 0L));
        Instance(pass, "ReserveExpanded", 64L * 1024 * 1024);
        ThrowsIo(() => Instance(retained, "ReserveExpanded", 1L));
        budget = Nested("PackageSetInputBudget", new CliReuseProof.ProofBudget(default));
        for (var i = 0; i < 255; i++)
            Instance(budget, "ReserveBytes", 1024L * 1024);
        var cumulative = budget;
        ThrowsIo(() => Instance(cumulative, "Read", path, null, SharpDeps.Analysis.Core.Diagnostics.PhaseMetrics.ReadCategory.Package, 64L * 1024 * 1024));
        Assert.Equal(255L * 1024 * 1024, Field(budget, "_bytes"));
        Instance(budget, "ReserveBytes", 1024L * 1024);
        ThrowsIo(() => Instance(cumulative, "ReserveBytes", 1L));
        File.WriteAllBytes(path, new byte[4097]);
        budget = Nested("PackageSetInputBudget", new CliReuseProof.ProofBudget(default));
        var metadataBudget = budget;
        ThrowsIo(() => Instance(metadataBudget, "Read", path, null,
            SharpDeps.Analysis.Core.Diagnostics.PhaseMetrics.ReadCategory.Package, 4096L));
        Assert.Equal(0L, Field(metadataBudget, "_bytes"));
    }

    [Theory]
    [InlineData(".NUPKG.METADATA", 4096L)]
    [InlineData(".NuPkG.MeTaDaTa", 4096L)]
    [InlineData("FIRST.PACKAGE.1.0.0.NUPKG.SHA512", 128L)]
    [InlineData("First.Package.1.0.0.NuPkG.ShA512", 128L)]
    public void MetadataReadCapsRejectUpperAndMixedCaseBeforeRetainingOrReading(string relative, long expectedMaximum)
    {
        var maximum = (long)SdkProfileTestData.Invoke("PackageSetFileMaximum", relative)!;
        Assert.Equal(expectedMaximum, maximum);
        var root = SdkProfileTestData.NewRoot("sharpdeps-package-case-cap");
        var path = Path.Combine(root, relative);
        File.WriteAllBytes(path, new byte[checked((int)expectedMaximum + 1)]);
        var pass = Nested("PackageSetPassBudget");
        var budget = Nested("PackageSetInputBudget", new CliReuseProof.ProofBudget(default));
        ThrowsIo(() => Instance(budget, "Read", path, pass,
            SharpDeps.Analysis.Core.Diagnostics.PhaseMetrics.ReadCategory.Package, maximum));
        Assert.Equal(0L, Field(budget, "_bytes"));
        Assert.Equal(0L, Field(pass, "_retained"));
        Assert.Equal(0, Field(pass, "_files"));
    }

    [Fact]
    public void CentralDirectoryAndMetadataAreBoundedBeforeZipMaterialization()
    {
        var f = Pair();
        var p = f.Packages[0];
        bool Archive(Dictionary<string, byte[]> files) => SdkProfileTestData.Boolean("ValidatePackageSetArchive", files,
            f.Spec(p, f.Projects[0].Path), Nested("PackageSetInputBudget", new CliReuseProof.ProofBudget(default)), Nested("PackageSetPassBudget"), null);
        Assert.True(Archive(p.Files));
        var differentMetadata = new Dictionary<string, byte[]>(p.Files, StringComparer.OrdinalIgnoreCase)
        { [".nupkg.metadata"] = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, contentHash = Convert.ToBase64String(new byte[64]) }) };
        Assert.Equal(p.Files[p.Archive + ".sha512"], differentMetadata[p.Archive + ".sha512"]);
        Assert.False(Archive(differentMetadata));
        foreach (var (archiveName, diskName) in new[]
        {
            (".SIGNATURE.P7S", ".signature.p7s"), (".sIgNaTuRe.p7s", ".signature.p7s"),
            (".signature.p7s", ".SIGNATURE.P7S"), (".signature.p7s", ".sIgNaTuRe.p7s")
        })
            Assert.False(Archive(f.SignatureAliasArchive(p, archiveName, diskName)));
        using var central = new MemoryStream();
        using (var zip = new ZipArchive(central, ZipArchiveMode.Create, leaveOpen: true))
            for (var i = 0; i < 2048; i++)
                using (zip.CreateEntry("package/services/metadata/core-properties/" + i + ".psmdcp", CompressionLevel.NoCompression).Open())
                {
                }
        var manifest = (Array)SdkProfileTestData.Invoke("PackageSetZipDirectory", central.ToArray(), f.Spec(p, f.Projects[0].Path),
            Nested("PackageSetInputBudget", new CliReuseProof.ProofBudget(default)), Nested("PackageSetPassBudget"))!;
        Assert.Equal(2048, manifest.Length);
        var oversized = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(oversized, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(oversized.AsSpan(8), 2049);
        BinaryPrimitives.WriteUInt16LittleEndian(oversized.AsSpan(10), 2049);
        Assert.Equal("Package ZIP central directory exceeds the admitted shape.",
            ThrowsInvalidData(() => SdkProfileTestData.Invoke("PackageSetZipDirectory", oversized, f.Spec(p, f.Projects[0].Path),
                Nested("PackageSetInputBudget", new CliReuseProof.ProofBudget(default)), Nested("PackageSetPassBudget"))).Message);
        foreach (var metadata in new[] { new string('a', 4097), "{\"version\":2,\"contentHash\":\"" + new string('A', 4000) + "\"}" })
        {
            var changed = new Dictionary<string, byte[]>(p.Files, StringComparer.OrdinalIgnoreCase) { [".nupkg.metadata"] = SdkProfileTestData.Utf8.GetBytes(metadata) };
            Assert.False(Archive(changed));
        }
        var marker = new Dictionary<string, byte[]>(p.Files, StringComparer.OrdinalIgnoreCase)
        { [p.Archive + ".sha512"] = SdkProfileTestData.Utf8.GetBytes(new string('A', 129)) };
        Assert.False(Archive(marker));
        foreach (var mutation in new[] { "metadata-short", "metadata-long", "expanded-budget" })
            Assert.False(Archive(f.MalformedArchive(p, mutation)), $"Malformed archive was admitted: {mutation}");
        foreach (var mutation in new[] { "stored-length", "extra", "descriptor", "metadata-invalid-deflate" })
            ThrowsInvalidData(() => Archive(f.MalformedArchive(p, mutation)));
    }

    [Fact]
    public void ReachableProjectAssetsAndLockRequireExactOwnerTfmIdentityAndDeclaredEdges()
    {
        var f = Pair(withProjects: true);
        var owner = f.Projects.Last();
        var good = f.Assets(owner);
        Assert.True(f.ValidateAssets(owner, good, out _));
        Assert.True(f.ValidateLock(owner, f.Lock(owner)));
        var child = f.Projects[0];
        var key = child.Name + "/1.0.0";
        var mutations = new Action<JsonObject>[]
        {
            n => n["version"] = 3,
            n => n["version"] = 5,
            n => n["project"]!["restore"]!["frameworks"]!["net10.0"]!["framework"] = "net9.0",
            n => n["project"]!["restore"]!["frameworks"]!["net10.0"]!.AsObject().Remove("framework"),
            n => n["project"]!["restore"]!["frameworks"]!["net10.0"]!["targetAlias"] = "net9.0",
            n => n["project"]!["restore"]!["frameworks"]!["net10.0"]!.AsObject().Remove("targetAlias"),
            n => n["project"]!["restore"]!["frameworks"]!["net10.0"]!["unknown"] = "net10.0",
            n => n["targets"]!["net10.0"]!.AsObject().Remove(key),
            n => n["libraries"]![key]!["path"] = "../Other/Other.csproj",
            n => n["targets"]!["net10.0"]![key]!["framework"] = ".NETCoreApp,Version=v9.0",
            n => n["targets"]!["net10.0"]![key]!["dependencies"] = new JsonObject { ["Unknown.Package"] = "1.0.0" },
            n => n["project"]!["restore"]!["projectName"] = "Other",
            n => n["project"]!["restore"]!["projectPath"] = child.Path,
            n => n["project"]!["restore"]!["frameworks"]!["net10.0"]!["projectReferences"]!.AsObject().Clear(),
            n => n["projectFileDependencyGroups"]!["net10.0"] = new JsonArray("Unknown.Owner"),
            n =>
            {
                n["libraries"]!["Unknown.Owner/1.0.0"] = new JsonObject { ["type"] = "project", ["path"] = "../Other/Other.csproj", ["msbuildProject"] = "../Other/Other.csproj" };
                n["targets"]!["net10.0"]!["Unknown.Owner/1.0.0"] = n["targets"]!["net10.0"]![key]!.DeepClone();
            }
        };
        foreach (var mutate in mutations)
            Assert.False(f.ValidateAssets(owner, SdkProfileTestData.ChangeJson(good, mutate), out _));
        foreach (var mutate in new Action<JsonObject>[]
        {
            n => n["dependencies"]!["net10.0"]![child.LockName]!["dependencies"] = new JsonObject { ["Unknown.Package"] = "1.0.0" },
            n => n["dependencies"]!["net10.0"]!["Unknown.Owner"] = new JsonObject { ["type"] = "Project" },
            n => n["dependencies"]!["net10.0"]!.AsObject().Remove(child.LockName),
            n =>
            {
                var record = n["dependencies"]!["net10.0"]![child.LockName]!.DeepClone();
                n["dependencies"]!["net10.0"]!.AsObject().Remove(child.LockName);
                n["dependencies"]!["net10.0"]![child.Name] = record;
            }
        })
            Assert.False(f.ValidateLock(owner, SdkProfileTestData.ChangeJson(f.Lock(owner), mutate)));
    }

    [Fact]
    public void CacheRequiresAllSelectedItemsAndExactRequiredMetadataAndWindowsIdentities()
    {
        var f = Pair();
        var owner = f.Projects[0];
        Assert.True(f.ValidateAssets(owner, f.Assets(owner), out var contract));
        var baseline = Items(contract!);
        bool Valid(List<Item> items) => SdkProfileTestData.Boolean("PackageSetAssetsCache", Cache(items), contract);
        Assert.True(Valid(baseline));
        Assert.False(Valid([]));
        var second = f.Packages[1];
        var mutations = new Action<List<Item>>[]
        {
            items => items.RemoveAll(p => p.ItemSpec.Contains(second.Id, StringComparison.OrdinalIgnoreCase)),
            items => items.RemoveAll(p => p.Group == 13),
            items => items.First(p => p.Group == 2).Metadata.Remove("NuGetPackageId"),
            items => items.First(p => p.Group == 2).Metadata["NuGetPackageId"] = second.Id,
            items => items.First(p => p.Group == 2).Metadata["PathInPackage"] = "lib/net10.0/Unselected.dll",
            items => items[items.FindIndex(p => p.Group == 2)] = new(2, Path.Combine(f.Packages[0].Root, "ref", "net10.0", "Unselected.dll"), new(StringComparer.Ordinal)),
            items =>
            {
                var first = items.First(p => p.Group == 2);
                var index = items.FindLastIndex(p => p.Group == 2);
                items[index] = first with { ItemSpec = first.ItemSpec.ToUpperInvariant() };
            },
            items => items.Add(new(0, "Generator.dll", new(StringComparer.Ordinal))),
            items => items.First(p => p.Group == 9).Metadata["DiagnosticLevel"] = "Warning",
            items => items.First(p => p.Group == 9).Metadata["Path"] = f.Packages[1].Root,
            items =>
            {
                var i = items.FindIndex(p => p.Group == 2);
                items[i] = items[i] with { ItemSpec = Path.Combine(f.Cache, "outside", "1.0.0", "Other.dll") };
            },
            items =>
            {
                var i = items.FindIndex(p => p.Group == 2);
                items[i] = items[i] with { ItemSpec = Path.Combine(Path.GetDirectoryName(items[i].ItemSpec)!, ".", Path.GetFileName(items[i].ItemSpec)) };
            }
        };
        foreach (var mutate in mutations)
        {
            var changed = Items(contract!);
            mutate(changed);
            Assert.False(Valid(changed));
        }
        var bytes = Cache(baseline);
        Assert.Equal("Package cache string byte budget exceeded.",
            ThrowsInvalidData(() => SdkProfileTestData.Invoke("PackageSetAssetsCache", bytes[..^1], contract)).Message);
        foreach (var offset in new[] { 0, 4, 40 })
        {
            var bad = (byte[])bytes.Clone();
            bad[offset] ^= 127;
            Assert.False(SdkProfileTestData.Boolean("PackageSetAssetsCache", bad, contract));
        }
        var reader = Nested("PackageSetCacheReader", new byte[] { 255, 255, 255, 255, 127 });
        Assert.Equal("Package cache string length is invalid.", ThrowsInvalidData(() => Instance(reader, "String")).Message);
        using var text = new MemoryStream();
        using (var writer = new BinaryWriter(text, SdkProfileTestData.Utf8, leaveOpen: true))
            writer.Write(new string('a', 16385));
        reader = Nested("PackageSetCacheReader", text.ToArray());
        var oversized = reader;
        Assert.Equal("Package cache string character budget exceeded.", ThrowsInvalidData(() => Instance(oversized, "String")).Message);
    }

    [Fact]
    public void CanonicalRootSetHasValueEqualityAndExactVersionDirectoryContainment()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sharpdeps-package-root-values"));
        var roots = new[] { Path.Combine(root, "first", "1.0.0"), Path.Combine(root, "second", "1.0.0") };
        var a = new CliReuseFingerprint(root, "env", "deps", null, JsonSerializer.Serialize(roots));
        var b = new CliReuseFingerprint(root, "env", "deps", null, JsonSerializer.Serialize(roots));
        Assert.Equal(a, b);
        Assert.True(CliReuseProof.PackageRootSetContains(Path.Combine(roots[1], "lib", "A.dll"), a.PackageRootSet));
        Assert.False(CliReuseProof.PackageRootSetContains(Path.Combine(root, "third", "1.0.0", "A.dll"), a.PackageRootSet));
        Assert.False(CliReuseProof.PackageRootSetContains(Path.Combine(roots[1] + "-alias", "A.dll"), a.PackageRootSet));
    }

    private static Fixture Pair(bool withProjects = false)
    {
        var f = new Fixture();
        f.AddPackage("First.Package");
        f.AddPackage("Second.Package");
        f.AddProject("First", ["First.Package", "Second.Package"]);
        if (withProjects)
        {
            f.AddProject("Middle", ["First.Package"], ["First"]);
            f.AddProject("Consumer", [], ["Middle"]);
        }
        f.WriteArtifacts();
        return f;
    }

    private static object Nested(string name, params object?[] arguments) => Activator.CreateInstance(
        typeof(CliReuseProof).GetNestedType(name, BindingFlags.NonPublic)!, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
        binder: null, args: arguments, culture: null)!;
    private static object? Instance(object value, string name, params object?[] arguments) => value.GetType()
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(value, arguments);
    private static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
    private static object? Field(object value, string name) => value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value);
    private static void ThrowsIo(Action action) => Assert.IsAssignableFrom<IOException>(Assert.Throws<TargetInvocationException>(action).InnerException);
    private static InvalidDataException ThrowsInvalidData(Action action) => Assert.IsType<InvalidDataException>(Assert.Throws<TargetInvocationException>(action).InnerException);
    private sealed record Item(int Group, string ItemSpec, Dictionary<string, string> Metadata);
    private static List<Item> Items(object contract)
    {
        var items = new List<Item>();
        var group = 0;
        foreach (IDictionary dictionary in (IEnumerable)Property(contract, "Groups")!)
        {
            foreach (var value in dictionary.Values)
                items.Add(new(group, (string)Property(value!, "Item")!, new((Dictionary<string, string>)Property(value!, "Metadata")!, StringComparer.Ordinal)));
            group++;
        }
        return items;
    }
    private static byte[] Cache(List<Item> items)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, SdkProfileTestData.Utf8, leaveOpen: true);
        var strings = items.SelectMany(p => p.Metadata).SelectMany(p => new[] { p.Key, p.Value }).Distinct(StringComparer.Ordinal).ToList();
        writer.Write(0x41474b50);
        writer.Write(12);
        writer.Write(new byte[32]);
        writer.Write(0);
        for (var group = 0; group < 17; group++)
        {
            var selected = items.Where(p => p.Group == group).ToArray();
            writer.Write(selected.Length);
            foreach (var item in selected)
            {
                writer.Write(item.ItemSpec);
                writer.Write(item.Metadata.Count);
                foreach (var (name, value) in item.Metadata)
                {
                    writer.Write(strings.IndexOf(name));
                    writer.Write(strings.IndexOf(value));
                }
            }
        }
        var table = (int)stream.Position;
        writer.Write(strings.Count);
        foreach (var value in strings)
            writer.Write(value);
        writer.Seek(40, SeekOrigin.Begin);
        writer.Write(table);
        writer.Flush();
        return stream.ToArray();
    }

    private sealed record Package(string Id, string Root, Dictionary<string, byte[]> Files)
    {
        internal string Version => "1.0.0";
        internal string Key => Id + "/" + Version;
        internal string RelativeDll => "lib/net10.0/" + Id + ".dll";
        internal string Archive => Id.ToLowerInvariant() + ".1.0.0.nupkg";
        internal string Hash => SdkProfileTestData.ContentHash(Files);
    }
    private sealed record Project(string Name, string Path, string[] Packages, string[] References)
    {
        internal string LockName => Name.ToLowerInvariant();
    }

    private sealed class Fixture
    {
        internal string RunRoot { get; } = SdkProfileTestData.NewRoot("sharpdeps-package-set-v2");
        internal string Root => Path.Combine(RunRoot, "workspace");
        internal string Cache => Path.Combine(RunRoot, "packages");
        internal string Sdk => SdkProfileTestData.InstalledSdk("10.0.403");
        internal List<Package> Packages { get; } = [];
        internal List<Project> Projects { get; } = [];

        internal void AddPackage(string id)
        {
            var compilation = CSharpCompilation.Create(id,
                [CSharpSyntaxTree.ParseText("namespace Fixture; public static class PackageApi { public static int Value => 7; }", encoding: SdkProfileTestData.Utf8)],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, deterministic: true));
            using var dll = new MemoryStream();
            var emitted = compilation.Emit(dll);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [id.ToLowerInvariant() + ".nuspec"] = SdkProfileTestData.Utf8.GetBytes("<package><metadata><id>" + id + "</id><version>1.0.0</version><authors>Fixture</authors><description>Closed package</description></metadata></package>"),
                ["lib/net10.0/" + id + ".dll"] = dll.ToArray(),
                ["ref/net10.0/Unselected.dll"] = dll.ToArray()
            };
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, bytes) in files)
                {
                    using var output = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
                    output.Write(bytes);
                }
                using var metadata = zip.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal).Open();
                metadata.Write(SdkProfileTestData.Utf8.GetBytes("<Types/>"));
            }
            var name = id.ToLowerInvariant() + ".1.0.0.nupkg";
            BindArchive(files, name, archive.ToArray());
            Packages.Add(new(id, Path.Combine(Cache, id.ToLowerInvariant(), "1.0.0"), files));
        }

        internal void AddProject(string name, string[] packages, string[]? references = null)
        {
            var path = Path.Combine(Root, name, name + ".csproj");
            Projects.Add(new(name, path, packages, references ?? []));
            var xml = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"),
                    new XElement("EnableNETAnalyzers", "false"), new XElement("GitRepositoryConfigurationScope", "local")),
                new XElement("ItemGroup", packages.Select(id => new XElement("PackageReference", new XAttribute("Include", id), new XAttribute("Version", "1.0.0"))),
                    (references ?? []).Select(id => new XElement("ProjectReference", new XAttribute("Include", "../" + id + "/" + id + ".csproj")))));
            SdkProfileTestData.Write(Root, Path.GetRelativePath(Root, path), xml.ToString());
        }

        internal void WriteArtifacts()
        {
            foreach (var package in Packages)
                foreach (var (path, bytes) in package.Files)
                {
                    var destination = Path.Combine(package.Root, path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.WriteAllBytes(destination, bytes);
                }
            foreach (var project in Projects)
            {
                var obj = Path.Combine(Path.GetDirectoryName(project.Path)!, "obj");
                Directory.CreateDirectory(obj);
                File.WriteAllBytes(Path.Combine(obj, "project.assets.json"), Assets(project));
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(project.Path)!, "packages.lock.json"), Lock(project));
            }
        }

        internal Project[] Reachable(Project owner)
        {
            var selected = new HashSet<string>(StringComparer.Ordinal) { owner.Name };
            var pending = new Stack<string>(owner.References);
            while (pending.TryPop(out var id))
                if (selected.Add(id))
                    foreach (var next in Projects.Single(p => p.Name == id).References)
                        pending.Push(next);
            return Projects.Where(p => p != owner && selected.Contains(p.Name)).ToArray();
        }
        internal Package[] Selected(Project owner)
        {
            var ids = Reachable(owner).Append(owner).SelectMany(p => p.Packages).ToHashSet(StringComparer.Ordinal);
            return Packages.Where(p => ids.Contains(p.Id)).ToArray();
        }
        internal Dictionary<string, string> Dependencies(Project project) => project.Packages.Concat(project.References)
            .ToDictionary(id => id, _ => "1.0.0", StringComparer.Ordinal);
        internal object Spec(Package package, string owner) => Nested("PackageSpec", owner, package.Id, package.Version);
        internal Array Payloads(Project owner)
        {
            var selected = Selected(owner);
            var type = typeof(CliReuseProof).GetNestedType("PackagePayload", BindingFlags.NonPublic)!;
            var result = Array.CreateInstance(type, selected.Length);
            for (var i = 0; i < selected.Length; i++)
            {
                var p = selected[i];
                result.SetValue(Nested("PackagePayload", Spec(p, owner.Path), p.Root, p.Files, p.Hash), i);
            }
            return result;
        }
        internal object Contract(Project owner)
        {
            var specType = typeof(CliReuseProof).GetNestedType("PackageSpec", BindingFlags.NonPublic)!;
            var specs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(specType))!;
            foreach (var project in Projects)
                foreach (var id in project.Packages)
                    specs.Add(Spec(Packages.Single(p => p.Id == id), project.Path));
            var edges = Projects.ToDictionary(p => p.Path, p => p.References.Select(id => Projects.Single(other => other.Name == id).Path).ToArray(), StringComparer.OrdinalIgnoreCase);
            var direct = Projects.ToDictionary(p => p.Path, p => p.Packages.ToHashSet(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
            var contracts = (IDictionary)SdkProfileTestData.Invoke("PackageSetOwners", Projects.Select(p => p.Path).ToList(), edges, direct,
                specs, Nested("PackageSetInputBudget", new CliReuseProof.ProofBudget(default)))!;
            return contracts[owner.Path]!;
        }
        internal byte[] Assets(Project owner)
        {
            var libraries = new JsonObject();
            var targets = new JsonObject();
            foreach (var p in Selected(owner))
            {
                libraries[p.Key] = new JsonObject
                {
                    ["type"] = "package",
                    ["path"] = p.Id.ToLowerInvariant() + "/1.0.0",
                    ["sha512"] = p.Hash,
                    ["files"] = new JsonArray(p.Files.Keys.Where(path => !path.EndsWith(".nupkg", StringComparison.Ordinal)).Select(path => (JsonNode?)JsonValue.Create(path)).ToArray())
                };
                targets[p.Key] = new JsonObject
                {
                    ["type"] = "package",
                    ["compile"] = new JsonObject { [p.RelativeDll] = new JsonObject() },
                    ["runtime"] = new JsonObject { [p.RelativeDll] = new JsonObject() }
                };
            }
            foreach (var p in Reachable(owner))
            {
                var key = p.Name + "/1.0.0";
                var relative = Path.GetRelativePath(Path.GetDirectoryName(owner.Path)!, p.Path).Replace('\\', '/');
                libraries[key] = new JsonObject { ["type"] = "project", ["path"] = relative, ["msbuildProject"] = relative };
                targets[key] = new JsonObject
                {
                    ["type"] = "project",
                    ["framework"] = ".NETCoreApp,Version=v10.0",
                    ["dependencies"] = DependencyJson(p),
                    ["compile"] = new JsonObject { ["bin/placeholder/" + p.Name + ".dll"] = new JsonObject() },
                    ["runtime"] = new JsonObject { ["bin/placeholder/" + p.Name + ".dll"] = new JsonObject() }
                };
            }
            var references = new JsonObject();
            foreach (var id in owner.References)
            {
                var path = Projects.Single(p => p.Name == id).Path;
                references[path] = new JsonObject { ["projectPath"] = path };
            }
            var direct = new JsonObject();
            foreach (var id in owner.Packages)
                direct[id] = new JsonObject { ["target"] = "Package", ["version"] = "[1.0.0, )" };
            return JsonSerializer.SerializeToUtf8Bytes(new JsonObject
            {
                ["version"] = 4,
                ["libraries"] = libraries,
                ["targets"] = new JsonObject { ["net10.0"] = targets },
                ["projectFileDependencyGroups"] = new JsonObject
                {
                    ["net10.0"] = new JsonArray(owner.Packages.Select(id => id + " >= 1.0.0")
                    .Concat(owner.References).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
                },
                ["packageFolders"] = new JsonObject { [Cache + Path.DirectorySeparatorChar] = new JsonObject() },
                ["project"] = new JsonObject
                {
                    ["version"] = "1.0.0",
                    ["restore"] = new JsonObject
                    {
                        ["projectUniqueName"] = owner.Path,
                        ["projectName"] = owner.Name,
                        ["projectPath"] = owner.Path,
                        ["packagesPath"] = Cache,
                        ["frameworks"] = new JsonObject { ["net10.0"] = new JsonObject { ["framework"] = "net10.0", ["targetAlias"] = "net10.0", ["projectReferences"] = references } }
                    },
                    ["frameworks"] = new JsonObject
                    {
                        ["net10.0"] = new JsonObject
                        {
                            ["framework"] = "net10.0",
                            ["targetAlias"] = "net10.0",
                            ["dependencies"] = direct,
                            ["frameworkReferences"] = new JsonObject { ["Microsoft.NETCore.App"] = new JsonObject { ["privateAssets"] = "all" } }
                        }
                    }
                }
            });
        }
        private JsonObject DependencyJson(Project project)
        {
            var result = new JsonObject();
            foreach (var (id, version) in Dependencies(project))
                result[id] = "[" + version + ", )";
            return result;
        }
        internal byte[] Lock(Project owner)
        {
            var items = new JsonObject();
            foreach (var p in Selected(owner))
            {
                var direct = owner.Packages.Contains(p.Id, StringComparer.Ordinal);
                var item = new JsonObject { ["type"] = direct ? "Direct" : "Transitive", ["resolved"] = "1.0.0", ["contentHash"] = p.Hash };
                if (direct)
                    item["requested"] = "[1.0.0, )";
                items[p.Id] = item;
            }
            foreach (var p in Reachable(owner))
                items[p.LockName] = new JsonObject { ["type"] = "Project", ["dependencies"] = DependencyJson(p) };
            return JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["version"] = 1, ["dependencies"] = new JsonObject { ["net10.0"] = items } });
        }
        internal bool ValidateAssets(Project owner, byte[] bytes, out object? cacheContract)
        {
            object?[] arguments = [bytes, Path.Combine(Path.GetDirectoryName(owner.Path)!, "obj", "project.assets.json"), Root, Sdk, Cache,
                Contract(owner), Payloads(owner), new CliReuseProof.ProofBudget(default), null];
            var valid = SdkProfileTestData.Boolean("PackageSetAssets", arguments);
            cacheContract = arguments[8];
            return valid;
        }
        internal bool ValidateLock(Project owner, byte[] bytes) => SdkProfileTestData.Boolean("PackageSetLock", bytes, Payloads(owner), Contract(owner));

        internal Dictionary<string, byte[]> MalformedArchive(Package package, string mutation)
        {
            var files = new Dictionary<string, byte[]>(package.Files, StringComparer.OrdinalIgnoreCase);
            var bytes = (byte[])files[package.Archive].Clone();
            var end = bytes.Length - 22;
            var cursor = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
            while (cursor < end)
            {
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 28));
                var name = SdkProfileTestData.Utf8.GetString(bytes.AsSpan(cursor + 46, nameLength));
                if (name == "[Content_Types].xml")
                    break;
                cursor += 46 + nameLength;
            }
            Assert.True(cursor < end);
            var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 42));
            var expanded = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 24));
            if (mutation == "stored-length")
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(cursor + 10), 0);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(local + 8), 0);
                expanded = 0;
            }
            if (mutation == "expanded-budget")
                expanded = 64 * 1024 * 1024 + 1;
            if (mutation == "metadata-short")
                expanded++;
            if (mutation == "metadata-long")
                expanded--;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(cursor + 24), expanded);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(local + 22), expanded);
            if (mutation == "metadata-invalid-deflate")
            {
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 20));
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 26));
                bytes.AsSpan(local + 30 + nameLength, length).Fill(255);
            }
            if (mutation == "extra")
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(cursor + 30), 1);
            if (mutation == "descriptor")
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(cursor + 8), 8);
            BindArchive(files, package.Archive, bytes);
            return files;
        }
        internal Dictionary<string, byte[]> SignatureAliasArchive(Package package, string archiveName, string diskName)
        {
            var files = package.Files.Where(pair => pair.Key != package.Archive && pair.Key != package.Archive + ".sha512"
                    && pair.Key != ".nupkg.metadata")
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            files[diskName] = [1, 2, 3];
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (path, bytes) in files)
                {
                    using var output = zip.CreateEntry(path == diskName ? archiveName : path, CompressionLevel.Optimal).Open();
                    output.Write(bytes);
                }
            BindArchive(files, package.Archive, archive.ToArray());
            return files;
        }

        private static void BindArchive(Dictionary<string, byte[]> files, string name, byte[] bytes)
        {
            var hash = Convert.ToBase64String(SHA512.HashData(bytes));
            files[name] = bytes;
            files[name + ".sha512"] = SdkProfileTestData.Utf8.GetBytes(hash);
            files[".nupkg.metadata"] = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, contentHash = hash, source = "synthetic-unit-only" });
        }
    }
}
