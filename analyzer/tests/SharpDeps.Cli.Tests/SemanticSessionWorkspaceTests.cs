namespace SharpDeps.Cli.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using SharpDeps.Analysis.Core.Text;
using SharpDeps.Analysis.Roslyn;
using SharpDeps.Query;
using Xunit;
using Xunit.Abstractions;

[Collection("markdown-index")]
public sealed class SemanticSessionWorkspaceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealWorkspaceCoordinatesStayColdForGeneratorsAndFrameworkFreePrototypeMatchesFull()
    {
        var root = Path.Combine(Path.GetTempPath(), "sharpdeps-semantic-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "App.csproj");
        var source = Path.Combine(root, "Service.cs");
        Assert.True(SemanticEnvironment.TryRegister(TestPathsRoot(), out var reason), reason);
        var environment = SemanticEnvironment.Describe(root);
        Assert.NotNull(environment.SdkVersion);
        File.WriteAllText(Path.Combine(root, "global.json"), JsonSerializer.Serialize(new
        { sdk = new { version = environment.SdkVersion, rollForward = "disable" } }));
        var config = Path.Combine(root, "restore.config");
        File.WriteAllText(config, "<configuration><packageSources><clear /></packageSources></configuration>");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var options = new SemanticLoadOptions(target);
        try
        {
            File.WriteAllText(target, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
                "<EnableNETAnalyzers>false</EnableNETAnalyzers></PropertyGroup></Project>");
            const string ordinary = "public class Service { public string Text() => \"\U0001f642 \u65e5\u672c\u8a9e\"; }\r\n";
            File.WriteAllText(source, ordinary, new UTF8Encoding(false, true));
            await Restore();
            foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true),
                         new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) })
            {
                File.WriteAllBytes(source, encoding.GetPreamble().Concat(encoding.GetBytes(ordinary)).ToArray());
                var bytes = File.ReadAllBytes(source);
                using var cold = await SemanticLoader.LoadCandidateAsync(options, deadline.Token);
                Assert.NotNull(cold.Solution);
                Assert.Empty(cold.Result.GeneratedDocuments);
                var tree = Assert.Single(cold.Result.Compilations.Values.SelectMany(c => c.SyntaxTrees), t => t.FilePath == source);
                Assert.Equal(ordinary, tree.GetText(deadline.Token).ToString());
                Assert.Equal(encoding.WebName, tree.GetText(deadline.Token).Encoding?.WebName);
                var rawText = SourceTextEncoding.Decode(bytes);
                Assert.Equal(ordinary, rawText.StartsWith('\uFEFF') ? rawText[1..] : rawText);
                Assert.Contains(cold.Solution.Projects, p => p.AnalyzerReferences.Any(a => a.GetGenerators(LanguageNames.CSharp).Length != 0));
                using var session = new SemanticSessionOwner();
                Assert.Null(await session.PrepareColdAsync(cold, Proof(), Capture(cold.Solution), deadline.Token));
                Assert.Equal(bytes, File.ReadAllBytes(source));
                output.WriteLine($"Real {encoding.WebName}/preamble={encoding.GetPreamble().Length}: raw bytes and compiler coordinates matched; generators kept session cold.");
            }
            // This explicit framework-free project is a test-only prototype profile, not CLI proof admission.
            File.WriteAllText(target, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
                "<EnableNETAnalyzers>false</EnableNETAnalyzers><DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>" +
                "<GenerateAssemblyInfo>false</GenerateAssemblyInfo><GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>" +
                "</PropertyGroup></Project>");
            const string prototype = "namespace System { public class Object {} public class ValueType {} public struct Void {} public struct Int32 {} }\r\n" +
                "public class Service { public const int Value = 1; public int Stable() => Value; public int Other() => Value; }\r\n// \U0001f642 \u65e5\u672c\u8a9e\r\n";
            File.WriteAllText(source, prototype, new UTF8Encoding(false, true));
            await Restore();
            using var owner = new CliSemanticSession();
            using var first = await SemanticLoader.LoadCandidateAsync(options, deadline.Token);
            var receipt = Proof();
            using var prepared = owner.BeginUpdate(full: true);
            await prepared.PrepareColdAsync(first, receipt, Capture(first.Solution!), deadline.Token);
            prepared.Adopt(1);
            var generation = 1L;
            foreach (var text in new[] { prototype.Replace("= 1", "= 2"), prototype,
                         prototype.Replace("\U0001f642", "\U0001f680"), prototype.Replace("\r\n", "\n"), prototype.Replace("const int Value", "const int Renamed").Replace("=> Value", "=> Renamed") })
            {
                File.WriteAllText(source, text, new UTF8Encoding(false, true));
                using var update = owner.BeginUpdate(full: false);
                Assert.True(update.CanUpdate);
                var candidate = await update.TryUpdateAsync(receipt, generation, Capture(first.Solution!), deadline.Token);
                Assert.NotNull(candidate);
                Assert.True(update.Warm);
                Assert.All(candidate.Report.Variants, v => Assert.Equal(0, v.ErrorDiagnosticCount));
                using var full = await SemanticLoader.LoadCandidateAsync(options, deadline.Token);
                var expected = Facts(full.Result);
                var actual = Facts(candidate);
                File.WriteAllText(Path.Combine(root, $"full-{generation}.facts.json"), JsonSerializer.Serialize(expected));
                File.WriteAllText(Path.Combine(root, $"warm-{generation}.facts.json"), JsonSerializer.Serialize(actual));
                Assert.Equal(expected, actual);
                update.Adopt(++generation);
                Assert.Equal(text, File.ReadAllText(source));
                output.WriteLine($"Framework-free prototype step {generation}: reused workspace matched independent MSBuild full load.");
            }
            var malformed = prototype.Replace("=> Value", "=> Missing");
            File.WriteAllText(source, malformed, new UTF8Encoding(false, true));
            using var malformedUpdate = owner.BeginUpdate(full: false);
            Assert.Null(await malformedUpdate.TryUpdateAsync(receipt, generation, Capture(first.Solution!), deadline.Token));
            Assert.True(malformedUpdate.CanUpdate); // The old committed baseline survives until cold publication succeeds.
            using var malformedCold = await SemanticLoader.LoadCandidateAsync(options, deadline.Token);
            using var malformedOracle = await SemanticLoader.LoadCandidateAsync(options, deadline.Token);
            foreach (var result in new[] { malformedCold.Result, malformedOracle.Result })
            {
                var errors = result.Compilations.Values.SelectMany(c => c.GetDiagnostics())
                    .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
                Assert.Equal(2, errors.Length);
                Assert.All(errors, d =>
                {
                    Assert.Equal("CS0103", d.Id);
                    Assert.False(d.IsSuppressed);
                    Assert.Equal(source, d.Location.GetLineSpan().Path);
                    Assert.Contains("Missing", d.GetMessage());
                    Assert.Equal("Missing".Length, d.Location.SourceSpan.Length);
                });
                var firstMissing = malformed.IndexOf("Missing", StringComparison.Ordinal);
                Assert.Equal(new[] { firstMissing, malformed.IndexOf("Missing", firstMissing + 1, StringComparison.Ordinal) },
                    errors.Select(d => d.Location.SourceSpan.Start).Order().ToArray());
                Assert.Equal(2, Assert.Single(result.Report.Variants).ErrorDiagnosticCount);
                Assert.Equal(2, Assert.Single(result.Report.Limitations, l => l.Code == "semantic.compilationErrors").Count);
            }
            // Error items and multiplicity are asserted above; the public report and syntax remain exact.
            Assert.Equal(Facts(malformedOracle.Result, includeRawDiagnostics: false), Facts(malformedCold.Result, includeRawDiagnostics: false));
            await malformedUpdate.PrepareColdAsync(malformedCold, receipt, Capture(malformedCold.Solution!), deadline.Token);
            malformedUpdate.Adopt(++generation);
            using var recovery = owner.BeginUpdate(full: false);
            Assert.False(recovery.CanUpdate); // An error baseline was not seeded after the cold publication.
            File.WriteAllText(source, prototype, new UTF8Encoding(false, true));
            using var recoveryCold = await SemanticLoader.LoadCandidateAsync(options, deadline.Token);
            await recovery.PrepareColdAsync(recoveryCold, receipt, Capture(recoveryCold.Solution!), deadline.Token);
            recovery.Adopt(++generation);
            using var recovered = owner.BeginUpdate(full: false);
            Assert.True(recovered.CanUpdate);
            var recoveredResult = await recovered.TryUpdateAsync(receipt, generation, Capture(recoveryCold.Solution!), deadline.Token);
            Assert.NotNull(recoveredResult);
            using var recoveredFull = await SemanticLoader.LoadCandidateAsync(options, deadline.Token);
            Assert.Equal(Facts(recoveredFull.Result), Facts(recoveredResult));
            output.WriteLine("Malformed source: warm rejected, both CS0103 items/spans/counts preserved cold, error baseline rejected, valid session recovered.");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("Semantic workspace artifacts: " + root, error);
        }
        output.WriteLine("Semantic workspace artifacts: " + root);

        Task Restore() => OracleProcess.ExecuteAsync(root, ["restore", "App.csproj", "--configfile", config, "--verbosity", "quiet"],
            deadline.Token, packageRoot: Path.Combine(root, "packages"));
        SemanticSessionProof Proof() => new(target, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))),
            environment.RoslynVersion ?? "", environment.HostRuntimeVersion ?? "", environment.MsBuildPath ?? "");
    }

    private static string TestPathsRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("SHARPDEPS_TEST_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            if (!Path.IsPathFullyQualified(explicitRoot) ||
                !Directory.Exists(Path.Combine(explicitRoot, "tests", "fixtures")) ||
                !Directory.Exists(Path.Combine(explicitRoot, "analyzer", "src")))
            {
                throw new InvalidOperationException("SHARPDEPS_TEST_REPOSITORY_ROOT must identify the repository root.");
            }

            return Path.GetFullPath(explicitRoot);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root was not found.");
    }

    private static IReadOnlyDictionary<string, SemanticSourceInput> Capture(Solution solution)
        => solution.Projects.SelectMany(p => p.Documents).Select(d => d.FilePath!).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, path =>
            {
                var bytes = File.ReadAllBytes(path);
                var encoding = bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe ? "utf16le"
                    : bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff ? "utf16be" : "utf8";
                return new SemanticSourceInput(SourceTextEncoding.Decode(bytes), Convert.ToHexString(SHA256.HashData(bytes)), encoding);
            }, StringComparer.OrdinalIgnoreCase);

    private static string[] Facts(SemanticLoadResult result, bool includeRawDiagnostics = true)
        => new[] { JsonSerializer.Serialize(result.Report with { CreatedAt = "<run>" }, QueryJson.Options) }
            .Concat(result.Compilations.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(p =>
                p.Value.SyntaxTrees.OrderBy(t => t.FilePath, StringComparer.Ordinal).SelectMany(t =>
                    t.GetRoot().DescendantTokens(descendIntoTrivia: true).Select(token =>
                        p.Key + "|" + t.FilePath + "|" + token.RawKind + "|" + token.Span + "|" + token.ToFullString()))
                    .Concat(includeRawDiagnostics ? p.Value.GetDiagnostics().Select(d => p.Key + "|" + d.Id + "|" + d.Severity + "|" + d.IsSuppressed
                        + "|" + d.Location.GetLineSpan().Path + "|" + d.Location.SourceSpan + "|" + d.GetMessage()) : []))).ToArray();
}
