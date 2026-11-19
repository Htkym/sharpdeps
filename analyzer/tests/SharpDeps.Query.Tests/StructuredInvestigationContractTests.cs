namespace SharpDeps.Query.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Index;
using Xunit;

/// <summary>
/// Independent, synthetic regressions for the saved renderer-contract mismatch:
/// requested source without a selection suffix, reads without occurrence selectors,
/// and caller evidence. No recorded answer/source payload or benchmark guard is copied.
/// This typed JSON contract is NOT understood by the historical text-only guard.
/// </summary>
public sealed class StructuredInvestigationContractTests
{
    private static readonly QueryBudget Generous = new(MaxMilliseconds: 30000, MaxChars: 100000, MaxBytes: 400000);

    [Fact]
    public void FindThenReadPreservesRequestedDeclarationAndScopeWithoutParsingDisplayText()
    {
        using var f = new Fixture();
        var service = f.Service();
        var found = service.ExecuteSymbols(f.Request(SymbolQueryOperation.FindSymbols) with { Query = "Target" });
        Assert.True(found.Succeeded);
        var pointer = Assert.Single(found.Envelope.Items, item => item.Selector == f.Occurrence(f.Target));
        Assert.All(found.Envelope.Items.SelectMany(item => item.Declarations), declaration => Assert.Null(declaration.Text));
        Assert.Equal(f.Project, pointer.ProjectId);
        Assert.Equal(f.Variant, pointer.VariantId);

        var read = service.ExecuteSymbols(f.Request(SymbolQueryOperation.ReadSymbols, pointer.Selector));
        Assert.True(read.Succeeded);
        Assert.Equal(found.Envelope.Snapshot, read.Envelope.Snapshot);
        var item = Assert.Single(read.Envelope.Items);
        Assert.Equal(pointer.Selector, item.Selector);
        Assert.Equal(f.Target, item.LogicalId);
        Assert.Equal(f.Project, item.ProjectId);
        Assert.Equal(f.Variant, item.VariantId);
        var body = Assert.Single(item.Declarations, declaration => declaration.Text is not null);
        Assert.Equal(Fixture.TargetBody, body.Text);
        Assert.Equal(Fixture.SourcePath, body.Path);
        Assert.Equal(Fixture.Hash(f.Source), body.ContentHash);
        Assert.Equal(f.Source.Substring(body.Span.Start, body.Span.Length), body.Text);
        Assert.True(body.CompleteSavedRange);
        Assert.DoesNotContain(Fixture.CallerBody, read.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.HelperBody, read.Json, StringComparison.Ordinal);
        AssertRoundTrip(read);
    }

    [Fact]
    public void OneHopRelationsKeepReadTargetsWithoutInventingOccurrenceSelectors()
    {
        using var f = new Fixture();
        var reply = f.Service().ExecuteSymbols(f.Request(SymbolQueryOperation.Relations, f.Occurrence(f.Target))
            with { EdgeKinds = ["calls", "reads"] });
        Assert.True(reply.Succeeded);
        var item = Assert.Single(reply.Envelope.Items);
        var reads = item.Relations.Where(edge => edge.Kind == "reads").ToArray();
        Assert.Equal(2, reads.Length);
        Assert.All(reads, edge =>
        {
            Assert.Equal(f.Target, edge.SourceLogicalId);
            Assert.Equal(f.Occurrence(f.Target), edge.SourceSelector);
            Assert.Null(edge.TargetSelector);
            Assert.Equal(f.Variant, edge.VariantId);
            Assert.Equal(HarnessCertainty.Resolved, edge.Certainty);
            Assert.NotNull(edge.Evidence);
        });
        Assert.Contains(reads, edge => edge.TargetLogicalId == f.ExternalFlag);
        Assert.Contains(reads, edge => edge.TargetLogicalId == f.ExternalLogger);
        Assert.Contains(item.Relations, edge => edge.Kind == "calls" && edge.TargetLogicalId == f.Helper);
        // Helper -> Leaf is two hops from Target and must not be silently expanded.
        Assert.DoesNotContain(item.Relations, edge => edge.TargetLogicalId == f.Leaf);
        Assert.All(item.Declarations, declaration => Assert.Null(declaration.Text));
        AssertRoundTrip(reply);
    }

