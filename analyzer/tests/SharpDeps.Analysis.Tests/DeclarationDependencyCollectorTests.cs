using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpDeps.Analysis.Core.Graph;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Contracts.Harness;
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
        IReadOnlyList<CollectedEvidence> Evidence,
        IReadOnlyList<CollectedEvidence> HarnessEvidence,
        IReadOnlyDictionary<string, string> ExternalTypes)
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
        => Collect(false, false, files);

    private Collected Collect(bool allowErrors, bool verifyRepeat, params (string Path, string Text)[] files)
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
        if (!allowErrors)
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

        var external = new ExternalTypeRegistry();
        var collector = new DeclarationDependencyCollector(resolver, documents, index, ProfileHash, external);
        var firstLegacy = verifyRepeat ? collector.Collect([input]) : null;
        var legacyExternal = verifyRepeat ? JsonSerializer.Serialize(external.Entries) : null;
        var result = collector.CollectWithHarness([input]);
        if (verifyRepeat)
        {
            Assert.Equal(JsonSerializer.Serialize(firstLegacy), JsonSerializer.Serialize(result.Evidence));
            Assert.Equal(legacyExternal, JsonSerializer.Serialize(external.Entries));
            var repeated = collector.CollectWithHarness([input]);
            Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(repeated));
        }
        return new(index, documents, result.Evidence, result.HarnessEvidence ?? result.Evidence, external.Entries);
    }

    [Fact]
    public void KeepsResolvedMemberDeclarationsInHarnessWithoutChangingLegacyEvidence()
    {
        const string text = """
            public interface IContract<T>
            {
                T Convert(T value);
                T Value { get; }
                T this[int index] { get; }
                event System.Action Changed;
            }
            public class Base<T>
            {
                public virtual T Convert(T value) => value;
                public virtual T Value => default!;
                public virtual T this[int index] => default!;
                public virtual event System.Action Changed { add { } remove { } }
                public virtual event System.Action Field, Other;
            }
            public class Derived : Base<string>, IContract<string>
            {
                public override string Convert(string value) => value;
                public override string Value => "saved";
                public override string this[int index] => "saved";
                public override event System.Action Changed { add { } remove { } }
                public override event System.Action Field, Other;
                string IContract<string>.Convert(string value) => value;
                string IContract<string>.Value => "saved";
                string IContract<string>.this[int index] => "saved";
                event System.Action IContract<string>.Changed { add { } remove { } }
            }
            """;
        var collected = Collect(false, true, ("Members.cs", text));
        var facts = collected.HarnessEvidence.Except(collected.Evidence).ToArray();
        Assert.Equal(6, facts.Count(e => e.Evidence.Kind == "overrides"));
        Assert.Equal(4, facts.Count(e => e.Evidence.Kind == "implements_explicitly"));
        Assert.DoesNotContain(collected.Evidence, e => e.Evidence.Kind is "overrides" or "implements_explicitly");
        var members = collected.Index.Members.ToDictionary(m => m.Id, StringComparer.Ordinal);
        Assert.All(facts, entry =>
        {
            var source = members[entry.CanonicalSourceMemberId!];
            var target = members[entry.CanonicalTargetMemberId!];
            Assert.Equal(collected.Type("Derived").Id, source.TypeId);
            Assert.Equal(collected.Type(entry.Evidence.Kind == "overrides" ? "Base" : "IContract").Id, target.TypeId);
            Assert.Equal(source.Id, entry.Evidence.SourceMemberId);
            Assert.Equal(target.Id, entry.Evidence.TargetMemberId);
            Assert.Equal("var_sample", entry.SourceVariantId);
            Assert.Equal("var_sample", entry.TargetVariantId);
            Assert.Equal("roslyn-declaration", entry.Producer);
            Assert.Equal("resolved", entry.Evidence.Confidence);
            Assert.Equal("userSource", entry.Evidence.Origin);
            Assert.False(entry.TargetIsExternal);
            Assert.Null(entry.HarnessDeclarationOwners);
            var span = Assert.IsType<SharpDeps.Analysis.Contracts.PhysicalSpan>(entry.Evidence.PhysicalSpan);
            var written = text.Substring(span.Start, span.Length);
            if (entry.Evidence.Kind == "overrides")
                Assert.Contains(written, new[] { "Convert", "Value", "this", "Changed", "Field", "Other" });
            else Assert.Equal("IContract<string>.", written);
            Assert.Equal(Assert.Single(collected.Index.Documents).ContentHash, entry.Evidence.SourceContentHash);
        });
        Assert.Equal(facts.Length, facts.Select(e => e.Evidence.Id).Distinct().Count());
    }

    [Fact]
    public void KeepsExternalMemberIdentityWithoutPromotingHidingImplicitOrUnboundDeclarations()
    {
        var collected = Collect(true, false, ("Targets.cs", """
            public interface IContract { void Work(); }
            public class Base { public virtual void Work() { } }
            public class Hiding : Base, IContract { public new void Work() { } }
            public class Outside : System.IDisposable
            {
                public override string ToString() => "saved";
                void System.IDisposable.Dispose() { }
            }
            public class Invalid : MissingBase
            {
                public override void Gone() { }
                void MissingContract.Gone() { }
            }
            """));
        var facts = collected.HarnessEvidence.Except(collected.Evidence).ToArray();
        Assert.Equal(2, facts.Length);
        Assert.All(facts, entry =>
        {
            Assert.Equal(collected.Type("Outside").Id, entry.Evidence.SourceTypeId);
            Assert.True(entry.TargetIsExternal);
            Assert.Null(entry.TargetVariantId);
            var target = Assert.IsType<HarnessTargetSymbol>(entry.TargetSymbol);
            Assert.Equal("method", target.Kind);
            Assert.Equal(entry.CanonicalTargetMemberId, target.LegacyMemberId);
            Assert.Contains("Version=", target.AssemblyIdentity, StringComparison.Ordinal);
            Assert.Contains("doc:T:System.", target.TypeCanonicalSignature, StringComparison.Ordinal);
        });
        var objectOverride = Assert.Single(facts, entry => entry.Evidence.Kind == "overrides");
        Assert.Equal("ToString", objectOverride.TargetSymbol!.Name);
        Assert.DoesNotContain(objectOverride.Evidence.TargetTypeId!, collected.ExternalTypes.Keys);
        Assert.Equal("Dispose", Assert.Single(facts, entry => entry.Evidence.Kind == "implements_explicitly").TargetSymbol!.Name);
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
        var external = collected.Evidence.Where(entry => entry.TargetIsExternal).ToArray();
        Assert.NotEmpty(external);
        Assert.All(external, entry =>
        {
            var symbol = Assert.IsType<HarnessTargetSymbol>(entry.TargetSymbol);
            Assert.Equal("type", symbol.Kind);
            Assert.Equal(entry.Evidence.TargetTypeId, symbol.LegacyTypeId);
            Assert.Null(symbol.LegacyMemberId);
            Assert.Equal(symbol.TypeCanonicalSignature, symbol.CanonicalSignature);
            Assert.Equal(symbol.TypeName, symbol.Name);
            Assert.Contains("Version=", symbol.AssemblyIdentity, StringComparison.Ordinal);
            Assert.Equal("roslyn-declaration", entry.Producer);
            Assert.Equal(method.Id, entry.CanonicalSourceMemberId);
        });
        Assert.All(references, entry => Assert.Null(entry.TargetSymbol));
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
    public void PreservesLiteralHttpGetFragmentsOnlyInHarnessAttributeCopies()
    {
        const string text = """
            using Microsoft.AspNetCore.Mvc;
            using Get = Microsoft.AspNetCore.Mvc.HttpGetAttribute;
            [Route("api/[controller]")]
            public class Endpoints
            {
                [Get("orders/{id:int}", Name = "declared", Order = 3)] public int Read() => 1;
                [HttpGet(template: "")] private int Empty() => 2;
                [NonAction, HttpGet(" /raw/ ")] public static int NotAnAction() => 3;
                [HttpGet("one"), HttpGet("two")] public int Multiple() => 4;
            }
            """;
        var collected = Collect(false, true, ("Endpoints.cs", text));
        Assert.All(collected.Evidence, entry => Assert.Null(entry.HttpGetDeclaration));
        var declarations = collected.HarnessEvidence.Where(entry => entry.HttpGetDeclaration is not null).ToArray();
        Assert.Equal(5, declarations.Length);
        foreach (var entry in declarations)
        {
            var original = Assert.Single(collected.Evidence, original => original.Evidence.Id == entry.Evidence.Id);
            Assert.Equal(original, entry with { HttpGetDeclaration = null });
            var declaration = Assert.IsType<HarnessHttpGetDeclaration>(entry.HttpGetDeclaration);
            Assert.Equal(HarnessHttpGetDeclarationState.Saved, declaration.State);
            Assert.Null(declaration.OmissionReason);
            Assert.Equal("attribute", entry.Evidence.Kind);
            Assert.Equal("roslyn-declaration", entry.Producer);
            Assert.Equal("resolved", entry.Evidence.Confidence);
            Assert.Equal("var_sample", entry.SourceVariantId);
            Assert.Null(entry.TargetVariantId);
            Assert.True(entry.TargetIsExternal);
            var target = Assert.IsType<HarnessTargetSymbol>(entry.TargetSymbol);
            Assert.Equal(typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute).Assembly.GetName().FullName, target.AssemblyIdentity);
            Assert.Equal("Microsoft.AspNetCore.Mvc.HttpGetAttribute", target.TypeName.Replace("global::", "", StringComparison.Ordinal));
            var name = entry.Evidence.PhysicalSpan!;
            Assert.Contains(text.Substring(name.Start, name.Length), new[] { "Get", "HttpGet" });
            var literal = declaration.TemplateSpan!.Value;
            Assert.True(literal.Start >= name.Start + name.Length);
            Assert.Equal("\"" + declaration.Template + "\"", text.Substring(literal.Start, literal.Length));
        }
        var byName = declarations.ToLookup(entry => collected.Index.Members.First(member => member.Id == entry.CanonicalSourceMemberId).Name);
        Assert.Equal("orders/{id:int}", Assert.Single(byName["Read"]).HttpGetDeclaration!.Template);
        Assert.Equal("", Assert.Single(byName["Empty"]).HttpGetDeclaration!.Template);
        Assert.Equal(" /raw/ ", Assert.Single(byName["NotAnAction"]).HttpGetDeclaration!.Template);
        Assert.Equal(new[] { "one", "two" }, byName["Multiple"].Select(entry => entry.HttpGetDeclaration!.Template).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OmitsUnsupportedHttpGetTemplatesAndLeavesLookalikesOutsideTheSlice()
    {
        var quotes = new string('"', HarnessHttpGetDeclaration.MaximumLiteralSpanUtf16 / 2);
        var text = """
            using Microsoft.AspNetCore.Mvc;
            public class Endpoints
            {
                const string Constant = "constant";
                [HttpGet] public void Unspecified() { }
                [HttpGet(null)] public void NullValue() { }
                [HttpGet(Constant)] public void NamedConstant() { }
                [HttpGet("one" + "two")] public void Concatenated() { }
                [HttpGet($"literal")] public void Interpolated() { }
                [HttpGet("line\nbreak")] public void Control() { }
                [HttpGet("\uD800")] public void InvalidUtf16() { }
                [HttpGet(__LARGE__)] public void LargeValue() { }
                [HttpGet(__RAW__)] public void LargeSyntax() { }
                [return: HttpGet("not-a-method-attribute")] public int ReturnTarget() => 1;
                [DerivedGet("custom")] public void Derived() { }
            }
            public sealed class DerivedGetAttribute : HttpGetAttribute
            {
                public DerivedGetAttribute(string template) : base(template) { }
            }
            """.Replace("__LARGE__", "\"" + new string('x', HarnessHttpGetDeclaration.MaximumTemplateUtf16 + 1) + "\"", StringComparison.Ordinal)
                .Replace("__RAW__", quotes + "short" + quotes, StringComparison.Ordinal);
        var collected = Collect(true, true, ("Unsupported.cs", text));
        var declarations = collected.HarnessEvidence.Where(entry => entry.HttpGetDeclaration is not null).ToArray();
        Assert.DoesNotContain(declarations, entry => entry.HttpGetDeclaration!.State == HarnessHttpGetDeclarationState.Saved);
        var byName = declarations.ToLookup(entry => collected.Index.Members.First(member => member.Id == entry.CanonicalSourceMemberId).Name);
        foreach (var name in new[] { "NullValue", "NamedConstant", "Concatenated", "Interpolated" })
            Assert.Equal("nonliteral-template", Assert.Single(byName[name]).HttpGetDeclaration!.OmissionReason);
        Assert.Equal("no-template", Assert.Single(byName["Unspecified"]).HttpGetDeclaration!.OmissionReason);
        foreach (var name in new[] { "Control", "InvalidUtf16" })
            Assert.Equal("semantic-unavailable", Assert.Single(byName[name]).HttpGetDeclaration!.OmissionReason);
        foreach (var name in new[] { "LargeValue", "LargeSyntax" })
            Assert.Equal("metadata-budget", Assert.Single(byName[name]).HttpGetDeclaration!.OmissionReason);
        Assert.Empty(byName["ReturnTarget"]);
        Assert.Empty(byName["Derived"]);
        Assert.All(declarations, entry =>
        {
            Assert.Null(entry.HttpGetDeclaration!.Template);
            Assert.Null(entry.HttpGetDeclaration.TemplateSpan);
        });

        var sameName = Collect(("Lookalike.cs", """
            public class Endpoints
            {
                [Microsoft.AspNetCore.Mvc.HttpGet("source")] public void Read() { }
            }
            namespace Microsoft.AspNetCore.Mvc
            {
                public sealed class HttpGetAttribute : System.Attribute
                {
                    public HttpGetAttribute(string template) { }
                }
            }
            """));
        Assert.DoesNotContain(sameName.HarnessEvidence, entry => entry.HttpGetDeclaration is not null);
        Assert.Contains(sameName.Evidence, entry => entry.Evidence.Kind == "attribute" && !entry.TargetIsExternal);
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

    [Fact]
    public void KeepsEveryFieldAndEventOwnerWithoutIncreasingLegacyEvidenceCounts()
    {
        var collected = Collect(("Multiple.cs", """
            public delegate void D();
            public class C
            {
                public D a, b;
                public event D Changed, Other;
            }
            """));
        var legacy = collected.For("C", "D").ToArray();
        Assert.Equal(2, legacy.Length); // One shared type token per declaration in report v2.
        var members = collected.Index.Members.ToDictionary(m => m.Id, m => m.Name);
        Assert.Equal(new[] { "a", "Changed" }, legacy.Select(e => members[e.Evidence.SourceMemberId!]).ToArray());
        Assert.Equal(new[] { "Changed", "Other", "a", "b" }, legacy
            .SelectMany(e => e.HarnessDeclarationOwners!)
            .Select(o => members[o.SourceMemberId!]).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        var relation = Assert.Single(AnalysisGraphBuilder.Build(legacy.Select(e => e.ToGraphEvidence()).ToArray(),
            GraphGranularity.Type).Relations);
        Assert.Equal(2, relation.EvidenceCount);
        Assert.Equal(2, relation.DistinctSourceMemberCount);
        Assert.Equal(2, relation.PublicSurfaceEvidenceCount);
        Assert.Equal(new[] { "signature" }, relation.Kinds);
        Assert.All(legacy, entry =>
        {
            Assert.Equal(2, entry.HarnessDeclarationOwners!.Count);
            Assert.All(entry.HarnessDeclarationOwners, owner => Assert.True(owner.PublicSurface));
            Assert.Equal(entry.Evidence.SourceMemberId, entry.ToGraphEvidence().SourceMemberId);
            var span = entry.Evidence.PhysicalSpan!;
            Assert.Equal(Identity.EvidenceId(entry.Evidence.RelationId, "signature", entry.Evidence.DocumentId,
                $"{span.StartLine}:{span.StartCharacter}:{span.Length}"), entry.Evidence.Id);
        });
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
