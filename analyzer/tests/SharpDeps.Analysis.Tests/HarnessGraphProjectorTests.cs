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

    [Fact]
    public void ProjectsMemberDeclarationFactsToDefiningAndExternalOccurrences()
    {
        var sample = Collect("""
            public class Derived : Library.Base<string>, Library.IContract<string>
            {
                public override string Convert(string value) => value;
                string Library.IContract<string>.Convert(string value) => value;
                public override string ToString() => "saved";
            }
            """, twoVariants: true, includeDeclarations: true, referencedSource: """
            namespace Library;
            public class Base<T> { public virtual T Convert(T value) => value; }
            public interface IContract<T> { T Convert(T value); }
            """);
        var graph = Project(sample);
        var facts = graph.Edges.Where(e => e.Kind is "overrides" or "implements_explicitly").ToArray();
        Assert.Equal(6, facts.Length);
        Assert.Equal(HarnessCoverage.CompleteWithinScope, graph.Coverage);
        var libraryVariant = Assert.Single(graph.Variants, v => v.ProjectId == HarnessIdentity.ProjectId(Workspace, "Library.csproj"));
        Assert.All(facts, edge =>
        {
            var source = Assert.Single(graph.SymbolOccurrences, o => o.Id == edge.SourceOccurrenceId);
            var target = Assert.Single(graph.SymbolOccurrences, o => o.Id == edge.TargetOccurrenceId);
            Assert.Equal(HarnessNodeKind.Member, Assert.Single(graph.Nodes, n => n.Id == source.LogicalSymbolId).Kind);
            Assert.Equal(edge.VariantId, source.VariantId);
            var targetNode = Assert.Single(graph.Nodes, n => n.Id == target.LogicalSymbolId);
            if (targetNode.Kind == HarnessNodeKind.ExternalSymbol)
            {
                Assert.Equal("ToString", targetNode.Name);
                Assert.Equal(source.VariantId, target.VariantId);
                Assert.Contains("System.Object.ToString", targetNode.Signature!, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(HarnessNodeKind.Member, targetNode.Kind);
                Assert.Equal(libraryVariant.Id, target.VariantId);
                Assert.NotEqual(source.VariantId, target.VariantId);
            }
            Assert.Equal(HarnessCertainty.Resolved, edge.Certainty);
            Assert.Equal("roslyn-declaration", edge.Producer);
            Assert.Equal("userSource", edge.Origin);
            Assert.NotNull(edge.Evidence?.RawSpan);
            Assert.NotEmpty(edge.Evidence!.ContentHash!);
        });
        Assert.DoesNotContain(graph.Edges, e => e.Kind is "calls" or "constructs");
        Assert.Equal(facts.Select(e => e.Id), Project(sample, generation: 2).Edges
            .Where(e => e.Kind is "overrides" or "implements_explicitly").Select(e => e.Id));
        var restored = HarnessGraphContract.Read(JsonSerializer.Serialize(graph, HarnessGraphJsonContext.Default.HarnessGraphEnvelope));
        Assert.Equal(facts.Select(e => e.Id), restored.Edges
            .Where(e => e.Kind is "overrides" or "implements_explicitly").Select(e => e.Id));

        var missing = Project(sample with
        {
            Index = sample.Index with { Members = sample.Index.Members.Where(m => m.ProjectVariantId != "library10").ToArray() }
        });
        Assert.Equal(HarnessCoverage.Partial, missing.Coverage);
        Assert.Contains(missing.Diagnostics!, d => d.Code == "roslyn.missingTarget" && d.Count == 4);
        Assert.DoesNotContain(missing.Edges, e => e.Kind == "implements_explicitly");
    }

    [Fact]
    public void PreservesHttpGetLiteralProvenanceOnMembersWithoutChangingAttributeIdentities()
    {
        const string text = """
            using Microsoft.AspNetCore.Mvc;
            public partial class Endpoints
            {
                [HttpGet("one"), HttpGet(template: "")]
                public partial int Read();
            }
            public partial class Endpoints
            {
                public partial int Read() => 1;
            }
            """;
        var sample = Collect(text, twoVariants: true, includeDeclarations: true);
        var graph = Project(sample);
        var declarations = graph.Edges.Where(edge => edge.HttpGetDeclaration is not null).ToArray();
        Assert.Equal(4, declarations.Length);
        foreach (var edge in declarations)
        {
            var declaration = Assert.IsType<HarnessHttpGetDeclaration>(edge.HttpGetDeclaration);
            Assert.Equal(HarnessHttpGetDeclarationState.Saved, declaration.State);
            Assert.Equal("attribute", edge.Kind);
            Assert.Equal("roslyn-declaration", edge.Producer);
            var evidence = edge.Evidence!;
            var name = evidence.RawSpan!.Value;
            Assert.Equal("HttpGet", text.Substring(name.Start, name.Length));
            var literal = declaration.TemplateSpan!.Value;
            Assert.Equal("\"" + declaration.Template + "\"", text.Substring(literal.Start, literal.Length));
            var source = graph.SymbolOccurrences.Single(occurrence => occurrence.Id == edge.SourceOccurrenceId);
            Assert.Equal(edge.VariantId, source.VariantId);
            Assert.Contains(source.Declarations!, saved => saved.SourceId == evidence.SourceId
                && saved.SourceVersion == evidence.SourceVersion && saved.ContentHash == evidence.ContentHash
                && saved.RawSpan is { } range && range.Start <= name.Start && range.End >= literal.End);
            Assert.True(source.Declarations!.Count > 1);
            Assert.Equal(HarnessNodeKind.ExternalSymbol, graph.Nodes.Single(node => node.Id == edge.TargetNodeId).Kind);
            Assert.Equal(edge.VariantId, graph.SymbolOccurrences.Single(occurrence => occurrence.Id == edge.TargetOccurrenceId).VariantId);
        }
        var plain = Project(sample with { Evidence = sample.Evidence.Select(entry => entry with { HttpGetDeclaration = null }).ToArray() });
        Assert.Equal(plain.Nodes, graph.Nodes);
        Assert.Equal(plain.Edges, graph.Edges.Select(edge => edge with { HttpGetDeclaration = null }));
        Assert.Equal(graph.Edges, Project(sample, generation: 2).Edges);
        Assert.Equal(graph.Edges, HarnessGraphContract.Read(JsonSerializer.Serialize(graph, HarnessGraphJsonContext.Default.HarnessGraphEnvelope)).Edges);

        var entry = sample.Evidence.First(entry => entry.HttpGetDeclaration is not null);
        var conflict = entry with { HttpGetDeclaration = entry.HttpGetDeclaration! with { Template = "different" } };
        var conflicted = Project(sample with { Evidence = sample.Evidence.Append(conflict).ToArray() });
        var omission = Assert.Single(conflicted.Edges, edge => edge.HttpGetDeclaration?.State == HarnessHttpGetDeclarationState.Omitted);
        Assert.Equal("semantic-unavailable", omission.HttpGetDeclaration!.OmissionReason);
        Assert.Null(omission.HttpGetDeclaration.Template);
        Assert.Null(omission.HttpGetDeclaration.TemplateSpan);
        Assert.Equal(graph.Coverage, conflicted.Coverage);
        Assert.Equal(conflicted.Edges, Project(sample with { Evidence = sample.Evidence.Append(conflict).Reverse().ToArray() }).Edges);

        var candidate = Project(sample with { Evidence = sample.Evidence.Append(entry with
        { Evidence = entry.Evidence with { Confidence = "candidate" } }).ToArray() });
        Assert.Contains(candidate.Edges, edge => edge.Id == omission.Id && edge.Certainty == HarnessCertainty.Candidate && edge.HttpGetDeclaration is null);
        var foreignSpan = Project(sample with { Evidence = sample.Evidence.Select(value => value == entry ? value with
        { HttpGetDeclaration = value.HttpGetDeclaration! with { TemplateSpan = new(0, 1) } } : value).ToArray() });
        Assert.Contains(foreignSpan.Edges, edge => edge.Id == omission.Id && edge.HttpGetDeclaration is null);
        var missingHash = Project(sample with { Evidence = sample.Evidence.Select(value => value with
        { Evidence = value.Evidence with { SourceContentHash = null! } }).ToArray() });
        Assert.DoesNotContain(missingHash.Edges, edge => edge.HttpGetDeclaration is not null);
    }

    [Fact]
    public void PreservesExtensionBindingWithoutChangingCallIdentityOrDefiningOccurrences()
    {
        var sample = Collect("""
            using System.Collections.Generic;
            using System.Linq;
            using Library;
            public class Service
            {
                public string Run(List<int> items) => items.Convert("saved");
                public int[] Copy(IEnumerable<int> items) => items.ToArray();
                public int[] InstanceCopy(List<int> items) => items.ToArray();
            }
            """, twoVariants: true, referencedSource: """
            namespace Library;
            public static class Extensions
            {
                public static R Convert<T, R>(this System.Collections.Generic.IEnumerable<T> items, R result) => result;
            }
            """);
        var graph = Project(sample);
        var bindings = graph.Edges.Where(edge => edge.ExtensionBinding is not null).ToArray();
        Assert.Equal(4, bindings.Length);
        var library = graph.Variants.Single(v => v.ProjectId == sample.Projects.Single(p => p.Key == "library10").ProjectId);
        foreach (var edge in bindings)
        {
            Assert.Equal("calls", edge.Kind);
            Assert.Equal(HarnessCertainty.Resolved, edge.Certainty);
            var binding = Assert.IsType<HarnessExtensionBinding>(edge.ExtensionBinding);
            Assert.Equal(HarnessExtensionBindingState.Saved, binding.State);
            Assert.Contains("IEnumerable<int>", binding.ConstructedMethodDisplay!, StringComparison.Ordinal);
            var target = graph.Nodes.Single(n => n.Id == edge.TargetNodeId);
            var occurrence = graph.SymbolOccurrences.Single(o => o.Id == edge.TargetOccurrenceId);
            if (target.Kind == HarnessNodeKind.ExternalSymbol)
            {
                Assert.Equal(edge.VariantId, occurrence.VariantId);
                Assert.Contains("IEnumerable<int>", binding.ReceiverTypeDisplay!, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("List<int>", binding.ReceiverTypeDisplay!, StringComparison.Ordinal);
                Assert.Equal(library.Id, occurrence.VariantId);
                Assert.Contains("Convert<int, string>", binding.ConstructedMethodDisplay!, StringComparison.Ordinal);
            }
        }
        var instanceOwner = graph.Nodes.Single(node => node.Kind == HarnessNodeKind.Member && node.Name == "InstanceCopy");
        var instanceCalls = graph.Edges.Where(edge => edge.SourceNodeId == instanceOwner.Id && edge.Kind == "calls").ToArray();
        Assert.Equal(2, instanceCalls.Length);
        Assert.All(instanceCalls, edge =>
        {
            Assert.Null(edge.ExtensionBinding);
            var target = graph.Nodes.Single(node => node.Id == edge.TargetNodeId);
            Assert.Equal("ToArray", target.Name);
            Assert.Contains("List", graph.Nodes.Single(node => node.Id == target.ParentId).Name, StringComparison.Ordinal);
        });
        var plain = Project(sample with { Evidence = sample.Evidence.Select(entry => entry with { ExtensionBinding = null }).ToArray() });
        Assert.Equal(plain.Edges, graph.Edges.Select(edge => edge with { ExtensionBinding = null }));
        Assert.Equal(plain.Nodes, graph.Nodes);
        var restored = HarnessGraphContract.Read(JsonSerializer.Serialize(graph, HarnessGraphJsonContext.Default.HarnessGraphEnvelope));
        Assert.Equal(graph.Edges, restored.Edges);
        Assert.Equal(graph.Edges, Project(sample, generation: 2).Edges);

        var entry = sample.Evidence.First(value => value.ExtensionBinding is not null);
        var conflict = entry with { ExtensionBinding = entry.ExtensionBinding! with { ReceiverTypeDisplay = "different saved label" } };
        var conflicted = Project(sample with { Evidence = sample.Evidence.Append(conflict).ToArray() });
        var omitted = Assert.Single(conflicted.Edges, edge => edge.ExtensionBinding?.State == HarnessExtensionBindingState.Omitted);
        Assert.Equal("semantic-unavailable", omitted.ExtensionBinding!.OmissionReason);
        Assert.Equal(graph.Coverage, conflicted.Coverage);

        var unavailable = Project(sample with { Evidence = sample.Evidence.Select(value => value with
        { Evidence = value.Evidence with { SourceContentHash = null } }).ToArray() });
        Assert.DoesNotContain(unavailable.Edges, edge => edge.ExtensionBinding is not null);
        Assert.Equal(graph.Edges.Count, unavailable.Edges.Count);
    }

    [Fact]
    public void ProjectsScopedRegistrationRolesAsCompleteSitesWithDefiningAndExternalVariants()
    {
        var sample = Collect("""
            using Microsoft.Extensions.DependencyInjection;
            public class Startup
            {
                public void Configure(IServiceCollection services)
                {
                    services.AddScoped<Library.IService, Library.Implementation>();
                    services.AddScoped<System.IDisposable, System.IO.MemoryStream>();
                }
            }
            """, twoVariants: true, includeDi: true, referencedSource: """
            namespace Library;
            public interface IService { }
            public sealed class Implementation : IService { }
            """);
        var graph = Project(sample);
        var roles = graph.Edges.Where(edge => edge.Producer == "roslyn-di-registration").ToArray();
        Assert.Equal(8, roles.Length);
        foreach (var site in roles.GroupBy(edge => (edge.SourceOccurrenceId, edge.Evidence)))
        {
            Assert.Equal(new[] { "registers_scoped_implementation", "registers_scoped_service" },
                site.Select(edge => edge.Kind).Order(StringComparer.Ordinal));
            Assert.All(site, edge =>
            {
                Assert.Equal(HarnessCertainty.Resolved, edge.Certainty);
                Assert.NotNull(edge.Evidence!.RawSpan);
                Assert.NotEmpty(edge.Evidence.ContentHash!);
                Assert.Equal(edge.VariantId, graph.SymbolOccurrences.Single(o => o.Id == edge.SourceOccurrenceId).VariantId);
                var target = graph.SymbolOccurrences.Single(o => o.Id == edge.TargetOccurrenceId);
                var node = graph.Nodes.Single(n => n.Id == target.LogicalSymbolId);
                if (node.Kind == HarnessNodeKind.ExternalSymbol) Assert.Equal(edge.VariantId, target.VariantId);
                else Assert.Equal(graph.Variants.Single(v => v.ProjectId == sample.Projects.Single(p => p.Key == "library10").ProjectId).Id, target.VariantId);
            });
        }
        var restored = HarnessGraphContract.Read(JsonSerializer.Serialize(graph, HarnessGraphJsonContext.Default.HarnessGraphEnvelope));
        Assert.Equal(roles.Select(edge => edge.Id), restored.Edges.Where(edge => edge.Producer == "roslyn-di-registration").Select(edge => edge.Id));
        Assert.Equal(roles.Select(edge => edge.Id), Project(sample, generation: 2).Edges.Where(edge => edge.Producer == "roslyn-di-registration").Select(edge => edge.Id));

        var pair = sample.Evidence.Where(entry => entry.Producer == "roslyn-di-registration" && entry.TargetIsExternal)
            .GroupBy(entry => (entry.SourceVariantId, entry.Evidence.PhysicalSpan)).First().ToArray();
        Assert.Equal(2, pair.Length);
        foreach (var incomplete in new[]
        {
            pair.Take(1).ToArray(),
            pair.Select(entry => entry.Evidence.Kind == "registers_scoped_service" ? entry with
            { TargetIsExternal = false, TargetSymbol = null, Evidence = entry.Evidence with { TargetEntityId = "missing-type" } } : entry).ToArray()
        })
        {
            var missing = Project(sample with { Evidence = incomplete });
            Assert.Empty(missing.Edges);
            Assert.DoesNotContain(missing.Nodes, node => node.Kind == HarnessNodeKind.ExternalSymbol);
            Assert.Equal(HarnessCoverage.Partial, missing.Coverage);
            Assert.Contains(missing.Diagnostics!, diagnostic => diagnostic.Code == "roslyn.incompleteDiRegistration" && diagnostic.Count == 1);
        }
    }

    private sealed record Sample(SymbolIndex Index, IReadOnlyList<CollectedEvidence> Evidence,
        OperationCollectionStats Stats, IReadOnlyList<HarnessProjectVariant> Projects);

    private HarnessGraphEnvelope Project(Sample sample, long generation = 1)
        => HarnessGraphProjector.Project(Workspace, "snapshot", generation, HarnessCoverage.CompleteWithinScope,
            sample.Projects, sample.Index, sample.Evidence, sample.Stats);

    private Sample Collect(string text, bool twoVariants = false, bool includeDeclarations = false,
        string? referencedSource = null, bool includeDi = false)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "Sample.cs");
        File.WriteAllText(path, text);
        var tree = CSharpSyntaxTree.ParseText(text, path: path);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(includeDi ? new[] { typeof(Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions).Assembly.Location } : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
        CSharpCompilation? library = null;
        if (referencedSource is not null)
        {
            var libraryPath = Path.Combine(_root, "Library.cs");
            File.WriteAllText(libraryPath, referencedSource);
            library = CSharpCompilation.Create("Library", [CSharpSyntaxTree.ParseText(referencedSource, path: libraryPath)],
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.DoesNotContain(library.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        }
        var compilation = CSharpCompilation.Create("Sample", [tree],
            library is null ? references : references.Append(library.ToMetadataReference()),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
        var sampleInputs = twoVariants
            ? new[] { new SymbolIndexInput("sample10", "Sample", compilation), new SymbolIndexInput("sample9", "Sample", compilation) }
            : new[] { new SymbolIndexInput("sample10", "Sample", compilation) };
        var inputs = library is null ? sampleInputs : sampleInputs.Append(new("library10", "Library", library)).ToArray();
        var documents = new SourceDocumentRegistry(Identity.WorkspaceRootId(_root), _root);
        var index = SymbolIndexBuilder.Build(documents, inputs);
        var resolver = new SymbolResolver(inputs.ToDictionary(i => i.VariantId, i =>
        {
            var map = new Dictionary<string, string> { [i.Compilation.AssemblyName!] = i.VariantId };
            if (library is not null) map["Library"] = "library10";
            return (IReadOnlyDictionary<string, string>)map;
        }));
        var collector = new OperationDependencyCollector(resolver, documents, index, "fixture");
        var collected = collector.Collect(inputs);
        var projects = inputs.Select(i => new HarnessProjectVariant(i.VariantId,
            HarnessIdentity.ProjectId(Workspace, i.ProjectName + ".csproj"), i.ProjectName,
            i.VariantId == "sample9" ? "net9.0" : "net10.0", "Debug")).ToArray();
        var evidence = collected.HarnessEvidence ?? collected.Evidence;
        if (includeDeclarations)
        {
            var declaration = new DeclarationDependencyCollector(resolver, documents, index, "fixture").CollectWithHarness(inputs);
            evidence = (declaration.HarnessEvidence ?? declaration.Evidence).Concat(evidence).ToArray();
        }
        return new(index, evidence, collected.Stats, projects);
    }
}
