using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpDeps.Analysis.Core.Graph;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Roslyn;
using SharpDeps.Analysis.Roslyn.Evidence;
using SharpDeps.Analysis.Roslyn.Symbols;
using Xunit;

namespace SharpDeps.Analysis.Tests;

[Collection("semantic")]
public sealed class AnalysisGraphTests : IDisposable
{
    private readonly List<string> _temporaryDirectories = [];

    static AnalysisGraphTests()
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

    private static GraphEvidence Evidence(
        string source,
        string target,
        string kind = "calls",
        string basis = "symbolResolved",
        string? sourceNamespace = null,
        string? targetNamespace = null,
        string? sourceProject = null,
        string? targetProject = null,
        string? sourceMember = null,
        string document = "doc_0000000000000001",
        bool publicSurface = false,
        string confidence = "resolved")
        => new(
            basis,
            source,
            target,
            kind,
            sourceMember,
            document,
            publicSurface,
            false,
            confidence,
            sourceNamespace,
            targetNamespace,
            sourceProject,
            targetProject,
            false);

    [Fact]
    public void AggregatesEvidenceIntoRelationsWithSeparateCounts()
    {
        var graph = AnalysisGraphBuilder.Build(
        [
            Evidence("ty_a", "ty_b", "calls", sourceMember: "mb_1", publicSurface: true),
            Evidence("ty_a", "ty_b", "memberAccess", sourceMember: "mb_2"),
            Evidence("ty_a", "ty_b", "calls", sourceMember: "mb_1"),
            Evidence("ty_a", "ty_c", "constructs", confidence: "inferred")
        ],
        GraphGranularity.Type);

        Assert.Equal(2, graph.RelationCount);
        Assert.Equal(4, graph.OccurrenceCount);

        var relation = graph.Relations.Single(entry => entry.TargetEntityId == "ty_b");
        Assert.Equal(3, relation.EvidenceCount);
        Assert.Equal(["calls", "memberAccess"], relation.Kinds);
        Assert.Equal(2, relation.DistinctSourceMemberCount);
        Assert.Equal(1, relation.DistinctSourceDocumentCount);
        Assert.Equal(1, relation.PublicSurfaceEvidenceCount);
        Assert.Equal("resolved", relation.Confidence);

        Assert.Equal("inferred", graph.Relations.Single(entry => entry.TargetEntityId == "ty_c").Confidence);
    }

