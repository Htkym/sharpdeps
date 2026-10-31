namespace SharpDeps.Cli;

using System.Security.Cryptography;
using System.Text.RegularExpressions;

internal static partial class CliReuseProof
{
    // This closure was traced against the 10.0.402 Git tasks (dotnet/dotnet a8dd0577).
    // A different provider or an unproven filesystem keeps the full-extraction path.
    private static (bool Present, string Hash)? CaptureGit(string root, string sdk, ProofBudget parent)
    {
        for (var ancestor = Directory.GetParent(root); ancestor is not null; ancestor = ancestor.Parent)
            if (Exists(Path.Combine(ancestor.FullName, ".git")))
                return null;
        var directory = Path.Combine(root, ".git");
        if (!Exists(directory))
            return (false, "git-input/1:absent");
        if (!OperatingSystem.IsWindows() || Path.GetFileName(sdk) != "10.0.402"
            || (Attributes(directory) & FileAttributes.Directory) == 0)
            return null;
        var provider = Path.Combine(sdk, "Sdks", "Microsoft.Build.Tasks.Git");
        foreach (var (path, expected) in new[]
        {
            ("tools/net/Microsoft.Build.Tasks.Git.dll", "a68655c3c976a8837897f9bae8308069691e3477922d60cc92636a1483fb8ecb"),
            ("build/Microsoft.Build.Tasks.Git.targets", "a3499b2ed15095be71667a690d86e4a18abb7889f224f52c7bc5459c8479ad34"),
            ("build/Microsoft.Build.Tasks.Git.props", "19a1134fa4c2825c016df1b8c42e375f0c744e9b1a2262222c2ac101e74f0544")
        })
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (AddFile(Path.Combine(provider, path), path, hash, parent) != expected)
                return null;
        }
        var budget = new ProofBudget(parent.Token, parent, git: true);
        var first = GitInventory(directory, budget, validate: true);
        budget.NextGitPass();
        var second = GitInventory(directory, budget, validate: false);
        budget.Check();
        return first is not null && first == second ? (true, "git-input/1:" + first) : null;
    }

    private static string? GitInventory(string root, ProofBudget budget, bool validate)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "ordinary-git/1");
        var directories = new Stack<string>();
        directories.Push(root);
        var required = new HashSet<string>(StringComparer.Ordinal) { "HEAD", "config", "objects", "refs" };
        while (directories.TryPop(out var current))
        {
            CliPaths.RejectLinks(current);
            if ((Attributes(current) & FileAttributes.Directory) == 0)
                return null;
            Append(hash, Path.GetRelativePath(root, current));
            Append(hash, "directory");
            foreach (var entry in Entries(current, budget))
            {
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                var attributes = Attributes(entry);
                if ((attributes & FileAttributes.Device) != 0 || !KnownGitEntry(relative, attributes))
                    return null;
                required.Remove(relative);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                    continue;
                }
                Append(hash, "file");
                var digest = AddFile(entry, relative, hash, budget);
                if (validate && (relative is "config" or "HEAD" or "packed-refs" || relative.StartsWith("refs/", StringComparison.Ordinal)))
                {
                    // Validate exactly the bytes included in this inventory, without following their references.
                    var text = GitText(entry, digest, budget);
                    if (text is null || (relative == "config" ? !PlainGitConfig(text)
                        : relative == "packed-refs" ? !PlainPackedRefs(text)
                        : relative == "HEAD" ? !PlainGitRef(text) : !Hex(text.TrimEnd('\r', '\n'))))
                        return null;
                }
            }
        }
        return required.Count == 0 ? Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() : null;
    }

    private static bool KnownGitEntry(string path, FileAttributes attributes)
    {
        var parts = path.Split('/');
        if (parts.Any(p => p is "." or ".." || p.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)))
            return false;
        var directory = (attributes & FileAttributes.Directory) != 0;
        if (parts.Length == 1)
            return directory ? path is "objects" or "refs" or "logs" or "info" or "hooks"
                : path is "HEAD" or "config" or "description" or "index" or "packed-refs" or "shallow"
                    or "ORIG_HEAD" or "FETCH_HEAD" or "COMMIT_EDITMSG" or "MERGE_HEAD" or "MERGE_MSG" or "MERGE_MODE"
                    or "AUTO_MERGE" or "REBASE_HEAD" or "CHERRY_PICK_HEAD" or "REVERT_HEAD" or "SQUASH_MSG"
                    || (path.StartsWith("sharedindex.", StringComparison.Ordinal) && Hex(path[12..]));
        if (parts[0] == "objects")
        {
            if (path.Equals("objects/info/alternates", StringComparison.OrdinalIgnoreCase)
                || path.Equals("objects/info/http-alternates", StringComparison.OrdinalIgnoreCase))
                return false;
            return parts[1] is "info" or "pack" || (parts[1].Length == 2 && parts[1].All(Uri.IsHexDigit));
        }
        if (parts[0] == "refs")
            return RefName(path);
        return parts[0] is "logs" or "info" or "hooks";
    }

    private static string? GitText(string path, string expectedHash, ProofBudget budget)
    {
        CliPaths.RejectLinks(path);
        if ((Attributes(path) & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 64 * 1024)
            return null;
        budget.Bytes(input.Length);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        budget.Check();
        if (input.ReadByte() != -1 || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != expectedHash)
            return null;
        return StrictUtf8.GetString(bytes);
    }

    private static bool Hex(string text) => text.Length == 40 && text.All(Uri.IsHexDigit);

    private static bool RefName(string text) => text.StartsWith("refs/", StringComparison.Ordinal)
        && text.Length > 5 && !text.Contains("..", StringComparison.Ordinal) && !text.Contains("@{", StringComparison.Ordinal)
        && text.Split('/').All(p => p.Length > 0 && !p.StartsWith('.') && !p.EndsWith('.')
            && !p.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            && p.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c is not ('\\' or ':' or '?' or '*' or '[' or '~' or '^')));

    private static bool PlainGitRef(string text)
    {
        text = text.TrimEnd('\r', '\n');
        return Hex(text) || (text.StartsWith("ref: ", StringComparison.Ordinal) && RefName(text[5..]));
    }

    private static bool PlainPackedRefs(string text)
    {
        using var reader = new StringReader(text);
        if (reader.ReadLine() is not { } header || !header.StartsWith("# pack-refs with:", StringComparison.Ordinal))
            return false;
        while (reader.ReadLine() is { } line)
            if (!(line.StartsWith('^') && Hex(line[1..]))
                && !(line.Length > 41 && Hex(line[..40]) && line[40] == ' ' && RefName(line[41..])))
                return false;
        return true;
    }

    private static bool PlainGitConfig(string text)
    {
        // Accept a small, unescaped subset. Unknown keys/sections, includes, worktree paths,
        // excludesFile, extensions and URL rewrite rules cannot establish this closure.
        var section = "";
        var format = false;
        var bare = false;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;
            if (line.Any(c => char.IsControl(c) && c != '\t') || line.Contains('\\'))
                return false;
            if (line.StartsWith('['))
            {
                var match = Regex.Match(line, "^\\[(core|user|remote|branch)(?: \\\"[^\\\"\\[\\]]+\\\")?\\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success)
                    return false;
                section = match.Groups[1].Value.ToLowerInvariant();
                if ((section is "remote" or "branch") != line.Contains('"'))
                    return false;
                continue;
            }
            var equal = line.IndexOf('=');
            if (equal < 1)
                return false;
            var key = line[..equal].Trim().ToLowerInvariant();
            var value = line[(equal + 1)..].Trim();
            if (value.Length == 0 || value.IndexOfAny(['[', ']', '"', '#', ';']) >= 0)
                return false;
            switch (section, key)
            {
                case ("core", "repositoryformatversion"):
                    if (format || value != "0")
                        return false;
                    format = true;
                    break;
                case ("core", "bare"):
                    if (bare || value != "false")
                        return false;
                    bare = true;
                    break;
                case ("core", "filemode" or "logallrefupdates" or "ignorecase" or "precomposeunicode" or "symlinks" or "autocrlf"):
                    if (value is not ("true" or "false" or "input"))
                        return false;
                    break;
                case ("user", "name" or "email"):
                    break;
                case ("remote", "url"):
                    if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
                        return false;
                    break;
                case ("remote", "fetch"):
                    break;
                case ("branch", "remote" or "merge"):
                    break;
                default:
                    return false;
            }
        }
        return format && bare;
    }

    private static bool ExternalGitConfigurationAbsent()
    {
        // Mirrors the pinned GitEnvironment/GitConfig.Reader discovery. Never open external
        // configuration or follow includes. A present path (even a directory) disables reuse.
        var paths = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home))
            return false;
        paths.Add(Path.Combine(home, ".gitconfig"));
        paths.Add(Path.Combine(home, ".config", "git", "config"));
        if (Environment.GetEnvironmentVariable("PROGRAMDATA") is { Length: > 0 } programData)
        {
            if (!Path.IsPathFullyQualified(programData))
                return false;
            paths.Add(Path.Combine(programData, "git", "config"));
        }
        if (!OperatingSystem.IsWindows())
            paths.Add("/etc/gitconfig");
        else
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (path is null)
                return false;
            var entries = path.Split(Path.PathSeparator).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            if (entries.Any(p => !Path.IsPathFullyQualified(p)))
                return false;
            string? installation = null;
            foreach (var name in new[] { "git.exe", "git.cmd" })
            {
                foreach (var entry in entries)
                {
                    var executable = Path.Combine(entry, name);
                    if (!Exists(executable))
                        continue;
                    // The SDK selects files, not directories. Ambiguous entries cannot prove absence.
                    if (!File.Exists(executable)
                        || (Attributes(executable) & (FileAttributes.Directory | FileAttributes.Device)) != 0)
                        return false;
                    installation = Path.GetDirectoryName(entry);
                    break;
                }
                if (installation is not null)
                    break;
            }
            if (installation is not null)
            {
                // Checking both possible system locations is deliberately conservative.
                paths.Add(Path.Combine(installation, "etc", "gitconfig"));
                paths.Add(Path.Combine(installation, "mingw64", "etc", "gitconfig"));
            }
        }
        return paths.All(p => !Exists(p));
    }
}
