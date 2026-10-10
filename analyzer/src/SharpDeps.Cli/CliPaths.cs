namespace SharpDeps.Cli;

using System.Security.Cryptography;
using System.Text.Json;
using SharpDeps.Query;

internal sealed class CliFailure(string code, int exitCode) : Exception(code)
{
    public string Code { get; } = code;
    public int ExitCode { get; } = exitCode;
}

internal sealed record CliWorkspaceState(int SchemaVersion, Guid WorkspaceUuid, string CursorKey)
{
    public static CliWorkspaceState Open(string root, bool create)
    {
        var path = CliPaths.InRoot(root, ".sharpdeps/workspace.json");
        CliPaths.RejectLinks(path);
        if (!File.Exists(path))
        {
            if (!create) throw new CliFailure("WORKSPACE_STATE_MISSING", 3);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            CliPaths.RejectLinks(path);
            var state = new CliWorkspaceState(1, Guid.NewGuid(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            // Publish atomically: readers must never observe a partially written identity/key.
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, state, QueryJson.Options);
                    stream.Flush(true);
                }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                try { File.Move(temporary, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { } // A concurrent first owner won; use its identity.
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        CliPaths.RejectLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 4096) throw new CliFailure("WORKSPACE_STATE_INVALID", 6);
        CliWorkspaceState? saved;
        try { saved = JsonSerializer.Deserialize<CliWorkspaceState>(input, QueryJson.Options); }
        catch (JsonException) { throw new CliFailure("WORKSPACE_STATE_INVALID", 6); }
        if (saved is null || saved.SchemaVersion != 1 || saved.WorkspaceUuid == Guid.Empty)
            throw new CliFailure("WORKSPACE_STATE_INVALID", 6);
        try { if (Convert.FromBase64String(saved.CursorKey).Length != 32) throw new FormatException(); }
        catch (Exception error) when (error is FormatException or ArgumentNullException)
        { throw new CliFailure("WORKSPACE_STATE_INVALID", 6); }
        return saved;
    }
}

internal static class CliPaths
{
    internal static string Root(string value)
    {
        if (!Path.IsPathFullyQualified(value) || IsNetwork(value))
            throw new CliFailure("ROOT_INVALID", 5);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (IsNetwork(root)) throw new CliFailure("ROOT_INVALID", 5);
        RejectLinks(root);
        if (!Directory.Exists(root)) throw new CliFailure("ROOT_INVALID", 5);
        return root;
    }

    internal static string InRoot(string root, string value)
    {
        var path = Path.GetFullPath(value, root);
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative) || IsNetwork(value) || IsNetwork(path))
            throw new CliFailure("PATH_OUTSIDE_ROOT", 5);
        RejectLinks(path);
        return path;
    }

    private static bool IsNetwork(string path) => path.StartsWith(@"\\", StringComparison.Ordinal)
        || path.StartsWith("//", StringComparison.Ordinal);

    internal static void RejectLinks(string path)
    {
        // Includes ancestors above root; missing leaves are allowed for a new trusted index.
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new CliFailure("PATH_LINK_FORBIDDEN", 5);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
