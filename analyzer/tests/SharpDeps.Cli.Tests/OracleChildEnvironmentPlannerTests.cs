namespace SharpDeps.Cli.Tests;

using Xunit;

public sealed class OracleChildEnvironmentPlannerTests
{
    private const string RunRoot = @"D:\gitroot\sharpdeps-fact2-owner-synthetic";
    private const string Host = @"C:\Program Files\dotnet\dotnet.exe";
    private const string DotnetRoot = @"C:\Program Files\dotnet";
    private const string Assembly = @"D:\gitroot\synthetic-build\SharpDeps.Cli.Tests.dll";
    private static readonly OracleCliIsolationSpec Isolation = new(RunRoot, Host, DotnetRoot);

    [WindowsEnvironmentFact]
    public void LegacyEnvironmentAndClassificationRemainUnchanged()
    {
        var source = Source();
        var common = source.Where(p => p.Value is not null && p.Key is not
            ("DOTNET_CLI_HOME" or "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH" or "NUGET_HTTP_CACHE_PATH" or "UNRECOGNIZED"
                or "SHARPDEPS_FACT2_FIXTURE_ROOT" or "SHARPDEPS_FACT2_DOTNET_HOST" or "SHARPDEPS_FACT2_DOTNET_ROOT"))
            .ToDictionary(p => p.Key, p => p.Value!, StringComparer.OrdinalIgnoreCase);
        common["MSBUILDDISABLENODEREUSE"] = "1";
        common["MSBuildEnableWorkloadResolver"] = "false";
        common["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        common["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        common["DOTNET_NOLOGO"] = "1";
        foreach (var arguments in new[] { Array.Empty<string>(), new[] { "pack" }, new[] { "build" }, new[] { "--version" }, new[] { "restore" } })
        {
            var expected = new Dictionary<string, string>(common, StringComparer.OrdinalIgnoreCase);
            if (arguments is ["restore"])
            {
                expected["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
                expected["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
                expected["DOTNET_CLI_HOME"] = source["DOTNET_CLI_HOME"]!;
            }
            var command = OracleChildEnvironmentPlanner.Classify(arguments);
            Assert.Equal(arguments is ["restore"] ? OracleChildCommand.Restore
                : arguments is ["pack"] ? OracleChildCommand.Pack : OracleChildCommand.ManagedDll, command);
            EqualMap(expected, OracleChildEnvironmentPlanner.Create(source, command));
            expected["NUGET_PACKAGES"] = Isolation.Packages;
            EqualMap(expected, OracleChildEnvironmentPlanner.Create(source, command, Isolation.Packages, isolation: null));
        }
    }

    [WindowsEnvironmentFact]
    public void Fact2RequiresOwnerInputsBeforeFixtureCreation()
    {
        Assert.Throws<InvalidOperationException>(() => OracleCliIsolationSpec.RequireOwnerInput(null, null, null));
        Assert.Throws<ArgumentException>(() => OracleCliIsolationSpec.RequireOwnerInput(RunRoot, null, DotnetRoot));
        Assert.Equal(Isolation, OracleCliIsolationSpec.RequireOwnerInput(RunRoot, Host, DotnetRoot));
    }

    [WindowsEnvironmentFact]
    public void ExplicitOwnerInputsAndKnownLaunchesAreRequired()
    {
        Assert.Null(OracleCliIsolationSpec.FromOwnerInput(null, null, null));
        Assert.Equal(Isolation, OracleCliIsolationSpec.FromOwnerInput(RunRoot, Host, DotnetRoot));
        Assert.Throws<ArgumentException>(() => OracleCliIsolationSpec.FromOwnerInput(RunRoot, null, DotnetRoot));
        Assert.Throws<ArgumentException>(() => OracleCliIsolationSpec.FromOwnerInput(null, Host, null));
        Assert.Throws<ArgumentException>(() => OracleCliIsolationSpec.FromOwnerInput("", Host, DotnetRoot));
        foreach (var root in new[] { @"C:\temp\owner", @"D:\gitroot", RunRoot + @"\nested", @"D:\gitroot\..\outside", @"D:\gitroot\CON", @"D:\gitroot\owner." })
            Assert.Throws<ArgumentException>(() => OracleCliIsolationSpec.FromOwnerInput(root, Host, DotnetRoot));
        Assert.Throws<ArgumentException>(() => OracleCliIsolationSpec.FromOwnerInput(RunRoot, "dotnet", DotnetRoot));
        Assert.Throws<ArgumentException>(() => OracleCliIsolationSpec.FromOwnerInput(RunRoot, Host, @"D:\other-sdk"));
        foreach (var label in new[] { "alpha", "beta", "beta-v2", "beta-alternate" })
        {
            var cwd = RunRoot + @"\package-build\" + label;
            Assert.Equal(OracleChildCommand.Restore, Validate(cwd, ProducerRestore(label)));
            Assert.Equal(OracleChildCommand.Pack, Validate(cwd, ProducerPack(label)));
        }
        Assert.Equal(OracleChildCommand.Restore, Validate(Isolation.Workspace, WorkspaceRestore()));
        Assert.Equal(OracleChildCommand.ManagedDll, Validate(Isolation.Workspace, Proof()));
        Assert.Throws<ArgumentException>(() => Isolation.ValidateLaunch("dotnet", Isolation.Workspace, Proof(), Isolation.Packages, Assembly));
        Assert.Throws<ArgumentException>(() => Validate(RunRoot + @"\other-workspace", Proof()));
        Assert.Throws<ArgumentException>(() => Isolation.ValidateLaunch(Host, Isolation.Workspace, Proof(), RunRoot + @"\other-packages", Assembly));
        foreach (var args in new[] { Array.Empty<string>(), new[] { "build" }, new[] { "--version" },
            WorkspaceRestore().Concat(new[] { "-p:CustomAfterMicrosoftCommonTargets=external" }).ToArray(),
            new[] { Assembly, "--semantic-encoding-oracle", RunRoot },
            new[] { @"D:\gitroot\substituted.dll", "--full-oracle", RunRoot + @"\prime.request.json", RunRoot + @"\prime.result.json" },
            new[] { Assembly, "--full-oracle", RunRoot + @"\..\prime.request.json", RunRoot + @"\prime.result.json" },
            new[] { Assembly, "--full-oracle", RunRoot + @"\prime.request.json", RunRoot + @"\other.result.json" } })
            Assert.Throws<ArgumentException>(() => Validate(Isolation.Workspace, args));
        Assert.Throws<ArgumentException>(() => Validate(RunRoot + @"\package-build\unknown", ProducerPack("alpha")));
        Assert.Throws<ArgumentException>(() => Validate(RunRoot + @"\package-build\alpha", WorkspaceRestore()));
    }

    [WindowsEnvironmentFact]
    public void PrivateCliControlsDoNotChangeTheManagedProofEnvironmentOrInputMap()
    {
        var source = Source();
        source["DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK"] = "true";
        source[OracleOwnedStop.PathKey] = @"D:\gitroot\owner-synthetic\owned-stop.request.json";
        source[OracleOwnedStop.NonceKey] = new string('A', 64);
        var before = source.ToArray();
        foreach (var command in new[] { OracleChildCommand.Restore, OracleChildCommand.Pack })
        {
            var actual = OracleChildEnvironmentPlanner.Create(source, command, Isolation.Packages, Isolation);
            Assert.Equal(Isolation.Packages, actual["NUGET_PACKAGES"]);
            Assert.Equal(RunRoot + @"\cli-home", actual["DOTNET_CLI_HOME"]);
            Assert.Equal(RunRoot + @"\nuget-http", actual["NUGET_HTTP_CACHE_PATH"]);
            Assert.Equal(RunRoot + @"\nuget-scratch", actual["NUGET_SCRATCH"]);
            foreach (var name in new[] { "TEMP", "TMP", "TMPDIR" })
                Assert.Equal(RunRoot + @"\temp", actual[name]);
            Assert.Equal("false", actual["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"]);
            Assert.Equal("false", actual["DOTNET_GENERATE_ASPNET_CERTIFICATE"]);
            Assert.Equal("0", actual["DOTNET_CLI_USE_MSBUILD_SERVER"]);
            Assert.False(actual.ContainsKey("DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK"));
            Assert.Equal("true", actual["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"]);
        }
        EqualMap(OracleChildEnvironmentPlanner.Create(source, OracleChildCommand.ManagedDll, Isolation.Packages),
            OracleChildEnvironmentPlanner.Create(source, OracleChildCommand.ManagedDll, Isolation.Packages, Isolation));
        Assert.Equal(before, source.ToArray());
        foreach (var command in new[] { OracleChildCommand.ManagedDll, OracleChildCommand.Restore, OracleChildCommand.Pack })
        {
            var actual = OracleChildEnvironmentPlanner.Create(source, command, Isolation.Packages, Isolation);
            foreach (var name in new[] { "SHARPDEPS_FACT2_FIXTURE_ROOT", "SHARPDEPS_FACT2_DOTNET_HOST", "SHARPDEPS_FACT2_DOTNET_ROOT", OracleOwnedStop.PathKey, OracleOwnedStop.NonceKey })
                Assert.False(actual.ContainsKey(name));
            Assert.Throws<ArgumentException>(() => OracleChildEnvironmentPlanner.Create(source, command, null, Isolation));
            Assert.Throws<ArgumentException>(() => OracleChildEnvironmentPlanner.Create(source, command, RunRoot + @"\other-packages", Isolation));
        }
        foreach (var name in new[] { "DOTNET_HOST_PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64" })
        {
            var wrong = new Dictionary<string, string?>(source, StringComparer.OrdinalIgnoreCase) { [name] = @"D:\other-sdk" };
            Assert.Throws<ArgumentException>(() => OracleChildEnvironmentPlanner.Create(wrong, OracleChildCommand.ManagedDll, Isolation.Packages, Isolation));
        }
    }

    private static OracleChildCommand Validate(string cwd, string[] args)
        => Isolation.ValidateLaunch(Host, cwd, args, Isolation.Packages, Assembly);
    private static string[] ProducerRestore(string label)
        => ["restore", RunRoot + @"\package-build\" + label + @"\Package.csproj", "--configfile", RunRoot + @"\restore.config", "--verbosity", "quiet", "-p:NuGetAudit=false"];
    private static string[] ProducerPack(string label)
        => ["pack", RunRoot + @"\package-build\" + label + @"\Package.csproj", "--no-restore", "--disable-build-servers",
            "-p:UseSharedCompilation=false", "-nodeReuse:false", "--configuration", "Release", "--output",
            RunRoot + (label == "beta-alternate" ? @"\alternate-feed" : @"\feed"), "--verbosity", "quiet"];
    private static string[] WorkspaceRestore()
        => ["restore", Isolation.Workspace + @"\Consumer\Consumer.csproj", "--configfile", RunRoot + @"\restore.config", "--verbosity", "quiet",
            "--force", "-p:RestorePackagesWithLockFile=true", "-p:NuGetAudit=false"];
    private static string[] Proof()
        => [Assembly, "--full-oracle", RunRoot + @"\prime.request.json", RunRoot + @"\prime.result.json"];
    private static void EqualMap(Dictionary<string, string> expected, Dictionary<string, string> actual)
        => Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(), actual.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray());
    private static Dictionary<string, string?> Source()
    {
        var source = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "PATH", "PATHEXT", "SystemRoot", "windir", "ComSpec", "ProgramFiles", "ProgramFiles(x86)",
            "ProgramW6432", "TEMP", "TMP", "TMPDIR", "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "NUGET_PACKAGES" })
            source[name] = "inherited:" + name;
        foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64" })
            source[name] = DotnetRoot;
        source["DOTNET_HOST_PATH"] = Host;
        source["DOTNET_CLI_HOME"] = @"C:\shared-home";
        source["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "true";
        source["NUGET_HTTP_CACHE_PATH"] = @"C:\shared-http";
        source["UNRECOGNIZED"] = "must not leak";
        source["SHARPDEPS_FACT2_FIXTURE_ROOT"] = RunRoot;
        source["SHARPDEPS_FACT2_DOTNET_HOST"] = Host;
        source["SHARPDEPS_FACT2_DOTNET_ROOT"] = DotnetRoot;
        source["NULL_VALUE"] = null;
        return source;
    }
}

internal sealed class WindowsEnvironmentFactAttribute : FactAttribute
{
    public WindowsEnvironmentFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "The opt-in Fact2 owner binding uses the verified Windows host and synthetic D paths.";
    }
}
