namespace SharpDeps.Cli;

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using SharpDeps.Analysis.Roslyn;
using SharpDeps.Index;

internal sealed record CoordinatorUpdate(CliIndexOptions Options, bool Full)
{
    internal static CoordinatorUpdate Parse(CliArguments arguments, string root, string indexPath, Guid uuid)
    {
        if (arguments.Command is not ("index" or "update") || !arguments.Has("trusted"))
            throw new CliFailure("WORKSPACE_UNTRUSTED", 5);
        var target = CliPaths.InRoot(root, arguments.Require("target"));
        if (!File.Exists(target) || Path.GetExtension(target).ToLowerInvariant() is not (".sln" or ".slnx" or ".csproj"))
            throw new CliFailure("TARGET_INVALID", 2);
        var projects = arguments.Many("project")?.Select(path => CliPaths.InRoot(root, path))
            .Order(StringComparer.Ordinal).ToArray();
        if (projects is not null && (projects.Length > 64 || projects.Distinct(StringComparer.OrdinalIgnoreCase).Count() != projects.Length
            || projects.Any(path => !File.Exists(path) || !path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))))
            throw new CliFailure("PROJECT_SELECTION_INVALID", 2);
        return new(new(root, target, indexPath, uuid, arguments.Get("configuration") ?? "Debug", arguments.Get("platform"),
            projects, arguments.Get("tfm"), arguments.Number("timeout-seconds", 180, 1, 300),
            AllowSemanticReuse: arguments.Command == "update"), arguments.Command == "index");
    }

    // Client timeout/request ID/quiet do not affect the requested extraction. The owner imposes its own deadline.
    internal string Key => System.Text.Json.JsonSerializer.Serialize(new { Target = KeyPath(Options.Target), Options.Configuration,
        Options.Platform, Projects = Options.ProjectPaths?.Select(KeyPath).Order(StringComparer.Ordinal).ToArray(), Options.TargetFramework, Full });

    private static string KeyPath(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    internal string[] Arguments()
    {
        var args = new List<string> { Full ? "index" : "update", "--root", Options.Root, "--index", Options.IndexPath,
            "--trusted", "--target", Options.Target, "--configuration", Options.Configuration };
        if (Options.Platform is { } platform) args.AddRange(["--platform", platform]);
        foreach (var project in Options.ProjectPaths ?? []) args.AddRange(["--project", project]);
        if (Options.TargetFramework is { } framework) args.AddRange(["--tfm", framework]);
        return args.ToArray();
    }
}

internal sealed class CoordinatorExecutor(IndexWriter writer)
{
    private CliSdkSelection? selection;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal async Task<CliIndexBuild> BuildAsync(CoordinatorUpdate update, CancellationToken token)
    {
        var options = update.Options with { TimeoutSeconds = 300 };
        CliSdkSelection selected;
        try { selected = CliSdkSelection.Capture(Path.GetDirectoryName(options.Target)!); }
        catch (CliFailure failure) when (selection is not null && failure.Code == "CLI_SDK_UNAVAILABLE")
        { throw new CliFailure("COORDINATOR_SDK_RESTART_REQUIRED", 6); }
        if (selection is not null && !selection.SameSdk(selected))
            throw new CliFailure("COORDINATOR_SDK_RESTART_REQUIRED", 6);
        selected.VerifyRegistered(Path.GetDirectoryName(options.Target)!);
        // Locator initializes MSBuild environment variables. Capture the publication stamp after that
        // initialization, and bind the owner to SDK identity rather than a target's ancestor path list.
        var current = CliSdkSelection.Capture(Path.GetDirectoryName(options.Target)!);
        if (!selected.SameSdk(current)) throw new CliFailure("COORDINATOR_SDK_RESTART_REQUIRED", 6);
        selection ??= current;
        IndexSnapshot? previous = null;
        try
        {
            using var reader = IndexReader.Open(writer.IndexPath, writer.WorkspaceUuid);
            previous = reader.Snapshot();
        }
        catch (IndexStoreException error) when (error.Code == "INDEX_NO_SNAPSHOT" && update.Full) { }
        var build = await CliIndexer.BuildForPublicationAsync(options, previous, token);
        return build with { VerifySelection = PublicationGuard(current, Path.GetDirectoryName(options.Target)!, build.VerifySelection) };
    }

    internal static Action PublicationGuard(CliSdkSelection current, string directory, Action verifyBuildSelection)
        => () =>
        {
            CliSdkSelection selected;
            try { selected = CliSdkSelection.Capture(directory); }
            catch (CliFailure failure) when (failure.Code == "CLI_SDK_UNAVAILABLE")
            { throw new CliFailure("COORDINATOR_SDK_RESTART_REQUIRED", 6); }
            if (current != selected)
                throw new CliFailure("COORDINATOR_SDK_RESTART_REQUIRED", 6);
            current.VerifyRegistered(directory);
            verifyBuildSelection();
        };
}

// MSBuildLocator is process-global. Never label a changed global.json with the old registered SDK.
internal sealed record CliSdkSelection(string SdkPath, string Version, string Inputs)
{
    internal bool SameSdk(CliSdkSelection other) => CliPaths.SamePath(SdkPath, other.SdkPath) && Version == other.Version;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static CliSdkSelection Capture(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (string? ancestor = directory; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            var path = Path.Combine(ancestor, "global.json");
            CliPaths.RejectLinks(path);
            // Include absence: a newly nearer global.json changes the selection proof.
            var identity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
            hash.AppendData(Encoding.UTF8.GetBytes(identity + "\0"));
            if (!File.Exists(path)) { hash.AppendData([0]); continue; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 1024 * 1024) throw new CliFailure("COORDINATOR_SDK_RESTART_REQUIRED", 6);
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            hash.AppendData([1]);
            hash.AppendData(bytes);
        }
        foreach (var key in new[] { "PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR",
            "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildEnableWorkloadResolver" })
            hash.AppendData(Encoding.UTF8.GetBytes(key + "\0" + Environment.GetEnvironmentVariable(key) + "\0"));
        try
        {
            var sdk = Microsoft.Build.Locator.MSBuildLocator.QueryVisualStudioInstances(
                new Microsoft.Build.Locator.VisualStudioInstanceQueryOptions { WorkingDirectory = directory,
                    DiscoveryTypes = Microsoft.Build.Locator.DiscoveryType.DotNetSdk,
                    // A global.json SDK may live in a different installation from the registered one.
                    // Discover it for the guard; actual registration must still match before publication.
                    AllowAllDotnetLocations = true }).FirstOrDefault();
            if (sdk is null) throw new CliFailure("CLI_SDK_UNAVAILABLE", 4);
            return new(sdk.MSBuildPath, sdk.Version.ToString(), Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch (InvalidOperationException) { throw new CliFailure("CLI_SDK_UNAVAILABLE", 4); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void VerifyRegistered(string directory)
    {
        if (!SemanticEnvironment.TryRegister(directory, out _)) throw new CliFailure("CLI_SDK_UNAVAILABLE", 4);
        var actual = SemanticEnvironment.Describe(directory);
        if (!CliPaths.SamePath(actual.MsBuildPath, SdkPath) || actual.SdkVersion != Version)
            throw new CliFailure("COORDINATOR_SDK_RESTART_REQUIRED", 6);
    }
}
