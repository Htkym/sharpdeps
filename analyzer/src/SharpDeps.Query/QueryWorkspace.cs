namespace SharpDeps.Query;

using System.Security.Cryptography;
using System.Text;
using SharpDeps.Index;

/// <summary>The trusted owner supplies the complete selected input set and verifies analysis configuration.</summary>
public sealed record QueryInputInventory(IReadOnlyList<string> RelativePaths, bool ConfigurationVerified);
public sealed record QueryFreshness(string State, DateTimeOffset? CheckedAt,
    IReadOnlyList<string> DirtyPaths, IReadOnlyList<string> Unverified);

public sealed class QuerySourceException(string code, string? path = null) : Exception(code)
{
    public string Code { get; } = code;
    public string? RelativePath { get; } = path;
}

/// <summary>Trusted source access only. ReadSaved queries must not construct or use this filesystem verifier.</summary>
public sealed class QueryWorkspace(string root, Func<CancellationToken, QueryInputInventory>? inspectInventory = null)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    // Normalization is lexical; source/root attributes are inspected only by Read or Verify.
    private readonly string canonicalRoot = NormalizeRoot(root);

    /// <summary>Returns the exact verified source bytes, decoded with strict UTF-8 before they can be used for snippets.</summary>
    public byte[] Read(IndexFile input, CancellationToken token = default)
        => ReadInput(input, token, requireText: true);

    private byte[] ReadInput(IndexFile input, CancellationToken token, bool requireText)
    {
        ArgumentNullException.ThrowIfNull(input);
        token.ThrowIfCancellationRequested();
        ValidateInput(input);
        var path = Resolve(input.RelativePath);
        try
        {
            CheckRoot(token);
            RejectLinks(path, token);
            if (input.Deleted) throw Changed(input.RelativePath);
            if (Attributes(path) is not { } attributes || (attributes & FileAttributes.Directory) != 0)
                throw Changed(input.RelativePath);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != input.ByteLength) throw Changed(input.RelativePath);
            var bytes = new byte[(int)input.ByteLength];
            var offset = 0;
            while (offset < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(bytes, offset, Math.Min(64 * 1024, bytes.Length - offset));
                if (read == 0) throw Changed(input.RelativePath);
                offset += read;
            }
            token.ThrowIfCancellationRequested();
            if (stream.ReadByte() != -1 || stream.Length != input.ByteLength) throw Changed(input.RelativePath);
            RejectLinks(path, token);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            token.ThrowIfCancellationRequested();
            if (hash != input.ContentHash) throw Changed(input.RelativePath);
            if (requireText || input.Utf16Length is not null)
            {
                var length = StrictUtf8.GetCharCount(bytes);
                if (input.Utf16Length is { } expected && length != expected) throw Changed(input.RelativePath);
            }
            token.ThrowIfCancellationRequested();
            return bytes;
        }
        catch (QuerySourceException error) when (error.RelativePath is null)
        { throw new QuerySourceException(error.Code, input.RelativePath); }
        catch (DecoderFallbackException) { throw new QuerySourceException("INVALID_SOURCE_ENCODING", input.RelativePath); }
        catch (UnauthorizedAccessException) { throw new QuerySourceException("ROOT_ACCESS_DENIED", input.RelativePath); }
        catch (FileNotFoundException) { throw Changed(input.RelativePath); }
        catch (DirectoryNotFoundException) { throw Changed(input.RelativePath); }
        catch (IOException) { throw new QuerySourceException("ROOT_ACCESS_DENIED", input.RelativePath); }
    }

    /// <summary>Verifies saved inputs, tombstones, the complete live input set, and owner-supplied configuration proof.</summary>
    public QueryFreshness Verify(IndexSnapshot snapshot, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        IndexSnapshotValidator.Validate(snapshot);
        token.ThrowIfCancellationRequested();
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        var unverified = new HashSet<string>(StringComparer.Ordinal);
        try { CheckRoot(token); }
        catch (QuerySourceException error)
        {
            return new("unverified", DateTimeOffset.UtcNow, Array.Empty<string>(), new[] { error.Code });
        }
        foreach (var input in snapshot.Files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (input.Deleted)
                {
                    var path = Resolve(input.RelativePath);
                    RejectLinks(path, token);
                    if (Attributes(path) is not null) throw Changed(input.RelativePath);
                }
                // Configuration/reference inputs may be binary; only source Read requires text.
                else _ = ReadInput(input, token, requireText: false);
            }
            catch (QuerySourceException error)
            {
                if (error.Code == "SOURCE_CHANGED_SINCE_SNAPSHOT") dirty.Add(input.RelativePath);
                else unverified.Add(error.Code + ":" + input.RelativePath);
            }
        }
        if (inspectInventory is null) unverified.Add("INPUT_INVENTORY_UNVERIFIED");
        else
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var inventory = inspectInventory(token);
                if (inventory?.RelativePaths is null
                    || inventory.RelativePaths.Count > IndexSnapshotValidator.MaximumCollectionItems)
                    unverified.Add("INPUT_INVENTORY_UNVERIFIED");
                else
                {
                    var paths = new HashSet<string>(StringComparer.Ordinal);
                    var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var relative in inventory.RelativePaths)
                    {
                        token.ThrowIfCancellationRequested();
                        _ = Resolve(relative);
                        if (!aliases.Add(relative)) throw new QuerySourceException("ROOT_ACCESS_DENIED", relative);
                        paths.Add(relative);
                    }
                    var expected = snapshot.Files.Where(file => !file.Deleted)
                        .Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
                    dirty.UnionWith(expected.Except(paths));
                    dirty.UnionWith(paths.Except(expected));
                    if (!inventory.ConfigurationVerified) unverified.Add("CONFIGURATION_UNVERIFIED");
                }
            }
            catch (Exception error) when (error is QuerySourceException or IOException or UnauthorizedAccessException
                or ArgumentException or InvalidOperationException)
            { unverified.Add("INPUT_INVENTORY_UNVERIFIED"); }
        }
        token.ThrowIfCancellationRequested();
        return new(dirty.Count != 0 ? "dirty" : unverified.Count != 0 ? "unverified" : "verified-current",
            DateTimeOffset.UtcNow, dirty.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            unverified.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private static void ValidateInput(IndexFile input)
    {
        try { IndexSnapshotValidator.ValidateHash(input.ContentHash); }
        catch (IndexStoreException) { throw Changed(input.RelativePath); }
        if (input.ByteLength is < 0 or > IndexSnapshotValidator.MaximumInputFileBytes
            || input.Utf16Length is < 0 or > IndexSnapshotValidator.MaximumInputFileUtf16)
            throw Changed(input.RelativePath);
    }

    private static string NormalizeRoot(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)
                || root.StartsWith("\\\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal)
                || root.Contains('|') || root.Contains('?') || root.Any(char.IsControl))
                throw new QuerySourceException("ROOT_ACCESS_DENIED");
            return Path.GetFullPath(root);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new QuerySourceException("ROOT_ACCESS_DENIED"); }
    }

    private string Resolve(string relative)
    {
        try
        {
            IndexSnapshotValidator.ValidateRelativePath(relative);
            var path = Path.GetFullPath(Path.Combine(canonicalRoot, relative));
            var prefix = Path.EndsInDirectorySeparator(canonicalRoot) ? canonicalRoot : canonicalRoot + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.StartsWith(prefix, comparison)) throw new QuerySourceException("ROOT_ACCESS_DENIED", relative);
            return path;
        }
        catch (Exception error) when (error is IndexStoreException or ArgumentException or NotSupportedException or PathTooLongException)
        { throw new QuerySourceException("ROOT_ACCESS_DENIED", relative); }
    }

    private void CheckRoot(CancellationToken token)
    {
        RejectLinks(canonicalRoot, token);
        if (Attributes(canonicalRoot) is not { } attributes || (attributes & FileAttributes.Directory) == 0)
            throw new QuerySourceException("ROOT_ACCESS_DENIED");
    }

    private static void RejectLinks(string path, CancellationToken token)
    {
        for (var entry = path; !string.IsNullOrEmpty(entry); entry = Path.GetDirectoryName(entry))
        {
            token.ThrowIfCancellationRequested();
            if (Attributes(entry) is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new QuerySourceException("ROOT_ACCESS_DENIED");
        }
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new QuerySourceException("ROOT_ACCESS_DENIED"); }
    }

    private static QuerySourceException Changed(string relative) => new("SOURCE_CHANGED_SINCE_SNAPSHOT", relative);
}
