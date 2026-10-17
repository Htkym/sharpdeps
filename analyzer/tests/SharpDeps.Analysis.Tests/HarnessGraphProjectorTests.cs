using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Roslyn;
using SharpDeps.Analysis.Roslyn.Evidence;
using SharpDeps.Analysis.Roslyn.Symbols;
using Xunit;

namespace SharpDeps.Analysis.Tests;

public sealed class HarnessGraphProjectorTests : IDisposable
{
    private static readonly Guid Workspace = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sharpdeps-harness-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ProjectsActualMemberEvidenceWithSeparateVariantsAndStableLogicalIds()
    {
        var sample = Collect("""
            namespace Sample;
            public partial class Service
            {
                public int Value { get; set; }
                public int Run() { Value = 1; Value += 2; return Value; }
            }
            """, twoVariants: true);
        var graph = Project(sample);
        var value = Assert.Single(graph.Nodes, n => n.Kind == HarnessNodeKind.Member && n.Name == "Value");
        var run = Assert.Single(graph.Nodes, n => n.Kind == HarnessNodeKind.Member && n.Name == "Run");
        Assert.NotEmpty(value.Signature!);
        Assert.Equal(2, graph.SymbolOccurrences.Count(o => o.LogicalSymbolId == value.Id));
        Assert.Equal(2, graph.Variants.Count);
        var accesses = graph.Edges.Where(e => e.SourceNodeId == run.Id && e.TargetNodeId == value.Id).ToArray();
        Assert.Equal(4, accesses.Count(e => e.Kind == "writes"));
        Assert.Equal(4, accesses.Count(e => e.Kind == "reads"));
        Assert.All(accesses, e =>
        {
            Assert.Equal(HarnessCertainty.Resolved, e.Certainty);
            Assert.Equal("roslyn-operation", e.Producer);
            Assert.Equal("userSource", e.Origin);
            Assert.NotNull(e.Evidence?.RawSpan);
            Assert.NotEmpty(e.Evidence!.ContentHash!);
            var source = Assert.Single(graph.SymbolOccurrences, o => o.Id == e.SourceOccurrenceId);
            var target = Assert.Single(graph.SymbolOccurrences, o => o.Id == e.TargetOccurrenceId);
            Assert.Equal(e.VariantId, source.VariantId);
            Assert.Equal(e.VariantId, target.VariantId);
        });
        Assert.All(graph.SymbolOccurrences.Where(o => o.LogicalSymbolId == run.Id), o => Assert.NotEmpty(o.Declarations!));
        Assert.Contains(graph.LegacyReferences, r => r.NodeId == value.Id && r.LegacyId.StartsWith("mb_", StringComparison.Ordinal));
        var json = JsonSerializer.Serialize(graph, HarnessGraphJsonContext.Default.HarnessGraphEnvelope);
        var restored = HarnessGraphContract.Read(json);
        Assert.Equal(graph.Edges.Count, restored.Edges.Count);
        Assert.Equal(value.Signature, Assert.Single(restored.Nodes, n => n.Id == value.Id).Signature);
        var again = Project(sample, generation: 2);
        Assert.Equal(graph.Nodes.Select(n => n.Id), again.Nodes.Select(n => n.Id));
        Assert.Equal(graph.Edges.Select(e => e.Id), again.Edges.Select(e => e.Id));
    }

