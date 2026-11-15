namespace SharpDeps.Analysis.Markdown;

using System.Security.Cryptography;
using Syntamark;

/// <summary>MD-05 immutable runtime pin. Startup verification is host I/O, outside the pure parser.</summary>
public static class MarkdownRuntimePin
{
    public const string ComponentVersion = "2.0.0";
    public const string CanonicalSourceHash = "42136e5e42d027dc15b4487b4fe381b21bcaf47ab1d30fb954ec21fbff25560f";
    public const string RuntimeAssemblySha256 = "b7b69d4879d8bede4a12a94557deacc6a64300077580abdf44bd91f1502f2264";
    public const string ContractVersion = "2.0";
    public const string YamlProfileId = "native-yaml/1";
    public const string ParserVersion = "1/" + CanonicalSourceHash;
    public const string ProfileId = "lithosharp-markdown/1";

    public static void RequireLoaded(string expectedParserVersion)
    {
        if (!string.Equals(expectedParserVersion, ParserVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("The requested Markdown parser stamp differs from the fixed runtime pin.");
        var runtime = typeof(MarkdownParser).Assembly;
        if (string.IsNullOrEmpty(runtime.Location)
            || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(runtime.Location))).ToLowerInvariant() != RuntimeAssemblySha256)
            throw new InvalidOperationException("The loaded Markdown runtime artifact cannot be verified.");
    }

    internal static void RequireFacts(MarkdownDocument facts)
    {
        if (facts.ParserVersion != ParserVersion || facts.ContractVersion != ContractVersion || facts.ProfileId != ProfileId)
            throw new InvalidOperationException("Markdown facts differ from the agreed parser/contract/profile.");
        if (facts.YamlProfileId != YamlProfileId)
            throw new InvalidOperationException("Markdown facts differ from the agreed native YAML profile.");
    }
}
