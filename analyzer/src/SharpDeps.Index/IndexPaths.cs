namespace SharpDeps.Index;

using System.Security.Cryptography;
using System.Text;

internal static class IndexPaths
{
    public static string Absolute(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('|') || path.Contains('?'))
            throw new IndexStoreException("INDEX_INVALID_PATH", "An absolute local filesystem path is required.");
        var full = Path.GetFullPath(path);
        RejectLinks(full);
        return full;
    }
    public static void RejectLinks(string path)
    {
        for (var entry = path; !string.IsNullOrEmpty(entry); entry = Path.GetDirectoryName(entry))
        {
            if ((File.Exists(entry) || Directory.Exists(entry)) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IndexStoreException("INDEX_INVALID_PATH", "Symbolic links and reparse points are not accepted by this storage owner.");
        }
    }
    public static void VerifyInputs(string root, IReadOnlyList<IndexFile> files, CancellationToken token)
    {
        foreach (var input in files)
        {
            token.ThrowIfCancellationRequested();
            IndexSnapshotValidator.ValidateRelativePath(input.RelativePath);
            var path = Path.GetFullPath(Path.Combine(root, input.RelativePath));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison))
                throw new IndexStoreException("INDEX_INVALID_PATH", "Input is outside the declared workspace.");
            RejectLinks(path);
            if (input.Deleted)
            {
                if (File.Exists(path) || Directory.Exists(path)) Changed();
                continue;
            }
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != input.ByteLength) Changed();
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (hash != input.ContentHash) Changed();
                if (input.Utf16Length is { } length)
                {
                    stream.Position = 0;
                    using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
                    if (reader.ReadToEnd().Length != length) Changed();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
            { throw new IndexStoreException("SOURCE_CHANGED_SINCE_STAGING", "An input cannot be verified against the staged manifest.", error); }
        }
        token.ThrowIfCancellationRequested();
        static void Changed() => throw new IndexStoreException("SOURCE_CHANGED_SINCE_STAGING", "An input changed after staging.");
    }
}
