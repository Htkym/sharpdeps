namespace SharpDeps.Cli;

using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

internal static partial class CliReuseProof
{
    private sealed record PackageSpec(string Project, string Id, string Version);

    private static bool PlainPackageReference(XElement item, string project, out PackageSpec? package)
    {
        package = null;
        var id = (string?)item.Attribute("Include");
        var version = (string?)item.Attribute("Version");
        if (item.Attributes().Count() != 2 || item.HasElements || !string.IsNullOrWhiteSpace(item.Value)
            || id is null || !Regex.IsMatch(id, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\z", RegexOptions.CultureInvariant)
            || version is null || !Regex.IsMatch(version, @"\A(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z", RegexOptions.CultureInvariant)) return false;
        package = new(project, id, version);
        return true;
    }

    private static bool CapturePackage(string root, string sdk, PackageSpec package, List<string> projects,
        List<string> imports, List<string> assets, ProofBudget budget, out (string Root, string Hash)? proof)
    {
        proof = null;
        if (!OperatingSystem.IsWindows() || Path.GetFileName(sdk) != "10.0.402"
            || Environment.GetEnvironmentVariable("NUGET_PACKAGES") is not { Length: > 0 } configured
            || !Path.IsPathFullyQualified(configured)) return false;
        foreach (var (path, expected) in new[]
        {
            ("Sdks/Microsoft.NET.Sdk/tools/net10.0/Microsoft.NET.Build.Tasks.dll", "a673d48e1aa2ede63547cf63cc94d59b90b361512e13210943dc70171e1c1ed1"),
            ("Sdks/Microsoft.NET.Sdk/targets/Microsoft.PackageDependencyResolution.targets", "cb0a2cf6ea590fbd3dfbf333d32fc7ee1db3344908ab04dc31288c5591c5136c")
        })
            if (Convert.ToHexString(SHA256.HashData(PackageBytes(Path.Combine(sdk, path), budget))).ToLowerInvariant() != expected) return false;
        var cache = CliPaths.Root(configured);
        var directory = CliPaths.Root(CliPaths.InRoot(cache, package.Id.ToLowerInvariant() + "/" + package.Version));
        // NuGet SourceRoot must not introduce an untracked Git repository above the cache.
        for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
            if (Exists(Path.Combine(ancestor.FullName, ".git"))) return false;
        var expectedAssets = projects.Select(p => Path.Combine(Path.GetDirectoryName(p)!, "obj", "project.assets.json")).ToHashSet(PathComparer);
        if (!expectedAssets.SetEquals(assets)) return false;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var first = Inventory();
        var second = Inventory();
        if (first is null || first != second || elapsed.ElapsedMilliseconds >= 5000) return false;
        proof = (directory, "package-input/2:" + first);
        return true;

        string? Inventory()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(hash, "one-package/2"); Append(hash, directory);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            var directories = new Stack<string>(); directories.Push(directory);
            while (directories.TryPop(out var current))
            {
                CliPaths.RejectLinks(current);
                foreach (var path in Entries(current, budget))
                {
                    if (elapsed.ElapsedMilliseconds >= 5000 || files.Count >= 2048) return null;
                    var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                    var attributes = Attributes(path);
                    if ((attributes & FileAttributes.Device) != 0) return null;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var parts = relative.Split('/');
                        if (parts.Length > 2 || parts[0] is not ("lib" or "ref")) return null;
                        directories.Push(path); continue;
                    }
                    if (!PackageFile(relative, package)) return null;
                    var bytes = PackageBytes(path, budget);
                    total += bytes.Length;
                    if (total > 64L * 1024 * 1024 || !files.TryAdd(relative, bytes)) return null;
                }
            }
            if (!ValidatePackageArchive(files, package, budget, out var contentHash)) return null;
            foreach (var (name, bytes) in files.Where(p => p.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                using var image = new PEReader(new MemoryStream(bytes));
                if (!image.HasMetadata || image.GetMetadataReader().AssemblyFiles.Any()) return null;
            }
            foreach (var (name, bytes) in files.OrderBy(p => p.Key, StringComparer.Ordinal))
            { Append(hash, name); Append(hash, Convert.ToHexString(SHA256.HashData(bytes))); }
            foreach (var path in imports.Order(StringComparer.Ordinal))
            {
                var bytes = PackageBytes(path, budget);
                using var xml = PackageXml(bytes);
                var document = XDocument.Load(xml);
                foreach (var item in document.Descendants())
                {
                    if (item.Name.LocalName is "NuGetPackageRoot" or "NuGetPackageFolders"
                        && !PackagePathMatches(item.Value, cache)) return null;
                    if (item.Name.LocalName == "SourceRoot" && !PackagePathMatches((string?)item.Attribute("Include"), cache)) return null;
                }
                AddInput(path, bytes);
            }
            foreach (var path in assets.Order(StringComparer.Ordinal))
            {
                var bytes = PackageBytes(path, budget);
                if (!PackageAssets(bytes, path, root, sdk, cache, package, files, contentHash, budget, out var hasPackage)) return null;
                AddInput(path, bytes);
                var projectDirectory = Directory.GetParent(Path.GetDirectoryName(path)!)!.FullName;
                var lockPath = Path.Combine(projectDirectory, "packages.lock.json");
                var lockBytes = PackageBytes(lockPath, budget);
                var owner = projects.Single(p => CliPaths.SamePath(Path.GetDirectoryName(p), projectDirectory));
                if (!PackageLock(lockBytes, package, contentHash, hasPackage, CliPaths.SamePath(owner, package.Project))) return null;
                AddInput(lockPath, lockBytes);
                foreach (var candidate in CacheInputs(Path.GetDirectoryName(path)!))
                {
                    var data = PackageBytes(candidate, budget);
                    if (!PackageAssetsCache(data, cache, directory, package, files)) return null;
                    AddInput(candidate, data);
                    // The fixed SDK chooses disk cache only when it is newer than assets.
                    // Equal bytes with the opposite choice are different compiler input proofs.
                    CliPaths.RejectLinks(candidate); CliPaths.RejectLinks(path);
                    using var cacheInput = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var assetsInput = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    Append(hash, "sdk-cache-newer-than-assets");
                    Append(hash, (File.GetLastWriteTimeUtc(cacheInput.SafeFileHandle)
                        > File.GetLastWriteTimeUtc(assetsInput.SafeFileHandle)).ToString());
                }
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

            void AddInput(string path, byte[] bytes)
            { Append(hash, path); Append(hash, Convert.ToHexString(SHA256.HashData(bytes))); }

            IEnumerable<string> CacheInputs(string obj)
            {
                var pending = new Stack<string>(); pending.Push(obj);
                while (pending.TryPop(out var current))
                {
                    CliPaths.RejectLinks(current);
                    foreach (var entry in Entries(current, budget))
                    {
                        if (elapsed.ElapsedMilliseconds >= 5000) throw new IOException("Package proof time budget exceeded.");
                        var attributes = Attributes(entry);
                        if ((attributes & FileAttributes.Device) != 0) throw new IOException("Package cache input is a device.");
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                        else if (entry.EndsWith(".assets.cache", StringComparison.OrdinalIgnoreCase)) yield return entry;
                    }
                }
            }
        }
    }

    private static byte[] PackageBytes(string path, ProofBudget budget)
    {
        CliPaths.RejectLinks(path);
        if ((Attributes(path) & (FileAttributes.Directory | FileAttributes.Device)) != 0) throw new IOException("Package input is not a file.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 64L * 1024 * 1024) throw new IOException("Package proof byte budget exceeded.");
        budget.Bytes(input.Length);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new IOException("Package input changed.");
        budget.Check(); return bytes;
    }

    private static XmlReader PackageXml(byte[] bytes) => XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings
    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024, CloseInput = true });

