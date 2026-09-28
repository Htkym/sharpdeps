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
        Assert.False(snapshot.Capabilities.TypeGraph);
        Assert.False(snapshot.Capabilities.CycleWitness);
        Assert.True(snapshot.Capabilities.Evidence);
        Assert.Equal(6, snapshot.Coverage.Loaded);
        Assert.Equal(0, snapshot.Coverage.Failed);

        var entityIds = snapshot.Projects.Select(project => project.Id)
            .Concat(snapshot.Namespaces.Select(node => node.Id))
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

        Assert.Contains("quick.typeGraphUnavailable", codes);
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
