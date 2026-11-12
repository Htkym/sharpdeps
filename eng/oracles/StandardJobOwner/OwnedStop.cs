using System.Security.Cryptography;
using System.Text.Json;

namespace SharpDeps.StandardJobOwner;

internal sealed record OwnedStopBinding(string Path, string Nonce);

internal static class OwnedStop
{
    internal const string PathKey = "SHARPDEPS_OWNED_STOP_PATH";
    internal const string NonceKey = "SHARPDEPS_OWNED_STOP_NONCE";
    internal const string RequestLeaf = "owned-stop.request.json";
    internal const string Schema = "sharpdeps-owned-stop-v1";
    internal const int MaxRequestBytes = 512;

    internal static void RejectReserved(IEnumerable<EnvironmentEntry> entries)
    {
        if (entries.Any(entry => string.Equals(entry.Name, PathKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Name, NonceKey, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Owned-stop environment names are reserved, including when disabled.");
    }

    internal static OwnedStopBinding CreateBinding(string runDirectory) => new(
        System.IO.Path.Combine(runDirectory, RequestLeaf), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    internal static EnvironmentEntry[] Environment(OwnedStopBinding binding) =>
        [new(PathKey, binding.Path), new(NonceKey, binding.Nonce)];

    internal static async Task<byte[]?> ReadAsync(OwnedStopBinding binding, CancellationToken token)
    {
        FileStream file;
        try
        {
            file = new(binding.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        await using (file)
        {
            var bytes = new byte[MaxRequestBytes + 1];
            int count = 0;
            while (count < bytes.Length)
            {
                int read = await file.ReadAsync(bytes.AsMemory(count), token);
                if (read == 0)
                    break;
                count += read;
            }
            return bytes[..count];
        }
    }

    internal static string Parse(byte[] bytes, string expectedNonce)
    {
        if (bytes.Length is 0 or > MaxRequestBytes)
            throw new InvalidDataException("Owned-stop request length is invalid.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Owned-stop request must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name) || property.Name is not ("schema" or "nonce" or "reason")
                || property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Owned-stop request has an unknown, duplicate or non-string member.");
        if (names.Count != 3 || root.GetProperty("schema").GetString() != Schema
            || root.GetProperty("nonce").GetString() != expectedNonce)
            throw new InvalidDataException("Owned-stop schema or run nonce does not match.");
        string reason = root.GetProperty("reason").GetString()!;
        if (reason is not ("cancelled" or "child-uncertain"))
            throw new InvalidDataException("Owned-stop reason is outside the fixed contract.");
        return reason;
    }
}
