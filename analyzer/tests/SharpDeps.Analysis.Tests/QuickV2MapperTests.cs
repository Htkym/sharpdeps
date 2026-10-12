using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Quick;
using Xunit;

namespace SharpDeps.Analysis.Tests;

public sealed class QuickV2MapperTests : IDisposable
{
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly List<string> _temporaryDirectories = [];

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

    private static async Task<QuickV2Mapper.Result> MapFixtureAsync(int maxProjects = 60, int maxEdges = 200)
    {
        var solution = TestPaths.Fixture("quick-baseline", "Baseline.sln");
        var collector = new QuickSourceIndexCollector(
            Identity.WorkspaceRootId(Path.GetDirectoryName(solution)!),
            Path.GetDirectoryName(solution)!);

        var report = await QuickAnalyzer.AnalyzeAsync(solution, maxProjects, maxEdges, collector);
        return QuickV2Mapper.Map(report, collector.Build(), FixedTimestamp);
    }

    [Fact]
    public async Task MapsTheBaselineFixtureToAConsistentSnapshot()
    {
        var result = await MapFixtureAsync();
        var snapshot = result.Snapshot;

        Assert.Equal(2, snapshot.SchemaVersion);
        Assert.Equal("quick", snapshot.Mode);
        Assert.Equal("partial", snapshot.Completeness);
        Assert.True(snapshot.Capabilities.TypeGraph);
        Assert.False(snapshot.Capabilities.CycleWitness);
        Assert.True(snapshot.Capabilities.Evidence);
        Assert.Equal(6, snapshot.Coverage.Loaded);
        Assert.Equal(0, snapshot.Coverage.Failed);

        var entityIds = snapshot.Projects.Select(project => project.Id)
            .Concat(snapshot.Namespaces.Select(node => node.Id))
            .Concat(snapshot.Types.Select(node => node.Id))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(snapshot.Relations);
        Assert.All(snapshot.Relations, relation =>
        {
            Assert.Contains(relation.SourceEntityId, entityIds);
            Assert.Contains(relation.TargetEntityId, entityIds);
            Assert.Equal("inferred", relation.Confidence);
            Assert.True(relation.EvidenceCount >= 1);
        });

        Assert.Equal(2, snapshot.CycleGroups.Count);
        Assert.All(snapshot.CycleGroups, group =>
        {
            Assert.Null(group.Witness);
            Assert.NotEmpty(group.InternalRelationIds);
            Assert.All(group.MemberIds, memberId => Assert.Contains(memberId, entityIds));
            var relationIds = snapshot.Relations.Select(relation => relation.Id).ToHashSet(StringComparer.Ordinal);
            Assert.All(group.InternalRelationIds, relationId => Assert.Contains(relationId, relationIds));
        });

        Assert.NotNull(snapshot.EvidenceIndex);
        var indexed = snapshot.EvidenceIndex!.Relations.Sum(entry => entry.Count);
        var lines = result.EvidenceNdjson.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(indexed, lines.Length);
        Assert.All(
            snapshot.EvidenceIndex.Relations,
            entry => Assert.Contains(snapshot.Relations, relation => relation.Id == entry.RelationId));
    }

    [Fact]
    public async Task IsDeterministicForTheSameInputs()
    {
        var first = await MapFixtureAsync();
        var second = await MapFixtureAsync();

        Assert.Equal(
            first.Snapshot.Relations.Select(relation => relation.Id),
            second.Snapshot.Relations.Select(relation => relation.Id));
        Assert.Equal(
            first.Snapshot.CycleGroups.Select(group => group.Id),
            second.Snapshot.CycleGroups.Select(group => group.Id));
        Assert.Equal(first.EvidenceNdjson, second.EvidenceNdjson);
        Assert.Equal(first.Snapshot.AnalysisId, second.Snapshot.AnalysisId);
        Assert.Equal(first.Snapshot.Types, second.Snapshot.Types);
        Assert.Equal(first.DeclarationsNdjson, second.DeclarationsNdjson);
    }

    [Fact]
    public async Task CycleDetectionDoesNotDependOnDisplayBudgets()
    {
        var full = await MapFixtureAsync(maxProjects: 60, maxEdges: 200);
        var minimal = await MapFixtureAsync(maxProjects: 1, maxEdges: 1);

        Assert.Equal(
            full.Snapshot.CycleGroups.Select(group => group.Id),
            minimal.Snapshot.CycleGroups.Select(group => group.Id));
        Assert.Equal(
            full.Snapshot.Relations.Select(relation => relation.Id),
            minimal.Snapshot.Relations.Select(relation => relation.Id));
    }

