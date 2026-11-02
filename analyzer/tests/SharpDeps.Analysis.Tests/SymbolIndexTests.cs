using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpDeps.Analysis.Roslyn.Symbols;
using Xunit;

namespace SharpDeps.Analysis.Tests;

public sealed class SymbolIndexTests : IDisposable
{
    private readonly List<string> _temporaryDirectories = [];

    public void Dispose()
    {
        foreach (var directory in _temporaryDirectories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Builds an index from source text only: fast, hermetic, and enough to pin the
    /// declaration/flags/position behavior.
    /// </summary>
    private SymbolIndex BuildFromSource(params (string Path, string Text)[] files)
    {
        var root = CreateTemporaryDirectory();
        var trees = new List<SyntaxTree>();
        foreach (var (relativePath, text) in files)
        {
            var fullPath = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, text);
            trees.Add(CSharpSyntaxTree.ParseText(text, path: fullPath));
        }

        var references = PlatformReferences();
        var hasTopLevelStatements = trees.Any(tree =>
            tree.GetRoot().ChildNodes().Any(node => node is Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax));
        var compilation = CSharpCompilation.Create(
            "Sample",
            trees,
            references,
            new CSharpCompilationOptions(
                hasTopLevelStatements ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(
            errors.Length == 0,
            string.Join(Environment.NewLine, errors.Select(error => error.ToString())));

        return SymbolIndexBuilder.Build(
            SharpDeps.Analysis.Core.Identity.Identity.WorkspaceRootId(root),
            root,
            [new SymbolIndexInput("var_sample", "Sample", compilation)]);
    }

    [Fact]
    public void DistinguishesFileLocalTypesAndUsesTheDeclaredNamespaceOfNestedTypes()
    {
        var index = BuildFromSource(
            ("One.cs", "namespace Sample; file class Same { } public class Outer { public class Inner { } }"),
            ("Two.cs", "namespace Sample; file class Same { }"));
        Assert.Equal(2, index.Types.Where(type => type.Name == "Same").Select(type => type.Id).Distinct().Count());
        Assert.Equal("Sample", Assert.Single(index.Namespaces).Name);
        Assert.Equal(4, index.Namespaces[0].TypeCount);
    }

    [Fact]
    public void MergesPartialDeclarationsIntoOneTypeWithSeveralLocations()
    {
        const string definition = "namespace Sample;\n\npublic sealed partial class PartialThing\n{\n    public string First { get; set; } = string.Empty;\n    partial void Update(ref int value);\n}\n";
        const string implementation = "namespace Sample;\n\npublic sealed partial class PartialThing\n{\n    public string Second { get; set; } = string.Empty;\n    partial void Update(ref int value) { value++; }\n}\n";
        var index = BuildFromSource(
            ("Partial.cs", definition),
            ("Partial.Second.cs", implementation));

        var type = Assert.Single(index.Types, entry => entry.Name == "PartialThing");

        Assert.True(type.IsPartial);
        Assert.Equal(2, type.Declarations.Count);
        Assert.Equal(2, type.Declarations.Select(declaration => declaration.DocumentId).Distinct().Count());
        Assert.Contains(
            index.Members,
            member => member.TypeId == type.Id && member.Name == "First");
        Assert.Contains(
            index.Members,
            member => member.TypeId == type.Id && member.Name == "Second");
        var method = Assert.Single(index.Members, member => member.Name == "Update");
        Assert.StartsWith("M:Sample.PartialThing.Update(System.Int32@)|", method.HarnessSignature);
        Assert.Contains("ref ", method.HarnessSignature, StringComparison.Ordinal);
        Assert.Equal(2, method.HarnessDeclarations!.Count);
        Assert.Equal(2, method.HarnessDeclarations.Select(location => location.DocumentId).Distinct().Count());
        foreach (var location in method.HarnessDeclarations)
        {
            var source = location.RelativePath == "Partial.cs" ? definition : implementation;
            var expected = location.RelativePath == "Partial.cs"
                ? "partial void Update(ref int value);"
                : "partial void Update(ref int value) { value++; }";
            Assert.Equal(expected, source.Substring(location.Start, location.Length));
        }
    }

    [Fact]
    public void HarnessFieldAndEventRangesRetainTheSharedDeclarationAndLegacyDeclarators()
    {
        const string field = "[System.Obsolete] private readonly T first = default!, second = default!;";
        const string @event = "public event System.Action? Changed, Other;";
        const string source = "namespace Sample;\r\npublic sealed class State<T>\r\n{\r\n    // 😀 state\r\n    "
            + field + "\r\n    " + @event + "\r\n}\r\n";
        var index = BuildFromSource(("State.cs", source));
        var document = Assert.Single(index.Documents);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant(), document.ContentHash);
        foreach (var (name, declaration, legacy) in new[] {
            ("first", field, "first = default!"), ("second", field, "second = default!"),
            ("Changed", @event, "Changed"), ("Other", @event, "Other") })
        {
            var member = Assert.Single(index.Members, member => member.Name == name);
            var harness = Assert.Single(member.HarnessDeclarations!);
            Assert.Equal(document.Id, harness.DocumentId);
            Assert.Equal(declaration, source.Substring(harness.Start, harness.Length));
            Assert.Equal(source.IndexOf(declaration, StringComparison.Ordinal), harness.Start);
            Assert.Equal(4, harness.StartCharacter);
            Assert.Equal(harness.StartLine, harness.EndLine);
            Assert.Equal(harness.StartCharacter + harness.Length, harness.EndCharacter);
            var original = Assert.Single(member.Declarations);
            Assert.Equal(legacy, source.Substring(original.Start, original.Length));
        }
    }

    [Fact]
    public void IdentifiesNestedGenericFileLocalAndTopLevelDeclarations()
    {
        var index = BuildFromSource((
            "Shapes.cs",
            """
            namespace Sample;

            public sealed class Outer
            {
                public sealed class Inner
                {
                }
            }

            public sealed class GenericThing<T>
            {
            }

            file sealed class FileLocalThing
            {
            }

            public enum Kind { First, Second }

            public delegate void Notifier(string message);

            public sealed record Recorded(string Name);
            """),
            ("Program.cs", "System.Console.WriteLine(\"top level statement\");\n"));

        var outer = Assert.Single(index.Types, entry => entry.Name == "Outer");
        var inner = Assert.Single(index.Types, entry => entry.Name == "Inner");
        var generic = Assert.Single(index.Types, entry => entry.Name == "GenericThing");
        var fileLocal = Assert.Single(index.Types, entry => entry.Name == "FileLocalThing");
        var kind = Assert.Single(index.Types, entry => entry.Name == "Kind");
        var notifier = Assert.Single(index.Types, entry => entry.Name == "Notifier");
        var recorded = Assert.Single(index.Types, entry => entry.Name == "Recorded");

        Assert.True(inner.IsNested);
        Assert.Equal(outer.Id, inner.ContainingTypeId);
        Assert.False(outer.IsNested);

        Assert.True(generic.IsGeneric);
        Assert.Equal(1, generic.Arity);

        Assert.True(fileLocal.IsFileLocal);
        Assert.Equal("file", fileLocal.Accessibility);

        Assert.Equal("enum", kind.Kind);
        Assert.Equal("delegate", notifier.Kind);
        Assert.Equal("record", recorded.Kind);
        Assert.True(index.Summary.HasTopLevelStatements);
    }

    [Fact]
    public void MergesNestedTypesWithTheSameNameInDifferentOuterTypes()
    {
        var index = BuildFromSource((
            "Two.cs",
            """
            namespace Sample;

            public sealed class First
            {
                public sealed class Shared
                {
                }
            }

            public sealed class Second
            {
                public sealed class Shared
                {
                }
            }
            """));

        var shared = index.Types.Where(type => type.Name == "Shared").ToArray();

        Assert.Equal(2, shared.Length);
        Assert.NotEqual(shared[0].Id, shared[1].Id);
        Assert.NotEqual(shared[0].ContainingTypeId, shared[1].ContainingTypeId);
    }

    [Fact]
    public void FindsTheInnermostTypeAtAPosition()
    {
        var index = BuildFromSource((
            "Nested.cs",
            "namespace Sample;\n\npublic sealed class Outer\n{\n    public sealed class Inner\n    {\n    }\n}\n"));

        var outer = Assert.Single(index.Types, entry => entry.Name == "Outer");
        var inner = Assert.Single(index.Types, entry => entry.Name == "Inner");
        var outerDeclaration = Assert.Single(outer.Declarations);

        // Inside the nested declaration the innermost type wins.
        Assert.Equal(inner.Id, index.PositionIndex.FindTypeIdAt(inner.Declarations[0].DocumentId, inner.Declarations[0].Start + 1));

        // Before the nested type, the outer type owns the position.
        Assert.Equal(outer.Id, index.PositionIndex.FindTypeIdAt(outerDeclaration.DocumentId, outerDeclaration.Start + 1));

        // Positions outside any declaration have no owner.
        Assert.Null(index.PositionIndex.FindTypeIdAt(outerDeclaration.DocumentId, 0));
        Assert.Null(index.PositionIndex.FindTypeIdAt("doc_missing", 10));
    }

    [Fact]
    public void IndexesMembersWithStableIdsAndSignatures()
    {
        var first = BuildFromSource((
            "Members.cs",
            "namespace Sample;\n\npublic sealed class Service\n{\n    public string Describe(int count, string name) => name;\n    public int Count { get; set; }\n    private readonly string _field = string.Empty;\n}\n"));
        var second = BuildFromSource((
            "Members.cs",
            "namespace Sample;\n\npublic sealed class Service\n{\n    public string Describe(int count, string name) => name;\n    public int Count { get; set; }\n    private readonly string _field = string.Empty;\n}\n"));

        var describe = Assert.Single(first.Members, member => member.Name == "Describe");

        Assert.Equal("method", describe.Kind);
        Assert.Equal("public", describe.Accessibility);
        Assert.Contains("System.Int32", describe.Signature, StringComparison.Ordinal);
        Assert.Contains("System.String", describe.Signature, StringComparison.Ordinal);
        Assert.Equal(
            second.Members.Select(member => member.Id).OrderBy(id => id, StringComparer.Ordinal),
            first.Members.Select(member => member.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void GeneratedTreeTypeWithGeneratedCodeAttributeIsIndexed()
    {
        var root = CreateTemporaryDirectory();
        var rootId = SharpDeps.Analysis.Core.Identity.Identity.WorkspaceRootId(root);
        const string variant = "Sample.csproj|net10.0|Debug|AnyCPU";
        const string source = "[System.CodeDom.Compiler.GeneratedCode(\"Some.Generator\", \"1.0\")] public class GeneratedType { public void Run() { } }";
        var tree = CSharpSyntaxTree.ParseText(source, path: Path.Combine(root, "obj", "Some.Generator", "Generated.g.cs"));
        var compilation = CSharpCompilation.Create("Sample", [tree], PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
        var registry = new SourceDocumentRegistry(rootId, root);
        var bytes = Encoding.UTF8.GetBytes(source);
        var document = registry.RegisterGenerated(variant, "Sample", "Some.Generator/Generator", "Generated.g.cs",
            tree.FilePath, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length);
        var inputs = new[] { new SymbolIndexInput(variant, "Sample", compilation) };

        Assert.True(registry.IsGeneratedTree(variant, tree));
        Assert.Same(document, registry.ResolveTreeDocument(variant, tree));
        var index = SymbolIndexBuilder.Build(registry, inputs);
        var type = Assert.Single(index.Types, entry => entry.Name == "GeneratedType");
        var member = Assert.Single(index.Members, entry => entry.TypeId == type.Id && entry.Name == "Run");
        foreach (var declaration in new[] { Assert.Single(type.Declarations), Assert.Single(member.Declarations) })
        {
            var declaredDocument = Assert.Single(index.Documents, entry => entry.Id == declaration.DocumentId);
            Assert.Equal(document.Id, declaredDocument.Id);
            Assert.StartsWith("generated/", declaredDocument.RelativePath);
            Assert.Contains("/net10.0_Debug_AnyCPU/", declaredDocument.RelativePath, StringComparison.Ordinal);
        }

        var unregistered = new SourceDocumentRegistry(rootId, root);
        Assert.False(unregistered.IsGeneratedTree(variant, tree));
        var excluded = SymbolIndexBuilder.Build(unregistered, inputs);
        Assert.DoesNotContain(excluded.Types, entry => entry.Name == "GeneratedType");
        Assert.DoesNotContain(excluded.Members, entry => entry.Name == "Run");
    }

    [Fact]
    public void GeneratedDocumentsWithTheSameHintAndTreePathRemainDistinctAcrossVariants()
    {
        var root = CreateTemporaryDirectory();
        var registry = new SourceDocumentRegistry(SharpDeps.Analysis.Core.Identity.Identity.WorkspaceRootId(root), root);
        var path = Path.Combine(root, "obj", "Generated.g.cs");
        var first = registry.RegisterGenerated("Sample.csproj|net10.0|Debug", "Sample", "Some.Generator/Generator",
            "Generated.g.cs", path, new string('a', 64), 10);
        var second = registry.RegisterGenerated("Sample.csproj|net8.0|Debug", "Sample", "Some.Generator/Generator",
            "Generated.g.cs", path, new string('a', 64), 10);

        Assert.Equal(2, registry.Documents.Count);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.RelativePath, second.RelativePath);
        var tree = CSharpSyntaxTree.ParseText("", path: path);
        Assert.Same(first, registry.ResolveTreeDocument("Sample.csproj|net10.0|Debug", tree));
        Assert.Same(second, registry.ResolveTreeDocument("Sample.csproj|net8.0|Debug", tree));
    }

    [Fact]
    public void GeneratedDocumentIdentityReusesTheSameBodyAndRejectsAConflictingHash()
    {
        var root = CreateTemporaryDirectory();
        var registry = new SourceDocumentRegistry(SharpDeps.Analysis.Core.Identity.Identity.WorkspaceRootId(root), root);
        var path = Path.Combine(root, "obj", "Generated.g.cs");
        var first = registry.RegisterGenerated("variant", "Sample", "Some.Generator/Generator", "Generated.g.cs",
            path, new string('a', 64), 10);

        Assert.Same(first, registry.RegisterGenerated("variant", "Sample", "Some.Generator/Generator", "Generated.g.cs",
            path, new string('a', 64), 10));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterGenerated("variant", "Sample",
            "Some.Generator/Generator", "Generated.g.cs", path, new string('b', 64), 10));
        Assert.Same(first, Assert.Single(registry.Documents));
    }

    [Fact]
    public void GeneratedDocumentPathsIncludeTheDistinctGeneratorIdentities()
    {
        var root = CreateTemporaryDirectory();
        var registry = new SourceDocumentRegistry(SharpDeps.Analysis.Core.Identity.Identity.WorkspaceRootId(root), root);
        var first = registry.RegisterGenerated("variant", "Sample", "First.Assembly/First.Generator", "Generated.g.cs",
            null, new string('a', 64), 10);
        var second = registry.RegisterGenerated("variant", "Sample", "Second.Assembly/Second.Generator", "Generated.g.cs",
            null, new string('a', 64), 10);

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.RelativePath, second.RelativePath);
        Assert.Contains("/First.Assembly/First.Generator/", first.RelativePath, StringComparison.Ordinal);
        Assert.Contains("/Second.Assembly/Second.Generator/", second.RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedDocumentPathsAreStableRegardlessOfRegistrationOrder()
    {
        var root = CreateTemporaryDirectory();
        const string project = "Sample";
        const string generator = "Some.Generator/Generator";
        const string hint = "Generated.g.cs";
        // Same project name, TFM, generator and hint: the identities differ only in
        // the project path inside the variant key, so both sanitize to one base path.
        const string firstVariant = "C:/work/first/src/Sample.csproj|net10.0|Debug|AnyCPU";
        const string secondVariant = "C:/work/second/src/Sample.csproj|net10.0|Debug|AnyCPU";
        var hash = new string('a', 64);

        var forward = new SourceDocumentRegistry(SharpDeps.Analysis.Core.Identity.Identity.WorkspaceRootId(root), root);
        var forwardFirst = forward.RegisterGenerated(firstVariant, project, generator, hint, null, hash, 10);
        var forwardSecond = forward.RegisterGenerated(secondVariant, project, generator, hint, null, hash, 10);
        var reverse = new SourceDocumentRegistry(SharpDeps.Analysis.Core.Identity.Identity.WorkspaceRootId(root), root);
        var reverseSecond = reverse.RegisterGenerated(secondVariant, project, generator, hint, null, hash, 10);
        var reverseFirst = reverse.RegisterGenerated(firstVariant, project, generator, hint, null, hash, 10);

        Assert.Equal(2, forward.Documents.Count);
        Assert.Equal(2, reverse.Documents.Count);
        Assert.Equal(forwardFirst.Id, reverseFirst.Id);
        Assert.Equal(forwardSecond.Id, reverseSecond.Id);
        Assert.Equal(forwardFirst.RelativePath, reverseFirst.RelativePath);
        Assert.Equal(forwardSecond.RelativePath, reverseSecond.RelativePath);
        // Neither identity may own the shared base path just by being registered first.
        const string basePath = "generated/Sample/net10.0_Debug_AnyCPU/Some.Generator/Generator/Generated.g.cs";
        Assert.NotEqual(basePath, forwardFirst.RelativePath);
        Assert.NotEqual(basePath, forwardSecond.RelativePath);
        Assert.NotEqual(forwardFirst.RelativePath, forwardSecond.RelativePath);
    }

    [Fact]
    public void GeneratedDocumentRegistrationRefusesAnUnidentifiedGenerator()
    {
        var root = CreateTemporaryDirectory();
        var registry = new SourceDocumentRegistry(SharpDeps.Analysis.Core.Identity.Identity.WorkspaceRootId(root), root);

        Assert.Throws<InvalidOperationException>(() => registry.RegisterGenerated("variant", "Sample", "",
            "Generated.g.cs", Path.Combine(root, "obj", "Generated.g.cs"), new string('a', 64), 10));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterGenerated("variant", "Sample", " ",
            "Generated.g.cs", null, new string('a', 64), 10));
        Assert.Empty(registry.Documents);
    }

    [Fact]
    public void GeneratedDocumentTreePathsRefuseASecondIdentity()
    {
        var root = CreateTemporaryDirectory();
        var registry = new SourceDocumentRegistry(SharpDeps.Analysis.Core.Identity.Identity.WorkspaceRootId(root), root);
        var path = Path.Combine(root, "obj", "Generated.g.cs");
        var first = registry.RegisterGenerated("variant", "Sample", "First.Assembly/Generator", "Generated.g.cs",
            path, new string('a', 64), 10);

        Assert.Same(first, registry.ResolveTreeDocument("variant", CSharpSyntaxTree.ParseText(string.Empty, path: path)));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterGenerated("variant", "Sample",
            "Second.Assembly/Generator", "Generated.g.cs", path, new string('b', 64), 10));
        Assert.Same(first, Assert.Single(registry.Documents));
    }

    private static MetadataReference[] PlatformReferences()
    {
        var trustedPlatformAssemblies =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        return trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "sharpdeps-symbols-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _temporaryDirectories.Add(directory);
        return directory;
    }
}
