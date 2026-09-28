using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Roslyn;
using SharpDeps.Analysis.Roslyn.Symbols;
using Xunit;

namespace SharpDeps.Analysis.Tests;

/// <summary>
/// Symbol index behavior against the real fixture solution: cross-project identity,
/// multi-variant separation, external types, and stability across analyses.
/// </summary>
[Collection("semantic")]
public sealed class SymbolIndexIntegrationTests
{
    private static readonly string FixtureRoot = TestPaths.Fixture("semantic-baseline");

    static SymbolIndexIntegrationTests()
    {
        Assert.True(
            SemanticEnvironment.TryRegister(TestPaths.RepositoryRoot, out var reason),
            reason ?? "MSBuild registration failed.");
    }

    private sealed record IndexedFixture(SymbolIndex Index, SymbolResolver Resolver, SemanticLoadResult Load);

    private static async Task<IndexedFixture> BuildIndexAsync()
    {
        var target = Path.Combine(FixtureRoot, "SemanticBaseline.sln");
        var load = await SemanticLoader.LoadAsync(new SemanticLoadOptions(target, "Debug"));
        var rootId = Identity.WorkspaceRootId(Path.GetDirectoryName(target)!);

        var inputs = load.Report.Variants
            .Where(variant => variant.LoadState == "loaded" && load.Compilations.ContainsKey(variant.VariantKey))
            .Select(variant => new SymbolIndexInput(
                variant.VariantKey,
                variant.ProjectName,
                load.Compilations[variant.VariantKey]))
            .ToArray();

        var index = SymbolIndexBuilder.Build(rootId, Path.GetDirectoryName(target)!, inputs);
        return new IndexedFixture(index, new SymbolResolver(load.DefiningVariantByAssembly), load);
    }

    private static string VariantKeyOf(IndexedFixture fixture, string projectName)
        => fixture.Load.Report.Variants
            .First(variant => variant.ProjectName == projectName || variant.ProjectName.StartsWith(projectName + "(", StringComparison.Ordinal))
            .VariantKey;

    [Fact]
    public async Task IndexesTheFixtureWithFlagsAndKinds()
    {
        var fixture = await BuildIndexAsync();
        var index = fixture.Index;

        Assert.NotEmpty(index.Types);
        Assert.NotEmpty(index.Members);
        Assert.NotEmpty(index.Documents);

        // Domain is multi-targeted, so every Domain type appears once per variant.
        var partialThing = index.Types.Where(type => type.Name == "PartialThing").ToArray();
        Assert.NotEmpty(partialThing);
        Assert.All(partialThing, type =>
        {
            Assert.True(type.IsPartial);
            Assert.Equal(2, type.Declarations.Count);
        });

        var inner = index.Types.Where(type => type.Name == "Inner").ToArray();
        Assert.NotEmpty(inner);
        Assert.All(inner, type => Assert.True(type.IsNested));

        var generic = index.Types.Where(type => type.Name == "GenericThing").ToArray();
        Assert.NotEmpty(generic);
        Assert.All(generic, type =>
        {
            Assert.True(type.IsGeneric);
            Assert.Equal(1, type.Arity);
        });

        var fileLocal = index.Types.Where(type => type.Name == "FileLocalThing").ToArray();
        Assert.NotEmpty(fileLocal);
        Assert.All(fileLocal, type =>
        {
            Assert.True(type.IsFileLocal);
            Assert.Equal("file", type.Accessibility);
        });

        Assert.Contains(index.Types, type => type.Kind == "enum");
        Assert.Contains(index.Types, type => type.Kind == "delegate");
        Assert.Contains(index.Types, type => type.Kind == "record");
        Assert.False(index.Summary.HasTopLevelStatements);
    }

    [Fact]
    public async Task KeepsSameNamedTypesApartAcrossProjects()
    {
        var fixture = await BuildIndexAsync();
        var info = fixture.Index.Types.Where(type => type.Name == "Info").ToArray();

        // Domain.Info exists once per Domain variant; Application.Info is a different project.
        Assert.True(
            info.Length >= 3,
            string.Join(", ", info.Select(type => $"{type.FullName}@{type.ProjectVariantId}")));
        Assert.Equal(3, info.Select(type => type.Id).Distinct().Count());
        Assert.Equal(3, info.Select(type => type.ProjectVariantId).Distinct().Count());
        Assert.Single(info, type => type.FullName == "Application.Info");
        Assert.All(
            info.Where(type => type.FullName == "Domain.Info"),
            type => Assert.Equal("Domain.Info", type.FullName));
    }

