namespace SharpDeps.Observation
{
    using System;
    using System.ComponentModel;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;
    using Microsoft.Win32.SafeHandles;

    // Read-only local-volume identity. No directory creation or file-content access.
    public static class ObservationPaths
    {
        private const uint ReadAttributes = 0x80;
        private const uint ShareAll = 7;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint VolumeNameGuid = 1;
        private const uint ReparsePoint = 0x400;
        private const uint DirectoryAttribute = 0x10;
        private const int MaximumPath = 32768;

        public static string LocalPath(string path)
        {
            string full = Validate(path);
            string root = Path.GetPathRoot(full);
            string name = IsVolumePath(root) ? root.Substring(4, root.Length - 5)
                : root.StartsWith(@"\\?\", StringComparison.Ordinal) ? root.Substring(4, 2) : root.Substring(0, 2);
            string device = LocalDevice(name);
            var volume = new StringBuilder(50);
            if (!GetVolumeNameForVolumeMountPointW(root, volume, (uint)volume.Capacity))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            string volumeRoot = volume.ToString();
            if (volumeRoot.Length != 49 || !IsVolumePath(volumeRoot)
                || !String.Equals(device, LocalDevice(volumeRoot.Substring(4, volumeRoot.Length - 5)), StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Local volume identity is unavailable.");
            return volumeRoot + full.Substring(root.Length);
        }

        public static string Canonicalize(string path)
        {
            string full = LocalPath(path);
            string root = Path.GetPathRoot(full);
            string[] parts = full.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            string canonical = null;
            for (int index = 0; index <= parts.Length; index++)
            {
                string candidate = index == 0 ? root : Path.Combine(canonical, parts[index - 1]);
                using (SafeFileHandle handle = CreateFileW(candidate, ReadAttributes, ShareAll,
                    IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (handle.IsInvalid)
                    {
                        if (index == 0 || (error != 2 && error != 3))
                            throw new Win32Exception(error);
                        for (int suffix = index - 1; suffix < parts.Length; suffix++)
                            canonical = Path.Combine(canonical, parts[suffix]);
                        return TrimSeparator(canonical);
                    }
                    FileAttributeTagInfo attributes;
                    if (!GetFileInformationByHandleEx(handle, 9, out attributes, 8))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if ((attributes.Attributes & ReparsePoint) != 0)
                        throw new IOException("Linked path is unsupported.");
                    if (index < parts.Length && (attributes.Attributes & DirectoryAttribute) == 0)
                        throw new IOException("Path ancestor is not a directory.");
                    canonical = FinalPath(handle);
                }
            }
            return TrimSeparator(canonical);
        }

        public static string ShortPath(string path)
        {
            string full = Canonicalize(path);
            int capacity = 1024;
            while (capacity <= MaximumPath)
            {
                var result = new StringBuilder(capacity);
                uint count = GetShortPathNameW(full, result, (uint)result.Capacity);
                if (count == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (count < result.Capacity)
                    return result.ToString();
                capacity = checked((int)count + 1);
            }
            throw new IOException("Path exceeds observation limit.");
        }

        private static string FinalPath(SafeFileHandle handle)
        {
            int capacity = 1024;
            while (capacity <= MaximumPath)
            {
                var result = new StringBuilder(capacity);
                uint count = GetFinalPathNameByHandleW(handle, result, (uint)result.Capacity, VolumeNameGuid);
                if (count == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (count < result.Capacity)
                {
                    string path = result.ToString();
                    if (!IsVolumePath(path))
                        throw new NotSupportedException("Local volume identity is unavailable.");
                    return path;
                }
                capacity = checked((int)count + 1);
            }
            throw new IOException("Path exceeds observation limit.");
        }

        private static string Validate(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("Absolute observation path required.");
            string rawRoot = Path.GetPathRoot(path);
            foreach (string part in path.Substring(rawRoot.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                if (part != "." && part != ".." && (part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal)))
                    throw new ArgumentException("Ambiguous observation path component.");
            string full = Path.GetFullPath(path);
            bool drive = full.Length >= 3 && Letter(full[0]) && full[1] == ':' && full[2] == '\\';
            bool extendedDrive = full.Length >= 7 && full.StartsWith(@"\\?\", StringComparison.Ordinal)
                && Letter(full[4]) && full[5] == ':' && full[6] == '\\';
            if (!drive && !extendedDrive && !IsVolumePath(full))
                throw new NotSupportedException("Only identified local volume paths are supported.");
            string root = Path.GetPathRoot(full);
            foreach (string part in full.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal)
                    || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException("Ambiguous observation path component.");
                string stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$" || stem == "CLOCK$"
                    || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                        || stem.StartsWith("LPT", StringComparison.Ordinal)) && ((stem[3] >= '1' && stem[3] <= '9') || stem[3] == '\u00b9' || stem[3] == '\u00b2' || stem[3] == '\u00b3')))
                    throw new ArgumentException("Device observation paths are unsupported.");
            }
            return full;
        }

        private static string LocalDevice(string name)
        {
            // Query the local DOS-device namespace before any path open. Network
            // redirectors, SUBST and unknown devices are refused, not traversed.
            var result = new StringBuilder(1024);
            if (QueryDosDeviceW(name, result, (uint)result.Capacity) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return CurrentLocalDevice(result.ToString());
        }

        public static string CurrentLocalDevice(string mappings)
        {
            // Only the first mapping is current. Later MULTI_SZ entries are history.
            string device = (mappings ?? String.Empty).Split('\0')[0];
            const string prefix = @"\Device\HarddiskVolume";
            if (!device.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || device.Length == prefix.Length)
                throw new NotSupportedException("Only identified local disk volumes are supported.");
            for (int index = prefix.Length; index < device.Length; index++)
                if (device[index] < '0' || device[index] > '9')
                    throw new NotSupportedException("Only identified local disk volumes are supported.");
            return device;
        }

        private static bool Letter(char value)
        {
            return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z');
        }

        private static bool IsVolumePath(string path)
        {
            Guid volume;
            return path.Length >= 49 && path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
                && path[47] == '}' && path[48] == '\\' && Guid.TryParse(path.Substring(11, 36), out volume);
        }

        private static string TrimSeparator(string path)
        {
            string root = Path.GetPathRoot(path);
            return path.Length > root.Length ? path.TrimEnd('\\', '/') : path;
        }

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
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path,
            uint capacity, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint capacity);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern uint QueryDosDeviceW(string name, StringBuilder target, uint capacity);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volume, uint capacity);
    }
}
