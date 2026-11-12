namespace SharpDeps.Cli;

using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using SharpDeps.Analysis.Core.Diagnostics;

internal static partial class CliReuseProof
{
    private const int MaximumPackageSetIdentities = 8;
    private const int MaximumPackageSetDirectReferences = 32;
    private const int MaximumPackageSetProjects = 128;
    private sealed record PackageClosure(string? Root, string Hash, string? RootSet = null);
    private sealed record PackagePayload(PackageSpec Spec, string Root, Dictionary<string, byte[]> Files, string ContentHash);

    private static long PackageSetFileMaximum(string relative)
        => relative.Equals(".nupkg.metadata", StringComparison.OrdinalIgnoreCase) ? 4096L
            : relative.EndsWith(".nupkg.sha512", StringComparison.OrdinalIgnoreCase) ? 128L : 64L * 1024 * 1024;

    private static bool PackageSetCounts(int identities, int declarations, int owners, int edges)
        => identities is >= 1 and <= MaximumPackageSetIdentities
            && declarations is >= 2 and <= MaximumPackageSetDirectReferences
            && owners is >= 1 and <= MaximumPackageSetProjects && edges is >= 0 and <= 512;

    private static bool CapturePackageSet(string root, string sdk, IReadOnlyList<PackageSpec> references,
        List<string> projects, Dictionary<string, string[]> projectReferences, List<string> imports,
        List<string> assets, ProofBudget budget, out PackageClosure? proof)
    {
        using var metrics = PhaseMetrics.Measure("reuse.package-set");
        proof = null;
        if (!PackageSetCounts(references.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                references.Count, projects.Count, projectReferences.Values.Sum(p => p.Length)))
            return false;
        var pins = PackageSdkPayloads(Path.GetFileName(sdk));
        if (!OperatingSystem.IsWindows() || pins is null
            || Environment.GetEnvironmentVariable("NUGET_PACKAGES") is not { Length: > 0 } configured
            || !Path.IsPathFullyQualified(configured))
            return false;
        var limits = new PackageSetInputBudget(budget);
        foreach (var (path, expected) in pins)
            if (Convert.ToHexString(SHA256.HashData(limits.Read(Path.Combine(sdk, path), category: PhaseMetrics.ReadCategory.Sdk))).ToLowerInvariant() != expected)
                return false;
        var cache = CliPaths.Root(configured);
        if (!PackageSetWindowsPath(root) || !PackageSetWindowsPath(cache) || !PackageSetWindowsPath(sdk))
            return false;
        var owners = projects.ToHashSet(PathComparer);
        if (owners.Count != projects.Count || projects.Select(Path.GetDirectoryName).Distinct(PathComparer).Count() != projects.Count
            || projects.Any(p => !PackageSetWindowsPath(p))
            || projectReferences.Values.Any(next => next.Distinct(PathComparer).Count() != next.Length)
            || projectReferences.Count != projects.Count || projectReferences.Any(p => !owners.Contains(p.Key)
                || p.Value.Any(next => !owners.Contains(next)))
            || projectReferences.Values.Sum(next => next.Length) > 512)
            return false;
        var identities = references.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        if (identities.Length > MaximumPackageSetIdentities
            || identities.Any(group => group.Any(p => p.Id != group.First().Id || p.Version != group.First().Version))
            || references.Any(p => !owners.Contains(p.Project)))
            return false;
        var direct = projects.ToDictionary(p => p, _ => new HashSet<string>(StringComparer.Ordinal), PathComparer);
        foreach (var package in references)
            if (!direct[package.Project].Add(package.Id))
                return false;
        var specs = identities.Select(group => group.First()).OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
        var directories = specs.Select(package => CliPaths.Root(CliPaths.InRoot(cache,
            package.Id.ToLowerInvariant() + "/" + package.Version))).ToArray();
        if (directories.Distinct(PathComparer).Count() != specs.Length || directories.Any(p => !PackageSetWindowsPath(p)))
            return false;
        foreach (var directory in directories)
            for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
            {
                limits.Check();
                if (Exists(Path.Combine(ancestor.FullName, ".git")))
                    return false;
            }
        var expectedAssets = projects.Select(p => Path.Combine(Path.GetDirectoryName(p)!, "obj", "project.assets.json")).ToHashSet(PathComparer);
        if (assets.Count != expectedAssets.Count || !expectedAssets.SetEquals(assets))
            return false;
        var visiting = new HashSet<string>(PathComparer);
        var completed = new HashSet<string>(PathComparer);
        foreach (var owner in projects)
            if (!Acyclic(owner))
                return false;
        var reachable = new Dictionary<string, HashSet<string>>(PathComparer);
        foreach (var owner in projects)
        {
            var selected = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(PathComparer);
            var pending = new Stack<string>();
            pending.Push(owner);
            while (pending.TryPop(out var current))
            {
                limits.Check();
                if (!visited.Add(current))
                    continue;
                selected.UnionWith(direct[current]);
                foreach (var next in projectReferences[current])
                    pending.Push(next);
            }
            reachable.Add(owner, selected);
        }
        var first = Inventory();
        var second = Inventory();
        limits.Check();
        if (first is null || first != second)
            return false;
        proof = new(null, "package-set-input/2:" + first,
            JsonSerializer.Serialize(directories.Order(StringComparer.Ordinal).ToArray()));
        return true;

        bool Acyclic(string owner)
        {
            limits.Check();
            if (completed.Contains(owner))
                return true;
            if (!visiting.Add(owner))
                return false;
            foreach (var next in projectReferences[owner])
                if (!Acyclic(next))
                    return false;
            visiting.Remove(owner);
            completed.Add(owner);
            return true;
        }

        string? Inventory()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(hash, "package-set/2");
            var payloads = new List<PackagePayload>();
            var pass = new PackageSetPassBudget();
            foreach (var (spec, directory) in specs.Zip(directories))
            {
                Append(hash, spec.Id);
                Append(hash, spec.Version);
                Append(hash, directory);
                var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                var pending = new Stack<string>();
                pending.Push(directory);
                while (pending.TryPop(out var current))
                {
                    CliPaths.RejectLinks(current);
                    foreach (var path in limits.Entries(current))
                    {
                        var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                        var attributes = Attributes(path);
                        if ((attributes & FileAttributes.Device) != 0)
                            return null;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            var parts = relative.Split('/');
                            if (parts.Length > 2 || parts[0] is not ("lib" or "ref"))
                                return null;
                            pending.Push(path);
                            continue;
                        }
                        if (!PackageFile(relative, spec) || !PackageSetLiteral(relative))
                            return null;
                        var maximum = PackageSetFileMaximum(relative);
                        var bytes = limits.Read(path, pass, PhaseMetrics.ReadCategory.Package, maximum);
                        if (!files.TryAdd(relative, bytes))
                            return null;
                    }
                }
                if (!ValidatePackageSetArchive(files, spec, limits, pass, out var contentHash))
                    return null;
                foreach (var bytes in files.Where(p => p.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value))
                {
                    limits.Check();
                    using var image = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes));
                    if (!image.HasMetadata || image.GetMetadataReader().AssemblyFiles.Any())
                        return null;
                }
                foreach (var (name, bytes) in files.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    Append(hash, name);
                    Append(hash, Convert.ToHexString(SHA256.HashData(bytes)));
                }
                payloads.Add(new(spec, directory, files, contentHash));
            }
            foreach (var owner in projects.Order(StringComparer.Ordinal))
            {
                Append(hash, owner);
                foreach (var id in direct[owner].Order(StringComparer.Ordinal))
                    Append(hash, "direct:" + id);
                foreach (var next in projectReferences[owner].Order(StringComparer.Ordinal))
                    Append(hash, "project:" + next);
            }
            foreach (var path in imports.Order(StringComparer.Ordinal))
            {
                var bytes = limits.Read(path);
                using var xml = PackageXml(bytes);
                foreach (var item in XDocument.Load(xml).Descendants())
                {
                    if (item.Name.LocalName is "NuGetPackageRoot" or "NuGetPackageFolders"
                        && !PackageSetPathMatches(item.Value, cache))
                        return null;
                    if (item.Name.LocalName == "SourceRoot" && !PackageSetPathMatches((string?)item.Attribute("Include"), cache))
                        return null;
                }
                AddInput(path, bytes);
            }
            var contracts = PackageSetOwners(projects, projectReferences, direct, references, limits);
            if (contracts is null)
                return null;
            foreach (var owner in projects.Order(StringComparer.Ordinal))
            {
                var contract = contracts[owner];
                var path = Path.Combine(Path.GetDirectoryName(owner)!, "obj", "project.assets.json");
                var selected = payloads.Where(p => reachable[owner].Contains(p.Spec.Id)).ToArray();
                var bytes = limits.Read(path);
                if (!PackageSetAssets(bytes, path, root, sdk, cache, contract, selected, budget, out var expectedCache))
                    return null;
                AddInput(path, bytes);
                var lockPath = Path.Combine(Path.GetDirectoryName(owner)!, "packages.lock.json");
                var locked = limits.Read(lockPath);
                if (!PackageSetLock(locked, selected, contract))
                    return null;
                AddInput(lockPath, locked);
                foreach (var candidate in CacheInputs(Path.GetDirectoryName(path)!))
                {
                    var data = limits.Read(candidate, maximumBytes: 4L * 1024 * 1024);
                    if (!PackageSetAssetsCache(data, expectedCache!))
                        return null;
                    AddInput(candidate, data);
                    CliPaths.RejectLinks(candidate);
                    CliPaths.RejectLinks(path);
                    using var cacheInput = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var assetsInput = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    Append(hash, "sdk-cache-newer-than-assets");
                    Append(hash, (File.GetLastWriteTimeUtc(cacheInput.SafeFileHandle)
                        > File.GetLastWriteTimeUtc(assetsInput.SafeFileHandle)).ToString());
                }
            }
            limits.Check();
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

            void AddInput(string path, byte[] bytes)
            {
                Append(hash, path);
                Append(hash, Convert.ToHexString(SHA256.HashData(bytes)));
            }
        }

        IEnumerable<string> CacheInputs(string obj)
        {
            var pending = new Stack<string>();
            pending.Push(obj);
            while (pending.TryPop(out var current))
            {
                CliPaths.RejectLinks(current);
                foreach (var entry in limits.Entries(current))
                {
                    var attributes = Attributes(entry);
                    if ((attributes & FileAttributes.Device) != 0)
                        throw new IOException("Package cache input is a device.");
                    if ((attributes & FileAttributes.Directory) != 0)
                        pending.Push(entry);
                    else if (entry.EndsWith(".assets.cache", StringComparison.OrdinalIgnoreCase))
                        yield return entry;
                }
            }
        }
    }

    private static bool PlainProject(string path, string root, ProofBudget budget, out bool localGitScope,
        out PackageSpec? package, out bool frameworkFree)
    {
        var valid = PlainProjectPackages(path, root, budget, out localGitScope, out var packages,
            out _, out frameworkFree);
        package = valid && packages.Count == 1 ? packages[0] : null;
        return valid && packages.Count <= 1;
    }

    private static bool PackageSetPathMatches(string? left, string? right)
    {
        if (left is null || right is null || !Path.IsPathFullyQualified(left) || !Path.IsPathFullyQualified(right))
            return false;
        var trimmed = Path.TrimEndingDirectorySeparator(left);
        return PathComparer.Equals(trimmed, Path.GetFullPath(trimmed)) && PackagePathMatches(left, right);
    }
    internal static bool PackageRootSetContains(string path, string? roots)
    {
        if (roots is null || !Path.IsPathFullyQualified(path))
            return false;
        var values = JsonSerializer.Deserialize<string[]>(roots);
        if (values is null || values.Length is < 1 or > MaximumPackageSetIdentities)
            return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return values.All(Path.IsPathFullyQualified) && values.Any(root => Path.GetFullPath(path).StartsWith(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, comparison));
    }
}
