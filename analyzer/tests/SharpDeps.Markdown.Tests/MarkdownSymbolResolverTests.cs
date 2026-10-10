namespace SharpDeps.Markdown.Tests;

using Syntamark;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Markdown;
using Xunit;

public sealed class MarkdownSymbolResolverTests
{
    private static readonly Guid Workspace = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid Document = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
    private static readonly string ProjectId = HarnessIdentity.ProjectId(Workspace, "src/App/App.csproj");
    private static readonly string Net10 = HarnessIdentity.VariantId(Workspace, ProjectId, "net10.0", "Debug");
    private static readonly string Net9 = HarnessIdentity.VariantId(Workspace, ProjectId, "net9.0", "Debug");

    [Fact]
    public void ExplicitSelectorsFiltersAndLogicalVariantDedupNeverChooseAnOverload()
    {
        var first = Symbol("App.Service.Handle", "Handle", "M:App.Service.Handle(System.Int32)", "App", Net10);
        var otherVariant = first with { VariantId = Net9 };
        var overload = Symbol("App.Service.Handle", "Handle", "M:App.Service.Handle(System.String)", "App", Net10);
        var otherProject = Symbol("Elsewhere.Service.Handle", "Handle", "M:Elsewhere.Service.Handle", "Elsewhere", Net10,
            "src/Elsewhere/Elsewhere.csproj");
        var raw = "---\nsharpdeps:\n  symbols:\n    - project: src/App/App.csproj\n      documentationId: M:App.Service.Handle(System.Int32)\n"
            + "    - fullName: App.Service.Handle\n      namespace: App\n      variant: " + Net10
            + "\n    - symbolId: " + new string('a', 64).Insert(0, "hsym_") + "\n      fullName: App.Service.Handle\n---\n"
            + "```csharp\nHandle();\n```\n";
        var projection = Parse(raw);
        var catalog = Catalog(projection, first, otherVariant, overload, otherProject);
        var result = MarkdownSymbolResolver.Resolve(projection, raw, catalog);
        var mappings = result.Mentions.Where(m => m.Origin == MarkdownMentionOrigin.ExplicitMetadata).ToArray();
        Assert.Equal(3, mappings.Length);
        Assert.Equal(HarnessCertainty.Resolved, mappings[0].Certainty);
        Assert.Equal(first.NodeId, mappings[0].TargetNodeId);
        Assert.Equal("documents_symbol", mappings[0].Relation);
        Assert.Equal(HarnessCertainty.Candidate, mappings[1].Certainty);
        Assert.Equal(2, mappings[1].CandidateNodeIds.Count);
        Assert.Null(mappings[1].TargetNodeId);
        Assert.Equal(HarnessCertainty.Unresolved, mappings[2].Certainty); // No name fallback for a deleted explicit ID.
        var fence = Assert.Single(result.Mentions, m => m.Origin == MarkdownMentionOrigin.CodeFence);
        Assert.Equal(3, fence.CandidateNodeIds.Count); // Same logical symbol in two TFMs is counted once.
        Assert.Equal(HarnessCertainty.Candidate, fence.Certainty);
        var scoped = MarkdownSymbolResolver.Resolve(projection, raw, catalog,
            new("src/App/App.csproj", "App", Net9));
        var scopedFence = Assert.Single(scoped.Mentions, m => m.Origin == MarkdownMentionOrigin.CodeFence);
        Assert.Equal(HarnessCertainty.Resolved, scopedFence.Certainty);
        Assert.Equal(first.NodeId, scopedFence.TargetNodeId);
        Assert.Equal("mentions_symbol", scopedFence.Relation);

        var globalRaw = "```csharp\nglobal::Service\nService\n```\n";
        var globalProjection = Parse(globalRaw);
        var rootNamespace = Symbol("Service", "Service", "T:Service", "");
        var otherNamespace = Symbol("Other.Service", "Service", "T:Other.Service", "Other");
        var globalResult = MarkdownSymbolResolver.Resolve(globalProjection, globalRaw,
            Catalog(globalProjection, rootNamespace, otherNamespace));
        var qualified = Assert.Single(globalResult.Mentions, m => m.Token == "global::Service");
        Assert.Equal(HarnessCertainty.Resolved, qualified.Certainty);
        Assert.Equal(rootNamespace.NodeId, qualified.TargetNodeId);
        Assert.Equal(new[] { rootNamespace.NodeId }, qualified.CandidateNodeIds);
        var plain = Assert.Single(globalResult.Mentions, m => m.Token == "Service");
        Assert.Equal(HarnessCertainty.Candidate, plain.Certainty);
        Assert.Equal(2, plain.CandidateNodeIds.Count);
        Assert.Null(plain.TargetNodeId);
    }

