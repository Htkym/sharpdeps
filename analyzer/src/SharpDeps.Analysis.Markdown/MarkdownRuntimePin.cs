namespace SharpDeps.Analysis.Markdown;

using System.Reflection;
using System.Security.Cryptography;
using Syntamark;

/// <summary>MD-05 immutable runtime pin. Startup verification is host I/O, outside the pure parser.</summary>
public static class MarkdownRuntimePin
{
    public const string ComponentVersion = "1.1.0";
    public const string CanonicalSourceHash = "622f375cff4e2997c92e7379873fcab5b8ee6b3928260f74c59be0d97b20e5fe";
    public const string ParserVersion = "1/" + CanonicalSourceHash;
    public const string ContractVersion = "1.0";
    public const string ProfileId = "lithosharp-markdown/1";
    public const string RuntimeAssemblySha256 = "6347fadec31fcc87796989d499d81ce9aa10df6763e9e1559e8dc1373c7a7bc8";
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
