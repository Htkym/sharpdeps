namespace SharpDeps.Analysis.Markdown;

using System.Reflection;
using System.Security.Cryptography;
using Syntamark;

/// <summary>MD-05 immutable runtime pin. Startup verification is host I/O, outside the pure parser.</summary>
public static class MarkdownRuntimePin
{
#if SHARPDEPS_MARKDOWN_SESSION_CANDIDATE
    public const string ComponentVersion = "2.0.0-preview.217731285591823";
    public const string CanonicalSourceHash = "5aeb32eff8dfa9741f4c44856be0f2db377c65592435ccee2a378824374cff42";
    public const string RuntimeAssemblySha256 = "24523554d8f2067310543527071762ad0190197ba5f70800118b6266b8a98542";
#else
    public const string ComponentVersion = "1.2.0";
    public const string CanonicalSourceHash = "17494090defc826957640640ddcdbca4268a9a1011cfe24a0e534247910a7faa";
    public const string RuntimeAssemblySha256 = "fd95fb1010972277f7c6d888aae109f9d293ebc19a74723e5c377f500624f625";
#endif
    public const string ParserVersion = "1/" + CanonicalSourceHash;
    public const string ContractVersion = "1.0";
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
        var yaml = typeof(YamlDotNet.Core.Parser).Assembly;
        if (yaml.FullName != YamlAssemblyName
            || yaml.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion != YamlInformationalVersion)
            throw new InvalidOperationException("The loaded Markdown YAML dependency differs from the fixed pin.");
    }

    internal static void RequireFacts(MarkdownDocument facts)
    {
        if (facts.ParserVersion != ParserVersion || facts.ContractVersion != ContractVersion || facts.ProfileId != ProfileId)
            throw new InvalidOperationException("Markdown facts differ from the agreed parser/contract/profile.");
    }
}