    private static bool PackagePathMatches(string? left, string? right) => left is not null && right is not null
        && Path.IsPathFullyQualified(left) && Path.IsPathFullyQualified(right)
        && CliPaths.SamePath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));

    private static bool PackageFile(string path, PackageSpec package)
    {
        var parts = path.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.Any(c => c is '\\' or ':' || char.IsControl(c)))) return false;
        if (parts.Length == 3 && parts[0] is "lib" or "ref")
            return Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".xml" or ".pdb";
        var name = path.ToLowerInvariant();
        var id = package.Id.ToLowerInvariant();
        return parts.Length == 1 && (name is ".nupkg.metadata" or ".signature.p7s" or "license" or "license.md" or "license.txt"
            or "readme.md" or "readme.txt" or "packageicon.png" or "icon.png"
            || name == id + ".nuspec" || name == id + "." + package.Version + ".nupkg"
            || name == id + "." + package.Version + ".nupkg.sha512");
    }

    private static bool ValidatePackageArchive(Dictionary<string, byte[]> files, PackageSpec package, ProofBudget budget, out string contentHash)
    {
        contentHash = "";
        var name = package.Id.ToLowerInvariant() + "." + package.Version + ".nupkg";
        if (!files.TryGetValue(name, out var archive) || !files.TryGetValue(name + ".sha512", out var marker)
            || !files.TryGetValue(".nupkg.metadata", out var metadata)) return false;
        using var meta = JsonDocument.Parse(metadata);
        // NuGet metadata v2 contentHash is not the signed archive's whole-file SHA512.
        // Bind it independently to assets/lock, and verify raw archive bytes with the archive marker.
        if (meta.RootElement.GetProperty("version").GetInt32() != 2
            || meta.RootElement.GetProperty("contentHash").GetString() is not { } recorded
            || Convert.FromBase64String(recorded).Length != 64
            || StrictUtf8.GetString(marker).Trim() != Convert.ToBase64String(SHA512.HashData(archive))) return false;
        contentHash = recorded;
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            if (names.Count >= 2048 || !names.Add(entry.FullName)) return false;
            if (entry.FullName is "[Content_Types].xml" or "_rels/.rels"
                || entry.FullName.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal)) continue;
            if (!PackageFile(entry.FullName, package) || !files.TryGetValue(entry.FullName, out var bytes)
                || entry.Length != bytes.Length || (expanded += entry.Length) > 64L * 1024 * 1024) return false;
            budget.Bytes(entry.Length);
            using var input = entry.Open();
            if (!SHA256.HashData(input).SequenceEqual(SHA256.HashData(bytes))) return false;
        }
        return files.Keys.All(p => p == name || p == name + ".sha512" || p == ".nupkg.metadata" || names.Contains(p));
    }

    private static bool PackageAssets(byte[] bytes, string path, string root, string sdk, string cache,
        PackageSpec package, Dictionary<string, byte[]> files, string contentHash, ProofBudget budget, out bool hasPackage)
    {
        hasPackage = false;
        var node = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 64 })!.AsObject();
        var libraries = node["libraries"]!.AsObject(); var target = node["targets"]!["net10.0"]!.AsObject();
        if (node["targets"]!.AsObject().Count != 1 || node["packageFolders"]!.AsObject().Count != 1
            || !PackagePathMatches(node["packageFolders"]!.AsObject().Single().Key, cache)
            || !PackagePathMatches(node["project"]!["restore"]!["packagesPath"]!.GetValue<string>(), cache)
            || node["project"]!["restore"]!["fallbackFolders"] is not null) return false;
        // Restore's projectPath is authoritative; it must be this conventional obj directory's owner.
        var owner = node["project"]!["restore"]!["projectPath"]!.GetValue<string>();
        if (!CliPaths.SamePath(Path.GetDirectoryName(owner), Directory.GetParent(Path.GetDirectoryName(path)!)!.FullName)
            || !File.Exists(CliPaths.InRoot(root, owner))) return false;
        var key = package.Id + "/" + package.Version;
        var packageLibraries = libraries.Where(p => p.Value?["type"]?.GetValue<string>() == "package").ToArray();
        hasPackage = packageLibraries.Length == 1;
        if (packageLibraries.Length > 1) return false;
        if (packageLibraries.Length == 1)
        {
            if (packageLibraries[0].Key != key || target[key] is not JsonObject selected || libraries[key] is not JsonObject library
                || library.Any(p => p.Key is not ("type" or "path" or "sha512" or "files"))
                || library["path"]!.GetValue<string>() != package.Id.ToLowerInvariant() + "/" + package.Version
                || library["sha512"]!.GetValue<string>() != contentHash
                || !files.Keys.Where(p => !p.EndsWith(".nupkg", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(library["files"]!.AsArray().Select(p => p!.GetValue<string>()))
                || selected.Any(p => p.Key is not ("type" or "compile" or "runtime"))
                || selected["type"]!.GetValue<string>() != "package") return false;
            foreach (var kind in new[] { "compile", "runtime" })
            {
                if (selected[kind] is not JsonObject references || references.Count == 0) return false;
                foreach (var (relative, metadata) in references)
                    if (!files.ContainsKey(relative) || !relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        || metadata is not JsonObject fields || fields.Any(p => p.Key != "related" || p.Value?.GetValue<string>() is not (".xml" or ".pdb" or ".pdb;.xml"))) return false;
            }
            libraries.Remove(key); target.Remove(key);
        }
        var framework = node["project"]!["frameworks"]!["net10.0"]!.AsObject();
        if (framework["dependencies"] is JsonObject dependencies && dependencies.Count > 0)
        {
            if (!CliPaths.SamePath(owner, package.Project) || packageLibraries.Length != 1 || dependencies.Count != 1
                || dependencies[package.Id] is not JsonObject dependency
                || dependency.Any(p => p.Key is not ("target" or "version"))
                || dependency["target"]!.GetValue<string>() != "Package"
                || dependency["version"]!.GetValue<string>() != "[" + package.Version + ", )") return false;
            dependencies.Clear();
        }
        else if (CliPaths.SamePath(owner, package.Project)) return false;
        using var document = JsonDocument.Parse(node.ToJsonString());
        return PlainAssetDocument(document.RootElement, path, root, sdk, budget);
    }

    private static bool PackageLock(byte[] bytes, PackageSpec package, string contentHash, bool hasPackage, bool direct)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        var value = document.RootElement;
        if (value.GetProperty("version").GetInt32() != 1 || value.GetProperty("dependencies").EnumerateObject().Count() != 1) return false;
        var seen = false;
        foreach (var item in value.GetProperty("dependencies").GetProperty("net10.0").EnumerateObject())
        {
            var type = item.Value.GetProperty("type").GetString();
            if (type == "Project") continue;
            if (seen || item.Name != package.Id || type != (direct ? "Direct" : "Transitive")
                || item.Value.GetProperty("resolved").GetString() != package.Version
                || item.Value.GetProperty("contentHash").GetString() != contentHash
                || item.Value.TryGetProperty("dependencies", out var dependencies) && dependencies.EnumerateObject().Any()
                || direct && item.Value.GetProperty("requested").GetString() != "[" + package.Version + ", )") return false;
            seen = true;
        }
        return seen == hasPackage && (!direct || seen);
    }

    private static bool PackageAssetsCache(byte[] bytes, string cache, string packageRoot,
        PackageSpec package, Dictionary<string, byte[]> files)
    {
        // Fixed SDK ResolvePackageAssets cache v12: 17 ordered item groups, then a metadata table.
        // Validate even stale caches; never let an opaque cache introduce analyzers or external inputs.
        if (bytes.Length > 4 * 1024 * 1024) return false;
        using var reader = new BinaryReader(new MemoryStream(bytes), StrictUtf8);
        if (reader.ReadInt32() != 0x41474b50 || reader.ReadInt32() != 12 || reader.ReadBytes(32).Length != 32) return false;
        var table = reader.ReadInt32(); var items = reader.BaseStream.Position;
        if (table < items || table >= bytes.Length) return false;
        reader.BaseStream.Position = table;
        var count = reader.ReadInt32(); if (count is < 0 or > 4096) return false;
        var strings = new string[count];
        for (var i = 0; i < count; i++) { strings[i] = reader.ReadString(); if (strings[i].Length > 16384) return false; }
        if (reader.BaseStream.Position != bytes.Length) return false;
        reader.BaseStream.Position = items;
        for (var group = 0; group < 17; group++)
        {
            var length = reader.ReadInt32(); if (length is < 0 or > 1024) return false;
            if (group is not (2 or 4 or 8 or 9 or 10 or 11 or 13) && length != 0) return false;
            for (var i = 0; i < length; i++)
            {
                var item = reader.ReadString(); if (item.Length > 16384) return false;
                if (group is 2 or 4 or 11 or 13)
                {
                    if (!Path.IsPathFullyQualified(item)) return false;
                    var relative = Path.GetRelativePath(packageRoot, item).Replace('\\', '/');
                    var extension = group == 4 ? ".pdb" : group == 11 ? ".xml" : ".dll";
                    if (!files.ContainsKey(relative) || !item.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return false;
                }
                if (group == 10 && !PackagePathMatches(item, cache)) return false;
                if (group == 8 && item != package.Id || group == 9 && item != package.Id + "/" + package.Version) return false;
                var metadata = reader.ReadInt32(); if (metadata is < 0 or > 128) return false;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var j = 0; j < metadata; j++)
                {
                    var key = reader.ReadInt32(); var value = reader.ReadInt32();
                    if ((uint)key >= strings.Length || (uint)value >= strings.Length) return false;
                    var name = strings[key]; var text = strings[value];
                    if (!seen.Add(name) || !(group switch
                    {
                        2 or 4 or 11 or 13 => name switch
                        {
                            "NuGetPackageId" => text == package.Id,
                            "NuGetPackageVersion" => text == package.Version,
                            "PathInPackage" when group is 2 or 13 => text == Path.GetRelativePath(packageRoot, item).Replace('\\', '/'),
                            "AssetType" when group == 13 => text == "runtime",
                            "CopyLocal" when group == 13 => text == "true",
                            "DestinationSubPath" when group == 13 => text == Path.GetFileName(item),
                            _ => false
                        },
                        9 => name switch
                        {
                            "Name" => text == package.Id,
                            "Version" => text == package.Version,
                            "IsImplicitlyDefined" => text == "False",
                            "Resolved" => text == "True",
                            "Path" => CliPaths.SamePath(text, packageRoot),
                            _ => false
                        },
                        _ => false
                    })) return false;
                }
            }
        }
        return reader.BaseStream.Position == table;
    }
}