    [Fact]
    public void PreservesExternalAssemblyIdentityAndReportsUnknownWithoutConfirmedEdges()
    {
        var sample = Collect("""
            public class Service
            {
                public void Run(dynamic value) { System.Console.WriteLine("saved"); value.Missing(); }
            }
            """);
        var graph = Project(sample);
        Assert.Equal(HarnessCoverage.Partial, graph.Coverage);
        Assert.Contains(graph.Diagnostics!, d => d.Code == "roslyn.dynamicReferences" && d.Count > 0);
        var call = Assert.Single(graph.Edges, e => e.Kind == "calls");
        var external = Assert.Single(graph.Nodes, n => n.Id == call.TargetNodeId);
        Assert.Equal(HarnessNodeKind.ExternalSymbol, external.Kind);
        Assert.Contains("WriteLine", external.Signature!);
        Assert.DoesNotContain(graph.Nodes, n => n.Name == "Missing");
        Assert.NotEqual(HarnessIdentity.ExternalSymbolId(Workspace, "A, Version=1.0.0.0", "method", "M:X.Run"),
            HarnessIdentity.ExternalSymbolId(Workspace, "B, Version=1.0.0.0", "method", "M:X.Run"));

        var confirmed = Assert.Single(sample.Evidence, e => e.Evidence.Kind == "calls");
        var candidate = confirmed with { Evidence = confirmed.Evidence with { Confidence = "candidate" } };
        var ambiguous = Project(sample with { Evidence = sample.Evidence.Concat([candidate]).ToArray() });
        Assert.Equal(HarnessCertainty.Candidate, Assert.Single(ambiguous.Edges, e => e.Kind == "calls").Certainty);
        Assert.Equal(HarnessCoverage.Partial, ambiguous.Coverage);
        var local = Collect("public class Service { public void Run() { void Local() { } Local(); } }");
        var localGraph = Project(local);
        Assert.DoesNotContain(localGraph.Edges, e => e.Kind == "calls");
        Assert.Contains(localGraph.Diagnostics!, d => d.Code == "roslyn.missingTarget" && d.Count == 1);
        Assert.Equal(HarnessCoverage.Partial, localGraph.Coverage);
        Assert.Throws<ArgumentException>(() => HarnessGraphProjector.Project(Workspace, "snapshot", 1,
            HarnessCoverage.Partial, [], sample.Index, sample.Evidence, sample.Stats));
        Assert.Throws<OperationCanceledException>(() => HarnessGraphProjector.Project(Workspace, "snapshot", 1,
            HarnessCoverage.Partial, [], sample.Index with { Namespaces = [], Types = [], Members = [] }, [],
            new(0, 0, 0, 0), new CancellationToken(canceled: true)));
    }

    [Fact]
    public void ProjectsEachOwnerOfSharedFieldAndEventDeclarationTokens()
    {
        var sample = Collect("""
            public delegate void D();
            public class C
            {
                [System.Obsolete] public D a, b;
                [System.Obsolete] public event D Changed, Other;
            }
            """, includeDeclarations: true);
        var graph = Project(sample);
        Assert.Equal(HarnessCoverage.CompleteWithinScope, graph.Coverage);
        var target = Assert.Single(graph.Nodes, n => n.Kind == HarnessNodeKind.Type && n.Name == "D");
        foreach (var name in new[] { "a", "b", "Changed", "Other" })
        {
            var owner = Assert.Single(graph.Nodes, n => n.Kind == HarnessNodeKind.Member && n.Name == name);
            var signature = Assert.Single(graph.Edges, e => e.SourceNodeId == owner.Id && e.Kind == "signature");
            Assert.Equal(target.Id, signature.TargetNodeId);
            Assert.Equal("roslyn-declaration", signature.Producer);
            Assert.Equal(HarnessCertainty.Resolved, signature.Certainty);
            Assert.NotNull(signature.Evidence?.RawSpan);
            var attribute = Assert.Single(graph.Edges, e => e.SourceNodeId == owner.Id && e.Kind == "attribute");
            Assert.Contains("ObsoleteAttribute", graph.Nodes.Single(n => n.Id == attribute.TargetNodeId).Signature!);
        }
        Assert.Equal(4, graph.Edges.Count(e => e.Kind == "signature" && e.TargetNodeId == target.Id));
    }

