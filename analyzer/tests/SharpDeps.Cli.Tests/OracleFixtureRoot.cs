namespace SharpDeps.Cli.Tests;

using System.ComponentModel;
using System.Runtime.InteropServices;

internal static class OracleFixtureRoot
{
    internal static void CreateNew(string root)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Atomic oracle fixture-root creation requires Windows.");
        OracleChildEnvironmentPlanner.ValidateRunRoot(root);
        if (!CreateDirectoryW(root, 0))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException("Atomic fixture-root creation failed: " + root, new Win32Exception(error));
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, nint attributes);
}
