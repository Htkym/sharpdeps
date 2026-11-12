using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpDeps.StandardJobOwner;

internal sealed record InputPin(string Path, string Sha256);
internal sealed record EnvironmentEntry(string Name, string Value);
internal sealed record LaunchPlan(
    string Application, string ApplicationSha256, string[] Arguments,
    string WorkingDirectory, string RunDirectory, EnvironmentEntry[] Environment,
    InputPin[] Inputs, string? StandardTrxPath,
    int WorkSeconds, int CleanupSeconds, int DescendantGraceSeconds, long LogCapBytes, bool EnableOwnedStop = false)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal void Validate()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Windows is required.");
        Canonical(Application);
        Canonical(WorkingDirectory);
        Canonical(RunDirectory);
        if (!Application.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Application must be an explicit .exe, not a shell command.");
        if (!string.Equals(Path.GetDirectoryName(RunDirectory), @"D:\gitroot", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("RunDirectory must be a fresh direct child of D:\\gitroot.");
        if (!WorkingDirectory.StartsWith(@"D:\gitroot\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("WorkingDirectory must be under D:\\gitroot.");
        if (Directory.Exists(RunDirectory) || File.Exists(RunDirectory))
            throw new ArgumentException("RunDirectory already exists.");
        NoReparseParents(WorkingDirectory);
        NoReparseParents(Path.GetDirectoryName(RunDirectory)!);
        if (!Directory.Exists(WorkingDirectory))
            throw new ArgumentException("WorkingDirectory is absent.");
        if (Arguments is null || Environment is null || Inputs is null)
            throw new ArgumentException("Arguments, Environment and Inputs must be explicit arrays.");
        foreach (string arg in Arguments)
            Text(arg);
        if (Environment.Length == 0)
            throw new ArgumentException("An explicit nonempty environment is required.");
        OwnedStop.RejectReserved(Environment);
        _ = EnvironmentBlock(Environment);
        if (WorkSeconds is < 1 or > 1200 || CleanupSeconds is < 5 or > 180 ||
            DescendantGraceSeconds < 1 || DescendantGraceSeconds > WorkSeconds ||
            LogCapBytes is < 1024 or > 67108864)
            throw new ArgumentException("Explicit bounded work/cleanup/grace/log budgets are required.");
        HashText(ApplicationSha256);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (InputPin input in Inputs)
        {
            Canonical(input.Path);
            HashText(input.Sha256);
            if (!seen.Add(input.Path))
                throw new ArgumentException("Duplicate input path.");
        }
        if (StandardTrxPath is not null)
        {
            Canonical(StandardTrxPath);
            if (!StandardTrxPath.StartsWith(RunDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("StandardTrxPath must be below RunDirectory.");
        }
        if (CommandLine(Application, Arguments).Length >= 32767)
            throw new ArgumentException("Windows command line is too long.");
    }

    internal static void Canonical(string path)
    {
        Text(path);
        if (!Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(Path.DirectorySeparatorChar) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Use a canonical local absolute path without a trailing separator.");
    }

    internal static void NoReparseParents(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Reparse ancestor is not accepted: " + current);
    }

    internal static void Text(string value)
    {
        if (value is null || value.Contains('\0'))
            throw new ArgumentException("Null/NUL text is not accepted.");
    }

    internal static void HashText(string value)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected SHA256 must have 64 hexadecimal characters.");
    }

    internal static string EnvironmentBlock(IEnumerable<EnvironmentEntry> entries)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (EnvironmentEntry entry in entries)
        {
            Text(entry.Name);
            Text(entry.Value);
            if (entry.Name.Length == 0 || entry.Name.Contains('=') || !sorted.TryAdd(entry.Name, entry.Value))
                throw new ArgumentException("Empty, duplicate or '=' environment name.");
        }
        return string.Join('\0', sorted.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
    }

    // Windows CRT argv quoting. The explicit lpApplicationName controls executable selection.
    internal static string Quote(string value)
    {
        Text(value);
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    internal static string CommandLine(string application, IEnumerable<string> args) =>
        string.Join(' ', new[] { application }.Concat(args).Select(Quote));

    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}