    [Fact]
    public void DropsRelationsThatStayInsideOneParent()
    {
        var evidence = new[]
        {
            // Same namespace: no namespace-level edge, but different projects.
            Evidence("ty_a", "ty_b", sourceNamespace: "ns_one", targetNamespace: "ns_one", sourceProject: "prj_1", targetProject: "prj_2"),
            // Different namespace, same project: a namespace edge, no project edge.
            Evidence("ty_a", "ty_c", sourceNamespace: "ns_one", targetNamespace: "ns_two", sourceProject: "prj_1", targetProject: "prj_1")
        };

        var namespaceOfType = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ty_a"] = "ns_one",
            ["ty_b"] = "ns_one",
            ["ty_c"] = "ns_two"
        };
        var projectOfType = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ty_a"] = "prj_1",
            ["ty_b"] = "prj_2",
            ["ty_c"] = "prj_1"
        };
        var namespaces = AnalysisGraphBuilder.Build(evidence, GraphGranularity.Namespace, id => namespaceOfType.GetValueOrDefault(id));
        var projects = AnalysisGraphBuilder.Build(evidence, GraphGranularity.Project, id => projectOfType.GetValueOrDefault(id));

        // The relation that stays inside ns_one is not a namespace edge; the other one is.
        var namespaceRelation = Assert.Single(namespaces.Relations);
        Assert.Equal("ns_one", namespaceRelation.SourceEntityId);
        Assert.Equal("ns_two", namespaceRelation.TargetEntityId);

        // At project level the first relation crosses prj_1 → prj_2, the second is
        // inside prj_1 and therefore not a project edge.
        var projectRelation = Assert.Single(projects.Relations);
        Assert.Equal("prj_1", projectRelation.SourceEntityId);
        Assert.Equal("prj_2", projectRelation.TargetEntityId);
        Assert.DoesNotContain(projects.Relations, relation => relation.SourceEntityId == relation.TargetEntityId);
    }

    [Fact]
    public void KeepsSelfReferencesButNotAsCycles()
    {
        var graph = AnalysisGraphBuilder.Build(
        [
            Evidence("ty_self", "ty_self"),
            Evidence("ty_a", "ty_b"),
            Evidence("ty_b", "ty_a")
        ],
        GraphGranularity.Type);

        Assert.Equal(3, graph.RelationCount);

        var cycles = GraphCycles.Find(graph);
        var cycle = Assert.Single(cycles);
        Assert.DoesNotContain("ty_self", cycle.MemberIds);
        Assert.Equal(["ty_a", "ty_b"], cycle.MemberIds);
    }

    [Fact]
    public void ProvidesAWitnessWhoseEdgesAllExist()
    {
        var graph = AnalysisGraphBuilder.Build(
        [
            Evidence("ty_a", "ty_b"),
            Evidence("ty_b", "ty_c"),
            Evidence("ty_c", "ty_a"),
            Evidence("ty_tail", "ty_a")
        ],
        GraphGranularity.Type);

        var cycle = Assert.Single(GraphCycles.Find(graph));
        Assert.Equal(["ty_a", "ty_b", "ty_c"], cycle.MemberIds);
        Assert.True(cycle.HasWitness);

        var relationKeys = graph.Relations.Select(relation => relation.Key).ToHashSet(StringComparer.Ordinal);
        Assert.All(cycle.WitnessEdges, edge => Assert.Contains(edge.Key, relationKeys));

        // The witness is a closed path: consecutive edges connect.
        Assert.Equal(3, cycle.WitnessEdges.Count);
        for (var index = 0; index < cycle.WitnessEdges.Count; index++)
        {
            var current = cycle.WitnessEdges[index];
            var next = cycle.WitnessEdges[(index + 1) % cycle.WitnessEdges.Count];
            Assert.Equal(current.TargetEntityId, next.SourceEntityId);
        }
    }

    [Fact]
    public void DoesNotMixBasesWhenComputingComponents()
    {
        var graph = AnalysisGraphBuilder.Build(
        [
            Evidence("ty_a", "ty_b", basis: "symbolResolved"),
            Evidence("ty_b", "ty_a", basis: "usingInferred"),
            Evidence("ty_c", "ty_d", basis: "symbolResolved"),
            Evidence("ty_d", "ty_c", basis: "symbolResolved")
        ],
        GraphGranularity.Type);

        var cycles = GraphCycles.Find(graph);

        var cycle = Assert.Single(cycles);
        Assert.Equal("symbolResolved", cycle.Basis);
        Assert.Equal(["ty_c", "ty_d"], cycle.MemberIds);
    }

    [Fact]
    public void ExplorationTerminatesOnCyclicGraphs()
    {
        var graph = AnalysisGraphBuilder.Build(
        [
            Evidence("ty_a", "ty_b"),
            Evidence("ty_b", "ty_c"),
            Evidence("ty_c", "ty_a")
        ],
        GraphGranularity.Type);
        var index = GraphIndex.Build(graph);

        var dependencies = index.Explore("ty_a", depth: 3);
        var dependents = index.Explore("ty_a", depth: 3, dependents: true);

        // Depth 3 from ty_a reaches both other members, once each.
        Assert.Equal(["ty_b", "ty_c"], dependencies.Nodes.Select(node => node.EntityId));
        Assert.Equal(["ty_b", "ty_c"], dependents.Nodes.Select(node => node.EntityId));
        Assert.DoesNotContain(dependencies.Relations, relation => relation.SourceEntityId == relation.TargetEntityId);
        Assert.False(dependencies.Truncated);
    }

    [Fact]
    public void ExplorationReportsTruncation()
    {
        var evidence = Enumerable.Range(0, 40)
            .Select(index => Evidence("ty_root", $"ty_{index:D2}"))
            .ToArray();
        var index = GraphIndex.Build(AnalysisGraphBuilder.Build(evidence, GraphGranularity.Type));

        var exploration = index.Explore("ty_root", depth: 1, maxNodes: 10);

        Assert.True(exploration.Truncated);
        Assert.True(exploration.CutoffCount > 0);
        Assert.True(exploration.Nodes.Count <= 10);
    }

    [Fact]
    public void ProjectionKeepsPriorityIdsAndReportsOmissions()
    {
        var graph = AnalysisGraphBuilder.Build(
        [
            Evidence("ty_a", "ty_b"),
            Evidence("ty_b", "ty_c"),
            Evidence("ty_c", "ty_d"),
            Evidence("ty_d", "ty_a")
        ],
        GraphGranularity.Type);
        var index = GraphIndex.Build(graph);

        var projection = index.Project(new GraphBudget(MaxNodes: 2, MaxEdges: 1), ["ty_d"]);

        Assert.Contains("ty_d", projection.NodeIds);
        Assert.Single(projection.Relations);
        Assert.Equal(4, projection.TotalNodeCount);
        Assert.Equal(4, projection.TotalEdgeCount);
        Assert.True(projection.Truncated);
        Assert.Equal(2, projection.OmittedNodeCount);
        Assert.Equal(3, projection.OmittedEdgeCount);

        // The projection is a view: the analysis (and its cycles) did not change.
        Assert.Single(GraphCycles.Find(graph));
    }

    [Fact]
    public async Task BuildsACycleFromTheRealCollectors()
    {
        var collected = await CollectMutualCallsAsync();

        var evidence = collected.Evidence
            .Select(entry => entry.ToGraphEvidence())
            .ToArray();
        var graph = AnalysisGraphBuilder.Build(evidence, GraphGranularity.Type);
        var cycle = Assert.Single(GraphCycles.Find(graph));

        Assert.Equal(2, cycle.MemberIds.Count);
        Assert.True(cycle.HasWitness);
        Assert.All(cycle.WitnessEdges, edge => Assert.Equal("calls", Assert.Single(edge.Kinds)));

        var index = GraphIndex.Build(graph);
        // The origin is never re-reported, so a two-node cycle shows one neighbour.
        Assert.Single(index.Explore(cycle.MemberIds[0], depth: 2).Nodes);
        Assert.True(index.Explore(cycle.MemberIds[0], depth: 2).Nodes[0].EntityId != cycle.MemberIds[0]);
    }

    [Fact]
    public async Task AggregatesTheFixtureToNamespaceAndProjectGraphs()
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
        var declarations = new DeclarationDependencyCollector(resolver, documents, index, "graph00000000000");
        var operations = new OperationDependencyCollector(resolver, documents, index, "graph00000000000");
        var evidence = declarations.Collect(inputs)
            .Concat(operations.Collect(inputs).Evidence)
            .Select(entry => entry.ToGraphEvidence())
            .ToArray();

        var namespaceByType = index.Types.ToDictionary(type => type.Id, type => type.NamespaceId, StringComparer.Ordinal);
        var projectByType = index.Types.ToDictionary(type => type.Id, type => type.ProjectVariantId, StringComparer.Ordinal);

        var namespaces = AnalysisGraphBuilder.Build(evidence, GraphGranularity.Namespace, id => namespaceByType.GetValueOrDefault(id));
        var projects = AnalysisGraphBuilder.Build(evidence, GraphGranularity.Project, id => projectByType.GetValueOrDefault(id));

        // Infrastructure implements/uses Domain types, so the namespaces are connected.
        var orderStore = index.Types.First(type => type.Name == "OrderStore");
        var domainOrder = index.Types.First(type => type.Name == "Order" && type.FullName == "Domain.Order");
        var infrastructureNamespace = namespaceByType[orderStore.Id];
        var domainNamespace = namespaceByType[domainOrder.Id];

        Assert.Contains(namespaces.Relations, relation =>
            relation.SourceEntityId == infrastructureNamespace && relation.TargetEntityId == domainNamespace);
        Assert.DoesNotContain(namespaces.Relations, relation => relation.SourceEntityId == relation.TargetEntityId);
        Assert.DoesNotContain(projects.Relations, relation => relation.SourceEntityId == relation.TargetEntityId);

        // The project graph keeps the actual project-to-project dependencies.
        Assert.Contains(projects.Relations, relation =>
            relation.SourceEntityId == projectByType[orderStore.Id] && relation.TargetEntityId == projectByType[domainOrder.Id]);

        // Occurrence counts stay distinguishable from relation counts.
        Assert.True(namespaces.OccurrenceCount >= namespaces.RelationCount);

        // The fixture has no dependency cycle.
        Assert.Empty(GraphCycles.Find(projects));
    }

    private async Task<Collected> CollectMutualCallsAsync()
    {
        var root = CreateTemporaryDirectory();
        const string text = """
            namespace Sample;

            public sealed class First
            {
                public void Run(Second second) => second.RunBack(this);
            }

            public sealed class Second
            {
                public void RunBack(First first) => first.Run(this);
            }
            """;
        var path = Path.Combine(root, "Sample.cs");
        await File.WriteAllTextAsync(path, text);

        var tree = CSharpSyntaxTree.ParseText(text, path: path);
        var compilation = CSharpCompilation.Create(
            "Sample",
            [tree],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var documents = new SourceDocumentRegistry(Identity.WorkspaceRootId(root), root);
        var input = new SymbolIndexInput("var_sample", "Sample", compilation);
        var index = SymbolIndexBuilder.Build(documents, [input]);
        var resolver = new SymbolResolver(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["var_sample"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Sample"] = "var_sample" }
        });

        var collector = new OperationDependencyCollector(resolver, documents, index, "graph00000000000");
        return new Collected(collector.Collect([input]).Evidence);
    }

    private sealed record Collected(IReadOnlyList<CollectedEvidence> Evidence);

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
        var directory = Path.Combine(Path.GetTempPath(), "sharpdeps-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _temporaryDirectories.Add(directory);
        return directory;
    }
}