    [Fact]
    public async Task RecordsDeclarationEvidenceWithPositionsAndHashes()
    {
        var result = await MapFixtureAsync();
        var records = result.EvidenceNdjson
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Json.JsonSerializer.Deserialize(
                line,
                SharpDeps.Analysis.Contracts.EvidenceJsonContext.Default.EvidenceRecord))
            .Where(record => record is not null)
            .Select(record => record!)
            .ToArray();

        Assert.NotEmpty(records);
        Assert.Equal(records.Length, records.Select(record => record.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(records, record =>
        {
            Assert.Matches("^ev_[0-9a-f]{16}$", record.Id);
            Assert.NotNull(record.PhysicalSpan);
            Assert.True(record.PhysicalSpan!.StartLine >= 0);
            Assert.Matches("^[0-9a-f]{64}$", record.SourceContentHash);
            Assert.Equal("inferred", record.Confidence);
        });

        // The unused `using App.Services;` in the test project is a declaration-only
        // relation: Quick reports it, Semantic must not (FX-01).
        var appServices = result.Snapshot.Namespaces.Single(node => node.Name == "App.Services");
        var coreTests = result.Snapshot.Namespaces.Single(node => node.Name == "Core.Tests");
        var unusedUsingRelation = result.Snapshot.Relations.Single(relation =>
            relation.TargetEntityId == appServices.Id && relation.SourceEntityId == coreTests.Id);
        var unusedUsingEvidence = records.Where(record => record.RelationId == unusedUsingRelation.Id).ToArray();

        Assert.NotEmpty(unusedUsingEvidence);
        Assert.Contains(
            unusedUsingEvidence,
            record => record.PhysicalSpan!.StartLine == 0
                && record.PhysicalSpan.Length == "using App.Services;".Length);
    }

    [Fact]
    public async Task DocumentsTheQuickLimitations()
    {
        var result = await MapFixtureAsync();
        var codes = result.Snapshot.Limitations.Select(limitation => limitation.Code).ToArray();

        Assert.Contains("quick.typeReferenceInferred", codes);
        Assert.Contains("quick.usingInferred", codes);
        Assert.Contains("quick.evidenceIsDeclarationOnly", codes);
        Assert.Contains("quick.witnessUnavailable", codes);
        Assert.Contains("quick.conditionNotEvaluated", codes);
    }

    [Fact]
    public async Task KeepsEveryCandidateWhenANamespaceNameIsAmbiguous()
    {
        var root = CreateTemporaryDirectory();
        Write(root, "Ambiguous.sln", Sln(
            ("Shared", @"Shared\Shared.csproj"),
            ("Other", @"Other\Other.csproj"),
            ("Consumer", @"Consumer\Consumer.csproj")));
        Write(root, @"Shared\Shared.csproj", Project());
        Write(root, @"Shared\Shared.cs", "namespace Shared;\n\npublic sealed class SharedType { }\n");
        Write(root, @"Other\Other.csproj", Project());
        Write(root, @"Other\Other.cs", "namespace Shared;\n\npublic sealed class OtherType { }\n");
        Write(root, @"Consumer\Consumer.csproj", Project());
        Write(
            root,
            @"Consumer\Consumer.cs",
            "using Shared;\n\nnamespace Consumer;\n\npublic sealed class ConsumerType { }\n");

        var solution = Path.Combine(root, "Ambiguous.sln");
        var collector = new QuickSourceIndexCollector(Identity.WorkspaceRootId(root), root);
        var report = await QuickAnalyzer.AnalyzeAsync(solution, 60, 200, collector);
        var snapshot = QuickV2Mapper.Map(report, collector.Build(), FixedTimestamp).Snapshot;

        var consumer = snapshot.Namespaces.Single(node => node.Name == "Consumer");
        var sharedCandidates = snapshot.Namespaces.Where(node => node.Name == "Shared").ToArray();
        Assert.Equal(2, sharedCandidates.Length);

        var relations = snapshot.Relations
            .Where(relation => relation.SourceEntityId == consumer.Id)
            .ToArray();

        Assert.Equal(2, relations.Length);
        Assert.All(relations, relation => Assert.Equal(2, relation.AmbiguousCandidates));
        Assert.All(relations, relation => Assert.Contains(relation.TargetEntityId, sharedCandidates.Select(node => node.Id)));
        Assert.Contains(
            snapshot.Limitations,
            limitation => limitation.Code == "quick.namespaceAmbiguousCandidates" && limitation.Count >= 1);
    }

    [Fact]
    public async Task GroupsSyntaxPartialDeclarationsWithoutMergingArityFileOrConflictedParents()
    {
        var deep = "namespace Depth; " + string.Concat(Enumerable.Range(0, QuickSourceIndexCollector.MaxTypeDepth + 1)
            .Select(index => $"class Deep{index} {{ ")) + new string('}', QuickSourceIndexCollector.MaxTypeDepth + 1);
        var namespacesAtLimit = string.Join('.', Enumerable.Range(0, QuickSourceIndexCollector.MaxNamespaceParts).Select(index => $"N{index}"));
        var overlongReference = string.Join('.', Enumerable.Range(0, QuickSourceIndexCollector.MaxQualifiedParts + 1).Select(index => $"N{index}"));
        var fixture = await MapSourcesAsync(
            ("One/A.cs", "namespace N; partial class Part {} class Box {} class Box<T> {} partial class Outer<T> { public partial class Inner<U> {} } file class Local {} class Same {} class Mixed {} class BadOuter { public partial class Leaf {} }"),
            ("One/B.cs", "namespace N; partial class Part {} partial class Outer<T> { public partial class Inner<U> {} } file class Local {} class Same {} struct Mixed {} class BadOuter { public partial class Leaf {} }"),
            ("One/C.cs", "namespace N { class PlainOuter { class Leaf {} } } namespace N.PlainOuter { class Leaf {} }"),
            ("One/Global.cs", "class GlobalType {}"),
            ("One/Depth.cs", deep),
            ("One/NamespaceLimit.cs", $"namespace {namespacesAtLimit}; class AtNamespaceLimit {{}}"),
            ("One/NamespaceOverLimit.cs", $"namespace {namespacesAtLimit}.Extra; class OmittedNamespaceType {{}}"),
            ("One/ReferenceOverLimit.cs", $"class LongBase : global::{overlongReference} {{}}"));
        var types = fixture.Result.Snapshot.Types;
        Assert.Equal(2, types.Single(type => type.Name == "Part").DeclarationCount);
        Assert.Equal(2, types.Single(type => type.Name == "Inner").DeclarationCount);
        Assert.Equal(2, types.Count(type => type.Name == "Box"));
        Assert.Equal(2, types.Count(type => type.Name == "Local"));
        Assert.Equal(2, types.Count(type => type.Name == "Same"));
        Assert.Equal(2, types.Count(type => type.Name == "Mixed"));
        Assert.Equal(2, types.Count(type => type.FullName == "N.BadOuter.Leaf"));
        Assert.Equal(2, types.Count(type => type.FullName == "N.PlainOuter.Leaf"));
        Assert.Null(types.Single(type => type.Name == "GlobalType").NamespaceId);
        Assert.Equal(types.Count, types.Select(type => type.Id).Distinct().Count());
        Assert.Contains(types, type => type.Name == "Deep31");
        Assert.DoesNotContain(types, type => type.Name is "Deep32" or "OmittedNamespaceType");
        Assert.Contains(types, type => type.Name == "AtNamespaceLimit");
        Assert.Equal(2, fixture.Index.TypeDeclarationsOmitted);
        Assert.All(fixture.Index.TypeDeclarations!, declaration =>
        {
            Assert.InRange(declaration.TypeSegments.Count, 1, QuickSourceIndexCollector.MaxTypeDepth);
            Assert.InRange(declaration.NamespaceSegments.Count, 0, QuickSourceIndexCollector.MaxNamespaceParts);
        });
        Assert.Equal("unsupported", fixture.Index.TypeBaseReferences!.Single().Shape);
        Assert.DoesNotContain(fixture.Result.Snapshot.Relations, relation => relation.Basis == "typeReferenceInferred");
        var part = types.Single(type => type.Name == "Part");
        var records = Declarations(fixture.Result).Where(record => record.TypeId == part.Id).ToArray();
        Assert.Equal(new[] { "One/A.cs", "One/B.cs" }, records.Select(record => record.RelativePath));
        Assert.Equal(new[] { 0, 1 }, records.Select(record => record.DeclarationIndex));
        Assert.All(records, record => Assert.True(record.IsPartial));
        var reversed = QuickV2Mapper.Map(fixture.Report, fixture.Index with
        {
            TypeDeclarations = fixture.Index.TypeDeclarations!.Reverse().ToArray(),
            TypeBaseReferences = fixture.Index.TypeBaseReferences!.Reverse().ToArray()
        }, FixedTimestamp);
        Assert.Equal(fixture.Result.Snapshot.Types, reversed.Snapshot.Types);
        Assert.Equal(fixture.Result.DeclarationsNdjson, reversed.DeclarationsNdjson);
        Assert.Contains(fixture.Result.Snapshot.Limitations, limitation => limitation.Code == "quick.typeDeclarationConflict");

        // Syntax identity normalization must not create a second namespace node.
        var escaped = await MapSourcesAsync(("One/Escaped.cs", "namespace @Escaped . Name { class Base {} class Derived : Base {} }"));
        var legacyNamespace = Assert.Single(escaped.Result.Snapshot.Namespaces);
        Assert.Equal("@Escaped . Name", legacyNamespace.Name);
        Assert.Equal(2, legacyNamespace.TypeCount);
        Assert.Equal(Identity.NamespaceId(legacyNamespace.ProjectVariantId, legacyNamespace.Name), legacyNamespace.Id);
        Assert.All(escaped.Result.Snapshot.Types, type => Assert.Equal(legacyNamespace.Id, type.NamespaceId));
        var reference = Assert.Single(escaped.Result.Snapshot.Relations);
        Assert.Equal("typeReferenceInferred", reference.Basis);
        Assert.Equal("inferred", reference.Confidence);
        Assert.Equal("Derived", escaped.Result.Snapshot.Types.Single(type => type.Id == reference.SourceEntityId).Name);
        Assert.Equal("Base", escaped.Result.Snapshot.Types.Single(type => type.Id == reference.TargetEntityId).Name);
    }

    [Fact]
    public async Task MatchesOnlySupportedSameVariantBaseListsAndWithholdsIncompleteOrAmbiguousInventory()
    {
        var fixture = await MapSourcesAsync(
            ("One/Input.cs", "namespace N; class Base {} class Input : Base {} class Explicit : global::N.Base {} class Missing : Outside {} class Generic<T> : Base {} class Box<T> {} class UsesGeneric : Box<int> {} class Outer { class Nested : Base {} } class Clash {} class UsesClash : Clash {}"),
            ("One/Conflict.cs", "namespace N; class Clash {}"),
            ("One/Alias.cs", "using Alias = N.Base; namespace N; class ViaAlias : Alias {}"),
            ("Other/Base.cs", "namespace N; class Base {} class Outside {}"));
        var snapshot = fixture.Result.Snapshot;
        var relations = snapshot.Relations.Where(relation => relation.Basis == "typeReferenceInferred").ToArray();
        Assert.Equal(2, relations.Length);
        Assert.All(relations, relation =>
        {
            var source = snapshot.Types.Single(type => type.Id == relation.SourceEntityId);
            var target = snapshot.Types.Single(type => type.Id == relation.TargetEntityId);
            Assert.Contains(source.Name, new[] { "Input", "Explicit" });
            Assert.Equal("Base", target.Name);
            Assert.Equal(source.ProjectVariantId, target.ProjectVariantId);
            Assert.Equal("inferred", relation.Confidence);
            Assert.Equal(new[] { "typeReferenceInferred" }, relation.Kinds);
        });
        Assert.Contains(snapshot.Limitations, limitation => limitation.Code == "quick.typeReferenceUnmatched" && limitation.Count == 1);
        Assert.Contains(snapshot.Limitations, limitation => limitation.Code == "quick.typeReferenceAmbiguous" && limitation.Count == 1);
        Assert.Contains(snapshot.Limitations, limitation => limitation.Code == "quick.typeReferenceUnsupported" && limitation.Count == 4);
        Assert.DoesNotContain(snapshot.Relations, relation => relation.Kinds.Contains("calls"));
        var incomplete = QuickV2Mapper.Map(fixture.Report, fixture.Index with { TypeDeclarationsOmitted = 1 }, FixedTimestamp);
        Assert.DoesNotContain(incomplete.Snapshot.Relations, relation => relation.Basis == "typeReferenceInferred");
        Assert.Contains(incomplete.Snapshot.Limitations, limitation => limitation.Code == "quick.typeDeclarationsOmitted" && limitation.Count == 1);
    }

    [Fact]
    public async Task PreservesDecodedDeclarationAndBaseListPositionsAndUtf8DeclarationOffsets()
    {
        const string source = "\uFEFF// 🐈\r\nnamespace N;\r\nclass Base {}\r\nclass Child : Base {}\r\n";
        var fixture = await MapSourcesAsync(("One/実装🐈.cs", source));
        var decoded = source[1..];
        var evidence = fixture.Result.EvidenceNdjson.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Json.JsonSerializer.Deserialize(line, EvidenceJsonContext.Default.EvidenceRecord)!)
            .Single(record => record.Kind == "typeReferenceInferred");
        Assert.Equal(decoded.LastIndexOf("Base", StringComparison.Ordinal), evidence.PhysicalSpan!.Start);
        Assert.Equal(4, evidence.PhysicalSpan.Length);
        Assert.Equal(3, evidence.PhysicalSpan.StartLine);
        Assert.Equal(14, evidence.PhysicalSpan.StartCharacter);
        Assert.Equal(QuickSourceIndexCollector.ComputeContentHash(System.Text.Encoding.UTF8.GetBytes(source)), evidence.SourceContentHash);
        var records = Declarations(fixture.Result);
        var child = fixture.Result.Snapshot.Types.Single(type => type.Name == "Child");
        var declaration = records.Single(record => record.TypeId == child.Id);
        Assert.Equal("class Child : Base {}", decoded.Substring(declaration.Span.Start, declaration.Span.Length));
        var bytes = System.Text.Encoding.UTF8.GetBytes(fixture.Result.DeclarationsNdjson);
        Assert.Equal(bytes.LongLength, fixture.Result.Snapshot.DeclarationIndex!.ByteLength);
        Assert.All(fixture.Result.Snapshot.DeclarationIndex.Types, entry =>
        {
            var suffix = System.Text.Encoding.UTF8.GetString(bytes.AsSpan(checked((int)entry.StartByte)));
            var first = System.Text.Json.JsonSerializer.Deserialize(suffix.Split('\n')[0], EvidenceJsonContext.Default.DeclarationRecord)!;
            Assert.Equal(entry.TypeId, first.TypeId);
            Assert.Equal(records.Count(record => record.TypeId == entry.TypeId), entry.Count);
        });
    }

