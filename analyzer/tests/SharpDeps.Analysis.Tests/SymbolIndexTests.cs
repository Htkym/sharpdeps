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
        var index = BuildFromSource(
            ("Partial.cs", "namespace Sample;\n\npublic sealed partial class PartialThing\n{\n    public string First { get; set; } = string.Empty;\n}\n"),
            ("Partial.Second.cs", "namespace Sample;\n\npublic sealed partial class PartialThing\n{\n    public string Second { get; set; } = string.Empty;\n}\n"));

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
