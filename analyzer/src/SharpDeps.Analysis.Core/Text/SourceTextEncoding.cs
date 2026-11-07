namespace SharpDeps.Analysis.Core.Text;

using System.Text;

/// <summary>Strict BOM-aware decoding that preserves the BOM in source coordinates.</summary>
public static class SourceTextEncoding
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly UnicodeEncoding Utf16Le = new(false, true, true);
    private static readonly UnicodeEncoding Utf16Be = new(true, true, true);

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var text = bytes.StartsWith(Utf16Le.Preamble) ? Utf16Le.GetString(bytes)
            : bytes.StartsWith(Utf16Be.Preamble) ? Utf16Be.GetString(bytes) : Utf8.GetString(bytes);
        if (text.Contains('\0'))
            throw new DecoderFallbackException("NUL/binary inputs are unsupported.");
        return text;
    }
}
