namespace SharpDeps.Cli.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Index;
using SharpDeps.Query;
using Xunit;

[Collection("markdown-index")]
public sealed class ExplorationPublicationTests
{
    private static readonly QueryBudget Budget = new(MaxMilliseconds: 10000, MaxChars: 100000, MaxBytes: 400000);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExtensionBindingSurvivesPublicationAsMetadataOnTheSameCall()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.SourcePath, """
            using System.Collections.Generic;
            public static class Extensions
            {
                public static R Convert<T, R>(this IEnumerable<T> items, R result) => result;
            }
            public class Service
            {
                public string Run(List<int> items) => items.Convert("saved"); // 😀
            }
            """, Fixture.Utf8);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await fixture.Restore(deadline.Token);
        var built = await CliIndexer.BuildForPublicationAsync(fixture.Options, null, deadline.Token);
        using var writer = IndexWriter.Open(fixture.Root, fixture.Options.IndexPath, fixture.Options.WorkspaceUuid, true);
        writer.Commit(IndexStage.Create(built.Snapshot), deadline.Token, built.VerifySelection);
        using var reader = fixture.Open();
        var saved = reader.Snapshot();
        var edge = Assert.Single(saved.Graph.Edges, edge => edge.ExtensionBinding is not null);
        Assert.Equal("calls", edge.Kind);
        Assert.Equal(Assert.Single(built.Snapshot.Graph.Edges, before => before.Id == edge.Id), edge);
        Assert.Equal(HarnessExtensionBindingState.Saved, edge.ExtensionBinding!.State);
        Assert.Contains("List<int>", edge.ExtensionBinding.ReceiverTypeDisplay!, StringComparison.Ordinal);
        Assert.Contains("Convert<int, string>", edge.ExtensionBinding.ConstructedMethodDisplay!, StringComparison.Ordinal);
        var source = saved.Files.Single(file => file.SourceId == edge.Evidence!.SourceId);
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Root, source.RelativePath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), edge.Evidence!.ContentHash);
        Assert.Equal("items.Convert(\"saved\")", Fixture.Utf8.GetString(bytes).Substring(edge.Evidence.RawSpan!.Value.Start, edge.Evidence.RawSpan.Value.Length));
        foreach (var format in new[] { ExploreFormat.Audit, ExploreFormat.Agent })
        {
            var reply = fixture.Engine(reader).Execute(new(edge.SourceOccurrenceId!, Format: format, Budget: Budget));
            Assert.True(reply.Succeeded);
            Assert.Contains("extension-binding=Saved", reply.Text);
            Assert.Contains("compile-time labels only", reply.Text);
            Assert.Contains("List<int>", reply.Text.Replace("\\u003C", "<", StringComparison.Ordinal).Replace("\\u003E", ">", StringComparison.Ordinal));
        }
        var callees = new QueryService(new PinnedQueryIndex(reader)).Execute(new("callees", QueryKind.Callees,
            NodeId: edge.SourceNodeId, Scope: new(VariantIds: [edge.VariantId!]), Budget: Budget));
        Assert.Contains(callees.Envelope.Items, item => item.Edge?.Id == edge.Id && item.Edge.ExtensionBinding == edge.ExtensionBinding);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScopedRegistrationRolesSurvivePublicationWithoutBecomingCalls()
    {
        using var fixture = new Fixture();
        var dependency = System.Security.SecurityElement.Escape(
            typeof(Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions).Assembly.Location);
        File.WriteAllText(Path.Combine(fixture.Root, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
            + "<ItemGroup><Reference Include=\"Microsoft.Extensions.DependencyInjection.Abstractions\"><HintPath>"
            + dependency + "</HintPath></Reference></ItemGroup></Project>", Fixture.Utf8);
        const string site = "services.AddScoped<IWork, Worker>()";
        File.WriteAllText(fixture.SourcePath, """
            using Microsoft.Extensions.DependencyInjection;
            public interface IWork { }
            public sealed class Worker : IWork { }
            public sealed class Startup
            {
                public void Configure(IServiceCollection services) { services.AddScoped<IWork, Worker>(); } // 😀
            }
            """, Fixture.Utf8);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await fixture.Restore(deadline.Token);
        var built = await CliIndexer.BuildForPublicationAsync(fixture.Options, null, deadline.Token);
        using var writer = IndexWriter.Open(fixture.Root, fixture.Options.IndexPath, fixture.Options.WorkspaceUuid, true);
        writer.Commit(IndexStage.Create(built.Snapshot), deadline.Token, built.VerifySelection);
        using var reader = fixture.Open();
        var saved = reader.Snapshot();
        var roles = saved.Graph.Edges.Where(edge => edge.Producer == "roslyn-di-registration").ToArray();
        Assert.Equal(2, roles.Length);
        foreach (var edge in roles)
        {
            Assert.Equal(Assert.Single(built.Snapshot.Graph.Edges, before => before.Id == edge.Id), edge);
            Assert.Equal(HarnessCertainty.Resolved, edge.Certainty);
            var file = Assert.Single(saved.Files, file => file.SourceId == edge.Evidence!.SourceId);
            var bytes = File.ReadAllBytes(Path.Combine(fixture.Root, file.RelativePath));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), edge.Evidence!.ContentHash);
            var span = edge.Evidence.RawSpan!.Value;
            Assert.Equal(site, Fixture.Utf8.GetString(bytes).Substring(span.Start, span.Length));
            Assert.Equal(edge.VariantId, saved.Graph.SymbolOccurrences.Single(o => o.Id == edge.TargetOccurrenceId).VariantId);
            var callers = new QueryService(new PinnedQueryIndex(reader)).Execute(new("callers", QueryKind.Callers,
                NodeId: edge.TargetNodeId, Scope: new(VariantIds: [edge.VariantId!]), Budget: Budget));
            Assert.DoesNotContain(callers.Envelope.Items, item => item.Edge?.Producer == "roslyn-di-registration");
        }
        foreach (var format in new[] { ExploreFormat.Audit, ExploreFormat.Agent })
        {
            var reply = fixture.Engine(reader).Execute(new(roles[0].SourceOccurrenceId!, Format: format, Budget: Budget));
            Assert.True(reply.Succeeded);
            Assert.Contains("registers_scoped_service", reply.Text);
            Assert.Contains("registers_scoped_implementation", reply.Text);
            Assert.All(reply.Units, unit => Assert.Contains(roles[0].SourceNodeId, unit.SymbolIds));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DeclarationMemberFactsSurvivePublicationAndRemainSeparateFromCalls()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.SourcePath, """
            namespace Demo;
            public interface IWork { int Run(); }
            public abstract class Base { public abstract int Run(); }
            public sealed class Cell : Base, IWork
            {
                public override int Run() => 1; // 😀
                int IWork.Run() => 2;
            }
            """, Fixture.Utf8);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await fixture.Restore(deadline.Token);
        var built = await CliIndexer.BuildForPublicationAsync(fixture.Options, null, deadline.Token);
        using var writer = IndexWriter.Open(fixture.Root, fixture.Options.IndexPath, fixture.Options.WorkspaceUuid, true);
        writer.Commit(IndexStage.Create(built.Snapshot), deadline.Token, built.VerifySelection);
        using var reader = fixture.Open();
        var saved = reader.Snapshot();
        var overridden = Assert.Single(saved.Graph.Edges, edge => edge.Kind == "overrides");
        var implemented = Assert.Single(saved.Graph.Edges, edge => edge.Kind == "implements_explicitly");
        foreach (var edge in new[] { overridden, implemented })
        {
            Assert.Equal(Assert.Single(built.Snapshot.Graph.Edges, before => before.Id == edge.Id), edge);
            Assert.Equal(HarnessCertainty.Resolved, edge.Certainty);
            Assert.Equal("roslyn-declaration", edge.Producer);
            Assert.Equal("userSource", edge.Origin);
            Assert.Equal(edge.VariantId, Assert.Single(saved.Graph.SymbolOccurrences,
                occurrence => occurrence.Id == edge.SourceOccurrenceId).VariantId);
            Assert.Equal(edge.VariantId, Assert.Single(saved.Graph.SymbolOccurrences,
                occurrence => occurrence.Id == edge.TargetOccurrenceId).VariantId);
            var source = Assert.Single(saved.Files, file => file.SourceId == edge.Evidence!.SourceId);
            var bytes = File.ReadAllBytes(Path.Combine(fixture.Root, source.RelativePath));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), edge.Evidence!.ContentHash);
            var span = edge.Evidence.RawSpan!.Value;
            Assert.Equal(edge.Kind == "overrides" ? "Run" : "IWork.",
                Fixture.Utf8.GetString(bytes).Substring(span.Start, span.Length));
            Assert.Equal(HarnessNodeKind.Member, Assert.Single(saved.Graph.Nodes, node => node.Id == edge.TargetNodeId).Kind);
        }
        var pinned = new PinnedQueryIndex(reader);
        var query = new QueryService(pinned);
        var scope = new QueryScope(VariantIds: [overridden.VariantId!]);
        var context = query.Execute(new("declaration", QueryKind.Context, NodeId: overridden.SourceNodeId,
            Scope: scope, EdgeKinds: ["overrides"], Budget: Budget));
        Assert.Contains(context.Envelope.Items, item => item.Edge?.Id == overridden.Id);
        var callers = query.Execute(new("callers", QueryKind.Callers, NodeId: overridden.TargetNodeId,
            Scope: scope, Budget: Budget));
        Assert.DoesNotContain(callers.Envelope.Items, item => item.Edge?.Kind is "overrides" or "implements_explicitly");

        var reply = fixture.Engine(reader).Execute(new(overridden.SourceOccurrenceId!, Budget: Budget));
        Assert.True(reply.Succeeded);
        Assert.Contains("overrides", reply.Text);
        Assert.All(reply.Units, unit => Assert.Contains(overridden.SourceNodeId, unit.SymbolIds));
        Assert.NotEmpty(reply.Units);
        var output = new StringWriter();
        var exit = await CliApplication.RunAsync(["explore", "--root", fixture.Root, "--trusted",
            "--term", overridden.SourceOccurrenceId!, "--format", "audit", "--max-milliseconds", "10000",
            "--max-chars", "100000", "--max-bytes", "400000"], output, TextWriter.Null, deadline.Token);
        Assert.Equal(0, exit);
        Assert.Equal(reply.Text.TrimEnd('\r', '\n'), output.ToString().TrimEnd('\r', '\n'));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RoslynPublicationAndSavedSearchFollowTypedStateThroughEditRenameAndDelete()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await fixture.Restore(deadline.Token);
        using var writer = IndexWriter.Open(fixture.Root, fixture.Options.IndexPath, fixture.Options.WorkspaceUuid, true);
        var initial = await Publish(null);
        using var originalPin = fixture.Open();
        var original = originalPin.Snapshot();
        var count = Member(original, "count");
        var changed = Member(original, "Changed");
        var retired = Member(original, "Retired");
        var originalEngine = fixture.Engine(originalPin);
        AssertRange(originalEngine.Execute(new(count, Budget: Budget)), count, Fixture.OriginalField, Fixture.OriginalPath);
        AssertRange(originalEngine.Execute(new(changed, Budget: Budget)), changed, Fixture.OriginalEvent, Fixture.OriginalPath);

        Assert.DoesNotContain(original.Graph.Nodes.Where(node => node.Kind is HarnessNodeKind.Type or HarnessNodeKind.Member),
            node => node.Name.Contains(Fixture.SearchTerm, StringComparison.OrdinalIgnoreCase)
                || (node.Signature?.Contains(Fixture.SearchTerm, StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.Contains(originalPin.Search(Fixture.SearchTerm), hit => hit.NodeId == count);
        var searched = originalEngine.Execute(new(Fixture.SearchTerm, Budget: Budget));
        Assert.True(searched.Succeeded);
        Assert.Contains(searched.Units, unit => unit.Path == Fixture.OriginalPath);
        AssertVerifiedRanges(searched);

        fixture.Write(Fixture.EditedField, Fixture.EditedEvent, "Updated");
        var edited = await Publish(initial);
        using var editedPin = fixture.Open();
        var editedSnapshot = editedPin.Snapshot();
        Assert.Equal(count, Member(editedSnapshot, "count"));
        Assert.DoesNotContain(editedSnapshot.Graph.Nodes, node => node.Id == changed || node.Id == retired);
        var updated = Member(editedSnapshot, "Updated");
        var editedEngine = fixture.Engine(editedPin);
        AssertRange(editedEngine.Execute(new(count, Budget: Budget)), count, Fixture.EditedField, Fixture.OriginalPath);
        AssertRange(editedEngine.Execute(new(updated, Budget: Budget)), updated, Fixture.EditedEvent, Fixture.OriginalPath);
        Assert.DoesNotContain(originalEngine.Execute(new(count, Budget: Budget)).Units, unit => unit.Path == Fixture.OriginalPath);

        File.Move(fixture.SourcePath, Path.Combine(fixture.Root, Fixture.RenamedPath));
        var renamed = await Publish(edited);
        using var renamedPin = fixture.Open();
        var renamedSnapshot = renamedPin.Snapshot();
        Assert.DoesNotContain(renamedSnapshot.Files, file => !file.Deleted && file.RelativePath == Fixture.OriginalPath);
        Assert.Equal(count, Member(renamedSnapshot, "count"));
        var renamedEngine = fixture.Engine(renamedPin);
        AssertRange(renamedEngine.Execute(new(count, Budget: Budget)), count, Fixture.EditedField, Fixture.RenamedPath);
        AssertRange(renamedEngine.Execute(new(updated, Budget: Budget)), updated, Fixture.EditedEvent, Fixture.RenamedPath);
        Assert.DoesNotContain(renamedPin.Search(Fixture.SearchTerm), hit => hit.NodeId == count || hit.NodeId == updated);
        Assert.DoesNotContain(editedEngine.Execute(new(count, Budget: Budget)).Units, unit => unit.Path == Fixture.OriginalPath);

        File.Delete(Path.Combine(fixture.Root, Fixture.RenamedPath));
        var deleted = await Publish(renamed);
        using var deletedPin = fixture.Open();
        Assert.DoesNotContain(deletedPin.Snapshot().Files, file => !file.Deleted
            && file.RelativePath is Fixture.OriginalPath or Fixture.RenamedPath);
        Assert.DoesNotContain(deletedPin.Snapshot().Graph.Nodes, node => node.Id == count || node.Id == updated);
        var missing = fixture.Engine(deletedPin).Execute(new(count, Budget: Budget));
        Assert.Empty(missing.Units);
        Assert.Contains(missing.Errors, error => error.Code == "SYMBOL_NOT_FOUND");
        Assert.DoesNotContain(renamedEngine.Execute(new(count, Budget: Budget)).Units, unit => unit.Path == Fixture.RenamedPath);
        Assert.Equal(1, originalPin.Snapshot().Graph.Generation);
        Assert.Equal(2, editedPin.Snapshot().Graph.Generation);
        Assert.Equal(3, renamedPin.Snapshot().Graph.Generation);
        Assert.Equal(4, deleted.Graph.Generation);

        async Task<IndexSnapshot> Publish(IndexSnapshot? previous)
        {
            var build = await CliIndexer.BuildForPublicationAsync(fixture.Options, previous, deadline.Token);
            Assert.Equal(previous is null ? 1 : previous.Graph.Generation + 1, build.Snapshot.Graph.Generation);
            Assert.Equal(HarnessCoverage.Partial, build.Snapshot.Graph.Coverage);
            writer.Commit(IndexStage.Create(build.Snapshot), deadline.Token, build.VerifySelection);
            return build.Snapshot;
        }

        void AssertRange(ExploreReply reply, string id, string expected, string path)
        {
            Assert.True(reply.Succeeded);
            var unit = Assert.Single(reply.Units, unit => unit.SymbolIds.Contains(id));
            Assert.Equal(path, unit.Path);
            Assert.Equal(expected, unit.Text);
            Assert.True(unit.CompleteSavedRange);
            AssertVerifiedRanges(reply);
        }

        void AssertVerifiedRanges(ExploreReply reply)
        {
            Assert.All(reply.Units, unit =>
            {
                var bytes = File.ReadAllBytes(Path.Combine(fixture.Root, unit.Path));
                Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), unit.ContentHash);
                Assert.Equal(Fixture.Utf8.GetString(bytes).Substring(unit.Span.Start, unit.Span.Length), unit.Text);
            });
        }
    }

    private static string Member(IndexSnapshot snapshot, string name)
        => Assert.Single(snapshot.Graph.Nodes, node => node.Kind == HarnessNodeKind.Member && node.Name == name).Id;

    private sealed class Fixture : IDisposable
    {
        public static readonly UTF8Encoding Utf8 = new(false, true);
        private static readonly string Base = OperatingSystem.IsMacOS()
            ? "/private/tmp/sharpdeps-exploration-publication" : Path.Combine(Path.GetTempPath(), "sharpdeps-exploration-publication");
        public const string SearchTerm = "DiscoveryEvidence", OriginalPath = SearchTerm + ".cs", RenamedPath = "MovedEvidence.cs";
        public const string OriginalField = "public int count = 1, total = 2;";
        public const string EditedField = "public int count = 7, total = 8;";
        public const string OriginalEvent = "public event System.Action Changed, Retired;";
        public const string EditedEvent = "public event System.Action Updated;";
        public string Root { get; } = Path.Combine(Base, Guid.NewGuid().ToString("N"));
        public string SourcePath => Path.Combine(Root, OriginalPath);
        private string RestoreConfig => Root + ".restore.config";
        public CliIndexOptions Options { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", Utf8);
            File.WriteAllText(Path.Combine(Root, "Anchor.cs"), "public sealed class Anchor { }", Utf8);
            File.WriteAllText(RestoreConfig, "<configuration><packageSources><clear /></packageSources></configuration>", Utf8);
            Write(OriginalField, OriginalEvent, "Changed");
            var state = CliWorkspaceState.Open(Root, create: true);
            Options = new(Root, Path.Combine(Root, "App.csproj"), Path.Combine(Root, ".sharpdeps", "index.sqlite"),
                state.WorkspaceUuid, AllowSemanticReuse: true);
        }

        public void Write(string field, string declaredEvent, string eventName)
            => File.WriteAllText(SourcePath, "namespace Demo;\r\npublic sealed class Cell\r\n{\r\n"
                + field + "\r\n" + declaredEvent + "\r\npublic void Raise() => " + eventName + "?.Invoke();\r\n}\r\n", Utf8);

        public IndexReader Open() => IndexReader.Open(Options.IndexPath, Options.WorkspaceUuid);
        public ExplorationService Engine(IndexReader reader) => new(new PinnedQueryIndex(reader), new QueryWorkspace(Root), true);

        public async Task Restore(CancellationToken token)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            foreach (var argument in new[] { "restore", Options.Target, "--configfile", RestoreConfig, "--verbosity", "quiet" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Offline fixture restore did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(token); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.True(process.ExitCode == 0, await output + await errors);
        }

        public void Dispose()
        {
            if (Path.GetDirectoryName(Path.GetFullPath(Root)) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Base))
                || Path.GetDirectoryName(Path.GetFullPath(RestoreConfig)) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Base)))
                throw new InvalidOperationException("Fixture cleanup escaped its temporary directory.");
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            File.Delete(RestoreConfig);
        }
    }
}
