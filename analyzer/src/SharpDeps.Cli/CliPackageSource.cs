namespace SharpDeps.Cli;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SharpDeps.Analysis.Roslyn.Symbols;
using SharpDeps.Index;

/// <summary>Checks only actual compilation candidates, never adopts or reads other assets-listed files.</summary>
internal static class CliPackageSource
{
    internal sealed record Binding(string PackageId, string Version, string RelativePath, string Text,
        string ContentHash, long ByteLength, string EvidenceHash);

    internal static Binding Bind(string projectPath, string framework, string filePath, string compilationText,
        CancellationToken token = default)
    {
        var label = "unidentified/unidentified/unidentified";
        try
        {
            Require(Path.IsPathFullyQualified(projectPath) && Path.IsPathFullyQualified(filePath));
            using var evidence = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var directory = Path.GetDirectoryName(projectPath)!;
            var assetsPath = Path.Combine(directory, "obj", "project.assets.json");
            using var assets = JsonDocument.Parse(ReadEvidence(assetsPath));
            var restore = assets.RootElement.GetProperty("project").GetProperty("restore");
            Require(CliPaths.SamePath(restore.GetProperty("projectPath").GetString(), projectPath));
            var cache = CliPaths.Root(restore.GetProperty("packagesPath").GetString()!);
            var candidate = CliPaths.InRoot(cache, filePath);
            var relative = Path.GetRelativePath(cache, candidate).Replace('\\', '/');
            var parts = relative.Split('/');
            Require(parts.Length >= 3 && SafeRelative(relative));
            var id = parts[0]; var version = parts[1]; var source = string.Join('/', parts[2..]);
            Require(Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9_.-]*\z") && NormalizeVersion(version) == version);
            label = $"{id}/{version}/{source}";
            var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            Append(configured ?? "");
            var confirmed = SameDirectory(configured, cache);
            var props = Path.Combine(directory, "obj", Path.GetFileName(projectPath) + ".nuget.g.props");
            if (EvidenceExists(props))
            {
                using var xml = XmlReader.Create(new MemoryStream(ReadEvidence(props)), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
                var document = XDocument.Load(xml);
                confirmed |= document.Descendants().Where(e => e.Name.LocalName is "NuGetPackageRoot" or "NuGetPackageFolders")
                    .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    .Any(value => SameDirectory(value.Trim(), cache));
            }
            else Append("no-props");
            Require(confirmed);
            var targets = assets.RootElement.GetProperty("targets");
            Require(targets.TryGetProperty(framework, out var selected));
            var matches = new List<(string Id, string Version, string Source)>();
            foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
            {
                token.ThrowIfCancellationRequested();
                var identity = library.Name.Split('/');
                if (identity.Length != 2 || library.Value.GetProperty("type").GetString() != "package"
                    || !selected.TryGetProperty(library.Name, out var target)
                    || target.GetProperty("type").GetString() != "package") continue;
                var libraryPath = library.Value.GetProperty("path").GetString()!;
                // Do not inspect irrelevant file entries: even malformed/nonexistent non-trees are ignored.
                if (!string.Equals(libraryPath, id + "/" + version, StringComparison.Ordinal)) continue;
                Require(identity[0].ToLowerInvariant() == id && NormalizeVersion(identity[1]) == version
                    && SafeRelative(libraryPath));
                foreach (var file in library.Value.GetProperty("files").EnumerateArray())
                {
                    var name = file.GetString();
                    if (name != source) continue;
                    Require(SafeRelative(name));
                    Require(CliPaths.SamePath(CliPaths.InRoot(cache, libraryPath + "/" + name), candidate));
                    matches.Add((identity[0].ToLowerInvariant(), version, name));
                }
            }
            Require(matches.Count > 0);
            if (matches.Distinct().Count() != 1)
                throw new PackageDocumentRegistrationException(PackageDocumentRegistrationException.IdentityAmbiguous,
                    $"Package '{label}' at CliPackageSource.Bind has multiple restore identities.");
            var packageDirectory = CliPaths.InRoot(cache, id + "/" + version);
            CliPaths.RejectLinks(packageDirectory);
            using var metadata = JsonDocument.Parse(ReadEvidence(Path.Combine(packageDirectory, ".nupkg.metadata")));
            var archivePath = Path.Combine(packageDirectory, id + "." + version + ".nupkg");
            CliPaths.RejectLinks(archivePath);
            using var archiveInput = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            token.ThrowIfCancellationRequested();
            var archiveHash = SHA512.HashData(archiveInput);
            Append(Convert.ToBase64String(archiveHash));
            Require(HashMatches(metadata.RootElement.GetProperty("contentHash").GetString(), archiveHash));
            var lockPath = Path.Combine(directory, "packages.lock.json");
            if (EvidenceExists(lockPath))
            {
                using var locked = JsonDocument.Parse(ReadEvidence(lockPath));
                foreach (var group in locked.RootElement.GetProperty("dependencies").EnumerateObject())
                    foreach (var package in group.Value.EnumerateObject())
                        if (package.Name.Equals(id, StringComparison.OrdinalIgnoreCase)
                            && package.Value.TryGetProperty("contentHash", out var hash))
                        {
                            if (package.Value.TryGetProperty("resolved", out var resolved)
                                && NormalizeVersion(resolved.GetString()!) != version)
                            {
                                // Another TFM may legitimately restore a different version of the same ID.
                                Require(group.Name != framework);
                                continue;
                            }
                            Require(HashMatches(hash.GetString(), archiveHash));
                        }
            }
            else Append("no-lock");
            // Without a lock file, metadata plus actual archive bytes carry the evidence.
            // This proves consistency with the user's restore, not authenticity from the publisher.
            var extracted = ReadEvidence(candidate, IndexSnapshotValidator.MaximumInputFileBytes);
            archiveInput.Position = 0;
            using var zip = new ZipArchive(archiveInput, ZipArchiveMode.Read);
            var entries = zip.Entries.Where(e => e.FullName == source).ToArray();
            Require(entries.Length == 1 && entries[0].Length == extracted.LongLength);
            using var entry = entries[0].Open();
            var archiveBody = new byte[extracted.Length];
            entry.ReadExactly(archiveBody);
            Require(entry.ReadByte() == -1 && archiveBody.AsSpan().SequenceEqual(extracted));
            var text = new UTF8Encoding(false, true).GetString(extracted);
            Require(text == compilationText && !text.Contains('\0'));
            CliPaths.RejectLinks(candidate); CliPaths.RejectLinks(archivePath);
            token.ThrowIfCancellationRequested();
            return new(id, version, source, text, Convert.ToHexString(SHA256.HashData(extracted)).ToLowerInvariant(),
                extracted.LongLength, Convert.ToHexString(evidence.GetHashAndReset()).ToLowerInvariant());

            byte[] ReadEvidence(string path, long maximum = IndexSnapshotValidator.MaximumInputFileBytes)
            {
                token.ThrowIfCancellationRequested();
                CliPaths.RejectLinks(path);
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Require(input.Length <= maximum);
                var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
                Require(input.ReadByte() == -1);
                CliPaths.RejectLinks(path);
                evidence.AppendData(SHA256.HashData(bytes));
                return bytes;
            }
            void Append(string value) => evidence.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or XmlException or InvalidOperationException or ArgumentException or FormatException or OverflowException
            or KeyNotFoundException or CliFailure)
        {
            if (error is PackageDocumentRegistrationException) throw;
            // Never forward filesystem exception messages: cache paths can contain a user profile.
            throw new PackageDocumentRegistrationException(PackageDocumentRegistrationException.ProvenanceUnidentified,
                $"Package '{label}' at CliPackageSource.Bind failed restore/archive/body verification; outside-root input refused.");
        }
    }

    private static bool HashMatches(string? value, byte[] expected)
        => value is not null && Convert.FromBase64String(value).AsSpan().SequenceEqual(expected);

    private static bool EvidenceExists(string path)
    {
        CliPaths.RejectLinks(path);
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool SameDirectory(string? value, string expected)
        => !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)
            && !value.StartsWith("\\\\", StringComparison.Ordinal) && !value.StartsWith("//", StringComparison.Ordinal)
            && CliPaths.SamePath(Path.TrimEndingDirectorySeparator(value), expected);

    private static bool SafeRelative(string value) => !string.IsNullOrEmpty(value)
        && !value.Split('/').Any(p => p is "" or "." or ".." || p.Any(c => c is '\\' or ':' || char.IsControl(c)))
        && !Path.IsPathRooted(value);

    private static string? NormalizeVersion(string value)
    {
        var match = Regex.Match(value, @"\A(?<v>[0-9]+(?:\.[0-9]+){0,3})(?<pre>-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z.-]+)?\z");
        if (!match.Success) return null;
        var parts = match.Groups["v"].Value.Split('.').Select(int.Parse).ToList();
        while (parts.Count < 3) parts.Add(0);
        if (parts.Count == 4 && parts[3] == 0) parts.RemoveAt(3);
        return string.Join('.', parts) + match.Groups["pre"].Value.ToLowerInvariant();
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Package evidence is incomplete or inconsistent.");
    }
}
