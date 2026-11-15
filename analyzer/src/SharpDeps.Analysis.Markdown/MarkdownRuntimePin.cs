namespace SharpDeps.Analysis.Markdown;

using System.Reflection;
using System.Security.Cryptography;
using Syntamark;

/// <summary>MD-05 immutable runtime pin. Startup verification is host I/O, outside the pure parser.</summary>
public static class MarkdownRuntimePin
{
#if SHARPDEPS_MARKDOWN_SESSION_CANDIDATE
    public const string ComponentVersion = "2.0.0-preview.89831114732901";
    public const string CanonicalSourceHash = "42136e5e42d027dc15b4487b4fe381b21bcaf47ab1d30fb954ec21fbff25560f";
    public const string RuntimeAssemblySha256 = "6e1ec01d1b14a27832b0896279889367aab64181daeffdce0b59afe7a97a3629";
    public const string ContractVersion = "2.0";
    public const string YamlProfileId = "native-yaml/1";
#else
    public const string ComponentVersion = "1.2.0";
    public const string CanonicalSourceHash = "17494090defc826957640640ddcdbca4268a9a1011cfe24a0e534247910a7faa";
    public const string RuntimeAssemblySha256 = "fd95fb1010972277f7c6d888aae109f9d293ebc19a74723e5c377f500624f625";
    public const string ContractVersion = "1.0";
#endif
    public const string ParserVersion = "1/" + CanonicalSourceHash;
    public const string ProfileId = "lithosharp-markdown/1";
    public const string YamlPackageVersion = "18.1.0";
    public const string YamlAssemblyName = "YamlDotNet, Version=18.0.0.0, Culture=neutral, PublicKeyToken=ec19458f3c15af5e";
    public const string YamlInformationalVersion = "18.1.0";

    public static void RequireLoaded(string expectedParserVersion)
    {
        if (!string.Equals(expectedParserVersion, ParserVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("The requested Markdown parser stamp differs from the fixed runtime pin.");
        var runtime = typeof(MarkdownParser).Assembly;
        if (string.IsNullOrEmpty(runtime.Location)
            || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(runtime.Location))).ToLowerInvariant() != RuntimeAssemblySha256)
            throw new InvalidOperationException("The loaded Markdown runtime artifact cannot be verified.");
#if !SHARPDEPS_MARKDOWN_SESSION_CANDIDATE
        var yaml = typeof(YamlDotNet.Core.Parser).Assembly;
        if (yaml.FullName != YamlAssemblyName
            || yaml.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion != YamlInformationalVersion)
            throw new InvalidOperationException("The loaded Markdown YAML dependency differs from the fixed pin.");
#endif
    }

    internal static void RequireFacts(MarkdownDocument facts)
    {
        if (facts.ParserVersion != ParserVersion || facts.ContractVersion != ContractVersion || facts.ProfileId != ProfileId)
            throw new InvalidOperationException("Markdown facts differ from the agreed parser/contract/profile.");
#if SHARPDEPS_MARKDOWN_SESSION_CANDIDATE
        if (facts.YamlProfileId != YamlProfileId)
            throw new InvalidOperationException("Markdown facts differ from the agreed native YAML profile.");
#endif
    }
}
