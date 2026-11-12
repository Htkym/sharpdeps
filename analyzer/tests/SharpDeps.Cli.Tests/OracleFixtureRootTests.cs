namespace SharpDeps.Cli.Tests;

using Xunit;
using Xunit.Abstractions;

public sealed class OracleFixtureRootTests(ITestOutputHelper output)
{
    [WindowsFixtureRootFact]
    [Trait("ControlledSdkOracle", "true")]
    public void CreatesFreshRootAndRejectsExistingEntriesWithoutChangingBytes()
    {
        var root = NewInputPath("directory");
        OracleFixtureRoot.CreateNew(root);
        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        var marker = Path.Combine(root, "marker.bin");
        var config = Path.Combine(root, "restore.config");
        var markerBytes = "owned marker bytes\n"u8.ToArray();
        var configBytes = "<configuration />\n"u8.ToArray();
        WriteNew(marker, markerBytes);
        WriteNew(config, configBytes);

        Assert.Throws<IOException>(() => OracleFixtureRoot.CreateNew(root));
        Assert.True(Directory.Exists(root));
        Assert.Equal(markerBytes, File.ReadAllBytes(marker));
        Assert.Equal(configBytes, File.ReadAllBytes(config));

        var file = NewInputPath("file");
        var fileBytes = "owned file-entry bytes\n"u8.ToArray();
        WriteNew(file, fileBytes);
        Assert.Throws<IOException>(() => OracleFixtureRoot.CreateNew(file));
        Assert.True(File.Exists(file));
        Assert.False(Directory.Exists(file));
        Assert.Equal(fileBytes, File.ReadAllBytes(file));
    }

    private string NewInputPath(string role)
    {
        var path = @"D:\gitroot\sharpdeps-atomic-fixture-test-" + role + "-" + Guid.NewGuid().ToString("N");
        output.WriteLine("Unused input path; retained as test evidence if created: " + path);
        return path;
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(bytes);
    }
}

internal sealed class WindowsFixtureRootFactAttribute : FactAttribute
{
    public WindowsFixtureRootFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "The atomic fixture-root filesystem operation requires Windows.";
    }
}
