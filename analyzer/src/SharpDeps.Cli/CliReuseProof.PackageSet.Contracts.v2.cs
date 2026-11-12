namespace SharpDeps.Cli;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using NuGet.Versioning;

internal static partial class CliReuseProof
{
    private sealed record PackageSetProject(string Path, string Name, string LockName,
        string[] PackageIds, string[] ProjectPaths, Dictionary<string, string> Dependencies);
    private sealed record PackageSetOwner(PackageSetProject Self, PackageSetProject[] Projects);
    private sealed record PackageSetCacheItem(string Item, Dictionary<string, string> Metadata);
    private sealed record PackageSetCacheContract(Dictionary<string, PackageSetCacheItem>[] Groups);

    private static bool PackageSetLiteral(string value) => value.Length is > 0 and <= 16384
        && !value.Any(c => c is '%' or '$' or '@' or '*' or '?' or '(' or ')' or ';' or '\'' || char.IsControl(c));

    private static bool PackageSetWindowsPath(string value)
    {
        if (!PackageSetLiteral(value) || value.Length < 3 || !char.IsAsciiLetter(value[0])
            || value[1] != ':' || value[2] != '\\' || value.Contains('/')
            || !PathComparer.Equals(Path.TrimEndingDirectorySeparator(value), Path.TrimEndingDirectorySeparator(Path.GetFullPath(value))))
            return false;
        var tail = Path.TrimEndingDirectorySeparator(value)[3..];
        if (tail.Length == 0)
            return true;
        foreach (var part in tail.Split('\\'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(c => c is ':' or '<' or '>' or '"' or '|' || char.IsControl(c)))
                return false;
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && stem[3] is >= '1' and <= '9')
                return false;
        }
        return true;
    }

    private static Dictionary<string, PackageSetOwner>? PackageSetOwners(List<string> projects,
        Dictionary<string, string[]> edges, Dictionary<string, HashSet<string>> direct,
        IReadOnlyList<PackageSpec> references, PackageSetInputBudget budget)
    {
        var all = new Dictionary<string, PackageSetProject>(PathComparer);
        var names = new HashSet<string>(references.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var path in projects)
        {
            using var xml = PackageXml(budget.Read(path));
            var document = XDocument.Load(xml);
            var values = document.Root!.Elements("PropertyGroup").Elements("AssemblyName").ToArray();
            var name = values.Length == 0 ? Path.GetFileNameWithoutExtension(path) : values[0].Value.Trim();
            if (values.Length > 1 || !PackageSetLiteral(name)
                || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) || !names.Add(name))
                return null;
            var lockName = name.Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
                ? name.ToLowerInvariant() : name;
            all.Add(path, new(path, name, lockName, direct[path].Order(StringComparer.Ordinal).ToArray(), edges[path],
                new Dictionary<string, string>(StringComparer.Ordinal)));
        }
        foreach (var project in all.Values)
        {
            foreach (var id in project.PackageIds)
                project.Dependencies.Add(id, references.First(p => p.Id == id).Version);
            foreach (var path in project.ProjectPaths)
                project.Dependencies.Add(all[path].Name, "1.0.0");
        }
        var result = new Dictionary<string, PackageSetOwner>(PathComparer);
        foreach (var owner in projects)
        {
            var visited = new HashSet<string>(PathComparer) { owner };
            var pending = new Stack<string>(edges[owner]);
            while (pending.TryPop(out var path))
            {
                budget.Check();
                if (!visited.Add(path))
                    continue;
                foreach (var next in edges[path])
                    pending.Push(next);
            }
            result.Add(owner, new(all[owner], visited.Where(p => !PathComparer.Equals(p, owner))
                .Select(p => all[p]).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray()));
        }
        return result;
    }

    private static bool PackageSetRange(JsonNode? value, string version) => value is JsonValue json
        && json.TryGetValue<string>(out var text) && text is not null && text.Length <= 128
        && VersionRange.TryParse(text, out var range) && range.MinVersion is not null
        && range.IsMinInclusive && range.MaxVersion is null && range.Float is null
        && range.MinVersion.ToNormalizedString() == version;

    private static bool PackageSetDependencies(JsonNode? value, Dictionary<string, string> expected)
    {
        if (value is null)
            return expected.Count == 0;
        return value is JsonObject actual && actual.Count == expected.Count
            && actual.All(p => expected.TryGetValue(p.Key, out var version) && PackageSetRange(p.Value, version));
    }

    private static bool PackageSetKeys(JsonObject value, params string[] keys)
        => value.Count == keys.Length && value.All(p => keys.Contains(p.Key, StringComparer.Ordinal));

    private static bool PackageSetProjectRecords(JsonObject libraries, JsonObject target, PackageSetOwner owner)
    {
        foreach (var project in owner.Projects)
        {
            var key = project.Name + "/1.0.0";
            var relative = Path.GetRelativePath(Path.GetDirectoryName(owner.Self.Path)!, project.Path).Replace('\\', '/');
            var placeholder = "bin/placeholder/" + project.Name + ".dll";
            if (libraries[key] is not JsonObject library || !PackageSetKeys(library, "type", "path", "msbuildProject")
                || library["type"]!.GetValue<string>() != "project"
                || library["path"]!.GetValue<string>() != relative || library["msbuildProject"]!.GetValue<string>() != relative
                || target[key] is not JsonObject selected
                || selected.Any(p => p.Key is not ("type" or "framework" or "dependencies" or "compile" or "runtime"))
                || selected["type"]!.GetValue<string>() != "project"
                || selected["framework"]!.GetValue<string>() != ".NETCoreApp,Version=v10.0"
                || !PackageSetDependencies(selected["dependencies"], project.Dependencies))
                return false;
            foreach (var kind in new[] { "compile", "runtime" })
                if (selected[kind] is not JsonObject paths || paths.Count != 1
                    || paths[placeholder] is not JsonObject metadata || metadata.Count != 0)
                    return false;
            libraries.Remove(key);
            target.Remove(key);
        }
        return libraries.Count == 0 && target.Count == 0;
    }

    private static bool PackageSetOwnerRestore(JsonObject node, PackageSetOwner owner, string cache)
    {
        var project = node["project"]!.AsObject();
        var restore = project["restore"]!.AsObject();
        if (project["version"]!.GetValue<string>() != "1.0.0"
            || !PackageSetPathMatches(restore["projectPath"]!.GetValue<string>(), owner.Self.Path)
            || !PackageSetPathMatches(restore["projectUniqueName"]!.GetValue<string>(), owner.Self.Path)
            || restore["projectName"]!.GetValue<string>() != owner.Self.Name
            || !PackageSetPathMatches(restore["packagesPath"]!.GetValue<string>(), cache)
            || restore["fallbackFolders"] is not null || restore["frameworks"] is not JsonObject frameworks
            || frameworks.Count != 1 || frameworks["net10.0"] is not JsonObject framework
            || !PackageSetKeys(framework, "framework", "targetAlias", "projectReferences")
            || framework["framework"]!.GetValue<string>() != "net10.0"
            || framework["targetAlias"]!.GetValue<string>() != "net10.0"
            || framework["projectReferences"] is not JsonObject references
            || references.Count != owner.Self.ProjectPaths.Length)
            return false;
        foreach (var path in owner.Self.ProjectPaths)
            if (references[path] is not JsonObject record || !PackageSetKeys(record, "projectPath")
                || record["projectPath"]!.GetValue<string>() != path)
                return false;
        if (node["projectFileDependencyGroups"] is not JsonObject groups || groups.Count != 1
            || groups["net10.0"] is not JsonArray declarations || declarations.Count != owner.Self.Dependencies.Count)
            return false;
        var remaining = new Dictionary<string, string>(owner.Self.Dependencies, StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            var text = declaration!.GetValue<string>();
            var split = text.IndexOf(' ');
            var id = split < 0 ? text : text[..split];
            if (!remaining.Remove(id, out var version))
                return false;
            if (owner.Self.PackageIds.Contains(id, StringComparer.Ordinal))
            {
                if (text != id + " >= " + version)
                    return false;
            }
            else if (text != id && text != id + " >= 1.0.0")
                return false;
        }
        return remaining.Count == 0;
    }

    private static bool PackageSetAssets(byte[] bytes, string path, string root, string sdk, string cache,
        PackageSetOwner owner, PackagePayload[] selectedPackages, ProofBudget budget, out PackageSetCacheContract? contract)
    {
        contract = null;
        var node = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 64 })!.AsObject();
        var libraries = node["libraries"]!.AsObject();
        var target = node["targets"]!["net10.0"]!.AsObject();
        if (node.Any(p => p.Key is not ("version" or "targets" or "libraries" or "projectFileDependencyGroups" or "packageFolders" or "project" or "logs"))
            || node["version"]!.GetValue<int>() != 4 || node["logs"] is JsonArray logs && logs.Count != 0
            || node["targets"]!.AsObject().Count != 1 || node["packageFolders"]!.AsObject().Count != 1
            || !PackageSetPathMatches(node["packageFolders"]!.AsObject().Single().Key, cache)
            || node["packageFolders"]!.AsObject().Single().Value is not JsonObject folderMetadata || folderMetadata.Count != 0
            || !PackageSetOwnerRestore(node, owner, cache)
            || !CliPaths.SamePath(Path.GetDirectoryName(owner.Self.Path), Directory.GetParent(Path.GetDirectoryName(path)!)!.FullName))
            return false;
        var expected = selectedPackages.Select(p => p.Spec.Id + "/" + p.Spec.Version)
            .Concat(owner.Projects.Select(p => p.Name + "/1.0.0")).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(libraries.Select(p => p.Key)) || !expected.SetEquals(target.Select(p => p.Key)))
            return false;
        var groups = Enumerable.Range(0, 17).Select(_ => new Dictionary<string, PackageSetCacheItem>(StringComparer.OrdinalIgnoreCase)).ToArray();
        var output = new PackageSetCacheContract(groups);
        Add(10, node["packageFolders"]!.AsObject().Single().Key);
        foreach (var package in selectedPackages)
        {
            var key = package.Spec.Id + "/" + package.Spec.Version;
            var library = libraries[key]!.AsObject();
            var selected = target[key]!.AsObject();
            if (!PackageSetKeys(library, "type", "path", "sha512", "files")
                || library["type"]!.GetValue<string>() != "package"
                || library["path"]!.GetValue<string>() != package.Spec.Id.ToLowerInvariant() + "/" + package.Spec.Version
                || library["sha512"]!.GetValue<string>() != package.ContentHash
                || library["files"] is not JsonArray files
                || files.Select(p => p!.GetValue<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count
                || !package.Files.Keys.Where(p => !p.EndsWith(".nupkg", StringComparison.Ordinal))
                    .ToHashSet(StringComparer.Ordinal).SetEquals(files.Select(p => p!.GetValue<string>()))
                || selected.Any(p => p.Key is not ("type" or "compile" or "runtime"))
                || selected["type"]!.GetValue<string>() != "package")
                return false;
            foreach (var kind in new[] { "compile", "runtime" })
            {
                if (selected[kind] is not JsonObject references || references.Count == 0)
                    return false;
                foreach (var (relative, metadata) in references)
                {
                    if (!PackageSetLiteral(relative) || !package.Files.Keys.Contains(relative, StringComparer.Ordinal)
                        || !relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        || metadata is not JsonObject fields || fields.Any(p => p.Key != "related"
                            || p.Value?.GetValue<string>() is not (".xml" or ".pdb" or ".pdb;.xml" or ".xml;.pdb")))
                        return false;
                    var item = Path.Combine(package.Root, relative.Replace('/', Path.DirectorySeparatorChar));
                    var pairs = Id(package).Concat(new[] { ("PathInPackage", relative) }).ToList();
                    if (kind == "runtime")
                        pairs.AddRange([("AssetType", "runtime"), ("CopyLocal", "true"), ("DestinationSubPath", Path.GetFileName(item))]);
                    Add(kind == "compile" ? 2 : 13, item, pairs.ToArray());
                    var extension = kind == "compile" ? ".xml" : ".pdb";
                    if (fields["related"]?.GetValue<string>().Split(';').Contains(extension, StringComparer.Ordinal) == true)
                    {
                        var related = Path.ChangeExtension(relative, extension).Replace('\\', '/');
                        if (package.Files.ContainsKey(related))
                            Add(kind == "compile" ? 11 : 4, Path.Combine(package.Root, related.Replace('/', Path.DirectorySeparatorChar)), Id(package));
                    }
                }
            }
            Add(8, package.Spec.Id);
            if (owner.Self.PackageIds.Contains(package.Spec.Id, StringComparer.Ordinal))
            {
                if (package.Spec.Id is "Microsoft.NETCore.App" or "Microsoft.AspNetCore.App" or "Microsoft.AspNetCore.All"
                    || package.Spec.Id.StartsWith("Microsoft.NETCore.", StringComparison.OrdinalIgnoreCase)
                    || package.Spec.Id.StartsWith("NETStandard.", StringComparison.OrdinalIgnoreCase))
                    return false;
                Add(9, key, ("Name", package.Spec.Id), ("Version", package.Spec.Version),
                    ("IsImplicitlyDefined", "False"), ("Resolved", "True"), ("Path", package.Root));
            }
            libraries.Remove(key);
            target.Remove(key);
        }
        foreach (var project in owner.Projects.Where(p => !owner.Self.ProjectPaths.Contains(p.Path, PathComparer)))
            Add(15, Path.GetRelativePath(Path.GetDirectoryName(owner.Self.Path)!, project.Path),
                ("NuGetPackageId", project.Name), ("NuGetPackageVersion", "1.0.0"));
        if (!PackageSetProjectRecords(libraries, target, owner))
            return false;
        var framework = node["project"]!["frameworks"]!["net10.0"]!.AsObject();
        if (framework["dependencies"] is not JsonObject dependencies)
        {
            if (owner.Self.PackageIds.Length != 0)
                return false;
        }
        else
        {
            if (!owner.Self.PackageIds.ToHashSet(StringComparer.Ordinal).SetEquals(dependencies.Select(p => p.Key)))
                return false;
            foreach (var package in selectedPackages.Where(p => owner.Self.PackageIds.Contains(p.Spec.Id, StringComparer.Ordinal)))
                if (dependencies[package.Spec.Id] is not JsonObject dependency || !PackageSetKeys(dependency, "target", "version")
                    || dependency["target"]!.GetValue<string>() != "Package" || !PackageSetRange(dependency["version"], package.Spec.Version))
                    return false;
            dependencies.Clear();
        }
        using var document = JsonDocument.Parse(node.ToJsonString());
        if (!PlainAssetDocument(document.RootElement, path, root, sdk, budget))
            return false;
        contract = output;
        return true;

        static (string, string)[] Id(PackagePayload p) => [("NuGetPackageId", p.Spec.Id), ("NuGetPackageVersion", p.Spec.Version)];
        void Add(int group, string item, params (string Name, string Value)[] metadata)
        {
            if (!PackageSetLiteral(item) || !groups[group].TryAdd(item, new(item, metadata.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal))))
                throw new InvalidDataException("Package set cache identity is ambiguous.");
        }
    }

    private static bool PackageSetLock(byte[] bytes, PackagePayload[] selected, PackageSetOwner owner)
    {
        var value = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 64 })!.AsObject();
        if (!PackageSetKeys(value, "version", "dependencies") || value["version"]!.GetValue<int>() != 1
            || value["dependencies"] is not JsonObject groups || groups.Count != 1 || groups["net10.0"] is not JsonObject items
            || items.Count != selected.Length + owner.Projects.Length)
            return false;
        foreach (var package in selected)
        {
            var direct = owner.Self.PackageIds.Contains(package.Spec.Id, StringComparer.Ordinal);
            if (items[package.Spec.Id] is not JsonObject item
                || item.Any(p => p.Key is not ("type" or "requested" or "resolved" or "contentHash" or "dependencies"))
                || item["type"]!.GetValue<string>() != (direct ? "Direct" : "Transitive")
                || item["resolved"]!.GetValue<string>() != package.Spec.Version || item["contentHash"]!.GetValue<string>() != package.ContentHash
                || !PackageSetDependencies(item["dependencies"], new(StringComparer.Ordinal))
                || direct && !PackageSetRange(item["requested"], package.Spec.Version) || !direct && item.ContainsKey("requested"))
                return false;
            items.Remove(package.Spec.Id);
        }
        foreach (var project in owner.Projects)
        {
            if (items[project.LockName] is not JsonObject item || item.Any(p => p.Key is not ("type" or "dependencies"))
                || item["type"]!.GetValue<string>() != "Project" || !PackageSetDependencies(item["dependencies"], project.Dependencies))
                return false;
            items.Remove(project.LockName);
        }
        return items.Count == 0;
    }
}
