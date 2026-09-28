namespace SharpDeps.Analysis.Roslyn;

/// <summary>
/// Records the environment the semantic loader actually used. Quick and Semantic
/// runtime/SDK discovery must not be conflated, so this is reported separately.
/// </summary>
public sealed record SemanticEnvironmentInfo(
    string? DotnetPath,
    string? SdkVersion,
    string? MsBuildVersion,
    string? MsBuildPath,
    string? GlobalJsonPath,
    string? GlobalJsonSdkVersion,
    string? RoslynVersion,
    string? MsBuildLocatorVersion,
    string? HostRuntimeVersion,
    IReadOnlyList<string> Notes);

/// <summary>
/// Registers the MSBuild instance before any MSBuild type is used.
/// </summary>
/// <remarks>
/// MSBuildLocator must run before the loader touches MSBuild/Workspace types.
/// The registration method is kept in its own type and marked NoInlining so the
/// JIT cannot load Microsoft.Build assemblies earlier than intended.
/// </remarks>
public static class SemanticEnvironment
{
    private static Microsoft.Build.Locator.VisualStudioInstance? registeredInstance;

    /// <summary>
    /// Locates and registers an MSBuild instance. Returns false with a reason when
    /// no usable instance exists; the caller must then report a diagnosable
    /// failure instead of attempting a load.
    /// </summary>
    public static bool TryRegister(string workingDirectory, out string? failureReason)
    {
        return RegisterCore(workingDirectory, out failureReason);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool RegisterCore(string workingDirectory, out string? failureReason)
    {
        failureReason = null;
        try
        {
            if (!Microsoft.Build.Locator.MSBuildLocator.IsRegistered)
            {
                var selected = Microsoft.Build.Locator.MSBuildLocator.QueryVisualStudioInstances(
                    new Microsoft.Build.Locator.VisualStudioInstanceQueryOptions
                    {
                        WorkingDirectory = Path.GetFullPath(workingDirectory),
                        DiscoveryTypes = Microsoft.Build.Locator.DiscoveryType.DotNetSdk
                    }).FirstOrDefault();
                if (selected is null)
                {
                    failureReason =
                        "No MSBuild instance was found. Install the .NET SDK for the target projects "
                        + "(SharpDeps never downloads an SDK automatically).";
                    return false;
                }

                // Locator returns the SDK selected by global.json before newer installations.
                Microsoft.Build.Locator.MSBuildLocator.RegisterInstance(selected);
                registeredInstance = selected;
            }

            return true;
        }
        catch (Exception error)
        {
            failureReason = $"MSBuild registration failed: {error.Message}";
            return false;
        }
    }

    /// <summary>
    /// Describes the registered instance and the host runtime. Only call after a
    /// successful <see cref="TryRegister"/>.
    /// </summary>
    public static SemanticEnvironmentInfo Describe(string workingDirectory)
    {
        var notes = new List<string>();

        var msBuildPath = registeredInstance?.MSBuildPath;
        var msBuildVersion = ReadMsBuildVersion(msBuildPath);

        var sdkVersion = registeredInstance?.Version.ToString();
        var roslynVersion = typeof(Microsoft.CodeAnalysis.Compilation).Assembly.GetName().Version?.ToString();

        var globalJsonPath = FindGlobalJson(workingDirectory);
        var globalJsonSdk = globalJsonPath is null ? null : ReadGlobalJsonSdkVersion(globalJsonPath);
        if (globalJsonPath is not null)
        {
            notes.Add(
                globalJsonSdk is null
                    ? "global.json was found but its sdk.version could not be read."
                    : $"global.json requests SDK {globalJsonSdk}; the registered SDK is {sdkVersion}.");
        }

        return new SemanticEnvironmentInfo(
            DotnetPath: null,
            SdkVersion: sdkVersion,
            MsBuildVersion: msBuildVersion,
            MsBuildPath: msBuildPath,
            GlobalJsonPath: globalJsonPath,
            GlobalJsonSdkVersion: globalJsonSdk,
            RoslynVersion: roslynVersion,
            MsBuildLocatorVersion: typeof(Microsoft.Build.Locator.MSBuildLocator).Assembly
                .GetName()
                .Version?.ToString(),
            HostRuntimeVersion: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Notes: notes);
    }

    private static string? ReadMsBuildVersion(string? msBuildPath)
    {
        if (string.IsNullOrWhiteSpace(msBuildPath))
        {
            return null;
        }

        var msBuildAssembly = Path.Combine(msBuildPath, "MSBuild.dll");
        if (!File.Exists(msBuildAssembly))
        {
            return null;
        }

        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(msBuildAssembly);
        return version.ProductVersion ?? version.FileVersion;
    }

    private static string? FindGlobalJson(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "global.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? ReadGlobalJsonSdkVersion(string globalJsonPath)
    {
        try
        {
            using var stream = File.OpenRead(globalJsonPath);
            using var document = System.Text.Json.JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("sdk", out var sdk)
                && sdk.TryGetProperty("version", out var version))
            {
                return version.GetString();
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }
}
