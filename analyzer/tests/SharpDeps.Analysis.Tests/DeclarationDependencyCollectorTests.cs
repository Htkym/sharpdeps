using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Roslyn;
using SharpDeps.Analysis.Roslyn.Evidence;
using SharpDeps.Analysis.Roslyn.Symbols;
using Xunit;

namespace SharpDeps.Analysis.Tests;

[Collection("semantic")]
public sealed class DeclarationDependencyCollectorTests : IDisposable
{
    private const string ProfileHash = "0123456789abcdef";
    private readonly List<string> _temporaryDirectories = [];

    static DeclarationDependencyCollectorTests()
    {
        Assert.True(
            SemanticEnvironment.TryRegister(TestPaths.RepositoryRoot, out var reason),
            reason ?? "MSBuild registration failed.");
    }

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

    private sealed record Collected(
        SymbolIndex Index,
        SourceDocumentRegistry Documents,
        IReadOnlyList<CollectedEvidence> Evidence)
    {
        public IndexedType Type(string fullNameOrSimpleName)
            => Index.Types.First(type =>
                type.FullName == fullNameOrSimpleName || type.Name == fullNameOrSimpleName);

        public IEnumerable<CollectedEvidence> For(string sourceFullName, string targetFullName)
        {
            var source = Type(sourceFullName).Id;
            var target = Type(targetFullName).Id;
            return Evidence.Where(entry => entry.Evidence.SourceEntityId == source && entry.Evidence.TargetEntityId == target);
        }
    }

    private Collected Collect(params (string Path, string Text)[] files)
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

