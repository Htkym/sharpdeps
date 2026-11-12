namespace SharpDeps.Cli.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

internal enum OracleChildCommand
{
    ManagedDll, Restore, Pack
}
internal sealed record OracleCliIsolationSpec(string RunRoot, string DotnetHostPath, string DotnetRoot)
{
    internal OracleOwnedStopBinding? OwnedStop
    {
        get; init;
    }

    // Optional helper compatibility only; the real Fact2 entry uses RequireOwnerInput.
    internal static OracleCliIsolationSpec? FromOwnerInput(string? runRoot, string? hostPath, string? dotnetRoot)
    {
        if (runRoot is null && hostPath is null && dotnetRoot is null)
            return null;
        if (string.IsNullOrEmpty(runRoot) || string.IsNullOrEmpty(hostPath) || string.IsNullOrEmpty(dotnetRoot))
            throw new ArgumentException("Fact2 owner inputs must supply run root, dotnet host and dotnet root together.");
        var result = new OracleCliIsolationSpec(runRoot, hostPath, dotnetRoot);
        result.Validate();
        return result;
    }

    internal static OracleCliIsolationSpec RequireOwnerInput(string? runRoot, string? hostPath, string? dotnetRoot)
        => FromOwnerInput(runRoot, hostPath, dotnetRoot)
            ?? throw new InvalidOperationException("Fact2 requires all explicit owner inputs before fixture creation.");

    internal string Workspace => RunRoot + "\\workspace";
    internal string Packages => RunRoot + "\\packages";

    internal void Validate()
    {
        OracleChildEnvironmentPlanner.ValidateRunRoot(RunRoot);
        if (!Same(DotnetRoot, @"C:\Program Files\dotnet") || !Same(DotnetHostPath, @"C:\Program Files\dotnet\dotnet.exe"))
            throw new ArgumentException("Fact2 must use the explicitly verified dotnet host and root.");
    }

    internal OracleChildCommand ValidateLaunch(string hostPath, string workingDirectory, string[] arguments,
        string? packageRoot, string oracleAssembly)
    {
        Validate();
        if (!Same(hostPath, DotnetHostPath) || !Same(packageRoot, Packages))
            throw new ArgumentException("Fact2 host or package root differs from its owner inputs.");
        var config = RunRoot + "\\restore.config";
        foreach (var label in new[] { "alpha", "beta", "beta-v2", "beta-alternate" })
        {
            var producer = RunRoot + "\\package-build\\" + label;
            if (!Same(workingDirectory, producer))
                continue;
            var project = producer + "\\Package.csproj";
            if (arguments.SequenceEqual(new[] { "restore", project, "--configfile", config, "--verbosity", "quiet", "-p:NuGetAudit=false" }))
                return OracleChildCommand.Restore;
            var feed = RunRoot + (label == "beta-alternate" ? "\\alternate-feed" : "\\feed");
            if (arguments.SequenceEqual(new[] { "pack", project, "--no-restore", "--disable-build-servers",
                "-p:UseSharedCompilation=false", "-nodeReuse:false", "--configuration", "Release", "--output", feed, "--verbosity", "quiet" }))
                return OracleChildCommand.Pack;
            throw new ArgumentException("Fact2 producer arguments are outside its closed restore/pack contract.");
        }
        if (!Same(workingDirectory, Workspace))
            throw new ArgumentException("Fact2 working directory is outside its explicit fixture binding.");
        if (arguments.SequenceEqual(new[] { "restore", Workspace + "\\Consumer\\Consumer.csproj", "--configfile", config,
            "--verbosity", "quiet", "--force", "-p:RestorePackagesWithLockFile=true", "-p:NuGetAudit=false" }))
            return OracleChildCommand.Restore;
        if (arguments is [var dll, "--full-oracle", var request, var result]
            && Path.IsPathFullyQualified(oracleAssembly) && string.Equals(dll, oracleAssembly, StringComparison.Ordinal)
            && request.StartsWith(RunRoot + "\\", StringComparison.Ordinal)
            && request.EndsWith(".request.json", StringComparison.Ordinal))
        {
            var label = request[(RunRoot.Length + 1)..^".request.json".Length];
            if (label.Length > 0 && label.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
                && result == RunRoot + "\\" + label + ".result.json")
                return OracleChildCommand.ManagedDll;
        }
        throw new ArgumentException("Fact2 arguments are outside its closed restore/pack/full-oracle contract.");
    }

