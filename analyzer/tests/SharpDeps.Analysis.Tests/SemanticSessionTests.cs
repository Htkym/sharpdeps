using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SharpDeps.Analysis.Roslyn;
using Xunit;

namespace SharpDeps.Analysis.Tests;

public sealed class SemanticSessionTests
{
    private static readonly string Root = Path.Combine(TestPaths.RepositoryRoot, ".local", "session-input");
    private static readonly string Shared = Path.Combine(Root, "Shared.cs");
    private static readonly string Dependent = Path.Combine(Root, "Dependent.cs");
    private static readonly string Indirect = Path.Combine(Root, "Indirect.cs");
    private static readonly SemanticSessionProof Proof = new("workspace/target/profile/tool", "structure", "dependencies", "environment", "sdk");
    private const string Initial = "#if FIRST\r\npublic class A { public const int Value = 1; public int Stable() => 3; }\r\n#else\r\npublic class B { public const int Value = 1; public int Stable() => 3; }\r\n#endif\r\n// \U0001f642 \u65e5\u672c\u8a9e\r\n";

    [Theory]
    [InlineData("", "utf8")]
    [InlineData("\uFEFF", "utf8")]
    [InlineData("\uFEFF", "utf16le")]
    [InlineData("\uFEFF", "utf16be")]
    public async Task LinkedOwnersAndDependentsMatchFreshEvaluationAcrossEditUndoAndDiscard(string bom, string encoding)
    {
        using var owner = new SemanticSessionOwner();
        var input = Inputs(bom + Initial, encoding);
        using var cold = await ColdAsync(input);
        using var first = await owner.PrepareColdAsync(cold, Proof, input, default);
        Assert.NotNull(first);
        Assert.True(owner.Adopt(first, 1));
        Assert.True(first.Result.Report.Variants.Single(v => v.ProjectName == "P3").AddedTransitiveReferences >= 1);
        Assert.True(owner.AdvanceCommitted(Proof, 1, 2));
        var generation = 2L;
        var previousResult = first.Result;
        foreach (var next in new[] { Initial.Replace("class A { public const int Value = 1", "class A { public const int Value = 2"), Initial,
                     Initial.Replace("\U0001f642", "\U0001f680"), Initial.Replace("\r\n", "\n"), Initial.Replace("Value", "Renamed") })
        {
            var captured = Inputs(bom + next, encoding);
            using var candidate = await owner.TryUpdateAsync(Proof, generation, captured, default);
            Assert.NotNull(candidate);
            Assert.True(candidate.Warm);
            Assert.Equal(4, candidate.DirtyVariants.Count);
            if (generation < 4)
            {
                foreach (var pair in candidate.Result.Compilations)
                {
                    if (!pair.Value.SyntaxTrees.Any(t => t.FilePath == Shared))
                        continue;
                    var oldMember = previousResult.Compilations[pair.Key].SyntaxTrees.Single(t => t.FilePath == Shared)
                        .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
                    var newMember = pair.Value.SyntaxTrees.Single(t => t.FilePath == Shared)
                        .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
                    Assert.True(newMember.IsIncrementallyIdenticalTo(oldMember));
                    var beforeText = oldMember.SyntaxTree.GetText();
                    var afterText = newMember.SyntaxTree.GetText();
                    Assert.Equal(beforeText.Encoding?.WebName, afterText.Encoding?.WebName);
                    Assert.Equal(beforeText.ChecksumAlgorithm, afterText.ChecksumAlgorithm);
                }
            }
            using var oracle = await ColdAsync(captured);
            Assert.Equal(CompilationFacts(oracle.Result), CompilationFacts(candidate.Result));
            Assert.All(candidate.Result.Compilations.Values.SelectMany(c => c.SyntaxTrees)
                .Where(t => t.FilePath == Shared), tree => Assert.Equal(next, tree.GetText().ToString()));
            Assert.True(owner.Adopt(candidate, ++generation));
            previousResult = candidate.Result;
        }
        // A candidate discarded before commit must not replace the adopted baseline.
        using (var abandoned = await owner.TryUpdateAsync(Proof, generation, Inputs(bom + Initial, encoding), default))
            Assert.NotNull(abandoned);
        using var retry = await owner.TryUpdateAsync(Proof, generation, Inputs(bom + Initial, encoding), default);
        Assert.NotNull(retry);
        using var expected = await ColdAsync(Inputs(bom + Initial, encoding));
        Assert.Equal(CompilationFacts(expected.Result), CompilationFacts(retry.Result));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.TryUpdateAsync(Proof, generation, input, cancelled.Token));
        Assert.True(owner.HasCommitted);
    }

    [Fact]
    public async Task StructuralProofEncodingMembershipAndStaleAdoptionFailClosed()
    {
        using var owner = new SemanticSessionOwner();
        var input = Inputs(Initial);
        using var cold = await ColdAsync(input);
        using var first = await owner.PrepareColdAsync(cold, Proof, input, default);
        Assert.True(owner.Adopt(first!, 1));
        using var stale = await owner.TryUpdateAsync(Proof, 1, Inputs(Initial.Replace("= 1", "= 2")), default);
        using var current = await owner.TryUpdateAsync(Proof, 1, Inputs(Initial.Replace("= 1", "= 3")), default);
        Assert.True(owner.Adopt(current!, 2));
        Assert.False(owner.Adopt(stale!, 3));
        Assert.False(owner.HasCommitted);
        using var reload = await ColdAsync(input);
        using var replacement = await owner.PrepareColdAsync(reload, Proof, input, default);
        Assert.True(owner.Adopt(replacement!, 3));
        Assert.Null(await owner.TryUpdateAsync(Proof with { StructureHash = "changed" }, 3, input, default));
        Assert.False(owner.HasCommitted);
        using var reload2 = await ColdAsync(input);
        using var replacement2 = await owner.PrepareColdAsync(reload2, Proof, input, default);
        Assert.True(owner.Adopt(replacement2!, 4));
        var encoding = Inputs(Initial.Replace("= 1", "= 4"));
        encoding[Shared] = encoding[Shared] with { EncodingName = "utf16be" };
        Assert.Null(await owner.TryUpdateAsync(Proof, 4, encoding, default));
        using var reload3 = await ColdAsync(input);
        using var replacement3 = await owner.PrepareColdAsync(reload3, Proof, input, default);
        Assert.True(owner.Adopt(replacement3!, 5));
        input[Path.Combine(Root, "Added.cs")] = new("class Added {}", "hash");
        Assert.Null(await owner.TryUpdateAsync(Proof, 5, input, default));
    }

    [Fact]
    public async Task LineageIsBoundedAndAdditionalInputsStayCold()
    {
        var clock = new SessionClock();
        using var owner = new SemanticSessionOwner(clock);
        var input = Inputs(Initial);
        using var cold = await ColdAsync(input);
        using var first = await owner.PrepareColdAsync(cold, Proof, input, default);
        Assert.True(owner.Adopt(first!, 1));
        for (var generation = 2; generation <= 32; generation++)
        {
            using var next = await owner.TryUpdateAsync(Proof, generation - 1, Inputs(Initial.Replace("= 1", "= " + generation)), default);
            Assert.NotNull(next);
            Assert.True(owner.Adopt(next, generation));
        }
        Assert.Null(await owner.TryUpdateAsync(Proof, 32, input, default));
        Assert.False(owner.HasCommitted);
        using var unsupported = await ColdAsync(input, additional: true);
        Assert.Null(await owner.PrepareColdAsync(unsupported, Proof, input, default));
        using var reloaded = await ColdAsync(input);
        using var resumed = await owner.PrepareColdAsync(reloaded, Proof, input, default);
        Assert.True(owner.Adopt(resumed!, 33));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(await owner.TryUpdateAsync(Proof, 33, input, default));
        Assert.False(owner.HasCommitted);
    }

    private static Dictionary<string, SemanticSourceInput> Inputs(string source, string encoding = "utf8")
    {
        var member = source.Contains("const int Renamed", StringComparison.Ordinal) ? "Renamed" : "Value";
        return new(StringComparer.OrdinalIgnoreCase)
        {
            [Shared] = Input(source, encoding),
            [Dependent] = Input("public class C { int X = A." + member + "; }"),
            [Indirect] = Input("public class D { int Y = A." + member + "; }")
        };
    }

    private static SemanticSourceInput Input(string text, string encoding = "utf8")
        => new(text, Convert.ToHexString(SHA256.HashData(EncodingFor(encoding).GetBytes(text))), encoding);

    private static async Task<SemanticColdLoad> ColdAsync(IReadOnlyDictionary<string, SemanticSourceInput> inputs, bool additional = false)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var ids = Enumerable.Range(0, 4).Select(_ => ProjectId.CreateNewId()).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            solution = solution.AddProject(ProjectInfo.Create(ids[i], VersionStamp.Create(), "P" + i, "P" + i,
                LanguageNames.CSharp, filePath: Path.Combine(Root, "P" + i + ".csproj"),
                outputFilePath: Path.Combine(Root, "net10.0", "P" + i + ".dll"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(preprocessorSymbols: i == 0 ? ["FIRST"] : []),
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
            var path = i < 2 ? Shared : i == 2 ? Dependent : Indirect;
            var text = inputs[path].Text;
            if (text.StartsWith('\uFEFF'))
                text = text[1..];
            solution = solution.AddDocument(DocumentId.CreateNewId(ids[i]), Path.GetFileName(path), SourceText.From(text, EncodingFor(inputs[path].EncodingName)), filePath: path);
        }
        solution = solution.AddProjectReference(ids[2], new ProjectReference(ids[0]));
        solution = solution.AddProjectReference(ids[3], new ProjectReference(ids[2]));
        if (additional)
            solution = solution.AddAdditionalDocument(DocumentId.CreateNewId(ids[0]), "data.txt", SourceText.From("data"));
        var options = new SemanticLoadOptions(Path.Combine(Root, "Test.slnx"));
        var result = await SemanticLoader.EvaluateAsync(solution, options, [], default);
        return new(workspace, solution, options, [], result);
    }

    private sealed class SessionClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }

    private static Encoding EncodingFor(string name) => name switch
    {
        "utf16le" => new UnicodeEncoding(false, true, true),
        "utf16be" => new UnicodeEncoding(true, true, true),
        _ => new UTF8Encoding(false, true)
    };

    private static string[] CompilationFacts(SemanticLoadResult loaded)
        => new[] { JsonSerializer.Serialize(loaded.Report with { CreatedAt = "<run>" }) }
            .Concat(loaded.Compilations.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(p =>
            p.Value.SyntaxTrees.OrderBy(t => t.FilePath, StringComparer.Ordinal).SelectMany(t =>
                t.GetRoot().DescendantTokens(descendIntoTrivia: true).Select(token =>
                    p.Key + "|" + t.FilePath + "|" + token.RawKind + "|" + token.Span + "|" + token.ToFullString()))
            .Concat(p.Value.GetDiagnostics().Select(d => p.Key + "|" + d.Id + "|" + d.Severity + "|" + d.IsSuppressed
                + "|" + d.Location.GetLineSpan().Path + "|" + d.Location.SourceSpan + "|" + d.GetMessage())))
            ).ToArray();
}
