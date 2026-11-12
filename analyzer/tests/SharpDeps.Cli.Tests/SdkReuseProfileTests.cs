namespace SharpDeps.Cli.Tests;

using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

[Collection("markdown-index")]
public sealed class SdkReuseProfileTests
{
    [Theory]
    [InlineData("10.0.402", "a673d48e1aa2ede63547cf63cc94d59b90b361512e13210943dc70171e1c1ed1", "a68655c3c976a8837897f9bae8308069691e3477922d60cc92636a1483fb8ecb")]
    [InlineData("10.0.403", "cbb0248c51684837e1d76ba3b9984420fc35817290d0289ff2d9fd48711142f5", "34e7a0fe3858c8a723122e8d128af9b1f33247b6c42aa3dabc55bbff86ecf8f9")]
    public void ExactSdkDescriptorsRetainEveryPayloadPin(string version, string packageTask, string gitTask)
    {
        var package = ((string Path, string Hash)[])SdkProfileTestData.Invoke("PackageSdkPayloads", version)!;
        var git = ((string Path, string Hash)[])SdkProfileTestData.Invoke("GitSdkPayloads", version)!;
        Assert.Equal(new[]
        {
            ("Sdks/Microsoft.NET.Sdk/tools/net10.0/Microsoft.NET.Build.Tasks.dll", packageTask),
            ("Sdks/Microsoft.NET.Sdk/targets/Microsoft.PackageDependencyResolution.targets", "cb0a2cf6ea590fbd3dfbf333d32fc7ee1db3344908ab04dc31288c5591c5136c")
        }, package);
        Assert.Equal(new[]
        {
            ("tools/net/Microsoft.Build.Tasks.Git.dll", gitTask),
            ("build/Microsoft.Build.Tasks.Git.targets", "a3499b2ed15095be71667a690d86e4a18abb7889f224f52c7bc5459c8479ad34"),
            ("build/Microsoft.Build.Tasks.Git.props", "19a1134fa4c2825c016df1b8c42e375f0c744e9b1a2262222c2ac101e74f0544")
        }, git);
    }

    [Theory]
    [InlineData("10.0.401")]
    [InlineData("10.0.404")]
    [InlineData("10.0.403-servicing.26523.112")]
    [InlineData("10.0.403/other")]
    [InlineData("11.0.100")]
    [InlineData("")]
    public void UnknownSdkVersionsHaveNoPackageOrGitDescriptor(string version)
    {
        Assert.Null(SdkProfileTestData.Invoke("PackageSdkPayloads", version));
        Assert.Null(SdkProfileTestData.Invoke("GitSdkPayloads", version));
    }

