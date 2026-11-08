namespace SharpDeps.Cli.Tests;

using System.Runtime.InteropServices;
using System.Text;
using SharpDeps.Analysis.Core.Diagnostics;
using Xunit;
using Xunit.Abstractions;

public sealed class MetricsPathTests(ITestOutputHelper output)
{
    [Fact]
    public void ActualSdkShortAliasCannotCreateDiagnosticOutput()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var sdk = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var shortName = new StringBuilder(32768);
        var count = GetShortPathNameW(sdk, shortName, (uint)shortName.Capacity);
        Assert.InRange(count, 1u, (uint)shortName.Capacity - 1);
        var alias = shortName.ToString();
        if (alias.Equals(sdk, StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("Actual SDK short-alias check unavailable: this volume returned no distinct 8.3 name.");
            return;
        }
        var suffix = Path.Combine(".sharpdeps", "metrics-refused-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(alias, suffix);
        var actual = Path.Combine(sdk, suffix);
        Assert.False(Directory.Exists(actual));
        Assert.False(PhaseMetrics.IsOutputIsolated(directory, alias, [sdk]));
        Assert.Null(PhaseMetrics.TryStart(directory, "fixture", alias));
        Assert.False(Directory.Exists(actual));
        Assert.False(PhaseMetrics.Enabled);
        output.WriteLine("Actual SDK 8.3 alias resolved and rejected; diagnostic directory was not created.");
    }

    [Fact]
    public void VolumeAndExtendedDriveAliasesCannotHideDependencyOverlap()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var fixture = new Fixture();
        var volume = MetricsPath.Canonicalize(fixture.Root);
        Assert.StartsWith(@"\\?\Volume{", volume, StringComparison.OrdinalIgnoreCase);
        var output = Path.Combine(volume, ".sharpdeps", "metrics");
        Assert.False(PhaseMetrics.IsOutputIsolated(output, fixture.Root, [fixture.Root]));
        Assert.False(PhaseMetrics.IsOutputIsolated(Path.Combine(fixture.Root, ".sharpdeps", "metrics"), @"\\?\" + fixture.Root, [volume]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact]
    public void ExistingShortAndLongNamesHaveTheSamePhysicalIdentity()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var sdk = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var shortName = new StringBuilder(32768);
        var count = GetShortPathNameW(sdk, shortName, (uint)shortName.Capacity);
        Assert.InRange(count, 1u, (uint)shortName.Capacity - 1);
        Assert.Equal(MetricsPath.Canonicalize(sdk), MetricsPath.Canonicalize(shortName.ToString()));
    }

    [Fact]
    public void MissingSuffixAndOrdinaryTildeNamesRemainSupported()
    {
        using var fixture = new Fixture();
        var directory = Path.Combine(fixture.Root, ".sharpdeps", "ordinary~name", "metrics");
        Assert.True(PhaseMetrics.IsOutputIsolated(directory, fixture.Root, [Path.Combine(fixture.Root, "dependency")]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
        using (var run = PhaseMetrics.TryStart(directory, "fixture", fixture.Root))
        {
            Assert.NotNull(run);
            run.Complete();
        }
        Assert.Single(Directory.GetFiles(directory, "*.jsonl"));
        Assert.False(PhaseMetrics.Enabled);
    }

    [Theory]
    [InlineData(@"\\server\share\metrics")]
    [InlineData(@"\\?\UNC\server\share\metrics")]
    [InlineData(@"\\.\pipe\metrics")]
    [InlineData(@"\??\C:\metrics")]
    [InlineData(@"C:metrics")]
    public void RemoteDeviceAndRelativePathsAreRejectedBeforeOpening(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.ThrowsAny<Exception>(() => MetricsPath.Canonicalize(path));
        Assert.Null(PhaseMetrics.Start(path, "fixture"));
        Assert.False(PhaseMetrics.Enabled);
    }

    [Theory]
    [InlineData("ambiguous.")]
    [InlineData("ambiguous ")]
    [InlineData(@"ambiguous.\child")]
    [InlineData(@"ambiguous \child")]
    [InlineData(@"ambiguous.\..\child")]
    [InlineData("NUL")]
    [InlineData("aux.log")]
    [InlineData("CON .txt")]
    [InlineData("COM1")]
    [InlineData("LPT\u00b2.txt")]
    public void OriginalAmbiguousComponentsAreRejectedBeforeNormalization(string suffix)
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var fixture = new Fixture();
        var directory = Path.Combine(fixture.Root, ".sharpdeps", suffix);
        Assert.Throws<ArgumentException>(() => MetricsPath.Canonicalize(directory));
        Assert.Null(PhaseMetrics.Start(directory, "fixture"));
        Assert.Null(PhaseMetrics.TryStart(directory, "fixture", fixture.Root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact]
    public void UnknownDependencyVolumeDisablesTheSinkWithoutCreatingOutput()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var fixture = new Fixture();
        var directory = Path.Combine(fixture.Root, ".sharpdeps", "metrics");
        const string unknown = @"\\?\Volume{00000000-0000-0000-0000-000000000000}\dependency";
        Assert.False(PhaseMetrics.IsOutputIsolated(directory, fixture.Root, [unknown]));
        Assert.Null(PhaseMetrics.Start(unknown, "fixture"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact]
    public void DisabledMetricsDoesNotInspectEvenAnInvalidWorkspace()
    {
        Assert.Null(PhaseMetrics.TryStart(null, "fixture", "\0"));
        Assert.Null(PhaseMetrics.TryStart(" ", "fixture", "\0"));
        Assert.False(PhaseMetrics.Enabled);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "sharpdeps-path-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint capacity);
}
