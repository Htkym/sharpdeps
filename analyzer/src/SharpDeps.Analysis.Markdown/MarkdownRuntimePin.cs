namespace SharpDeps.Analysis.Markdown;

using System.Reflection;
using System.Security.Cryptography;
using Syntamark;

/// <summary>MD-05 immutable runtime pin. Startup verification is host I/O, outside the pure parser.</summary>
public static class MarkdownRuntimePin
{
    public const string ComponentVersion = "2.0.0-preview.3";
    public const string CanonicalSourceHash = "7e854150d5cad96e554b90a550251dce74d29b79ceef2964fa9cfa4a226e2051";
    public const string ParserVersion = "1/" + CanonicalSourceHash;
    public const string ContractVersion = "1.0";
    public const string ProfileId = "lithosharp-markdown/1";
    public const string RuntimeAssemblySha256 = "1603cfdfe41cb06b2f7772990d5898fae5687947ab5adc93c78a82c41a1b13d4";
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