    [Fact]
    public void ProjectsMethodGroupsAsReferencesSeparatelyFromCallsAndEventWrites()
    {
        var sample = Collect("""
            public class Service
            {
                public event System.Action Changed;
                static void Target() { }
                void Handler() { }
                public void Run()
                {
                    System.Action a = Target;
                    System.Action b = new System.Action(Target);
                    Changed += Handler;
                    System.Action<string> outside = System.Console.WriteLine;
                    a();
                    Target();
                }
            }
            """);
        var graph = Project(sample);
        Assert.Equal(HarnessCoverage.CompleteWithinScope, graph.Coverage);
        var run = Assert.Single(graph.Nodes, n => n.Kind == HarnessNodeKind.Member && n.Name == "Run");
        var target = Assert.Single(graph.Nodes, n => n.Kind == HarnessNodeKind.Member && n.Name == "Target");
        var handler = Assert.Single(graph.Nodes, n => n.Kind == HarnessNodeKind.Member && n.Name == "Handler");
        var references = graph.Edges.Where(e => e.SourceNodeId == run.Id && e.Kind == "references_member").ToArray();
        Assert.Equal(4, references.Length);
        Assert.Equal(2, references.Count(e => e.TargetNodeId == target.Id));
        Assert.Single(references, e => e.TargetNodeId == handler.Id);
        var external = Assert.Single(references, e => graph.Nodes.Single(n => n.Id == e.TargetNodeId).Kind == HarnessNodeKind.ExternalSymbol);
        Assert.Contains("WriteLine", graph.Nodes.Single(n => n.Id == external.TargetNodeId).Signature!);
        Assert.All(references, edge =>
        {
            Assert.Equal("roslyn-operation", edge.Producer);
            Assert.Equal(HarnessCertainty.Resolved, edge.Certainty);
            Assert.NotNull(edge.Evidence?.RawSpan);
            Assert.NotEmpty(edge.Evidence!.ContentHash!);
        });
        Assert.Single(graph.Edges, e => e.SourceNodeId == run.Id && e.TargetNodeId == target.Id && e.Kind == "calls");
        Assert.DoesNotContain(graph.Edges, e => e.TargetNodeId == handler.Id && e.Kind == "calls");
        Assert.DoesNotContain(graph.Edges, e => e.TargetNodeId == external.TargetNodeId && e.Kind == "calls");
        var changed = Assert.Single(graph.Nodes, n => n.Kind == HarnessNodeKind.Member && n.Name == "Changed");
        Assert.Single(graph.Edges, e => e.SourceNodeId == run.Id && e.TargetNodeId == changed.Id && e.Kind == "writes");
    }

    private sealed record Sample(SymbolIndex Index, IReadOnlyList<CollectedEvidence> Evidence,
        OperationCollectionStats Stats, IReadOnlyList<HarnessProjectVariant> Projects);

    private HarnessGraphEnvelope Project(Sample sample, long generation = 1)
        => HarnessGraphProjector.Project(Workspace, "snapshot", generation, HarnessCoverage.CompleteWithinScope,
            sample.Projects, sample.Index, sample.Evidence, sample.Stats);

    private Sample Collect(string text, bool twoVariants = false, bool includeDeclarations = false)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "Sample.cs");
        File.WriteAllText(path, text);
        var tree = CSharpSyntaxTree.ParseText(text, path: path);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Sample", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
        var inputs = twoVariants
            ? new[] { new SymbolIndexInput("sample10", "Sample", compilation), new SymbolIndexInput("sample9", "Sample", compilation) }
            : new[] { new SymbolIndexInput("sample10", "Sample", compilation) };
        var documents = new SourceDocumentRegistry(Identity.WorkspaceRootId(_root), _root);
        var index = SymbolIndexBuilder.Build(documents, inputs);
        var resolver = new SymbolResolver(inputs.ToDictionary(i => i.VariantId,
            i => (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["Sample"] = i.VariantId }));
        var collector = new OperationDependencyCollector(resolver, documents, index, "fixture");
        var collected = collector.Collect(inputs);
        var projectId = HarnessIdentity.ProjectId(Workspace, "Sample.csproj");
        var projects = inputs.Select(i => new HarnessProjectVariant(i.VariantId, projectId, "Sample",
            i.VariantId == "sample9" ? "net9.0" : "net10.0", "Debug")).ToArray();
        var evidence = collected.HarnessEvidence ?? collected.Evidence;
        if (includeDeclarations)
            evidence = new DeclarationDependencyCollector(resolver, documents, index, "fixture")
                .Collect(inputs).Concat(evidence).ToArray();
        return new(index, evidence, collected.Stats, projects);
    }
}
