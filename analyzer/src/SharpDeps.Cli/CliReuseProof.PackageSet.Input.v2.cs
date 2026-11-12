namespace SharpDeps.Cli;

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SharpDeps.Analysis.Core.Diagnostics;

internal static partial class CliReuseProof
{
    private sealed class PackageSetPassBudget
    {
        private int _files;
        private long _retained;
        private long _expanded;

        internal void ReserveFile(long length)
        {
            if (length < 0 || _files >= 2048 || length > 64L * 1024 * 1024 - _retained)
                throw new IOException("Package set retained budget exceeded.");
            _files++;
            _retained += length;
        }

        internal void ReserveExpanded(long length)
        {
            if (length < 0 || length > 64L * 1024 * 1024 - _expanded)
                throw new IOException("Package set expanded budget exceeded.");
            _expanded += length;
        }
    }

    private sealed class PackageSetInputBudget(ProofBudget proof)
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private int _entries;
        private long _bytes;

        internal void Check()
        {
            proof.Check();
            if (_elapsed.ElapsedMilliseconds >= 5000)
                throw new IOException("Package set time budget exceeded.");
        }

        internal void ReserveEntries(int count)
        {
            Check();
            if (count < 0 || count > 8192 - _entries)
                throw new IOException("Package set entry budget exceeded.");
            for (var i = 0; i < count; i++)
                proof.Entry();
            _entries += count;
        }

        internal void ReserveBytes(long count)
        {
            Check();
            if (count < 0 || count > 256L * 1024 * 1024 - _bytes)
                throw new IOException("Package set cumulative byte budget exceeded.");
            proof.Bytes(count);
            _bytes += count;
        }

        internal string[] Entries(string directory)
        {
            CliPaths.RejectLinks(directory);
            _ = Attributes(directory);
            return CollectEntries(Directory.EnumerateFileSystemEntries(directory));
        }

        internal string[] CollectEntries(IEnumerable<string> entries)
        {
            var result = new List<string>();
            foreach (var entry in entries)
            {
                ReserveEntries(1);
                result.Add(entry);
            }
            result.Sort(StringComparer.Ordinal);
            return result.ToArray();
        }

