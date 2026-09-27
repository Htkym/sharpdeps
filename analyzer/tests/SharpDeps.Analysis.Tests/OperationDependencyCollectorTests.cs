using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Roslyn;
using SharpDeps.Analysis.Roslyn.Evidence;
using SharpDeps.Analysis.Roslyn.Symbols;
using Xunit;

namespace SharpDeps.Analysis.Tests;

[Collection("semantic")]
public sealed class OperationDependencyCollectorTests : IDisposable
{
    private const string ProfileHash = "0123456789abcdef";
    private readonly List<string> _temporaryDirectories = [];

    static OperationDependencyCollectorTests()
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
        IReadOnlyList<CollectedEvidence> Evidence,
        OperationCollectionStats Stats)
    {
        public IndexedType Type(string name)
            => Index.Types.First(type => type.Name == name);

        public IEnumerable<CollectedEvidence> From(string sourceName, string? kind = null)
        {
            var source = Type(sourceName).Id;
            return Evidence.Where(entry =>
                entry.Evidence.SourceEntityId == source
                && (kind is null || entry.Evidence.Kind == kind));
        }

        public IEnumerable<CollectedEvidence> To(IndexedType target, string? kind = null)
            => Evidence.Where(entry =>
                entry.Evidence.TargetEntityId == target.Id
                && (kind is null || entry.Evidence.Kind == kind));
    }

    private Collected Collect(params (string Path, string Text)[] files)
        => CollectCore(allowErrors: false, files);

    private Collected CollectAllowingErrors(params (string Path, string Text)[] files)
        => CollectCore(allowErrors: true, files);

    private Collected CollectCore(bool allowErrors, params (string Path, string Text)[] files)
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

        var hasTopLevelStatements = trees.Any(tree =>
            tree.GetRoot().ChildNodes().Any(node => node is Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax));
        var compilation = CSharpCompilation.Create(
            "Sample",
            trees,
            PlatformReferences(),
            new CSharpCompilationOptions(
                hasTopLevelStatements ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary));

        if (!allowErrors)
        {
            var errors = compilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToArray();
            Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
        }

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

        var collector = new OperationDependencyCollector(resolver, documents, index, ProfileHash);
        var result = collector.Collect([input]);
        return new Collected(index, result.Evidence, result.Stats);
    }

    [Fact]
    public void CollectsInitializersGenericArgumentsLocalsAndPatterns()
    {
        var collected = Collect(("References.cs", """
            namespace Sample;
            public class Foo { }
            public class G<T> { }
            public class Service
            {
                object field = new Foo();
                object Property { get; } = new Foo();
                void Run() { var g = new G<Foo>(); Foo local = null; if (local is Foo f) { } }
            }
            """));
        var foo = collected.Type("Foo");
        Assert.Equal(2, collected.To(foo, "constructs").Count());
        Assert.Equal(3, collected.To(foo, "typeUse").Count());
    }

    [Fact]
    public void UnusedUsingDoesNotAddEdges()
    {
        const string body = """
            namespace Sample;

            public sealed class Order
            {
                public string Id { get; set; } = string.Empty;
            }

            public sealed class Service
            {
                public string Read(Order order) => order.Id;
            }
            """;

        var without = Collect(("Service.cs", body));
        var with = Collect(("Service.cs", body.Replace(
            "namespace Sample;",
            "using Sample.Unrelated;\n\nnamespace Sample;",
            StringComparison.Ordinal)),
            ("Unrelated.cs", "namespace Sample.Unrelated;\n\npublic sealed class Unrelated\n{\n}\n"));

        // Compare structure, not ids: adding an unused using shifts line numbers, so
        // span-derived ids legitimately change while the graph must not.
        static string[] Shape(Collected collected) => collected.Evidence
            .Select(entry =>
                $"{entry.Evidence.Kind}:{collected.Index.Types.First(type => type.Id == entry.Evidence.SourceEntityId).FullName}"
                + $"->{collected.Index.Types.FirstOrDefault(type => type.Id == entry.Evidence.TargetEntityId)?.FullName ?? "external"}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Shape(without), Shape(with));
    }

    [Fact]
    public void ResolvesFullyQualifiedReferencesWithoutUsing()
    {
        var collected = Collect(
            (
                "Order.cs",
                "namespace Sample.Other;\n\npublic sealed class Order\n{\n}\n"),
            (
                "Service.cs",
                """
                namespace Sample.Client;

                public sealed class Service
                {
                    public Sample.Other.Order Create() => new Sample.Other.Order();
                }
                """));

        var order = collected.Type("Order");
        var created = collected.Evidence
            .Where(entry => entry.Evidence.Kind == "constructs" && entry.Evidence.TargetEntityId == order.Id)
            .ToArray();

        Assert.Single(created);
        Assert.Equal(collected.Type("Service").Id, created[0].Evidence.SourceEntityId);
    }

    [Fact]
    public void CountsAWrittenReferenceOnce()
    {
        var collected = Collect((
            "Service.cs",
            """
            namespace Sample;

            public sealed class Order
            {
            }

            public sealed class Service
            {
                public Order Create() => new Order();
                public void Assign()
                {
                    Order order = new();
                    _ = order;
                }
            }
            """));

        var order = collected.Type("Order");
        var creation = collected.To(order, "constructs").ToArray();

        // One `new Order()` and one target-typed `new()`: two constructs, no extra
        // typeUse evidence for the same syntax.
        Assert.Equal(2, creation.Length);
        // The explicitly written local type is a separate reference from new().
        Assert.Equal("Order".Length, Assert.Single(collected.To(order, "typeUse")).Evidence.PhysicalSpan!.Length);
        Assert.All(creation, entry => Assert.Matches("^ev_[0-9a-f]{16}$", entry.Evidence.Id));
    }

    [Fact]
    public void CollectsCallsAndMemberAccess()
    {
        var collected = Collect((
            "Service.cs",
            """
            namespace Sample;

            public sealed class Order
            {
                public string Id { get; set; } = string.Empty;
                public void Save()
                {
                }
            }

            public sealed class Service
            {
                public void Run(Order order)
                {
                    order.Save();
                    _ = order.Id;
                }
            }
            """));

        var order = collected.Type("Order");
        var calls = collected.To(order, "calls").ToArray();
        var accesses = collected.To(order, "memberAccess").ToArray();

        Assert.Single(calls);
        Assert.NotNull(calls[0].Evidence.TargetMemberId);
        Assert.Single(accesses);
        Assert.NotNull(accesses[0].Evidence.TargetMemberId);
        Assert.Equal(collected.Type("Service").Id, calls[0].Evidence.SourceEntityId);
    }

    [Fact]
    public void CollectsTypeUseAndCompileTimeNames()
    {
        var collected = Collect((
            "Service.cs",
            """
            namespace Sample;

            public sealed class Order
            {
                public string Id { get; set; } = string.Empty;
            }

            public sealed class Service
            {
                public void Run(object value)
                {
                    _ = (Order)value;
                    _ = typeof(Order);
                    _ = value is Order;
                    _ = default(Order);
                    _ = nameof(Order.Id);
                }
            }
            """));

        var order = collected.Type("Order");
        var typeUse = collected.To(order, "typeUse").ToArray();
        var compileTime = collected.To(order, "compileTimeName").ToArray();

        Assert.Equal(4, typeUse.Length);
        Assert.Single(compileTime);
        Assert.Equal(4, typeUse.Select(entry => entry.Evidence.PhysicalSpan!.Start).Distinct().Count());
    }

    [Fact]
    public void HandlesAliasesAndExtensionMethods()
    {
        var collected = Collect(
            (
                "Order.cs",
                "namespace Sample.Data;\n\npublic sealed class Order\n{\n}\n"),
            (
                "Extensions.cs",
                """
                namespace Sample.Tools;

                public static class OrderExtensions
                {
                    public static string Describe(this Sample.Data.Order order) => order.GetType().Name;
                }
                """),
            (
                "Service.cs",
                """
                using Alias = Sample.Data;
                using Sample.Tools;

                namespace Sample.Client;

                public sealed class Service
                {
                    public string Run()
                    {
                        var order = new Alias.Order();
                        return order.Describe();
                    }
                }
                """));

        var order = collected.Type("Order");
        var extensions = collected.Type("OrderExtensions");
        var service = collected.Type("Service");

        Assert.Single(collected.Evidence, entry =>
            entry.Evidence.Kind == "constructs"
            && entry.Evidence.SourceEntityId == service.Id
            && entry.Evidence.TargetEntityId == order.Id);

        // The call is recorded against the type that declares the extension method.
        Assert.Contains(collected.Evidence, entry =>
            entry.Evidence.Kind == "calls"
            && entry.Evidence.SourceEntityId == service.Id
            && entry.Evidence.TargetEntityId == extensions.Id);
    }

    [Fact]
    public void RecordsInterfaceCallsAgainstTheDeclaration()
    {
        var collected = Collect((
            "Contracts.cs",
            """
            namespace Sample;

            public interface IOrderStore
            {
                void Save(Order order);
            }

            public sealed class Order
            {
            }

            public sealed class OrderStore : IOrderStore
            {
                public void Save(Order order)
                {
                }
            }

            public sealed class Service
            {
                public void Run(IOrderStore store, Order order) => store.Save(order);
            }
            """));

        var contract = collected.Type("IOrderStore");
        var implementation = collected.Type("OrderStore");
        var service = collected.Type("Service");

        Assert.Contains(collected.Evidence, entry =>
            entry.Evidence.Kind == "calls"
            && entry.Evidence.SourceEntityId == service.Id
            && entry.Evidence.TargetEntityId == contract.Id);
        Assert.DoesNotContain(collected.Evidence, entry =>
            entry.Evidence.Kind == "calls"
            && entry.Evidence.SourceEntityId == service.Id
            && entry.Evidence.TargetEntityId == implementation.Id);
    }

    [Fact]
    public void DoesNotPromoteDynamicOrUnresolvedReferences()
    {
        var collected = CollectAllowingErrors(
            (
                "Order.cs",
                "namespace Sample;\n\npublic sealed class Order\n{\n}\n"),
            (
                "Service.cs",
                """
                namespace Sample;

                public sealed class Service
                {
                    public void Run(dynamic value)
                    {
                        value.Save();
                        MissingType other = null!;
                        other.ToString();
                    }
                }
                """));

        Assert.True(collected.Stats.DynamicReferences >= 1);
        Assert.True(collected.Stats.UnresolvedOperations >= 1);

        var order = collected.Type("Order");
        Assert.DoesNotContain(collected.Evidence, entry => entry.Evidence.TargetEntityId == order.Id);
    }

    [Fact]
    public void AttributesTopLevelStatementsToTheSynthesizedEntryPoint()
    {
        var collected = Collect(
            (
                "Order.cs",
                "namespace Sample;\n\npublic sealed class Order\n{\n    public void Save()\n    {\n    }\n}\n"),
            (
                "Program.cs",
                """
                using Sample;

                var order = new Order();
                order.Save();
                """));

        var program = collected.Index.Types.FirstOrDefault(type => type.Name == "Program");
        Assert.NotNull(program);

        var fromProgram = collected.From("Program").ToArray();
        Assert.Contains(fromProgram, entry => entry.Evidence.Kind == "constructs");
        Assert.Contains(fromProgram, entry => entry.Evidence.Kind == "calls");
    }

    [Fact]
    public void AttributesLambdasAndLocalFunctionsToTheEnclosingMember()
    {
        var collected = Collect((
            "Service.cs",
            """
            namespace Sample;

            public sealed class Order
            {
                public void Save()
                {
                }
            }

            public sealed class Service
            {
                public void Run(Order order)
                {
                    System.Action action = () => order.Save();
                    action();

                    void Local() => order.Save();
                    Local();
                }
            }
            """));

        var service = collected.Type("Service");
        var order = collected.Type("Order");
        var saves = collected.Evidence
            .Where(entry => entry.Evidence.Kind == "calls")
            .Where(entry => entry.Evidence.SourceEntityId == service.Id)
            .Where(entry => entry.Evidence.TargetEntityId == order.Id)
            .ToArray();

        // The call inside the lambda and the one inside the local function are both
        // attributed to the enclosing member.
        Assert.Equal(2, saves.Length);
        Assert.All(saves, entry => Assert.NotNull(entry.Evidence.SourceMemberId));
    }

    [Fact]
    public async Task CollectsTheGoldenFixtureUsage()
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
        var resolver = new SymbolResolver(load.DefiningVariantByAssembly);
        var collector = new OperationDependencyCollector(resolver, documents, index, "golden0000000000");
        var result = collector.Collect(inputs);

        var orderStore = index.Types.First(type => type.Name == "OrderStore");
        var sharedLog = index.Types.First(type => type.Name == "SharedLog");
        var domainOrder = index.Types.First(type => type.Name == "Order" && type.FullName == "Domain.Order");

        // OrderStore.Save calls SharedLog.Write and reads Order.Id.
        Assert.Contains(result.Evidence, entry =>
            entry.Evidence.Kind == "calls"
            && entry.Evidence.SourceEntityId == orderStore.Id
            && entry.Evidence.TargetEntityId == sharedLog.Id);
        Assert.Contains(result.Evidence, entry =>
            entry.Evidence.Kind == "memberAccess"
            && entry.Evidence.SourceEntityId == orderStore.Id
            && entry.Evidence.TargetEntityId == domainOrder.Id);

        Assert.True(result.Stats.BodiesScanned > 0);
        Assert.Equal(0, result.Stats.DynamicReferences);
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
            "sharpdeps-operations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _temporaryDirectories.Add(directory);
        return directory;
    }
}
