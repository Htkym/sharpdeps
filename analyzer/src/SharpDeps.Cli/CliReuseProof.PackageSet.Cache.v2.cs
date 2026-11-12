namespace SharpDeps.Cli;

using System.Buffers.Binary;

internal static partial class CliReuseProof
{
    private sealed class PackageSetCacheReader(byte[] bytes)
    {
        internal int Position { get; set; }
        internal int Limit { get; set; } = bytes.Length;

        internal int Int32()
        {
            if (Position < 0 || Position > Limit - 4)
                throw new InvalidDataException("Package cache integer is truncated.");
            var result = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(Position, 4));
            Position += 4;
            return result;
        }

        internal string String()
        {
            uint length = 0;
            var shift = 0;
            for (; shift <= 28; shift += 7)
            {
                if (Position >= Limit)
                    throw new InvalidDataException("Package cache string is truncated.");
                var next = bytes[Position++];
                if (shift == 28 && next > 7)
                    throw new InvalidDataException("Package cache string length is invalid.");
                length |= (uint)(next & 127) << shift;
                if ((next & 128) == 0)
                {
                    if (shift != 0 && next == 0 || length > 65536 || length > Limit - Position)
                        throw new InvalidDataException("Package cache string byte budget exceeded.");
                    var span = bytes.AsSpan(Position, (int)length);
                    if (StrictUtf8.GetCharCount(span) > 16384)
                        throw new InvalidDataException("Package cache string character budget exceeded.");
                    Position += (int)length;
                    return StrictUtf8.GetString(span);
                }
            }
            throw new InvalidDataException("Package cache string length is invalid.");
        }
    }

    private static bool PackageSetAssetsCache(byte[] bytes, PackageSetCacheContract expected)
    {
        if (bytes.Length is < 48 or > 4 * 1024 * 1024 || expected.Groups.Length != 17)
            return false;
        var reader = new PackageSetCacheReader(bytes);
        if (reader.Int32() != 0x41474b50 || reader.Int32() != 12)
            return false;
        reader.Position = 40;
        var table = reader.Int32();
        if (table < 44 + 17 * 4 || table > bytes.Length - 4)
            return false;
        reader.Position = table;
        var count = reader.Int32();
        if (count is < 0 or > 4096)
            return false;
        var strings = new string[count];
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            strings[i] = reader.String();
            if (strings[i].Length == 0 || !distinct.Add(strings[i]))
                return false;
        }
        if (reader.Position != bytes.Length)
            return false;
        var used = new HashSet<int>();
        reader.Position = 44;
        reader.Limit = table;
        for (var group = 0; group < 17; group++)
        {
            var items = expected.Groups[group];
            var length = reader.Int32();
            if (length is < 0 or > 1024 || length != items.Count)
                return false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < length; i++)
            {
                var item = reader.String();
                if (!seen.Add(item) || !items.TryGetValue(item, out var record) || item != record.Item)
                    return false;
                var metadata = reader.Int32();
                if (metadata is < 0 or > 128 || metadata != record.Metadata.Count)
                    return false;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var j = 0; j < metadata; j++)
                {
                    var key = reader.Int32();
                    var value = reader.Int32();
                    if ((uint)key >= strings.Length || (uint)value >= strings.Length
                        || !names.Add(strings[key]) || !record.Metadata.TryGetValue(strings[key], out var text)
                        || text != strings[value])
                        return false;
                    used.Add(key);
                    used.Add(value);
                }
            }
        }
        return reader.Position == table && used.Count == strings.Length;
    }
}