    private static DeclarationRecord[] Declarations(QuickV2Mapper.Result result)
        => result.DeclarationsNdjson.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Json.JsonSerializer.Deserialize(line, EvidenceJsonContext.Default.DeclarationRecord)!).ToArray();

    private async Task<(QuickV2Mapper.Result Result, CodeMapReport Report, QuickSourceIndex Index)> MapSourcesAsync(
        params (string Path, string Content)[] sources)
    {
        var root = CreateTemporaryDirectory();
        var projects = sources.Select(source => source.Path.Split('/')[0]).Distinct(StringComparer.Ordinal).ToArray();
        Write(root, "Syntax.sln", Sln(projects.Select(name => (name, $"{name}/{name}.csproj")).ToArray()));
        foreach (var project in projects) Write(root, $"{project}/{project}.csproj", Project());
        foreach (var source in sources) Write(root, source.Path, source.Content);
        var collector = new QuickSourceIndexCollector(Identity.WorkspaceRootId(root), root);
        var report = await QuickAnalyzer.AnalyzeAsync(Path.Combine(root, "Syntax.sln"), 60, 200, collector);
        var index = collector.Build();
        return (QuickV2Mapper.Map(report, index, FixedTimestamp), report, index);
    }

    private string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "sharpdeps-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _temporaryDirectories.Add(directory);
        return directory;
    }

    private static void Write(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content.Replace("\\n", "\n", StringComparison.Ordinal));
    }

    private static string Project() => """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>disable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private static string Sln(params (string Name, string Path)[] projects)
    {
        var entries = new System.Text.StringBuilder();
        var configuration = new System.Text.StringBuilder();
        var index = 0;
        foreach (var (name, path) in projects)
        {
            var guid = $"{{9{index:D3}00000-0000-0000-0000-000000000000}}";
            entries.AppendLine(
                $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{name}\", \"{path.Replace('/', '\\')}\", \"{guid}\"");
            entries.AppendLine("EndProject");
            configuration.AppendLine($"\t\t{guid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            index++;
        }

        return $"""
            Microsoft Visual Studio Solution File, Format Version 12.00
            {entries}Global
            	GlobalSection(SolutionConfigurationPlatforms) = preSolution
            		Debug|Any CPU = Debug|Any CPU
            	EndGlobalSection
            	GlobalSection(ProjectConfigurationPlatforms) = postSolution
            {configuration}	EndGlobalSection
            EndGlobal
            """;
    }
}