    [Fact]
    public void IncomingCallerEvidenceRetainsEndpointIdentityRatherThanDisplayCounts()
    {
        using var f = new Fixture();
        var reply = f.Service().ExecuteSymbols(f.Request(SymbolQueryOperation.Relations, f.Occurrence(f.Target))
            with { Direction = "incoming", EdgeKinds = ["calls"] });
        Assert.True(reply.Succeeded);
        var edge = Assert.Single(Assert.Single(reply.Envelope.Items).Relations);
        Assert.Equal(f.Caller, edge.SourceLogicalId);
        Assert.Equal(f.Target, edge.TargetLogicalId);
        Assert.Equal(f.Occurrence(f.Caller), edge.SourceSelector);
        Assert.Equal(f.Occurrence(f.Target), edge.TargetSelector);
        Assert.NotNull(edge.Evidence);
        Assert.Equal(HarnessCoverage.Partial, reply.Envelope.Snapshot!.Coverage);
        // This is one returned saved edge, not a runtime or whole-index caller total.
        AssertRoundTrip(reply);
    }

    [Fact]
    public void ReadBatchDoesNotReturnDuplicateBodiesForDuplicateSelectors()
    {
        using var f = new Fixture();
        var selector = f.Occurrence(f.Target);
        var reply = f.Service().ExecuteSymbols(f.Request(SymbolQueryOperation.ReadSymbols, selector, selector));
        Assert.True(reply.Succeeded);
        Assert.Single(reply.Envelope.Items);
        Assert.Single(reply.Envelope.Items.SelectMany(item => item.Declarations), declaration => declaration.Text is not null);
    }

    [Fact]
    public void MissingBatchItemAndBoundedRelationsRemainExplicitInsteadOfClaimingCompleteness()
    {
        using var f = new Fixture();
        var absent = "hocc_" + new string('0', 64);
        var batch = f.Service().ExecuteSymbols(f.Request(SymbolQueryOperation.ReadSymbols, f.Occurrence(f.Target), absent));
        Assert.True(batch.Succeeded);
        Assert.Equal(2, batch.Envelope.Items.Count);
        var missing = Assert.Single(batch.Envelope.Items, item => item.Selector == absent);
        Assert.Equal("not-found", missing.Outcome);
        Assert.Contains("SYMBOL_NOT_FOUND", missing.Omissions);
        Assert.Empty(missing.Declarations);
        AssertRoundTrip(batch);

        var limited = f.Service().ExecuteSymbols(f.Request(SymbolQueryOperation.Relations, f.Occurrence(f.Target))
            with { MaxResults = 1, EdgeKinds = ["calls", "reads"] });
        Assert.True(limited.Succeeded);
        Assert.Single(Assert.Single(limited.Envelope.Items).Relations);
        Assert.True(limited.Envelope.Truncated);
        Assert.NotEmpty(limited.Envelope.Omissions);
        AssertRoundTrip(limited);
    }