    internal void ValidateHostEnvironment(IReadOnlyDictionary<string, string?> source)
    {
        foreach (var name in new[] { "DOTNET_HOST_PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64" })
            if (source.TryGetValue(name, out var value) && value is not null
                && !Same(value, name == "DOTNET_HOST_PATH" ? DotnetHostPath : DotnetRoot))
                throw new ArgumentException("Fact2 inherited host setting differs from its verified host: " + name);
    }

    private static bool Same(string? first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}

// Test-side planning only. Lexical paths do not prove freshness, alias separation or ownership.
internal static class OracleChildEnvironmentPlanner
{
    private static readonly string[] InheritedNames =
    [
        "PATH", "PATHEXT", "SystemRoot", "windir", "ComSpec", "ProgramFiles",
        "ProgramFiles(x86)", "ProgramW6432", "TEMP", "TMP", "TMPDIR", "USERPROFILE", "HOME", "HOMEDRIVE",
        "HOMEPATH", "APPDATA", "LOCALAPPDATA", "NUGET_PACKAGES", "DOTNET_ROOT", "DOTNET_ROOT_X64",
        "DOTNET_ROOT_ARM64", "DOTNET_HOST_PATH"
    ];

    internal static OracleChildCommand Classify(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        // Legacy classification only; opt-in launches use ValidateLaunch instead.
        return arguments.Length == 0 ? OracleChildCommand.ManagedDll : arguments[0] switch
        {
            "restore" => OracleChildCommand.Restore,
            "pack" => OracleChildCommand.Pack,
            _ => OracleChildCommand.ManagedDll
        };
    }

    internal static Dictionary<string, string> Create(
        IReadOnlyDictionary<string, string?> inherited,
        OracleChildCommand command,
        string? packageRoot = null,
        OracleCliIsolationSpec? isolation = null)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        if (command is not (OracleChildCommand.ManagedDll or OracleChildCommand.Restore or OracleChildCommand.Pack))
            throw new ArgumentException("Oracle child command is unsupported.", nameof(command));
        var source = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in inherited)
            source.Add(pair.Key, pair.Value);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in InheritedNames)
            if (source.TryGetValue(name, out var value) && value is not null)
                result[name] = value;
        if (packageRoot is not null)
        {
            // IsPathFullyQualified is lexical and preserves the original Windows helper contract.
            if (!Path.IsPathFullyQualified(packageRoot))
                throw new ArgumentException("Oracle package root must be absolute.", nameof(packageRoot));
            result["NUGET_PACKAGES"] = packageRoot;
        }
        result["MSBUILDDISABLENODEREUSE"] = "1";
        result["MSBuildEnableWorkloadResolver"] = "false";
        result["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        result["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        result["DOTNET_NOLOGO"] = "1";
        if (command == OracleChildCommand.Restore)
        {
            result["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
            result["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            if (source.TryGetValue("DOTNET_CLI_HOME", out var cliHome) && cliHome is not null)
                result["DOTNET_CLI_HOME"] = cliHome;
        }
        if (isolation is null)
            return result;
        isolation.Validate();
        isolation.ValidateHostEnvironment(source);
        var runRoot = isolation.RunRoot;
        if (!string.Equals(packageRoot, isolation.Packages, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Isolated package root must match the explicit fixture cache.", nameof(packageRoot));
        if (command == OracleChildCommand.ManagedDll)
            return result;
        result["NUGET_PACKAGES"] = isolation.Packages;
        result["DOTNET_CLI_HOME"] = runRoot + "\\cli-home";
        result["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        result["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        result["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        result["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        result["NUGET_HTTP_CACHE_PATH"] = runRoot + "\\nuget-http";
        result["NUGET_SCRATCH"] = runRoot + "\\nuget-scratch";
        foreach (var name in new[] { "TEMP", "TMP", "TMPDIR" })
            result[name] = runRoot + "\\temp";
        return result;
    }

    internal static string ValidateRunRoot(string? runRoot)
    {
        const string prefix = "D:\\gitroot\\";
        if (runRoot is null || !runRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw InvalidRoot();
        var leaf = runRoot.AsSpan(prefix.Length);
        if (leaf.IsEmpty)
            throw InvalidRoot();
        foreach (var character in leaf)
            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
                throw InvalidRoot();
        var name = leaf.ToString();
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || name.Length == 4 && name[3] is >= '1' and <= '9'
                && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
            throw InvalidRoot();
        return runRoot;
    }

    private static ArgumentException InvalidRoot() => new(
        "Oracle isolation run root must be a canonical named direct child of D:\\gitroot; letters, digits, hyphens and underscores only.",
        "isolation");
}
