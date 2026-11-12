namespace SharpDeps.Analysis.Core.Diagnostics;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Diagnostic destinations must not alias a protected input tree. This is not a
// security boundary against concurrent changes to the filesystem namespace.
internal static class MetricsPath
{
    private const int MaximumPath = 32768;
    private const uint ReadAttributes = 0x80;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint ReparsePoint = 0x400;
    private const uint DirectoryAttribute = 0x10;
    private const uint VolumeNameGuid = 1;

    internal static string Canonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Absolute diagnostic path required.");
        if (OperatingSystem.IsWindows())
            return WindowsPath(LocalPath(ValidateWindows(path)));

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index <= parts.Length; index++)
        {
            if (index > 0)
                current = Path.Combine(current, parts[index - 1]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception error) when (index > 0 && error is FileNotFoundException or DirectoryNotFoundException)
            {
                return Path.TrimEndingDirectorySeparator(full);
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new IOException("Linked or non-directory diagnostic path.");
        }
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static string ValidateWindows(string path)
    {
        // Inspect the original components first: GetFullPath can remove trailing
        // dots/spaces and would otherwise hide ambiguous Windows names.
        var raw = path.Replace('/', '\\');
        var rootLength = raw.Length >= 3 && Letter(raw[0]) && raw[1] == ':' && raw[2] == '\\' ? 3
            : raw.Length >= 7 && raw.StartsWith(@"\\?\", StringComparison.Ordinal)
                && Letter(raw[4]) && raw[5] == ':' && raw[6] == '\\' ? 7
            : IsVolumePath(raw) ? 49 : 0;
        if (rootLength == 0)
            throw new NotSupportedException("Only identified local volume paths are supported.");
        foreach (var part in raw[rootLength..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Ambiguous diagnostic path component.");
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$"
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'))
                throw new ArgumentException("Device diagnostic paths are unsupported.");
        }
        return Path.GetFullPath(raw);
    }

    private static string LocalPath(string full)
    {
        var root = Path.GetPathRoot(full)!;
        var name = IsVolumePath(root) ? root[4..^1]
            : root.StartsWith(@"\\?\", StringComparison.Ordinal) ? root.Substring(4, 2) : root[..2];
        var device = LocalDevice(name);
        var volume = new StringBuilder(50);
        if (!GetVolumeNameForVolumeMountPointW(root, volume, (uint)volume.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var volumeRoot = volume.ToString();
        if (volumeRoot.Length != 49 || !IsVolumePath(volumeRoot)
            || !device.Equals(LocalDevice(volumeRoot[4..^1]), StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Local volume identity is unavailable.");
        return volumeRoot + full[root.Length..];
    }

    private static string LocalDevice(string name)
    {
        // QueryDosDevice returns the current mapping first. Old, undeleted
        // mappings after its first terminator are not evidence of local storage.
        var result = new StringBuilder(1024);
        if (QueryDosDeviceW(name, result, (uint)result.Capacity) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var device = result.ToString().Split('\0')[0];
        const string prefix = @"\Device\HarddiskVolume";
        if (!device.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || device.Length == prefix.Length
            || device[prefix.Length..].Any(value => value is < '0' or > '9'))
            throw new NotSupportedException("Only identified local disk volumes are supported.");
        return device;
    }

    private static string WindowsPath(string full)
    {
        var root = Path.GetPathRoot(full)!;
        var parts = full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var canonical = root;
        for (var index = 0; index <= parts.Length; index++)
        {
            var candidate = index == 0 ? root : Path.Combine(canonical, parts[index - 1]);
            using var handle = CreateFileW(candidate, ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting,
                BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            var error = Marshal.GetLastWin32Error();
            if (handle.IsInvalid)
            {
                if (index == 0 || error is not (2 or 3))
                    throw new Win32Exception(error);
                for (var suffix = index - 1; suffix < parts.Length; suffix++)
                    canonical = Path.Combine(canonical, parts[suffix]);
                return Path.TrimEndingDirectorySeparator(canonical);
            }
            if (!GetFileInformationByHandleEx(handle, 9, out var attributes, 8))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((attributes.Attributes & ReparsePoint) != 0 || (attributes.Attributes & DirectoryAttribute) == 0)
                throw new IOException("Linked or non-directory diagnostic path.");
            canonical = FinalPath(handle);
        }
        return Path.TrimEndingDirectorySeparator(canonical);
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var capacity = 1024;
        while (capacity <= MaximumPath)
        {
            var result = new StringBuilder(capacity);
            var count = GetFinalPathNameByHandleW(handle, result, (uint)result.Capacity, VolumeNameGuid);
            if (count == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (count < result.Capacity)
            {
                var path = result.ToString();
                if (!IsVolumePath(path))
                    throw new NotSupportedException("Local volume identity is unavailable.");
                return path;
            }
            if (count >= MaximumPath)
                break;
            capacity = (int)count + 1;
        }
        throw new IOException("Diagnostic path exceeds the supported limit.");
    }

    private static bool Letter(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    private static bool IsVolumePath(string path) => path.Length >= 49
        && path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
        && path[47] == '}' && path[48] == '\\' && Guid.TryParse(path.AsSpan(11, 36), out _);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint Attributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileAttributeTagInfo information, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint QueryDosDeviceW(string name, StringBuilder target, uint capacity);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volume, uint capacity);
}
