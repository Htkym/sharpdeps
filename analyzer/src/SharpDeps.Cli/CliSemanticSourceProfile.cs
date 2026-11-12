namespace SharpDeps.Cli;

using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SharpDeps.Analysis.Roslyn;

internal static class CliSemanticSourceProfile
{
    internal static async Task<bool> MatchesAsync(Solution? solution,
        IReadOnlyDictionary<string, SemanticSourceInput> sources, CancellationToken token)
    {
        if (solution is null)
            return false;
        try
        {
            foreach (var document in solution.Projects.SelectMany(project => project.Documents))
            {
                token.ThrowIfCancellationRequested();
                if (document.FilePath is null || !sources.TryGetValue(Path.GetFullPath(document.FilePath), out var input)
                    || !Matches(await document.GetTextAsync(token), input))
                    return false;
            }
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return false;
        }
    }

    internal static bool Matches(SourceText compiler, SemanticSourceInput input)
    {
        var bom = input.Text.StartsWith('\uFEFF');
        Encoding? encoding = input.EncodingName switch
        {
            "utf8" => new UTF8Encoding(bom, true),
            "utf16le" when bom => new UnicodeEncoding(false, true, true),
            "utf16be" when bom => new UnicodeEncoding(true, true, true),
            _ => null
        };
        if (encoding is null || compiler.Encoding is null || compiler.Encoding.WebName != encoding.WebName
            || !compiler.Encoding.Preamble.SequenceEqual(encoding.Preamble)
            || compiler.ToString() != input.Text[(bom ? 1 : 0)..])
            return false;
        byte[] bytes;
        try
        {
            bytes = encoding.GetBytes(input.Text);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(input.RawHash, StringComparison.OrdinalIgnoreCase))
            return false;
        // Roslyn checksums describe encoded file bytes, not the BOM-free compiler coordinate space.
        var checksum = compiler.ChecksumAlgorithm switch
        {
            SourceHashAlgorithm.Sha1 => SHA1.HashData(bytes),
            SourceHashAlgorithm.Sha256 => SHA256.HashData(bytes),
            _ => []
        };
        return checksum.Length != 0 && compiler.GetChecksum().AsSpan().SequenceEqual(checksum);
    }
}
