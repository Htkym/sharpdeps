namespace SharpDeps.Index;

using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

/// <summary>Proves the SQLite-owned file and the current path identify the same object.</summary>
internal static class IndexPhysicalIdentity
{
    // SQLite file-control contract and Windows FILE_ID_INFO, not a private VFS struct layout.
    private const int Win32GetHandle = 29;
    private const int FileIdInfo = 18;
    // HAS_MOVED alone does not prove device identity for every Unix VFS.
    internal static bool Supported => OperatingSystem.IsWindows();

    internal static bool IsCurrent(SqliteConnection connection, string path)
    {
        if (!Supported) return false;
        try
        {
            var nativeFile = IntPtr.Zero;
            // SQLITE_FCNTL_WIN32_GET_HANDLE returns a borrowed HANDLE; never close it.
            if (sqlite3_file_control(connection.Handle!.DangerousGetHandle(), "main", Win32GetHandle, ref nativeFile) != 0
                || nativeFile == IntPtr.Zero || nativeFile == new IntPtr(-1)
                || !GetFileInformationByHandleEx(nativeFile, FileIdInfo, out var sqliteIdentity, 24))
                return false;
            using var current = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandleEx(current.SafeFileHandle.DangerousGetHandle(), FileIdInfo, out var pathIdentity, 24))
                return false;
            return sqliteIdentity.Volume == pathIdentity.Volume
                && sqliteIdentity.IdLow == pathIdentity.IdLow && sqliteIdentity.IdHigh == pathIdentity.IdHigh;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or System.Security.SecurityException)
        { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        internal ulong Volume;
        internal ulong IdLow;
        internal ulong IdHigh;
    }

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_file_control(IntPtr database,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string databaseName, int operation, ref IntPtr value);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(IntPtr handle, int informationClass,
        out FileIdentity information, uint bufferSize);
}