    [Fact]
    public async Task SeparatesMultiTargetedVariants()
    {
        var fixture = await BuildIndexAsync();
        var domainTypes = fixture.Index.Types
            .Where(type => type.Name == "Order")
            .ToArray();

        Assert.Equal(2, domainTypes.Length);
        Assert.NotEqual(domainTypes[0].ProjectVariantId, domainTypes[1].ProjectVariantId);
        Assert.Equal(2, domainTypes.Select(type => type.Id).Distinct().Count());
    }

    [Fact]
    public async Task ResolvesReferencedTypesToTheirDefiningVariant()
    {
        var fixture = await BuildIndexAsync();

        var testProjectVariant = VariantKeyOf(fixture, "Infrastructure.Tests");
        var domainVariant = VariantKeyOf(fixture, "Domain(net10.0)");
        var domainOrder = Assert.Single(fixture.Index.Types, type =>
            type.Name == "Order" && type.ProjectVariantId == domainVariant);

        var resolver = fixture.Resolver;
        var testProject = fixture.Load.Report.Variants.Single(variant => variant.VariantKey == testProjectVariant);
        var compilation = fixture.Load.Compilations[testProjectVariant];
        var orderSymbol = compilation.GetTypeByMetadataName("Domain.Order");

        Assert.NotNull(orderSymbol);
        Assert.False(resolver.IsExternal(orderSymbol, testProjectVariant));
        Assert.Equal(domainOrder.Id, resolver.ResolveTypeId(orderSymbol, testProjectVariant));

        // Members resolve into the same defining variant.
        var idMember = orderSymbol!.GetMembers("Id").OfType<Microsoft.CodeAnalysis.IPropertySymbol>().Single();
        var resolvedMemberId = resolver.ResolveMemberId(idMember, testProjectVariant);
        Assert.NotNull(resolvedMemberId);
        Assert.Contains(
            fixture.Index.Members,
            member => member.Id == resolvedMemberId && member.TypeId == domainOrder.Id);

        // The test project only sees Domain through a transitive reference; the
        // resolution must still work (SD-007 adds it to the compilation).
        Assert.True(testProject.AddedTransitiveReferences >= 1);
    }

    [Fact]
    public async Task DistinguishesExternalTypesByAssemblyIdentity()
    {
        var fixture = await BuildIndexAsync();
        var variant = VariantKeyOf(fixture, "Infrastructure");
        var compilation = fixture.Load.Compilations[variant];
        var stringSymbol = compilation.GetSpecialType(Microsoft.CodeAnalysis.SpecialType.System_String);
        var taskSymbol = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");

        var resolver = fixture.Resolver;

        Assert.True(resolver.IsExternal(stringSymbol, variant));
        var stringId = resolver.ResolveTypeId(stringSymbol, variant);
        Assert.NotNull(stringId);
        Assert.DoesNotContain(fixture.Index.Types, type => type.Id == stringId);

        Assert.NotNull(taskSymbol);
        var taskId = resolver.ResolveTypeId(taskSymbol, variant);
        Assert.NotNull(taskId);
        Assert.NotEqual(stringId, taskId);

        // Unresolvable symbols must not be promoted to a confirmed type.
        Assert.Null(resolver.ResolveTypeId(null, variant));
    }

    [Fact]
    public async Task KeepsIdsStableAcrossAnalyses()
    {
        var first = await BuildIndexAsync();
        var second = await BuildIndexAsync();

        Assert.Equal(
            first.Index.Types.Select(type => type.Id).OrderBy(id => id, StringComparer.Ordinal),
            second.Index.Types.Select(type => type.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(
            first.Index.Members.Select(member => member.Id).OrderBy(id => id, StringComparer.Ordinal),
            second.Index.Members.Select(member => member.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(
            first.Index.Documents.Select(document => document.Id).OrderBy(id => id, StringComparer.Ordinal),
            second.Index.Documents.Select(document => document.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnswersPositionLookupsFromTheIndex()
    {
        var fixture = await BuildIndexAsync();
        var inner = fixture.Index.Types.First(type => type.Name == "Inner");
        var declaration = Assert.Single(inner.Declarations);

        // The same file is indexed once per variant, so every variant's type is a
        // candidate and the innermost of each variant is returned.
        var found = fixture.Index.PositionIndex.FindTypesAt(declaration.DocumentId, declaration.Start + 2);
        Assert.Contains(found, span => span.TypeId == inner.Id);
        Assert.All(found, span => Assert.Equal("Inner", span.FullName.Split('.').Last().Split('+').Last()));

        var outer = fixture.Index.Types.First(type => type.Name == "Shapes");
        var outerFound = fixture.Index.PositionIndex.FindTypesAt(outer.Declarations[0].DocumentId, outer.Declarations[0].Start + 2);
        Assert.Contains(outerFound, span => span.TypeId == outer.Id);

        Assert.Null(fixture.Index.PositionIndex.FindTypeIdAt(declaration.DocumentId, declaration.Start + declaration.Length + 100_000));
    }
}
