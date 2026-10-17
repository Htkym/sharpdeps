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
        OperationCollectionStats Stats,
        IReadOnlyList<CollectedEvidence>? HarnessEvidence = null)
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
        return new Collected(index, result.Evidence, result.Stats, result.HarnessEvidence);
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
                "namespace Sample;\n\npublic sealed class Order\n{\n    public static void Save(string value) { }\n    public static void Save(System.Uri value) { }\n}\n"),
            (
                "Service.cs",
                """
                namespace Sample;

                public sealed class Service
                {
                    public void Run(dynamic value)
                    {
                        value.Save();
                        Order.Save(null); // Ambiguous overload: candidates are not confirmed callees.
                        System.Action action = Order.Save; // No matching delegate signature either.
                        MissingType other = null!;
                        other.ToString();
                    }
                }
                """));

        Assert.True(collected.Stats.DynamicReferences >= 1);
        Assert.True(collected.Stats.UnresolvedOperations >= 1);

        var order = collected.Type("Order");
        Assert.DoesNotContain(collected.Evidence, entry => entry.Evidence.TargetEntityId == order.Id);
        Assert.DoesNotContain(collected.HarnessEvidence ?? collected.Evidence, entry => entry.Evidence.TargetEntityId == order.Id);

        // Isolate an invalid delegate signature so unrelated dynamic/errors cannot hide missing diagnostics.
        var invalidGroup = CollectAllowingErrors(("InvalidGroup.cs", """
            public sealed class Service
            {
                static void Target(string value) { }
                static void Target(System.Uri value) { }
                public void Run() { System.Action action = Target; }
            }
            """));
        Assert.True(invalidGroup.Stats.CandidateOnlySymbols > 0);
        var targets = invalidGroup.Index.Members.Where(member => member.Name == "Target").Select(member => member.Id).ToHashSet();
        Assert.DoesNotContain(invalidGroup.HarnessEvidence ?? invalidGroup.Evidence,
            entry => entry.CanonicalTargetMemberId is { } id && targets.Contains(id));
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
        var owner = Assert.Single(collected.Index.Members, member => member.TypeId == service.Id && member.Name == "Run");
        Assert.All(saves, entry =>
        {
            Assert.Equal(owner.Id, entry.Evidence.SourceMemberId);
            Assert.Equal(owner.Id, entry.CanonicalSourceMemberId);
            Assert.Equal("roslyn-operation", entry.Producer);
        });
        var localCall = Assert.Single(collected.From("Service", "calls"), entry => !entry.TargetIsExternal && entry.Evidence.TargetMemberId is not null
            && !collected.Index.Members.Any(member => member.Id == entry.Evidence.TargetMemberId));
        Assert.Equal(owner.Id, localCall.CanonicalSourceMemberId);
        Assert.Equal(localCall.Evidence.TargetMemberId, localCall.CanonicalTargetMemberId);
        Assert.NotEqual(owner.Id, localCall.CanonicalTargetMemberId);
    }

    [Fact]
    public void KeepsMethodGroupsAndEventHandlersAsHarnessReferencesWithoutGuessingDelegateCallees()
    {
        var collected = Collect(("References.cs", """
            namespace Sample;
            public sealed class Service
            {
                public event System.Action Changed;
                public void Run()
                {
                    System.Action action = Target;
                    Changed += Target;
                    Changed -= Target;
                    System.Action<string> external = System.Console.WriteLine;
                    void Local() { }
                    System.Action local = Local;
                    action();
                    local();
                }
                static void Target() { }
            }
            """));
        var harness = Assert.IsAssignableFrom<IReadOnlyList<CollectedEvidence>>(collected.HarnessEvidence);
        var references = harness.Except(collected.Evidence).ToArray();
        Assert.Equal(5, references.Length);
        Assert.Equal(collected.Evidence.Count + references.Length, harness.Count);
        Assert.All(collected.Evidence, entry => Assert.Contains(entry, harness));
        Assert.True(Assert.IsAssignableFrom<ICollection<CollectedEvidence>>(harness).IsReadOnly);
        var run = Assert.Single(collected.Index.Members, member => member.Name == "Run");
        var target = Assert.Single(collected.Index.Members, member => member.Name == "Target");
        Assert.Equal(3, references.Count(entry => entry.CanonicalTargetMemberId == target.Id));
        Assert.All(references, entry =>
        {
            Assert.Equal("memberAccess", entry.Evidence.Kind);
            Assert.Null(entry.Access);
            Assert.Equal(run.Id, entry.CanonicalSourceMemberId);
            Assert.Equal("roslyn-operation", entry.Producer);
        });
        var external = Assert.Single(references, entry => entry.TargetIsExternal);
        Assert.Equal("WriteLine", external.TargetSymbol!.Name);
        Assert.DoesNotContain(collected.Evidence, entry => entry.Evidence.TargetTypeId == external.Evidence.TargetTypeId);
        var local = Assert.Single(references, entry => !entry.TargetIsExternal && entry.CanonicalTargetMemberId != target.Id);
        Assert.Equal(local.Evidence.TargetMemberId, local.CanonicalTargetMemberId);
        Assert.NotEqual(run.Id, local.CanonicalTargetMemberId);
        // Delegate invocation binds to Invoke; assigning Target/Local does not prove its runtime callee.
        var calls = harness.Where(entry => entry.Evidence.Kind == "calls").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, entry => Assert.Equal("Invoke", entry.TargetSymbol!.Name));
    }

    [Fact]
    public void NormalizesAccessorAndPartialOwnersWithoutChangingLegacyEvidence()
    {
        var collected = Collect(
            ("Service.cs", """
                namespace Sample;
                public sealed class Order { public void Save() { } }
                public sealed partial class Service
                {
                    private readonly Order order = new();
                    public int Count { get { order.Save(); return 1; } set { order.Save(); } }
                    public event System.Action Changed { add { order.Save(); } remove { order.Save(); } }
                    partial void Work(Order order);
                    public void Run(Order order) => Work(order);
                }
                """),
            ("Service.Work.cs", """
                namespace Sample;
                public sealed partial class Service { partial void Work(Order order) { order.Save(); } }
                """));
        var members = collected.Index.Members.Where(member => member.TypeId == collected.Type("Service").Id).ToArray();
        var property = Assert.Single(members, member => member.Name == "Count");
        var @event = Assert.Single(members, member => member.Name == "Changed");
        var partial = Assert.Single(members, member => member.Name == "Work");
        var saves = collected.To(collected.Type("Order"), "calls").ToArray();
        Assert.Equal(5, saves.Length);
        Assert.Equal(2, saves.Count(entry => entry.CanonicalSourceMemberId == property.Id));
        Assert.Equal(2, saves.Count(entry => entry.CanonicalSourceMemberId == @event.Id));
        Assert.Single(saves, entry => entry.CanonicalSourceMemberId == partial.Id);
        Assert.All(saves.Where(entry => entry.CanonicalSourceMemberId != partial.Id), entry =>
            Assert.NotEqual(entry.Evidence.SourceMemberId, entry.CanonicalSourceMemberId));
        var call = Assert.Single(collected.From("Service", "calls"), entry => entry.CanonicalTargetMemberId == partial.Id);
        Assert.Equal(partial.Id, call.Evidence.TargetMemberId);
        Assert.Equal(2, partial.HarnessDeclarations!.Count);
    }

    [Fact]
    public void ClassifiesStorageAccessWithoutWritingReceiversOrReadingNameof()
    {
        var collected = Collect(("Access.cs", """
            namespace Sample;
            public sealed class Node
            {
                public int Read, Write, Compound, Increment, Ref, Out, In, NameOnly, TupleLeft, TupleRight;
                public int Property { get; set; }
                public object Optional;
                public Node Receiver => this;
                public event System.Action Changed;
            }
            public sealed class Service
            {
                public void Run(Node node)
                {
                    _ = node.Read;
                    node.Write = 1;
                    node.Compound += 1;
                    node.Increment++;
                    Ref(ref node.Ref);
                    Out(out node.Out);
                    In(in node.In);
                    node.Changed += Handler;
                    node.Changed -= Handler;
                    node.Property = 2;
                    node.Receiver.Write = 3;
                    _ = nameof(node.NameOnly);
                    (node.TupleLeft, node.TupleRight) = (1, 2);
                    node.Optional ??= new object();
                }
                static void Ref(ref int value) { }
                static void Out(out int value) { value = 0; }
                static void In(in int value) { }
                static void Handler() { }
            }
            """));
        var expected = new Dictionary<string, string?>
        {
            ["Read"] = "read", ["Write"] = "write", ["Compound"] = "readWrite",
            ["Increment"] = "readWrite", ["Ref"] = "readWrite", ["Out"] = "write", ["In"] = "read",
            ["Changed"] = "write", ["Property"] = "write", ["Receiver"] = "read", ["NameOnly"] = null,
            ["TupleLeft"] = "write", ["TupleRight"] = "write", ["Optional"] = "readWrite"
        };
        foreach (var (name, access) in expected)
        {
            var member = Assert.Single(collected.Index.Members, member => member.TypeId == collected.Type("Node").Id && member.Name == name);
            var entries = collected.From("Service", "memberAccess").Where(entry => entry.CanonicalTargetMemberId == member.Id).ToArray();
            Assert.Equal(name is "Write" or "Changed" ? 2 : 1, entries.Length);
            Assert.All(entries, entry =>
            {
                Assert.Equal(access, entry.Access);
                Assert.Equal(member.Id, entry.Evidence.TargetMemberId);
                Assert.Null(entry.TargetSymbol);
            });
        }
    }

    [Fact]
    public void RetainsExternalOverloadSignaturesInsteadOfUsingOpaqueLegacyIds()
    {
        var collected = Collect(("External.cs", """
            namespace Sample;
            public sealed class Service
            {
                public void Run() { System.Console.WriteLine(1); System.Console.WriteLine("text"); }
            }
            """));
        var calls = collected.From("Service", "calls").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, entry =>
        {
            Assert.True(entry.TargetIsExternal);
            var symbol = Assert.IsType<HarnessTargetSymbol>(entry.TargetSymbol);
            Assert.Equal(entry.Evidence.TargetTypeId, symbol.LegacyTypeId);
            Assert.Equal(entry.CanonicalTargetMemberId, symbol.LegacyMemberId);
            Assert.Equal("method", symbol.Kind);
            Assert.Equal("WriteLine", symbol.Name);
            Assert.Equal("doc:T:System.Console", symbol.TypeCanonicalSignature);
            Assert.Contains("Version=", symbol.AssemblyIdentity, StringComparison.Ordinal);
        });
        Assert.Equal(2, calls.Select(entry => entry.TargetSymbol!.CanonicalSignature).Distinct().Count());
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
        var domainVariant = load.Report.Variants.Single(variant =>
            variant.ProjectName.StartsWith("Domain", StringComparison.Ordinal) && variant.TargetFramework == "net10.0");
        var domainOrder = index.Types.Single(type => type.FullName == "Domain.Order"
            && type.ProjectVariantId == domainVariant.VariantKey);

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
