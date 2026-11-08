namespace SharpDeps.Cli.Tests;

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.Text;
using SharpDeps.Analysis.Core.Text;
using SharpDeps.Analysis.Roslyn;
using Xunit;

public sealed class CliSemanticSourceProfileTests
{
    [Theory]
    [InlineData("utf8", false)]
    [InlineData("utf8", true)]
    [InlineData("utf16le", true)]
    [InlineData("utf16be", true)]
    public void CompilerBodyEncodingPreambleAndChecksumMustDescribeTheExactCapturedBytes(string name, bool bom)
    {
        Encoding encoding = name switch
        {
            "utf16le" => new UnicodeEncoding(false, true, true),
            "utf16be" => new UnicodeEncoding(true, true, true),
            _ => new UTF8Encoding(bom, true)
        };
        const string body = "// \U0001f642 日本語\r\npublic class Service { public int Run() => 1; }\r\n";
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(body)).ToArray();
        var input = new SemanticSourceInput(SourceTextEncoding.Decode(bytes), Convert.ToHexString(SHA256.HashData(bytes)), name);
        foreach (var algorithm in new[] { SourceHashAlgorithm.Sha1, SourceHashAlgorithm.Sha256 })
        {
            using var stream = new MemoryStream(bytes);
            var compiler = SourceText.From(stream, new UTF8Encoding(false, true), algorithm, throwIfBinaryDetected: true);
            Assert.Equal(body, compiler.ToString());
            Assert.True(CliSemanticSourceProfile.Matches(compiler, input));
            var next = body.Replace("=> 1", "=> 2", StringComparison.Ordinal);
            var updated = SemanticSessionOwner.Replace(compiler, next, default);
            var nextBytes = encoding.GetPreamble().Concat(encoding.GetBytes(next)).ToArray();
            var nextInput = input with { Text = (bom ? "\uFEFF" : "") + next, RawHash = Convert.ToHexString(SHA256.HashData(nextBytes)) };
            Assert.True(CliSemanticSourceProfile.Matches(updated, nextInput));
            Assert.False(CliSemanticSourceProfile.Matches(updated, input));
            Assert.False(CliSemanticSourceProfile.Matches(compiler, input with { EncodingName = "unknown" }));
            Assert.False(CliSemanticSourceProfile.Matches(compiler, input with { RawHash = new string('0', 64) }));
            if (bom)
                Assert.False(CliSemanticSourceProfile.Matches(SourceText.From(input.Text, encoding, algorithm), input));
        }
    }

    [Fact]
    public void FrameworkFreeAdmissionRequiresTheCompleteLiteralPropertyProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "sde-profile", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "App.csproj");
        const string properties = "<DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>" +
            "<EnableNETAnalyzers>false</EnableNETAnalyzers><GenerateAssemblyInfo>false</GenerateAssemblyInfo>" +
            "<GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>";
        Write(properties);
        Assert.True(Admitted());
        var assetsPath = Path.Combine(root, "obj", "project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(assetsPath)!);
        WriteAssets(path, "");
        Assert.True(Admitted());
        foreach (var name in new[] { "DisableImplicitFrameworkReferences", "EnableNETAnalyzers", "GenerateAssemblyInfo", "GenerateTargetFrameworkAttribute" })
        {
            var value = name == "DisableImplicitFrameworkReferences" ? "true" : "false";
            Write(properties.Replace($"<{name}>{value}</{name}>", "", StringComparison.Ordinal));
            Assert.False(Admitted());
            Write(properties.Replace($"<{name}>{value}</{name}>", $"<{name}>{(value == "true" ? "false" : "true")}</{name}>", StringComparison.Ordinal));
            Assert.False(Admitted());
            Write(properties.Replace($"<{name}>{value}</{name}>", $"<{name} Condition=\"'1' == '1'\">{value}</{name}>", StringComparison.Ordinal));
            Assert.False(Admitted());
            Write(properties.Replace($"<{name}>{value}</{name}>", $"<{name}>$(ProfileValue)</{name}>", StringComparison.Ordinal));
            Assert.False(Admitted());
            Write(properties + $"<{name}>{value}</{name}>");
            Assert.False(Admitted());
        }
        Write(properties.Replace("<DisableImplicitFrameworkReferences>true", "<DisableImplicitFrameworkReferences>tr ue", StringComparison.Ordinal));
        Assert.False(Admitted());
        Write(properties + "<CustomInput>true</CustomInput>");
        Assert.False(Admitted());
        Write(properties);
        Assert.True(Admitted());

        WriteAssets(path, ",\"frameworkReferences\":{}");
        Assert.True(Admitted());
        WriteAssets(path, ",\"frameworkReferences\":{\"Microsoft.NETCore.App\":{}}");
        Assert.False(Admitted());
        WriteAssets(path, ",\"frameworkReferences\":[]");
        Assert.False(Admitted());
        WriteAssets(Path.Combine(root, "Other.csproj"), "");
        Assert.False(Admitted());
        WriteAssets(path, "");
        Write("<EnableNETAnalyzers>false</EnableNETAnalyzers>");
        Assert.False(Admitted());
        WriteAssets(path, ",\"frameworkReferences\":{\"Microsoft.NETCore.App\":{}}");
        Assert.True(Admitted());
        WriteAssets(path, "");
        Write(properties);
        Assert.True(Admitted());

        void WriteAssets(string owner, string references) => File.WriteAllText(assetsPath,
            "{\"libraries\":{},\"targets\":{\"net10.0\":{}},\"project\":{\"restore\":{\"projectPath\":" +
            System.Text.Json.JsonSerializer.Serialize(owner) + "},\"frameworks\":{\"net10.0\":{\"framework\":\"net10.0\"" +
            references + "}}}}");
        void Write(string contents) => File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFramework>net10.0</TargetFramework>" + contents + "</PropertyGroup></Project>", new UTF8Encoding(false, true));
        bool Admitted() => (bool)typeof(CliReuseProof).GetMethod("PlainProjects", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [root, root, new CliReuseProof.ProofBudget(default), false, null, null])!;
    }
}