        var compilation = CSharpCompilation.Create(
            "Sample",
            trees,
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));

        var documents = new SourceDocumentRegistry(Identity.WorkspaceRootId(root), root);
        var input = new SymbolIndexInput("var_sample", "Sample", compilation);
        var index = SymbolIndexBuilder.Build(documents, [input]);
        var resolver = new SymbolResolver(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["var_sample"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sample"] = "var_sample"
            }
        });

        var collector = new DeclarationDependencyCollector(resolver, documents, index, ProfileHash);
        return new Collected(index, documents, collector.Collect([input]));
    }

    [Fact]
    public void CollectsInheritanceAndImplementationWithoutImplicitObject()
    {
        var collected = Collect((
            "Types.cs",
            """
            namespace Sample;

            public class Base
            {
            }

            public interface IContract
            {
            }

            public sealed class Derived : Base, IContract
            {
            }

            public sealed class Plain
            {
            }
            """));

        var inherits = Assert.Single(collected.For("Sample.Derived", "Sample.Base"));
        Assert.Equal("inherits", inherits.Evidence.Kind);
        Assert.Matches("^rel_[0-9a-f]{16}$", inherits.Evidence.RelationId);
        Assert.False(inherits.TargetIsExternal);

        var implements = Assert.Single(collected.For("Sample.Derived", "Sample.IContract"));
        Assert.Equal("implements", implements.Evidence.Kind);

        // Implicit System.Object inheritance must not be emitted.
        Assert.DoesNotContain(
            collected.Evidence,
            entry => entry.Evidence.TargetEntityId.Contains("Object", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(collected.For("Sample.Plain", "Sample.Base"));
    }

    [Fact]
    public void CollectsSignatureReferencesIncludingNestedTypeArguments()
    {
        var collected = Collect((
            "Service.cs",
            """
            using System.Collections.Generic;

            namespace Sample;

            public sealed class Order
            {
            }

            public sealed class Service
            {
                public List<Order> Get(Dictionary<string, Order> map, Order[] items, (Order, string) pair) => null!;
            }
            """));

        var order = collected.Type("Sample.Order").Id;
        var references = collected.Evidence
            .Where(entry => entry.Evidence.SourceEntityId == collected.Type("Sample.Service").Id)
            .Where(entry => entry.Evidence.TargetEntityId == order)
            .ToArray();

        // return type, dictionary value, array element, tuple element: each written
        // occurrence is its own evidence with its own span.
        Assert.Equal(4, references.Length);
        Assert.All(references, entry => Assert.Equal("signature", entry.Evidence.Kind));
        Assert.All(references, entry => Assert.Equal(4, references.Select(r => r.Evidence.Id).Distinct().Count()));
        Assert.Equal(
            references.Select(entry => entry.Evidence.PhysicalSpan!.Start).Distinct().Count(),
            references.Length);

        // The method is the evidence owner; the type is not.
        var method = Assert.Single(collected.Index.Members, member => member.Name == "Get");
        Assert.All(references, entry => Assert.Equal(method.Id, entry.Evidence.SourceMemberId));

        // External collection types are still referenced (List/Dictionary/string), but
        // they are not part of the index.
        Assert.Contains(collected.Evidence, entry => entry.TargetIsExternal);
    }

    [Fact]
    public void CollectsGenericConstraints()
    {
        var collected = Collect((
            "Repository.cs",
            """
            namespace Sample;

            public class Order
            {
            }

            public sealed class Repository<T>
                where T : Order
            {
            }

            public sealed class MethodConstraints
            {
                public void Add<T>(T value) where T : Order => _ = value;
            }
            """));

        // Generic types keep their arity in FullName, so look them up by simple name.
        var typeConstraint = Assert.Single(collected.For("Repository", "Order"));
        Assert.Equal("constraint", typeConstraint.Evidence.Kind);

        var methodConstraint = Assert.Single(collected.For("MethodConstraints", "Order"));
        Assert.Equal("constraint", methodConstraint.Evidence.Kind);
        Assert.NotNull(methodConstraint.Evidence.SourceMemberId);
    }

    [Fact]
    public void CollectsAttributes()
    {
        var collected = Collect((
            "Legacy.cs",
            """
            namespace Sample;

            [System.Obsolete("old")]
            public sealed class Legacy
            {
                [System.Obsolete("member")]
                public int Value { get; set; }
            }
            """));

        // One attribute on the type, one on the member: both belong to the type.
        var attributes = collected.Evidence.Where(entry => entry.Evidence.Kind == "attribute").ToArray();
        Assert.Equal(2, attributes.Length);
        Assert.All(attributes, entry => Assert.True(entry.TargetIsExternal));
        Assert.All(attributes, entry => Assert.Equal(collected.Type("Sample.Legacy").Id, entry.Evidence.SourceEntityId));
        Assert.Single(attributes, entry => entry.Evidence.SourceMemberId is null);

        var memberAttribute = collected.Evidence
            .Where(entry => entry.Evidence.Kind == "attribute" && entry.Evidence.SourceMemberId is not null)
            .ToArray();
        Assert.Single(memberAttribute);
    }

    [Fact]
    public void JudgesPublicSurfaceIncludingContainingTypes()
    {
        var collected = Collect((
            "Surface.cs",
            """
            namespace Sample;

            public sealed class Order
            {
            }

            public sealed class Open
            {
                public Order Visible { get; set; } = new();
                internal Order Internal { get; set; } = new();
                private Order Hidden { get; set; } = new();
            }

            internal sealed class Closed
            {
                public Order LooksPublic { get; set; } = new();
            }
            """));

        var open = collected.Type("Sample.Open").Id;
        var closed = collected.Type("Sample.Closed").Id;
        var order = collected.Type("Sample.Order").Id;

        var visible = Assert.Single(
            collected.Evidence,
            entry => entry.Evidence.SourceEntityId == open && entry.Evidence.SourceMemberId is not null
                && entry.Evidence.PublicSurface && entry.Evidence.TargetEntityId == order);
        Assert.True(visible.Evidence.PublicSurface);

        var internalMember = collected.Evidence
            .Where(entry => entry.Evidence.SourceEntityId == open && entry.Evidence.TargetEntityId == order)
            .ToArray();
        Assert.Contains(internalMember, entry => !entry.Evidence.PublicSurface);

        // A public member of an internal type is not external surface.
        var closedMember = Assert.Single(
            collected.Evidence,
            entry => entry.Evidence.SourceEntityId == closed && entry.Evidence.TargetEntityId == order);
        Assert.False(closedMember.Evidence.PublicSurface);
    }

    [Fact]
    public void ClassifiesSameParentAndSelfReferences()
    {
        var collected = Collect(
            (
                "First.cs",
                """
                namespace Sample.First;

                public sealed class Order
                {
                    public Order Next { get; set; } = new();
                }

                public sealed class Neighbour
                {
                    public Order Other { get; set; } = new();
                }
                """),
            (
                "Second.cs",
                """
                namespace Sample.Second;

                public sealed class Far
                {
                    public Sample.First.Order Other { get; set; } = new();
                }
                """));

        var self = Assert.Single(collected.For("Sample.First.Order", "Sample.First.Order"));
        Assert.True(self.IsSelfReference);
        Assert.True(self.SameNamespace);
        Assert.True(self.SameProjectVariant);

        var neighbour = Assert.Single(collected.For("Sample.First.Neighbour", "Sample.First.Order"));
        Assert.True(neighbour.SameNamespace);
        Assert.False(neighbour.IsSelfReference);

        var far = Assert.Single(collected.For("Sample.Second.Far", "Sample.First.Order"));
        Assert.False(far.SameNamespace);
    }

    [Fact]
    public void CollectsRecordParameterTypes()
    {
        var collected = Collect((
            "Recorded.cs",
            """
            namespace Sample;

            public sealed class Order
            {
            }

            public sealed record Recorded(Order Value, string Name);
            """));

        var record = collected.Type("Sample.Recorded").Id;
        var order = collected.Type("Sample.Order").Id;
        var evidence = Assert.Single(
            collected.Evidence,
            entry => entry.Evidence.SourceEntityId == record && entry.Evidence.TargetEntityId == order);

        Assert.Equal("signature", evidence.Evidence.Kind);
        Assert.NotNull(evidence.Evidence.SourceMemberId);
    }

    [Fact]
    public async Task MatchesTheGoldenFixturePositionAndKind()
    {
        var fixtureRoot = TestPaths.Fixture("semantic-baseline");
        var target = Path.Combine(fixtureRoot, "SemanticBaseline.sln");
        var load = await SemanticLoader.LoadAsync(new SemanticLoadOptions(target, "Debug"));
        var documents = new SourceDocumentRegistry(Identity.WorkspaceRootId(fixtureRoot), fixtureRoot);

        var inputs = load.Report.Variants
            .Where(variant => variant.LoadState == "loaded" && load.Compilations.ContainsKey(variant.VariantKey))
            .Select(variant => new SymbolIndexInput(variant.VariantKey, variant.ProjectName, load.Compilations[variant.VariantKey]))
            .ToArray();

        var index = SymbolIndexBuilder.Build(documents, inputs);
        var collector = new DeclarationDependencyCollector(
            new SymbolResolver(load.DefiningVariantByAssembly),
            documents,
            index,
            "golden0000000000");
        var evidence = collector.Collect(inputs);

        var orderStore = index.Types.First(type =>
            type.Name == "OrderStore" && type.ProjectVariantId.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase));
        var storeContract = index.Types
            .Where(type => type.Name == "IOrderStore")
            .Select(type => type.Id)
            .ToHashSet(StringComparer.Ordinal);

        var implementation = Assert.Single(
            evidence,
            entry => entry.Evidence.SourceEntityId == orderStore.Id
                && storeContract.Contains(entry.Evidence.TargetEntityId));

        Assert.Equal("implements", implementation.Evidence.Kind);
        Assert.Equal("resolved", implementation.Evidence.Confidence);
        Assert.True(implementation.Evidence.PublicSurface);
        Assert.False(implementation.SameNamespace);
        Assert.False(implementation.TargetIsExternal);
        // Infrastructure/OrderStore.cs: "public sealed class OrderStore : IOrderStore"
        // is the fifth line, so the interface token is on 0-based line 4.
        Assert.Equal(4, implementation.Evidence.PhysicalSpan!.StartLine);
        Assert.EndsWith("Infrastructure/OrderStore.cs", index.Documents
            .First(document => document.Id == implementation.Evidence.DocumentId).RelativePath, StringComparison.Ordinal);
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
            "sharpdeps-declarations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _temporaryDirectories.Add(directory);
        return directory;
    }
}