    [Fact]
    public void ConfirmedAndExplicitMentionsReResolveAfterAdditionDeletionAndRenameWithoutParsing()
    {
        var raw = "---\nsharpdeps:\n  symbols: [Service]\n---\n```csharp\nService\n```\n";
        var projection = Parse(raw);
        var first = Symbol("App.Service", "Service", "T:App.Service", "App");
        var second = Symbol("Other.Service", "Service", "T:Other.Service", "Other");
        var resolved = MarkdownSymbolResolver.Resolve(projection, raw, Catalog(projection, first));
        Assert.All(resolved.Mentions, m => Assert.Equal(HarnessCertainty.Resolved, m.Certainty));
        Assert.Equal(2, resolved.TokenIndex["Service"].Count);
        Assert.Equal(2, resolved.TargetIndex[first.NodeId].Count);
        var ambiguous = MarkdownSymbolResolver.ReResolve(resolved, Catalog(projection, first, second),
            new(new[] { "Service", "Other.Service" }, Array.Empty<string>(), Array.Empty<string>(), Complete: true));
        Assert.All(ambiguous.Mentions, m =>
        {
            Assert.Equal(HarnessCertainty.Candidate, m.Certainty);
            Assert.Null(m.TargetNodeId);
            Assert.Equal(2, m.CandidateNodeIds.Count);
        });
        var renamed = Symbol("App.Renamed", "Renamed", "T:App.Renamed", "App");
        // Partial changes intentionally fall back to all mentions, including previously confirmed metadata.
        var missing = MarkdownSymbolResolver.ReResolve(ambiguous, Catalog(projection, renamed),
            new(TargetNodeIds: new[] { second.NodeId }));
        Assert.All(missing.Mentions, m =>
        {
            Assert.Equal(HarnessCertainty.Unresolved, m.Certainty);
            Assert.Empty(m.CandidateNodeIds);
            Assert.Null(m.TargetNodeId);
        });
        Assert.Empty(missing.TargetIndex);
        var restored = MarkdownSymbolResolver.ReResolve(missing, Catalog(projection, first),
            new(Array.Empty<string>(), Array.Empty<string>(), new[] { MarkdownSymbolResolver.ScopeKey(new()) }, Complete: true));
        Assert.All(restored.Mentions, m => Assert.Equal(first.NodeId, m.TargetNodeId));
        Assert.Equal(resolved.Mentions.Select(m => (m.Id, m.Location, m.Snippet)),
            restored.Mentions.Select(m => (m.Id, m.Location, m.Snippet)));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)restored.TokenIndex["Service"]).Clear());
    }

    [Fact]
    public void PreservesAtomicUtf16EvidenceAndKeepsInlineBodyHeuristicsSeparateFromExampleBindings()
    {
        var raw = "# Guide\r\n\r\n😀 `App.Service` and &#65;pp.Service\r\n\r\n```csharp\r\nApp.Service\r\n```\r\n";
        var projection = Parse(raw);
        var symbol = Symbol("App.Service", "Service", "T:App.Service", "App");
        var catalog = Catalog(projection, symbol);
        var result = MarkdownSymbolResolver.Resolve(projection, raw, catalog);
        var body = result.Mentions.Where(m => m.Token == "App.Service" && m.Origin == MarkdownMentionOrigin.BodyText).ToArray();
        Assert.Equal(2, body.Length);
        Assert.All(body, m =>
        {
            Assert.Equal(HarnessCertainty.Candidate, m.Certainty);
            Assert.Null(m.TargetNodeId);
            Assert.Equal("mentions_symbol", m.Relation);
            Assert.Equal("readme.md", m.Location.SourceId);
            Assert.Equal("version-a", m.Location.SourceVersion);
            Assert.Equal(projection.Facts.TextHash, m.Location.ContentHash);
            Assert.Equal(string.Concat(m.RawFragments.Select(s => raw.Substring(s.Start, s.Length))), m.Snippet);
        });
        var entity = Assert.Single(body, m => m.Snippet!.Contains("&#65;", StringComparison.Ordinal));
        Assert.NotEqual(MarkdownMappingPrecision.Unknown, entity.Precision);
        Assert.Equal(raw.IndexOf("&#65;", StringComparison.Ordinal), entity.RawFragments[0].Start);
        Assert.Contains(result.Reasons, r => r.Contains("inline-code-kind-unavailable", StringComparison.Ordinal));
        var fence = Assert.Single(result.Mentions, m => m.Origin == MarkdownMentionOrigin.CodeFence);
        Assert.Equal(HarnessCertainty.Resolved, fence.Certainty);
        Assert.Equal("App.Service", fence.Snippet);
        var merged = projection.Graph with { Nodes = projection.Graph.Nodes.Concat(catalog.Graph.Nodes
            .Where(n => n.Kind == HarnessNodeKind.Type)).ToArray() };
        var graph = MarkdownSymbolResolver.ProjectGraph(merged, result);
        Assert.Contains(graph.Edges, e => e.SourceNodeId == fence.Id && e.Certainty == HarnessCertainty.Resolved
            && e.Kind == "mentions_symbol" && e.Origin == "CodeFence");
        Assert.DoesNotContain(graph.Edges, e => e.Kind is "calls" or "conforms_to");
        Assert.All(graph.Edges.Where(e => result.Mentions.Any(m => m.Id == e.SourceNodeId)
            && e.Origin == "BodyText"), e => Assert.Equal(HarnessCertainty.Candidate, e.Certainty));
        Assert.Throws<ArgumentException>(() => MarkdownSymbolResolver.ProjectGraph(projection.Graph, result));
        Assert.Throws<ArgumentException>(() => MarkdownSymbolResolver.ProjectGraph(merged with { Generation = 2 }, result));
    }

    [Fact]
    public void ExplicitCodeFileLinksUseOwnerAliasesAndParserTargetEvidenceWithoutUrlGuessing()
    {
        var raw = "[source][code]\n[unknown](../unknown.cs)\n[remote](https://example.invalid/App.cs)\n\n[code]: src/App.cs\n";
        var projection = Parse(raw);
        Assert.Equal(4, projection.Links.Count); // Shared facts retain both use and definition.
        var definition = Assert.Single(projection.Links, l => l.Source.Kind == MarkdownLinkKind.ReferenceDefinition);
        var use = Assert.Single(projection.Links, l => l.Source.Kind == MarkdownLinkKind.ReferenceUse);
        Assert.Equal(MarkdownLinkResolution.ResolvedReference, use.Source.Resolution);
        Assert.Equal(definition.Source.Target, use.Source.Target);
        Assert.Equal(definition.Source.TargetRawSpan, use.Source.TargetRawSpan);
        var symbol = Symbol("App.Service", "Service", "T:App.Service", "App") with { SourceId = "src/App.cs" };
        var catalog = Catalog(projection, symbol) with
        { CodeFileAliases = new Dictionary<string, string> { ["src/App.cs"] = "src/App.cs" } };
        var result = MarkdownSymbolResolver.Resolve(projection, raw, catalog);
        var links = result.Mentions.Where(m => m.Origin == MarkdownMentionOrigin.CodeFileLink).ToArray();
        Assert.Equal(3, links.Length);
        var source = Assert.Single(links, m => m.Token == "src/App.cs");
        Assert.Equal(symbol.NodeId, source.TargetNodeId);
        Assert.Equal(HarnessCertainty.Resolved, source.Certainty);
        Assert.Equal("documents_symbol", source.Relation);
        Assert.Equal("src/App.cs", source.Snippet);
        Assert.Equal(new HarnessRawSpan(use.Source.TargetRawSpan!.Value.Start, use.Source.TargetRawSpan.Value.Length),
            source.Location.RawSpan);
        Assert.All(links.Where(m => m.Id != source.Id), m =>
        {
            Assert.Equal(HarnessCertainty.Unresolved, m.Certainty);
            Assert.Empty(m.CandidateNodeIds);
        });
        var rebound = MarkdownSymbolResolver.ReResolve(result, catalog with { CodeFileAliases = null });
        Assert.Equal(HarnessCertainty.Unresolved, rebound.Mentions.Single(m => m.Id == source.Id).Certainty);
        Assert.Null(rebound.Mentions.Single(m => m.Id == source.Id).TargetNodeId);
        const string unusedDefinitionRaw = "[code]: src/App.cs\n";
        var unusedDefinition = Parse(unusedDefinitionRaw);
        var unusedResult = MarkdownSymbolResolver.Resolve(unusedDefinition, unusedDefinitionRaw,
            Catalog(unusedDefinition, symbol) with { CodeFileAliases = catalog.CodeFileAliases });
        Assert.DoesNotContain(unusedResult.Mentions, m => m.Origin == MarkdownMentionOrigin.CodeFileLink);
        var duplicateRaw = "---\nsharpdeps:\n  symbols:\n    - name: Service\n      name: Another\n---\n";
        var duplicate = Parse(duplicateRaw);
        Assert.Equal(MarkdownFrontMatterState.Invalid, duplicate.Facts.FrontMatter.State);
        var rejectedMapping = MarkdownSymbolResolver.Resolve(duplicate, duplicateRaw, Catalog(duplicate, symbol));
        Assert.DoesNotContain(rejectedMapping.Mentions, m => m.Origin == MarkdownMentionOrigin.ExplicitMetadata);
        Assert.Contains(rejectedMapping.Reasons, r => r.StartsWith("front-matter-not-usable", StringComparison.Ordinal));
    }

    [Fact]
    public void IncompleteCatalogCoverageDowngradesAllBindingsAndPropagatesEvenWithoutMentions()
    {
        var raw = "---\nsharpdeps:\n  symbols: [Service]\n---\n```csharp\nApp.Service\n```\n";
        var projection = CompleteProjection(raw);
        var symbol = Symbol("App.Service", "Service", "T:App.Service", "App");
        var catalog = Catalog(projection, symbol);
        var resolved = MarkdownSymbolResolver.Resolve(projection, raw, catalog);
        Assert.Equal(2, resolved.Mentions.Count);
        Assert.All(resolved.Mentions, m =>
        {
            Assert.Equal(HarnessCertainty.Resolved, m.Certainty);
            Assert.Equal(symbol.NodeId, m.TargetNodeId);
        });
        var metadata = Assert.Single(resolved.Mentions, m => m.Origin == MarkdownMentionOrigin.ExplicitMetadata);
        Assert.Equal("Service", metadata.Token);
        Assert.Equal("documents_symbol", metadata.Relation);
        Assert.Equal(symbol.NodeId, metadata.TargetNodeId);
        Assert.Equal(HarnessCoverage.CompleteWithinScope, resolved.CatalogCoverage);
        var merged = projection.Graph with { Nodes = projection.Graph.Nodes.Concat(catalog.Graph.Nodes).ToArray() };
        Assert.Equal(HarnessCoverage.CompleteWithinScope, MarkdownSymbolResolver.ProjectGraph(merged, resolved).Coverage);
        var noSymbolChanges = new MarkdownSymbolChanges(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Complete: true);
        foreach (var coverage in new[] { HarnessCoverage.Partial, HarnessCoverage.Failed })
        {
            var incomplete = catalog with { Graph = catalog.Graph with { Coverage = coverage } };
            var direct = MarkdownSymbolResolver.Resolve(projection, raw, incomplete);
            Assert.Equal(2, direct.Mentions.Count);
            Assert.All(direct.Mentions, m =>
            {
                Assert.Equal(HarnessCertainty.Candidate, m.Certainty);
                Assert.Null(m.TargetNodeId);
                Assert.Equal("catalog-incomplete", m.Reason);
            });
            var downgraded = MarkdownSymbolResolver.ReResolve(resolved, incomplete, noSymbolChanges);
            Assert.Equal(coverage, downgraded.CatalogCoverage);
            Assert.Equal(2, downgraded.Mentions.Count);
            Assert.All(downgraded.Mentions, candidate =>
            {
                Assert.Equal(HarnessCertainty.Candidate, candidate.Certainty);
                Assert.Null(candidate.TargetNodeId);
                Assert.Equal(new[] { symbol.NodeId }, candidate.CandidateNodeIds);
                Assert.Equal("catalog-incomplete", candidate.Reason);
            });
            Assert.Contains("catalog-incomplete", downgraded.Reasons);
            Assert.Equal(HarnessCoverage.Partial, MarkdownSymbolResolver.ProjectGraph(merged, downgraded).Coverage);
            var restored = MarkdownSymbolResolver.ReResolve(downgraded, catalog, noSymbolChanges);
            Assert.Equal(HarnessCoverage.CompleteWithinScope, restored.CatalogCoverage);
            Assert.Equal(2, restored.Mentions.Count);
            Assert.All(restored.Mentions, m =>
            {
                Assert.Equal(symbol.NodeId, m.TargetNodeId);
                Assert.Equal(HarnessCertainty.Resolved, m.Certainty);
                Assert.Null(m.Reason);
            });
            Assert.Equal(symbol.NodeId, restored.Mentions.Single(m => m.Id == metadata.Id).TargetNodeId);
            Assert.DoesNotContain("catalog-incomplete", restored.Reasons);
            Assert.Equal(HarnessCoverage.CompleteWithinScope, MarkdownSymbolResolver.ProjectGraph(merged, restored).Coverage);

            var empty = CompleteProjection("");
            var emptyIncomplete = MarkdownSymbolResolver.Resolve(empty, "", incomplete);
            Assert.Empty(emptyIncomplete.Mentions);
            Assert.Contains("catalog-incomplete", emptyIncomplete.Reasons);
            Assert.Equal(HarnessCoverage.Partial, MarkdownSymbolResolver.ProjectGraph(empty.Graph, emptyIncomplete).Coverage);
            var emptyRestored = MarkdownSymbolResolver.ReResolve(emptyIncomplete, catalog, noSymbolChanges);
            Assert.DoesNotContain("catalog-incomplete", emptyRestored.Reasons);
            Assert.Equal(HarnessCoverage.CompleteWithinScope, MarkdownSymbolResolver.ProjectGraph(empty.Graph, emptyRestored).Coverage);
        }

        static MarkdownGraphProjection CompleteProjection(string text)
        {
            var parsed = Parse(text);
            var tokens = parsed.Facts.Sections.Select((s, i) => (s.LocalKey, Token: "persisted-coverage:" + i))
                .ToDictionary(s => s.LocalKey, s => s.Token, StringComparer.Ordinal);
            var bound = new LithoSharpMarkdownAdapter().Analyze(new(text, Workspace, Document, "scope", "readme.md", "version-a",
                "snapshot-a", 1, tokens, parsed.Facts.TextHash), true);
            Assert.Equal(HarnessCoverage.CompleteWithinScope, bound.Graph.Coverage);
            return bound;
        }
    }

    [Fact]
    public void RejectsStaleRawSourceWrongBindingAndUnownedCatalogIds()
    {
        var raw = "```csharp\nApp.Service\n```\n";
        var projection = Parse(raw);
        var symbol = Symbol("App.Service", "Service", "T:App.Service", "App");
        var catalog = Catalog(projection, symbol);
        Assert.Throws<ArgumentException>(() => MarkdownSymbolResolver.Resolve(projection, raw + " ", catalog));
        var wrongSource = projection with { Graph = projection.Graph with { Nodes = projection.Graph.Nodes.Select(n =>
            n.Kind == HarnessNodeKind.Document ? n with { Location = n.Location! with { SourceId = "other.md" } } : n).ToArray() } };
        Assert.Throws<ArgumentException>(() => MarkdownSymbolResolver.Resolve(wrongSource, raw, catalog));
        var wrongVersion = projection with { Graph = projection.Graph with { Nodes = projection.Graph.Nodes.Select(n =>
            n.Kind == HarnessNodeKind.Document ? n with { Location = n.Location! with { SourceVersion = "other-version" } } : n).ToArray() } };
        Assert.Throws<ArgumentException>(() => MarkdownSymbolResolver.Resolve(wrongVersion, raw, catalog));
        Assert.Throws<ArgumentException>(() => MarkdownSymbolResolver.Resolve(projection, raw,
            catalog with { Symbols = new[] { symbol with { NodeId = "legacy-opaque-id" } } }));
        Assert.Throws<ArgumentException>(() => MarkdownSymbolResolver.Resolve(projection, raw,
            catalog with { Graph = catalog.Graph with { Nodes = Array.Empty<HarnessNode>() } }));
        Assert.Throws<ArgumentException>(() => MarkdownSymbolResolver.Resolve(projection, raw,
            catalog with { Graph = catalog.Graph with { WorkspaceId = "hw_aaaaaaaaaaaa4aaa8aaaaaaaaaaaaaaa" } }));
        Assert.Throws<OperationCanceledException>(() => MarkdownSymbolResolver.Resolve(projection, raw, catalog,
            cancellationToken: new CancellationToken(true)));
    }

    private static MarkdownGraphProjection Parse(string raw)
        => new LithoSharpMarkdownAdapter().Analyze(new(raw, Workspace, Document, "scope", "readme.md", "version-a", "snapshot-a", 1), true);

    private static MarkdownSymbolCandidate Symbol(string fullName, string shortName, string documentationId, string ns,
        string? variant = null, string project = "src/App/App.csproj")
        => new(HarnessIdentity.LogicalSymbolId(Workspace, ProjectId, "type", documentationId),
            fullName, shortName, documentationId, project, ns, variant);

    private static MarkdownSymbolCatalog Catalog(MarkdownGraphProjection projection, params MarkdownSymbolCandidate[] symbols)
    {
        var nodes = symbols.DistinctBy(s => s.NodeId).Select(s => new HarnessNode(s.NodeId,
            s.DocumentationId?.StartsWith("M:", StringComparison.Ordinal) == true ? HarnessNodeKind.Member : HarnessNodeKind.Type,
            s.FullName, ProjectId, s.SourceId is null ? null : new HarnessLocation(s.SourceId, "code-version", "code-hash", new(0, 1)))).ToArray();
        var occurrences = symbols.Where(s => s.VariantId is not null).Select(s => new HarnessSymbolOccurrence(
            HarnessIdentity.SymbolOccurrenceId(Workspace, s.NodeId, s.VariantId!), s.NodeId, s.VariantId!, null)).ToArray();
        var variants = symbols.Where(s => s.VariantId is not null).DistinctBy(s => s.VariantId).Select(s =>
            new HarnessVariant(s.VariantId!, ProjectId, s.VariantId == Net9 ? "net9.0" : "net10.0", "Debug", null, null, null)).ToArray();
        // The symbol producer's coverage is independent of the Markdown document's pending section identities.
        return new(projection.Graph with { Coverage = HarnessCoverage.CompleteWithinScope,
            Nodes = nodes, SymbolOccurrences = occurrences, Variants = variants }, Array.AsReadOnly(symbols));
    }
}