    [Fact]
    public void OracleSdkRequestsCannotSelectAnUnauditedVersion()
    {
        var root = SdkProfileTestData.NewRoot("sharpdeps-oracle-sdk-request");
        var options = new CliIndexOptions(root, Path.Combine(root, "App.csproj"), Path.Combine(root, "index.sqlite"), Guid.NewGuid());
        var run = new OracleRun(options, null, []) { ExactSdkVersion = "10.0.404" };
        var method = typeof(OracleProcess).GetMethod("RegisterExactOracleSdk", BindingFlags.Static | BindingFlags.NonPublic)!;
        var failure = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [run]));
        Assert.StartsWith("oracle-sdk-request-unsupported:", Assert.IsType<InvalidDataException>(failure.InnerException).Message);
        Assert.False(File.Exists(Path.Combine(root, "global.json")));
    }

    [WindowsSdk403Fact]
    public void OracleSdkRequestsCannotOverwriteAnExistingPin()
    {
        var root = SdkProfileTestData.NewRoot("sharpdeps-oracle-sdk-pin-collision");
        var options = new CliIndexOptions(root, Path.Combine(root, "App.csproj"), Path.Combine(root, "index.sqlite"), Guid.NewGuid());
        var run = new OracleRun(options, null, []) { ExactSdkVersion = "10.0.403" };
        var method = typeof(OracleProcess).GetMethod("RegisterExactOracleSdk", BindingFlags.Static | BindingFlags.NonPublic)!;
        const string existing = "{\"sdk\":{\"version\":\"preserve-existing\"}}";
        SdkProfileTestData.Write(root, "global.json", existing);
        var collision = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [run]));
        Assert.IsAssignableFrom<IOException>(collision.InnerException);
        Assert.Equal(existing, File.ReadAllText(Path.Combine(root, "global.json")));
    }

    [Fact]
    public void ExperimentalNuGetMacroEnvironmentCannotEnterTheProof()
    {
        var environment = new Hashtable(StringComparer.OrdinalIgnoreCase) { ["APPDATA"] = Path.GetTempPath() };
        Assert.NotNull(SdkProfileTestData.Invoke("NormalizeEnvironment", environment, SdkProfileTestData.InstalledSdk("10.0.403")));
        environment["NUGET_ENABLE_EXPERIMENTAL_MACROS"] = "true";
        Assert.Null(SdkProfileTestData.Invoke("NormalizeEnvironment", environment, SdkProfileTestData.InstalledSdk("10.0.403")));
    }

    [Theory]
    [InlineData("[include]\npath = ../outside.config\n")]
    [InlineData("[core]\nrepositoryformatversion = 0\nbare = false\nexcludesfile = ../outside.ignore\n")]
    [InlineData("[core]\nrepositoryformatversion = 0\nbare = false\nworktree = ../other\n")]
    [InlineData("[core]\nrepositoryformatversion = 1\n[extensions]\nrefStorage = reftable\n")]
    public void ExternalOrUnsupportedGitConfigurationIsRejected(string config)
        => Assert.False(SdkProfileTestData.Boolean("PlainGitConfig", config));

    [Theory]
    [InlineData("ref: ../outside")]
    [InlineData("ref: refs/heads/../../outside")]
    [InlineData("ref: refs/heads/main.lock")]
    [InlineData("ref: C:/outside")]
    public void GitHeadCannotRedirectTheReferenceReadOutsideInventory(string head)
        => Assert.False(SdkProfileTestData.Boolean("PlainGitRef", head));

    [Fact]
    public void GitInventoryBindsRefsAndExcludeBytesAndRejectsAlternateLayouts()
    {
        var root = SdkProfileTestData.NewRoot("sharpdeps-git-profile");
        SdkProfileTestData.Git(root);
        string? Inventory() => (string?)SdkProfileTestData.Invoke("GitInventory", Path.Combine(root, ".git"),
            new CliReuseProof.ProofBudget(default), true);
        var first = Inventory();
        Assert.NotNull(first);
        var head = Path.Combine(root, ".git", "refs", "heads", "main");
        var stamp = File.GetLastWriteTimeUtc(head);
        SdkProfileTestData.Write(root, ".git/refs/heads/main", new string('2', 40) + "\n");
        File.SetLastWriteTimeUtc(head, stamp);
        Assert.NotEqual(first, Inventory());
        SdkProfileTestData.Write(root, ".git/info/exclude", "*.generated.cs\n");
        var exclude = Inventory();
        Assert.NotNull(exclude);
        SdkProfileTestData.Write(root, ".git/info/exclude", "*.ignored.cs\n");
        Assert.NotEqual(exclude, Inventory());
        foreach (var path in new[] { ".git/commondir", ".git/objects/info/alternates", ".git/refs/heads/main.lock" })
        {
            SdkProfileTestData.Write(root, path, "../outside\n");
            Assert.Null(Inventory());
            File.Delete(CliPaths.InRoot(root, path));
        }
        SdkProfileTestData.Write(root, ".git/refs/heads/main", "ref: refs/heads/other\n");
        Assert.Null(Inventory());
    }

    [Fact]
    public async Task IgnoreMembershipAndSameTimestampEditsRemainInTheInputFingerprint()
    {
        var root = SdkProfileTestData.NewRoot("sharpdeps-ignore-profile");
        var index = Path.Combine(root, ".sharpdeps", "index.sqlite");
        async Task<string?> Fingerprint() => await (Task<string?>)typeof(CliIndexer).GetMethod("FingerprintInputs",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [root, index, true, default(CancellationToken), false, null])!;
        var previous = await Fingerprint();
        Assert.NotNull(previous);
        foreach (var path in new[] { ".gitignore", "src/.gitignore", "obj/Debug/net10.0/.gitignore", "bin/.gitignore" })
        {
            SdkProfileTestData.Write(root, path, "old-pattern\n");
            var added = await Fingerprint();
            Assert.NotEqual(previous, added);
            var absolute = CliPaths.InRoot(root, path);
            var stamp = File.GetLastWriteTimeUtc(absolute);
            SdkProfileTestData.Write(root, path, "new-pattern\n");
            File.SetLastWriteTimeUtc(absolute, stamp);
            var changed = await Fingerprint();
            Assert.NotEqual(added, changed);
            File.Delete(absolute);
            previous = await Fingerprint();
            Assert.NotEqual(changed, previous);
        }
    }

    [Fact]
    public void PackageArchiveMetadataAssetsAndLockMustAgree()
    {
        var root = SdkProfileTestData.NewRoot("sharpdeps-package-profile");
        var project = Path.Combine(root, "App.csproj");
        SdkProfileTestData.Write(root, "App.csproj", SdkProfileTestData.Project);
        var cache = Path.Combine(root, "packages");
        var assetsPath = Path.Combine(root, "obj", "project.assets.json");
        var package = SdkProfileTestData.Package(project);
        var files = SdkProfileTestData.PackageFiles();
        var hash = SdkProfileTestData.ContentHash(files);
        bool Archive(Dictionary<string, byte[]> values) => SdkProfileTestData.Boolean("ValidatePackageArchive", values,
            package, new CliReuseProof.ProofBudget(default), null);
        bool Assets(byte[] bytes) => SdkProfileTestData.Boolean("PackageAssets", bytes, assetsPath, root,
            SdkProfileTestData.InstalledSdk("10.0.403"), cache, package, files, hash, new CliReuseProof.ProofBudget(default), false);
        bool Lock(byte[] bytes) => SdkProfileTestData.Boolean("PackageLock", bytes, package, hash, true, true);
        Assert.True(Archive(files));
        var assets = SdkProfileTestData.Assets(project, cache, files);
        var locked = SdkProfileTestData.Lock(files);
        Assert.True(Assets(assets));
        Assert.True(Lock(locked));
        foreach (var name in new[] { SdkProfileTestData.Dll, "proof.package.1.0.0.nupkg.sha512", ".nupkg.metadata" })
        {
            var changed = files.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            changed[name] = name == ".nupkg.metadata" ? SdkProfileTestData.Utf8.GetBytes("{\"version\":1,\"contentHash\":\"invalid\"}") : [1, 2, 3];
            Assert.False(Archive(changed));
        }
        Assert.False(Assets(SdkProfileTestData.ChangeJson(assets, value => value["targets"]!["net10.0"]![SdkProfileTestData.Key]!["analyzers"] = new JsonObject())));
        Assert.False(Assets(SdkProfileTestData.ChangeJson(assets, value => value["project"]!["restore"]!["fallbackFolders"] = new JsonArray(cache))));
        Assert.False(Assets(SdkProfileTestData.ChangeJson(assets, value => value["libraries"]![SdkProfileTestData.Key]!["sha512"] = Convert.ToBase64String(new byte[64]))));
        Assert.False(Lock(SdkProfileTestData.ChangeJson(locked, value => value["dependencies"]!["net10.0"]![SdkProfileTestData.Id]!["contentHash"] = Convert.ToBase64String(new byte[64]))));
        Assert.False(Lock(SdkProfileTestData.ChangeJson(locked, value => value["dependencies"]!["net10.0"]!["Another.Package"] = new JsonObject { ["type"] = "Transitive" })));
    }

    [Fact]
    public void PackageCacheRejectsGeneratorsExternalPathsAndUnknownMetadataEvenWhenStale()
    {
        var root = SdkProfileTestData.NewRoot("sharpdeps-package-cache");
        var cache = Path.Combine(root, "packages");
        var packageRoot = Path.Combine(cache, "proof.package", "1.0.0");
        var files = SdkProfileTestData.PackageFiles();
        var package = SdkProfileTestData.Package(Path.Combine(root, "App.csproj"));
        var dll = Path.Combine(packageRoot, SdkProfileTestData.Dll.Replace('/', Path.DirectorySeparatorChar));
        bool Valid(byte[] bytes) => SdkProfileTestData.Boolean("PackageAssetsCache", bytes, cache, packageRoot, package, files);
        var good = SdkProfileTestData.Cache(2, dll, ("NuGetPackageId", SdkProfileTestData.Id), ("NuGetPackageVersion", "1.0.0"));
        Assert.True(Valid(good));
        Assert.False(Valid(good[..^1]));
        Assert.False(Valid(SdkProfileTestData.Cache(0, dll)));
        Assert.False(Valid(SdkProfileTestData.Cache(2, Path.Combine(root, "outside.dll"))));
        Assert.False(Valid(SdkProfileTestData.Cache(2, dll, ("Generator", "true"))));
        Assert.False(Valid(SdkProfileTestData.Cache(2, dll, ("NuGetPackageId", "Other.Package"))));
        Assert.False(Valid(SdkProfileTestData.Cache(2, dll, ("NuGetPackageId", SdkProfileTestData.Id), ("NuGetPackageId", SdkProfileTestData.Id))));
        var changedVersion = (byte[])good.Clone();
        changedVersion[4] = 13;
        Assert.False(Valid(changedVersion));
    }

    [Fact]
    public void PackageProjectsDoNotExpandToCpmGeneratorsImportsOrMultipleDirectReferences()
    {
        var root = SdkProfileTestData.NewRoot("sharpdeps-package-shape");
        var path = Path.Combine(root, "App.csproj");
        bool Project(string text)
        {
            SdkProfileTestData.Write(root, "App.csproj", text);
            return SdkProfileTestData.Boolean("PlainProject", path, root, new CliReuseProof.ProofBudget(default), false, null, false);
        }
        Assert.True(Project(SdkProfileTestData.Project));
        foreach (var project in new[]
        {
            SdkProfileTestData.Project.Replace("Version=\"1.0.0\"", "Version=\"[1.0.0,2.0.0)\""),
            SdkProfileTestData.Project.Replace("Version=\"1.0.0\"", "Version=\"1.0.0\" PrivateAssets=\"all\""),
            SdkProfileTestData.Project.Replace("<TargetFramework>", "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally><TargetFramework>"),
            SdkProfileTestData.Project.Replace("</ItemGroup>", "<Analyzer Include=\"Generator.dll\" /></ItemGroup>"),
            SdkProfileTestData.Project.Replace("</ItemGroup>", "<AdditionalFiles Include=\"input.json\" /></ItemGroup>"),
            SdkProfileTestData.Project.Replace("</Project>", "<Import Project=\"Other.targets\" /></Project>"),
            SdkProfileTestData.Project.Replace("</ItemGroup>", "<PackageReference Include=\"Other.Package\" Version=\"1.0.0\" /></ItemGroup>")
        })
            Assert.False(Project(project), project);
        Assert.True(Project(SdkProfileTestData.Project));
        SdkProfileTestData.Write(root, "Other.csproj", SdkProfileTestData.Project);
        Assert.False(SdkProfileTestData.Boolean("PlainProjects", root, SdkProfileTestData.InstalledSdk("10.0.403"),
            new CliReuseProof.ProofBudget(default), false, false, null));
    }

    [Fact]
    public void PackedRefsAcceptOnlyContainedNamesAndObjectIds()
    {
        const string header = "# pack-refs with: peeled fully-peeled sorted\n";
        var sha = new string('1', 40);
        Assert.True(SdkProfileTestData.Boolean("PlainPackedRefs", header + sha + " refs/heads/main\n"));
        foreach (var line in new[] { sha + " ../outside", sha + " refs/../outside", sha + " C:/outside", "not-a-sha refs/heads/main" })
            Assert.False(SdkProfileTestData.Boolean("PlainPackedRefs", header + line + "\n"));
    }

    [WindowsSdk403Fact]
    public void RealPackagePayloadPinsAndCacheTimeChoiceStayFailClosed()
    {
        var parent = SdkProfileTestData.NewRoot("sharpdeps-package-capture");
        var root = Path.Combine(parent, "workspace");
        Directory.CreateDirectory(root);
        var sdk = Path.Combine(parent, "sdk", "10.0.403");
        var payloads = ((string Path, string Hash)[])SdkProfileTestData.Invoke("PackageSdkPayloads", "10.0.403")!;
        foreach (var (path, _) in payloads)
        {
            var target = Path.Combine(sdk, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(SdkProfileTestData.InstalledSdk("10.0.403"), path), target);
        }
        var project = Path.Combine(root, "App.csproj");
        SdkProfileTestData.Write(root, "App.csproj", SdkProfileTestData.Project);
        var cache = Path.Combine(parent, "packages");
        var packageRoot = Path.Combine(cache, "proof.package", "1.0.0");
        var files = SdkProfileTestData.PackageFiles();
        foreach (var (path, bytes) in files)
        {
            var target = CliPaths.InRoot(packageRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
        var assets = Path.Combine(root, "obj", "project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
        File.WriteAllBytes(assets, SdkProfileTestData.Assets(project, cache, files));
        File.WriteAllBytes(Path.Combine(root, "packages.lock.json"), SdkProfileTestData.Lock(files));
        var cacheFile = Path.Combine(root, "obj", "App.assets.cache");
        File.WriteAllBytes(cacheFile, SdkProfileTestData.Cache(2, Path.Combine(packageRoot, SdkProfileTestData.Dll.Replace('/', Path.DirectorySeparatorChar))));
        var package = SdkProfileTestData.Package(project);
        string? Capture(string? selectedSdk = null)
        {
            object?[] arguments = [root, selectedSdk ?? sdk, package, new List<string> { project }, new List<string>(),
                new List<string> { assets }, new CliReuseProof.ProofBudget(default), null];
            try
            {
                return (bool)SdkProfileTestData.Invoke("CapturePackage", arguments)!
                    ? ((ValueTuple<string, string>)arguments[7]!).Item2 : null;
            }
            catch (TargetInvocationException error) when (error.InnerException is IOException or System.Text.Json.JsonException
                or FormatException or InvalidOperationException or KeyNotFoundException or CliFailure)
            {
                return null;
            }
        }
        var previousEnvironment = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", cache);
            var stamp = DateTime.UtcNow.AddDays(-2);
            File.SetLastWriteTimeUtc(assets, stamp);
            File.SetLastWriteTimeUtc(cacheFile, stamp.AddHours(1));
            var newer = Capture();
            Assert.NotNull(newer);
            Assert.Equal(newer, Capture());
            File.SetLastWriteTimeUtc(cacheFile, stamp.AddHours(-1));
            var older = Capture();
            Assert.NotNull(older);
            Assert.NotEqual(newer, older);
            File.SetLastWriteTimeUtc(cacheFile, stamp);
            Assert.Equal(older, Capture());
            Assert.Null(Capture(Path.Combine(parent, "sdk", "10.0.404")));
            foreach (var (path, _) in payloads)
            {
                var target = Path.Combine(sdk, path);
                var original = File.ReadAllBytes(target);
                var changed = (byte[])original.Clone();
                changed[0] ^= 1;
                try
                {
                    File.WriteAllBytes(target, changed);
                    Assert.Null(Capture());
                }
                finally
                {
                    File.WriteAllBytes(target, original);
                }
            }
            Assert.Equal(older, Capture());
            var metadata = Path.Combine(packageRoot, ".nupkg.metadata");
            var saved = File.ReadAllBytes(metadata);
            try
            {
                File.WriteAllText(metadata, "{}", SdkProfileTestData.Utf8);
                Assert.Null(Capture());
            }
            finally
            {
                File.WriteAllBytes(metadata, saved);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previousEnvironment);
        }
    }

    [WindowsSdk403Fact]
    public void RealGitPayloadPinsRejectEveryModifiedProviderAndUnknownSdk()
    {
        var parent = SdkProfileTestData.NewRoot("sharpdeps-git-capture");
        var root = Path.Combine(parent, "workspace");
        Directory.CreateDirectory(root);
        SdkProfileTestData.Git(root);
        var sdk = Path.Combine(parent, "sdk", "10.0.403");
        var provider = Path.Combine("Sdks", "Microsoft.Build.Tasks.Git");
        var payloads = ((string Path, string Hash)[])SdkProfileTestData.Invoke("GitSdkPayloads", "10.0.403")!;
        foreach (var (path, _) in payloads)
        {
            var target = Path.Combine(sdk, provider, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(SdkProfileTestData.InstalledSdk("10.0.403"), provider, path), target);
        }
        object? Capture(string? selectedSdk = null) => SdkProfileTestData.Invoke("CaptureGit", root, selectedSdk ?? sdk,
            new CliReuseProof.ProofBudget(default));
        Assert.NotNull(Capture());
        Assert.Null(Capture(Path.Combine(parent, "sdk", "10.0.404")));
        foreach (var (path, _) in payloads)
        {
            var target = Path.Combine(sdk, provider, path);
            var original = File.ReadAllBytes(target);
            var changed = (byte[])original.Clone();
            changed[0] ^= 1;
            try
            {
                File.WriteAllBytes(target, changed);
                Assert.Null(Capture());
            }
            finally
            {
                File.WriteAllBytes(target, original);
            }
        }
        Assert.NotNull(Capture());
        var linked = Path.Combine(parent, "linked");
        Directory.CreateDirectory(linked);
        SdkProfileTestData.Write(linked, ".git", "gitdir: ../workspace/.git\n");
        Assert.Null(SdkProfileTestData.Invoke("CaptureGit", linked, sdk, new CliReuseProof.ProofBudget(default)));
        SdkProfileTestData.Write(parent, ".git", "gitdir: elsewhere\n");
        Assert.Null(Capture());
    }
}
