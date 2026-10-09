namespace SharpDeps.Analysis.Markdown;

using System.Reflection;
using System.Security.Cryptography;
using LithoSharp.Markdown;

/// <summary>MD-05 immutable runtime pin. Startup verification is host I/O, outside the pure parser.</summary>
public static class MarkdownRuntimePin
{
    public const string ComponentVersion = "2.0.0-preview.2";
    public const string CanonicalSourceHash = "02ce256eca6797e2c3a77eae5b1c1c1045a0a874b0936c61ec3dd08385fe0480";
    public const string ParserVersion = "1/" + CanonicalSourceHash;
    public const string ContractVersion = "1.0";
    public const string ProfileId = "lithosharp-markdown/1";
    public const string RuntimeAssemblySha256 = "f7a153d16f5ac06a8c9f975205c5529662a89aa9842fcfdfcfab238d24c4a817";
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