    [Theory]
    [InlineData(SymbolQueryOperation.FindSymbols)]
    [InlineData(SymbolQueryOperation.ReadSymbols)]
    [InlineData(SymbolQueryOperation.Relations)]
    public void MinimalBudgetIncludesSerializedTextEnvelope(SymbolQueryOperation operation)
    {
        using var f = new Fixture();
        var request = operation == SymbolQueryOperation.FindSymbols
            ? f.Request(operation) with { Query = "Target" }
            : f.Request(operation, f.Occurrence(f.Target));
        var reply = f.Service().ExecuteSymbols(request with { Budget = Generous with { MaxChars = 2048, MaxBytes = 2048 } });
        AssertRoundTrip(reply);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UntrustedOrChangedSourceNeverBecomesReturnedEvidence(bool changed)
    {
        using var f = new Fixture();
        if (changed) File.WriteAllText(f.FullPath, f.Source.Replace("return Helper()", "return 999", StringComparison.Ordinal));
        var reply = f.Service(trusted: changed).ExecuteSymbols(f.Request(SymbolQueryOperation.ReadSymbols, f.Occurrence(f.Target)));
        Assert.All(reply.Envelope.Items.SelectMany(item => item.Declarations), declaration => Assert.Null(declaration.Text));
        Assert.True(reply.Envelope.Errors.Count > 0 || reply.Envelope.Omissions.Count > 0
            || reply.Envelope.Items.Any(item => item.Omissions.Count > 0
                || item.Declarations.Any(declaration => !declaration.CompleteSavedRange)));
        AssertRoundTrip(reply);
    }

    private static void AssertRoundTrip(SymbolQueryReply reply)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        var parsed = JsonSerializer.Deserialize<SymbolQueryEnvelope>(reply.Json, options);
        Assert.NotNull(parsed);
        Assert.Equal(reply.Envelope.ApiVersion, parsed.ApiVersion);
        Assert.Equal(reply.Envelope.Operation, parsed.Operation);
        Assert.Equal(reply.Envelope.Snapshot, parsed.Snapshot);
        Assert.Equal(reply.Envelope.Items.Select(item => item.Selector), parsed.Items.Select(item => item.Selector));
        Assert.Equal(reply.Envelope.Items.SelectMany(item => item.Relations), parsed.Items.SelectMany(item => item.Relations));
        Assert.Equal(reply.Envelope.Items.SelectMany(item => item.Declarations), parsed.Items.SelectMany(item => item.Declarations));
        Assert.True(reply.Json.Length <= reply.Envelope.Limits.MaxChars);
        Assert.True(Encoding.UTF8.GetByteCount(reply.Json) <= reply.Envelope.Limits.MaxBytes);
        var wire = JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = reply.Json } }, isError = !reply.Succeeded });
        Assert.True(wire.Length <= reply.Envelope.Limits.MaxChars);
        Assert.True(Encoding.UTF8.GetByteCount(wire) <= reply.Envelope.Limits.MaxBytes);
    }

    private sealed class Fixture : IDisposable
    {
        public const string SourcePath = "src/Worker.cs";
        public const string TargetBody = "public int Target() { return Helper() + Remote.Flag + Remote.Logger; }";
        public const string CallerBody = "public int Caller() { return Target(); }";
        public const string HelperBody = "private int Helper() { return Leaf(); }";
        private const string LeafBody = "private int Leaf() { return 1; }";
        private static readonly string FixtureBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sharpdeps-typed-contract-tests"));
        private readonly Guid workspace = Guid.NewGuid();
        public string Root { get; } = Path.Combine(FixtureBase, Guid.NewGuid().ToString("N"));
        public string FullPath => Path.Combine(Root, SourcePath);
        public string Source { get; } = "namespace Demo;\r\nclass Worker\r\n{\r\n" + CallerBody + "\r\n" + TargetBody
            + "\r\n" + HelperBody + "\r\n" + LeafBody + "\r\n}\r\n";
        public string Project => HarnessIdentity.ProjectId(workspace, "src/Demo.csproj");
        public string Variant => HarnessIdentity.VariantId(workspace, Project, "net10.0", "Debug");
        private string Type => Symbol("type", "T:Demo.Worker");
        public string Target => Symbol("method", "M:Demo.Worker.Target");
        public string Caller => Symbol("method", "M:Demo.Worker.Caller");
        public string Helper => Symbol("method", "M:Demo.Worker.Helper");
        public string Leaf => Symbol("method", "M:Demo.Worker.Leaf");
        public string ExternalFlag => HarnessIdentity.ExternalSymbolId(workspace, "external", "property", "P:Remote.Flag");
        public string ExternalLogger => HarnessIdentity.ExternalSymbolId(workspace, "external", "field", "F:Remote.Logger");
        private IndexSnapshot Snapshot { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FullPath)!);
            File.WriteAllText(FullPath, Source, new UTF8Encoding(false));
            var workspaceId = HarnessIdentity.WorkspaceId(workspace);
            var nodes = new List<HarnessNode>
            {
                new(workspaceId, HarnessNodeKind.Workspace, "workspace", null, null),
                new(Project, HarnessNodeKind.Project, "Demo", workspaceId, null),
                new(Type, HarnessNodeKind.Type, "Demo.Worker", Project, null, "T:Demo.Worker"),
                new(ExternalFlag, HarnessNodeKind.ExternalSymbol, "Remote.Flag", null, null),
                new(ExternalLogger, HarnessNodeKind.ExternalSymbol, "Remote.Logger", null, null)
            };
            var typeStart = Source.IndexOf("class Worker", StringComparison.Ordinal);
            var typeLocation = new HarnessLocation(SourcePath, null, Hash(Source), new(typeStart, Source.Length - typeStart - 2));
            var occurrences = new List<HarnessSymbolOccurrence> { new(Occurrence(Type), Type, Variant, typeLocation, [typeLocation]) };
            foreach (var (id, name, body) in new[] { (Caller, "Caller", CallerBody), (Target, "Target", TargetBody), (Helper, "Helper", HelperBody), (Leaf, "Leaf", LeafBody) })
            {
                nodes.Add(new(id, HarnessNodeKind.Member, name, Type, null, "M:Demo.Worker." + name));
                var location = Location(body);
                occurrences.Add(new(Occurrence(id), id, Variant, location, [location]));
            }
            HarnessEdge Edge(string id, string from, string to, string kind, string body, bool external = false)
                => new(id, from, to, Occurrence(from), external ? null : Occurrence(to), Variant, kind,
                    HarnessCertainty.Resolved, "roslyn-operation", Location(body));
            var edges = new[] { Edge("caller-target", Caller, Target, "calls", CallerBody),
                Edge("target-helper", Target, Helper, "calls", TargetBody), Edge("helper-leaf", Helper, Leaf, "calls", HelperBody),
                Edge("target-flag", Target, ExternalFlag, "reads", TargetBody, true),
                Edge("target-logger", Target, ExternalLogger, "reads", TargetBody, true) };
            var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
                HarnessGraphContract.IdentityVersion, workspaceId, "typed-contract-fixture", 1, HarnessCoverage.Partial,
                [new(Variant, Project, "net10.0", "Debug", null, null, Hash("config"))], nodes, occurrences, edges, [],
                new("1.0.0", Hash(""), "parser-1", "contract-1", "profile-1", Hash("options")), []);
            Snapshot = new(graph, [new(SourcePath, SourcePath, "source", Hash(Source), Encoding.UTF8.GetByteCount(Source), Source.Length)], [], [],
                nodes.Select(node => new IndexSearchText(node.Id, node.Name, node.Signature ?? "", "", "", "")).ToArray());
            IndexSnapshotValidator.Validate(Snapshot);
        }

        public SymbolQueryRequest Request(SymbolQueryOperation operation, params string[] selectors)
            => new(operation, Selectors: selectors.Length == 0 ? null : selectors, ProjectId: Project, VariantId: Variant, Budget: Generous);
        public ExplorationService Service(bool trusted = true) => new(new FixedIndex(Snapshot), new QueryWorkspace(Root), trusted);
        public string Occurrence(string logical) => HarnessIdentity.SymbolOccurrenceId(workspace, logical, Variant);
        private string Symbol(string kind, string signature) => HarnessIdentity.LogicalSymbolId(workspace, Project, kind, signature);
        private HarnessLocation Location(string body) => new(SourcePath, null, Hash(Source), new(Source.IndexOf(body, StringComparison.Ordinal), body.Length));
        public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(FixtureBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup escaped its dedicated directory.");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class FixedIndex(IndexSnapshot snapshot) : IQueryIndex
    {
        public IndexSnapshot Snapshot => snapshot;
        public IReadOnlyList<IndexSearchHit> Search(string literal, int limit) => [];
    }
}
