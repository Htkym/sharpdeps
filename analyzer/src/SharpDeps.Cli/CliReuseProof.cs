namespace SharpDeps.Cli;

using System.Buffers.Binary;
using System.Collections;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using SharpDeps.Index;

internal sealed record CliReuseFingerprint(string SdkPath, string EnvironmentHash, string DependencyHash, string? PackageRoot = null);

/// <summary>
/// Trusted update input proof only. This helper never registers MSBuild, loads Roslyn, runs a process,
/// restores packages or publishes an index. Unknown input closure conservatively returns no proof.
/// </summary>
internal static partial class CliReuseProof
{
    internal const string OwnerVersion = "sharpdeps-cli-reuse/2";
    private const int MaximumEntries = 100_000;
    private const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly HashSet<string> ExcludedProjectDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".sharpdeps", "obj", "bin", "node_modules" };
    private static readonly HashSet<string> PlainProperties = new(StringComparer.Ordinal)
    {
        "TargetFramework", "LangVersion", "Nullable", "ImplicitUsings",
        "AssemblyName", "RootNamespace", "OutputType", "AllowUnsafeBlocks", "DefineConstants",
        "Optimize", "CheckForOverflowUnderflow", "WarningLevel", "NoWarn", "WarningsAsErrors",
        "WarningsNotAsErrors", "TreatWarningsAsErrors", "EnableNETAnalyzers", "AnalysisLevel",
        "GenerateAssemblyInfo", "GenerateGlobalUsings", "Deterministic", "DebugType", "DebugSymbols",
        "GitRepositoryConfigurationScope"
    };
    private static readonly string[] ImplicitConfiguration =
    [
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
        "Directory.Build.rsp", "MSBuild.rsp", "global.json", "NuGet.Config", "nuget.config", ".editorconfig"
    ];
    // SDK targets can treat any environment name as a property. An extensible path denylist
    // misses imports such as NuGetPropsFile; unknown environment names must disable reuse.
    private static readonly HashSet<string> PlainEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "Path", "PATHEXT", "ComSpec", "SystemRoot", "windir", "SystemDrive",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles",
        "CommonProgramFiles(x86)", "CommonProgramW6432", "ProgramData", "ALLUSERSPROFILE",
        "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",
        "USERNAME", "USERDOMAIN", "USERDOMAIN_ROAMINGPROFILE", "USER", "LOGNAME", "COMPUTERNAME",
        "OS", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER",
        "PROCESSOR_LEVEL", "PROCESSOR_REVISION", "TEMP", "TMP", "TMPDIR",
        "LANG", "LC_ALL", "LC_CTYPE", "TERM", "COLORTERM", "NO_COLOR", "PSModulePath",
        "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "NUGET_SCRATCH",
        "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE"
    };

    /// <summary>SdkPath may come from a saved proof; it is constrained to the current host's dotnet SDK tree before reading.</summary>
    internal static CliReuseFingerprint? Capture(string root, string? sdkPath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            root = CliPaths.Root(root);
            var runtime = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory()));
            var framework = Directory.GetParent(runtime);
            var shared = framework?.Parent;
            var dotnet = shared?.Parent;
            if (dotnet is null || shared!.Name != "shared" || framework!.Name != "Microsoft.NETCore.App") return null;
            var dotnetRoot = CliPaths.Root(dotnet.FullName);
            if (string.IsNullOrWhiteSpace(sdkPath) || !Path.IsPathFullyQualified(sdkPath)) return null;
            var sdk = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sdkPath));
            var sdkParent = Path.Combine(dotnetRoot, "sdk");
            // Check containment before following a saved path or examining its ancestors.
            if (!PathComparer.Equals(Path.GetDirectoryName(sdk), sdkParent)
                || !Path.GetFileName(sdk).StartsWith("10.", StringComparison.Ordinal)) return null;
            _ = CliPaths.InRoot(dotnetRoot, sdk);
            if (!Directory.Exists(sdk) || !File.Exists(Path.Combine(sdk, "MSBuild.dll"))) return null;
            CliPaths.RejectLinks(Path.Combine(sdk, "MSBuild.dll"));

            var budget = new ProofBudget(token);
            var environment = NormalizeEnvironment(Environment.GetEnvironmentVariables(), sdk);
            if (environment is null || HasUserHooks() || HasAncestorConfiguration(root)) return null;
            var git = CaptureGit(root, sdk, budget);
            if (git is null || !PlainProjects(root, sdk, budget, git.Value.Present, out var localGitScope, out var package)
                || (git.Value.Present && !localGitScope && !ExternalGitConfigurationAbsent())) return null;
            var environmentHash = HashEnvironment(environment, token);
            using var dependencies = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(dependencies, OwnerVersion);
            Append(dependencies, dotnetRoot);
            Append(dependencies, sdk);
            Append(dependencies, git.Value.Hash);
            if (package is not null) Append(dependencies, package.Value.Hash);
            AddTree(sdk, "sdk", dependencies, budget);
            if (Environment.GetEnvironmentVariable("MSBuildEnableWorkloadResolver") == "false")
                AddTree(Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref"), "dotnet/packs/Microsoft.NETCore.App.Ref", dependencies, budget);
            else AddTree(Path.Combine(dotnetRoot, "packs"), "dotnet/packs", dependencies, budget);
            foreach (var name in new[] { "shared", "host", "sdk-manifests", "metadata" })
                AddTree(Path.Combine(dotnetRoot, name), "dotnet/" + name, dependencies, budget);
            foreach (var entry in Entries(dotnetRoot, budget))
            {
                var attributes = Attributes(entry);
                if ((attributes & FileAttributes.Directory) == 0)
                    AddFile(entry, "dotnet/" + Path.GetFileName(entry), dependencies, budget);
            }
            Append(dependencies, "sdk-directory-inventory");
            foreach (var entry in Entries(sdkParent, budget))
            {
                var attributes = Attributes(entry);
                Append(dependencies, Path.GetFileName(entry));
                Append(dependencies, ((attributes & FileAttributes.Directory) != 0).ToString());
            }
            var payload = CliPaths.Root(Path.GetFullPath(AppContext.BaseDirectory));
            Append(dependencies, payload);
            AddTree(payload, "tool", dependencies, budget);
            token.ThrowIfCancellationRequested();
            return new(sdk, environmentHash, Convert.ToHexString(dependencies.GetHashAndReset()).ToLowerInvariant(), package?.Root);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    internal static bool Matches(IndexReuseProof proof, CliReuseFingerprint? current,
        string optionsHash, string inputHash)
        => current is not null && proof.OwnerVersion == OwnerVersion
            && proof.OptionsHash == optionsHash && proof.InputHash == inputHash
            && proof.EnvironmentHash == current.EnvironmentHash && proof.DependencyHash == current.DependencyHash
            && PathComparer.Equals(proof.SdkPath, current.SdkPath);

    private static bool PlainProjects(string root, string sdk, ProofBudget budget, bool git, out bool localGitScope,
        out (string Root, string Hash)? package)
    {
        localGitScope = true;
        package = null;
        var projectPaths = new List<string>();
        var packages = new List<PackageSpec>();
        var imports = new List<string>();
        var assets = new List<string>();
        var directories = new Stack<string>(); directories.Push(root);
        var projects = 0;
        while (directories.TryPop(out var directory))
        {
            foreach (var entry in Entries(directory, budget))
            {
                var attributes = Attributes(entry);
                if (Path.GetFileName(entry).Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    if (!git || !CliPaths.SamePath(entry, Path.Combine(root, ".git"))
                        || (attributes & FileAttributes.Directory) == 0) return false;
                    continue;
                }
                if (Path.GetFileName(entry).Equals(".gitmodules", StringComparison.OrdinalIgnoreCase)) return false;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!Path.GetFileName(entry).Equals(".sharpdeps", StringComparison.OrdinalIgnoreCase)) directories.Push(entry);
                    continue;
                }
                var name = Path.GetFileName(entry);
                if (ImplicitConfiguration.Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))) return false;
                if (Path.GetExtension(entry).Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(entry).Equals(".vbproj", StringComparison.OrdinalIgnoreCase)) return false;
                if (Path.GetExtension(entry).Equals(".props", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(entry).Equals(".targets", StringComparison.OrdinalIgnoreCase))
                {
                    imports.Add(entry);
                    continue;
                }
                if (name == "project.assets.json") assets.Add(entry);
                if (!Path.GetExtension(entry).Equals(".csproj", StringComparison.OrdinalIgnoreCase)) continue;
                if (!PlainProject(entry, root, budget, out var local, out var reference)) return false;
                projectPaths.Add(entry);
                if (reference is not null) packages.Add(reference);
                localGitScope &= local;
                projects++;
            }
        }
        if (projects == 0 || imports.Any(path => !PlainRestoreImport(path, budget))) return false;
        if (packages.Count == 0) return assets.All(path => PlainAssets(path, root, sdk, budget));
        return packages.Count == 1 && CapturePackage(root, sdk, packages[0], projectPaths, imports, assets, budget, out package);
    }

    private static bool PlainProject(string path, string root, ProofBudget budget, out bool localGitScope, out PackageSpec? package)
    {
        localGitScope = false;
        package = null;
        CliPaths.RejectLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        budget.Bytes(input.Length);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = IndexSnapshotValidator.MaximumInputFileBytes
        });
        var document = XDocument.Load(reader);
        var project = document.Root;
        if (project?.Name != "Project" || project.Attributes().Count() != 1
            || (string?)project.Attribute("Sdk") != "Microsoft.NET.Sdk") return false;
        var frameworks = 0;
        foreach (var child in project.Elements())
        {
            if (child.HasAttributes) return false;
            if (child.Name == "PropertyGroup")
            {
                foreach (var property in child.Elements())
                {
                    if (property.Name.Namespace != XNamespace.None || !PlainProperties.Contains(property.Name.LocalName)
                        || property.HasAttributes || property.HasElements || !Static(property.Value)) return false;
                    if (property.Name.LocalName == "TargetFramework")
                    {
                        frameworks++;
                        if (property.Value.Trim() != "net10.0") return false;
                    }
                    if (property.Name.LocalName == "GitRepositoryConfigurationScope")
                    {
                        if (localGitScope || !property.Value.Equals("local", StringComparison.OrdinalIgnoreCase)) return false;
                        localGitScope = true;
                    }
                }
            }
            else if (child.Name == "ItemGroup")
            {
                foreach (var reference in child.Elements())
                {
                    if (reference.Name == "PackageReference")
                    {
                        if (package is not null || !PlainPackageReference(reference, path, out package)) return false;
                        continue;
                    }
                    var include = (string?)reference.Attribute("Include");
                    if (reference.Name != "ProjectReference" || reference.Attributes().Count() != 1
                        || reference.HasElements || !string.IsNullOrWhiteSpace(reference.Value)
                        || string.IsNullOrWhiteSpace(include) || !Static(include) || include.Contains(';')
                        || (!OperatingSystem.IsWindows() && include.Contains('\\'))
                        || Path.IsPathRooted(include)) return false;
                    var full = Path.GetFullPath(include, Path.GetDirectoryName(path)!);
                    full = CliPaths.InRoot(root, full);
                    if (ExcludedProject(root, full)) return false;
                    if (!full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return false;
                }
            }
            else return false;
        }
        CliPaths.RejectLinks(path);
        return frameworks == 1;
    }

    private static bool PlainRestoreImport(string path, ProofBudget budget)
    {
        var directory = Path.GetDirectoryName(path)!;
        var project = Directory.GetParent(directory)?.FullName;
        var name = Path.GetFileName(path);
        if (Path.GetFileName(directory) != "obj" || project is null
            || (!name.EndsWith(".csproj.nuget.g.props", StringComparison.Ordinal)
                && !name.EndsWith(".csproj.nuget.g.targets", StringComparison.Ordinal))
            || !File.Exists(Path.Combine(project, name[..name.IndexOf(".nuget.g.", StringComparison.Ordinal)]))) return false;
        CliPaths.RejectLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        budget.Bytes(input.Length);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = IndexSnapshotValidator.MaximumInputFileBytes });
        var document = XDocument.Load(reader);
        var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
        var element = document.Root;
        if (element?.Name != ns + "Project" || (string?)element.Attribute("ToolsVersion") != "14.0"
            || element.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name != "ToolsVersion")) return false;
        // A package-free SDK restore has no imports/tasks. Only its inert conventional metadata is accepted.
        var names = new HashSet<string>(StringComparer.Ordinal)
        { "RestoreSuccess", "RestoreTool", "ProjectAssetsFile", "NuGetPackageRoot", "NuGetPackageFolders", "NuGetProjectStyle", "NuGetToolVersion" };
        foreach (var group in element.Elements())
        {
            if (group.Attributes().Count() != 1 || (string?)group.Attribute("Condition") != " '$(ExcludeRestorePackageImports)' != 'true' ") return false;
            if (group.Name == ns + "PropertyGroup")
            {
                foreach (var property in group.Elements())
                {
                    var key = property.Name.LocalName;
                    if (property.Name.Namespace != ns || !names.Contains(key) || property.HasElements
                        || property.Attributes().Count() != 1 || (string?)property.Attribute("Condition") != $" '$({key})' == '' ") return false;
                    if (key == "ProjectAssetsFile")
                    { if (property.Value != "$(MSBuildThisFileDirectory)project.assets.json") return false; }
                    else if (!Static(property.Value)) return false;
                }
            }
            else if (group.Name == ns + "ItemGroup")
            {
                foreach (var item in group.Elements())
                    if (item.Name != ns + "SourceRoot" || item.HasElements || !string.IsNullOrWhiteSpace(item.Value)
                        || item.Attributes().Count() != 1 || !Static((string?)item.Attribute("Include") ?? "")) return false;
            }
            else return false;
        }
        return true;
    }

    private static bool PlainAssets(string path, string root, string sdk, ProofBudget budget)
    {
        if (Path.GetFileName(Path.GetDirectoryName(path)) != "obj") return false;
        CliPaths.RejectLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        budget.Bytes(input.Length);
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 64 });
        return PlainAssetDocument(document.RootElement, path, root, sdk, budget);
    }

    private static bool PlainAssetDocument(JsonElement assets, string path, string root, string sdk, ProofBudget budget)
    {
        if (!assets.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object
            || !assets.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object
            || targets.EnumerateObject().Count() != 1 || !targets.TryGetProperty("net10.0", out var target)
            || target.ValueKind != JsonValueKind.Object) return false;
        foreach (var library in libraries.EnumerateObject())
        {
            budget.Token.ThrowIfCancellationRequested();
            if (library.Value.EnumerateObject().Any(field => field.Name is not ("type" or "path" or "msbuildProject"))) return false;
            if (!library.Value.TryGetProperty("type", out var type) || type.GetString() != "project"
                || !library.Value.TryGetProperty("path", out var projectPath) || projectPath.GetString() is not { } relative) return false;
            var project = CliPaths.InRoot(root, Path.GetFullPath(relative, Directory.GetParent(Path.GetDirectoryName(path)!)!.FullName));
            if (ExcludedProject(root, project) || !project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(project)) return false;
        }
        foreach (var item in target.EnumerateObject())
        {
            budget.Token.ThrowIfCancellationRequested();
            if (!libraries.TryGetProperty(item.Name, out _) || item.Value.ValueKind != JsonValueKind.Object
                || !item.Value.TryGetProperty("type", out var type) || type.GetString() != "project") return false;
            foreach (var field in item.Value.EnumerateObject())
                if (field.Name is not ("type" or "framework" or "dependencies" or "compile" or "runtime")) return false;
        }
        if (target.EnumerateObject().Count() != libraries.EnumerateObject().Count()
            || !assets.TryGetProperty("project", out var owner)
            || !owner.TryGetProperty("frameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.Object
            || frameworks.EnumerateObject().Count() != 1 || !frameworks.TryGetProperty("net10.0", out var framework)
            || framework.ValueKind != JsonValueKind.Object) return false;
        if (framework.EnumerateObject().Any(field => field.Name is not ("framework" or "targetAlias" or "imports"
                or "assetTargetFallback" or "warn" or "frameworkReferences" or "runtimeIdentifierGraphPath"
                or "packagesToPrune" or "dependencies" or "downloadDependencies"))) return false;
        if (!framework.TryGetProperty("framework", out var targetFramework) || targetFramework.GetString() != "net10.0"
            || (framework.TryGetProperty("targetAlias", out var alias) && alias.GetString() != "net10.0")) return false;
        if (framework.TryGetProperty("dependencies", out var dependencies) && dependencies.EnumerateObject().Any()) return false;
        if (framework.TryGetProperty("downloadDependencies", out var downloads) && downloads.EnumerateArray().Any()) return false;
        if (!framework.TryGetProperty("frameworkReferences", out var references) || references.ValueKind != JsonValueKind.Object
            || references.EnumerateObject().Count() != 1 || !references.TryGetProperty("Microsoft.NETCore.App", out _)) return false;
        if (framework.TryGetProperty("runtimeIdentifierGraphPath", out var graphPath))
        {
            if (graphPath.GetString() is not { } value || !Path.IsPathFullyQualified(value)) return false;
            _ = CliPaths.InRoot(sdk, value);
        }
        return true;
    }

    private static bool ExcludedProject(string root, string path)
        => Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(ExcludedProjectDirectories.Contains);

    private static bool Static(string value) => value.Length > 0
        && !value.Any(c => c is '$' or '@' or '%' or '*' or '?' || (char.IsControl(c) && !char.IsWhiteSpace(c)));

    private static IDictionary? NormalizeEnvironment(IDictionary environment, string sdk)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var registered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBUILD_EXE_PATH"] = Path.Combine(sdk, "MSBuild.dll"),
            ["MSBuildSDKsPath"] = Path.Combine(sdk, "Sdks"),
            ["MSBuildExtensionsPath"] = sdk
        };
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is not string name || entry.Value is not string value) return null;
            if (registered.TryGetValue(name, out var expected))
            {
                if (value.Length != 0 && (!Path.IsPathFullyQualified(value)
                    || !PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)), expected))) return null;
                continue;
            }
            if (name.Equals("MSBUILDDISABLENODEREUSE", StringComparison.OrdinalIgnoreCase))
            { if (value != "1") return null; }
            else if (name.Equals("MSBuildEnableWorkloadResolver", StringComparison.OrdinalIgnoreCase))
            { if (value != "false") return null; }
            else if (name.Equals("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DOTNET_ROOT_X64", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DOTNET_ROOT_ARM64", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DOTNET_HOST_PATH", StringComparison.OrdinalIgnoreCase))
            {
                var dotnet = Directory.GetParent(sdk)?.Parent?.FullName;
                var expectedHost = name.Equals("DOTNET_HOST_PATH", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(dotnet!, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet") : dotnet;
                if (expectedHost is null || !Path.IsPathFullyQualified(value)
                    || !PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)), expectedHost)) return null;
            }
            else if (!PlainEnvironment.Contains(name)) return null;
            values.Add(name, value);
        }
        // Only these known registration paths are normalized. Every other environment value is hashed verbatim.
        foreach (var (name, value) in registered) values.Add(name, value);
        return values;
    }

    private static bool HasUserHooks()
    {
        var paths = new HashSet<string>(PathComparer);
        foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
        {
            var directory = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(directory)) paths.Add(Path.Combine(directory, "Microsoft", "MSBuild"));
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home)) paths.Add(Path.Combine(home, ".local", "share", "Microsoft", "MSBuild"));
        foreach (var name in new[] { "APPDATA", "LOCALAPPDATA" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                if (!Path.IsPathFullyQualified(value)) return true;
                paths.Add(Path.Combine(value, "Microsoft", "MSBuild"));
            }
        foreach (var name in new[] { "HOME", "USERPROFILE" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                if (!Path.IsPathFullyQualified(value)) return true;
                paths.Add(Path.Combine(value, ".local", "share", "Microsoft", "MSBuild"));
            }
        return paths.Any(Exists);
    }

    private static bool HasAncestorConfiguration(string root)
    {
        for (var directory = Directory.GetParent(root); directory is not null; directory = directory.Parent)
            foreach (var name in ImplicitConfiguration)
                if (Exists(Path.Combine(directory.FullName, name))) return true;
        return false;
    }

    private static bool Exists(string path)
    {
        try { CliPaths.RejectLinks(path); _ = Attributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string HashEnvironment(IDictionary environment, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in environment.Keys.Cast<string>().Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            Append(hash, name); Append(hash, (string)environment[name]!);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string[] Entries(string directory, ProofBudget budget)
    {
        _ = Attributes(directory);
        var entries = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            budget.Entry(); entries.Add(entry);
        }
        entries.Sort(StringComparer.Ordinal);
        return entries.ToArray();
    }

    private static FileAttributes Attributes(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reuse proof does not follow links.");
        return attributes;
    }

    private static void AddTree(string directory, string label, IncrementalHash hash, ProofBudget budget)
    {
        Append(hash, label);
        if (!Exists(directory)) { Append(hash, "absent"); return; }
        Append(hash, "present");
        var directories = new Stack<string>(); directories.Push(directory);
        while (directories.TryPop(out var current))
        {
            Append(hash, Path.GetRelativePath(directory, current));
            foreach (var entry in Entries(current, budget))
            {
                if ((Attributes(entry) & FileAttributes.Directory) != 0) directories.Push(entry);
                else AddFile(entry, Path.GetRelativePath(directory, entry), hash, budget);
            }
        }
    }

    private static string AddFile(string path, string label, IncrementalHash hash, ProofBudget budget)
    {
        CliPaths.RejectLinks(path);
        _ = Attributes(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = input.Length;
        budget.Bytes(length);
        Append(hash, label);
        Append(hash, length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = budget.ReadBuffer;
        long read = 0;
        for (int count; (count = input.Read(buffer)) != 0;)
        {
            budget.Check();
            read = checked(read + count);
            if (read > length) throw new IOException("Reuse input changed.");
            content.AppendData(buffer, 0, count);
        }
        if (read != length || input.Length != length) throw new IOException("Reuse input changed.");
        CliPaths.RejectLinks(path);
        _ = Attributes(path);
        var digest = Convert.ToHexString(content.GetHashAndReset()).ToLowerInvariant();
        Append(hash, digest);
        return digest;
    }

    private static void Append(IncrementalHash hash, string text)
    {
        var bytes = StrictUtf8.GetBytes(text);
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(size, bytes.LongLength);
        hash.AppendData(size); hash.AppendData(bytes);
    }

    private sealed class ProofBudget(CancellationToken token, ProofBudget? parent = null, bool git = false)
    {
        private int entries;
        private long bytes;
        private byte[]? readBuffer;
        private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        internal CancellationToken Token { get; } = token;
        // Synchronous child passes share only this Capture's scratch storage.
        internal byte[] ReadBuffer => parent?.ReadBuffer ?? (readBuffer ??= new byte[65536]);
        internal void Check()
        {
            Token.ThrowIfCancellationRequested();
            if (git && elapsed.ElapsedMilliseconds >= 5000) throw new IOException("Git proof time budget exceeded.");
        }
        internal void NextGitPass() { entries = 0; bytes = 0; Check(); }
        internal void Entry()
        {
            Check();
            if (++entries > (git ? 10_000 : MaximumEntries)) throw new IOException("Reuse proof entry budget exceeded.");
            parent?.Entry();
        }
        internal void Bytes(long length)
        {
            Check();
            if (length < 0 || length > IndexSnapshotValidator.MaximumInputFileBytes
                || (bytes = checked(bytes + length)) > (git ? 64L * 1024 * 1024 : MaximumBytes)) throw new IOException("Reuse proof byte budget exceeded.");
            parent?.Bytes(length);
        }
    }
}