        internal byte[] Read(string path, PackageSetPassBudget? retained = null,
            PhaseMetrics.ReadCategory category = PhaseMetrics.ReadCategory.Workspace,
            long maximumBytes = 64L * 1024 * 1024)
        {
            ReserveEntries(1);
            CliPaths.RejectLinks(path);
            if ((Attributes(path) & (FileAttributes.Directory | FileAttributes.Device)) != 0)
                throw new IOException("Package set input is not a file.");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var metrics = PhaseMetrics.ObserveRead(category, input);
            var length = input.Length;
            if (length < 0 || length > maximumBytes || length > int.MaxValue)
                throw new IOException("Package set file budget exceeded.");
            retained?.ReserveFile(length);
            ReserveBytes(length);
            var bytes = new byte[(int)length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                Check();
                var read = input.Read(bytes.AsSpan(offset, Math.Min(65536, bytes.Length - offset)));
                if (read == 0)
                    throw new IOException("Package set input changed.");
                offset += read;
            }
            if (input.ReadByte() != -1 || input.Length != length)
                throw new IOException("Package set input changed.");
            CliPaths.RejectLinks(path);
            Check();
            return bytes;
        }
    }

    private sealed record PackageSetZipEntry(string Name, long Expanded, int Start, int End,
        int DataOffset, int CompressedLength, ushort Method);

    private static bool PackageSetArchiveMetadata(string name) => name is "[Content_Types].xml" or "_rels/.rels"
        || name.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal);

    private static PackageSetZipEntry[] PackageSetZipDirectory(byte[] bytes, PackageSpec package,
        PackageSetInputBudget budget, PackageSetPassBudget pass)
    {
        budget.Check();
        if (bytes.Length < 22)
            throw new InvalidDataException("Package ZIP has no supported footer.");
        var end = bytes.Length - 22;
        if (U32(end) != 0x06054b50 || U16(end + 4) != 0 || U16(end + 6) != 0 || U16(end + 20) != 0)
            throw new InvalidDataException("Package ZIP has an unsupported footer.");
        var count = U16(end + 10);
        var size = U32(end + 12);
        var offset = U32(end + 16);
        if (count == 0 || count > 2048 || U16(end + 8) != count
            || size == uint.MaxValue || offset == uint.MaxValue || (long)offset + size != end)
            throw new InvalidDataException("Package ZIP central directory exceeds the admitted shape.");
        budget.ReserveEntries(count);
        var result = new List<PackageSetZipEntry>(count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursor = checked((int)offset);
        for (var i = 0; i < count; i++)
        {
            budget.Check();
            if (cursor > end - 46 || U32(cursor) != 0x02014b50)
                throw new InvalidDataException("Package ZIP central entry is invalid.");
            var needed = U16(cursor + 6);
            var flags = U16(cursor + 8);
            var method = U16(cursor + 10);
            var compressed = U32(cursor + 20);
            var expanded = U32(cursor + 24);
            var nameLength = U16(cursor + 28);
            var extra = U16(cursor + 30);
            var comment = U16(cursor + 32);
            var local = U32(cursor + 42);
            if (needed > 20 || flags is not (0 or 0x800) || method is not (0 or 8)
                || U16(cursor + 34) != 0 || extra != 0 || comment != 0 || nameLength is 0 or > 4096
                || method == 0 && compressed != expanded
                || compressed == uint.MaxValue || expanded == uint.MaxValue || local >= offset
                || (long)cursor + 46 + nameLength > end)
                throw new InvalidDataException("Package ZIP uses an unsupported entry form.");
            pass.ReserveExpanded(expanded);
            budget.ReserveBytes(expanded);
            var nameBytes = bytes.AsSpan(cursor + 46, nameLength);
            if (StrictUtf8.GetCharCount(nameBytes) > 1024)
                throw new InvalidDataException("Package ZIP name budget exceeded.");
            if (flags == 0)
                foreach (var value in nameBytes)
                    if (value >= 128)
                        throw new InvalidDataException("Package ZIP legacy name encoding is unsupported.");
            var name = StrictUtf8.GetString(nameBytes);
            if (!names.Add(name) || name.Split('/').Any(part => part.Length == 0 || part is "." or ".."
                    || part.Any(c => c is '\\' or ':' || char.IsControl(c)))
                || !PackageFile(name, package) && !PackageSetArchiveMetadata(name))
                throw new InvalidDataException("Package ZIP payload is unsupported.");
            var start = checked((int)local);
            if (offset < 30 || start > (long)offset - 30 || U32(start) != 0x04034b50 || U16(start + 4) != needed
                || U16(start + 6) != flags || U16(start + 8) != method
                || U32(start + 14) != U32(cursor + 16) || U32(start + 18) != compressed
                || U32(start + 22) != expanded || U16(start + 26) != nameLength || U16(start + 28) != 0
                || (long)start + 30 + nameLength + compressed > offset
                || !bytes.AsSpan(start + 30, nameLength).SequenceEqual(nameBytes))
                throw new InvalidDataException("Package ZIP local and central headers disagree.");
            var dataOffset = checked(start + 30 + nameLength);
            var compressedLength = checked((int)compressed);
            result.Add(new(name, expanded, start, checked(dataOffset + compressedLength), dataOffset, compressedLength, method));
            cursor += 46 + nameLength;
        }
        if (cursor != end)
            throw new InvalidDataException("Package ZIP has extra central records.");
        var next = 0;
        foreach (var item in result.OrderBy(entry => entry.Start))
        {
            if (item.Start != next)
                throw new InvalidDataException("Package ZIP has overlapping or untracked local records.");
            next = item.End;
        }
        if (next != offset)
            throw new InvalidDataException("Package ZIP local record closure is incomplete.");
        return result.ToArray();

        ushort U16(int position) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position, 2));
        uint U32(int position) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position, 4));
    }

    private static bool ValidatePackageSetArchive(Dictionary<string, byte[]> files, PackageSpec package,
        PackageSetInputBudget budget, PackageSetPassBudget pass, out string contentHash)
    {
        contentHash = "";
        var name = package.Id.ToLowerInvariant() + "." + package.Version + ".nupkg";
        if (!files.TryGetValue(name, out var archive) || !files.TryGetValue(name + ".sha512", out var marker)
            || !files.TryGetValue(".nupkg.metadata", out var metadata) || metadata.Length > 4096 || marker.Length > 128)
            return false;
        const string signatureName = ".signature.p7s";
        if (files.Keys.Any(path => path.Equals(signatureName, StringComparison.OrdinalIgnoreCase) && path != signatureName))
            return false;
        var rawDigest = SHA512.HashData(archive);
        Span<byte> decodedHash = stackalloc byte[64];
        using var meta = JsonDocument.Parse(metadata);
        if (meta.RootElement.GetProperty("version").GetInt32() != 2
            || meta.RootElement.GetProperty("contentHash").GetString() is not { } recorded
            || recorded.Length != 88 || !Convert.TryFromBase64String(recorded, decodedHash, out var hashLength) || hashLength != 64
            || StrictUtf8.GetString(marker).Trim() != Convert.ToBase64String(rawDigest))
            return false;
        var manifest = PackageSetZipDirectory(archive, package, budget, pass);
        if (manifest.Any(entry => entry.Name.Equals(signatureName, StringComparison.OrdinalIgnoreCase) && entry.Name != signatureName)
            || !manifest.Any(entry => entry.Name == signatureName) && !decodedHash.SequenceEqual(rawDigest))
            return false;
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        if (zip.Entries.Count != manifest.Length)
            return false;
        Span<byte> scratch = stackalloc byte[8192];
        for (var i = 0; i < manifest.Length; i++)
        {
            budget.Check();
            var expected = manifest[i];
            var entry = zip.Entries[i];
            if (entry.FullName != expected.Name || entry.Length != expected.Expanded
                || entry.CompressedLength != expected.CompressedLength)
                return false;
            byte[]? bytes = null;
            if (!PackageSetArchiveMetadata(entry.FullName)
                && (!files.TryGetValue(entry.FullName, out bytes) || entry.Length != bytes.LongLength))
                return false;
            using var compressed = new MemoryStream(archive, expected.DataOffset, expected.CompressedLength, writable: false);
            // ZipArchiveEntry.Open caps inflation to the declared size and can hide excess output.
            using var inflater = expected.Method == 8
                ? new DeflateStream(compressed, CompressionMode.Decompress, leaveOpen: true)
                : null;
            Stream input = inflater is null ? compressed : inflater;
            long consumed = 0;
            while (consumed < expected.Expanded)
            {
                budget.Check();
                var read = input.Read(scratch[..(int)Math.Min(scratch.Length, expected.Expanded - consumed)]);
                if (read == 0 || bytes is not null && !scratch[..read].SequenceEqual(bytes.AsSpan((int)consumed, read)))
                    return false;
                consumed += read;
            }
            if (input.ReadByte() != -1)
                return false;
        }
        var archivedNames = manifest.Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!files.Keys.All(path => path == name || path == name + ".sha512" || path == ".nupkg.metadata" || archivedNames.Contains(path)))
            return false;
        budget.Check();
        contentHash = recorded;
        return true;
    }
}
