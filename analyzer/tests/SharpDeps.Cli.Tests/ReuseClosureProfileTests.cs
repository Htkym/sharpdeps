namespace SharpDeps.Cli.Tests;

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SharpDeps.Index;
using Xunit;
using Xunit.Abstractions;

[Collection("markdown-index")]
public sealed class ReuseClosureProfileTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiteralDirectoryBuildImportsStayClosedAcrossEditsAndFallBackForUnsupportedImports()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "sharpdeps-profile-oracle", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(runRoot, "workspace");
        Directory.CreateDirectory(root);
        var sdkRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "sdk"));
        var sdk = Directory.EnumerateDirectories(sdkRoot).Select(Path.GetFileName)
            .Where(s => s is not null && s.StartsWith("10.", StringComparison.Ordinal) && Version.TryParse(s, out _))
            .OrderByDescending(s => Version.Parse(s!)).First()
            ?? throw new InvalidOperationException("An installed .NET 10 SDK is required by the reuse profile.");
        Write("global.json", JsonSerializer.Serialize(new { sdk = new { version = sdk, rollForward = "disable" } }));
        File.WriteAllText(Path.Combine(runRoot, "restore.config"), "<configuration><packageSources><clear /></packageSources></configuration>");
        Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFramework>net10.0</TargetFramework><EnableNETAnalyzers>false</EnableNETAnalyzers>" +
            "</PropertyGroup></Project>");
        const string source = "namespace Demo; public class Service { public int Run() => 1; }\n" +
            "#if PROFILE_A\npublic class ProfileA { }\n#elif PROFILE_B\npublic class ProfileB { }\n#else\npublic class DefaultProfile { }\n#endif\n";
        const string props = "<Project><PropertyGroup><Nullable>enable</Nullable><LangVersion>latest</LangVersion>" +
            "<DefineConstants>PROFILE_A</DefineConstants></PropertyGroup></Project>";
        const string import = "<Project><Import Project=\"build/Compiler.props\" /></Project>";
        Write("Directory.Build.props", import);
        Write("build/Compiler.props", props);
        Write("Service.cs", source);
        Write("guide.md", "# Guide\n`Demo.Service.Run`\n");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            await OracleProcess.ExecuteAsync(root, ["restore", "App.csproj", "--configfile", Path.Combine(runRoot, "restore.config"), "--verbosity", "quiet"],
                deadline.Token, packageRoot: Path.Combine(runRoot, "packages"));
            File.Delete(Path.Combine(root, "global.json"));
            var options = new CliIndexOptions(root, Path.Combine(root, "App.csproj"),
                Path.Combine(root, ".sharpdeps", "baseline.sqlite"), Guid.Parse("21111111-2222-4333-8444-555555555555"));
            string[] terms = ["Service", "ProfileA", "ProfileB", "DefaultProfile", "Guide"];
            var previous = await OracleProcess.RunAsync(new(options, null, terms), runRoot, "baseline", deadline.Token);
            Assert.Single(previous.Snapshot.Files, f => f.Reuse is not null);
            Assert.Contains(previous.Snapshot.Graph.Nodes, n => n.Name == "Demo.ProfileA");
            for (var step = 0; step < 9; step++)
            {
                switch (step)
                {
                    case 0:
                        Write("guide.md", "# Guide\n`Demo.Service.Run` edited.\n");
                        break;
                    case 1:
                        Write("Service.cs", source.Replace("Run()", "Execute()"));
                        Write("guide.md", "# Guide\n`Demo.Service.Execute` edited together.\n");
                        break;
                    case 2:
                        var path = Path.Combine(root, "build", "Compiler.props");
                        var timestamp = File.GetLastWriteTimeUtc(path);
                        Write("build/Compiler.props", props.Replace("PROFILE_A", "PROFILE_B"));
                        File.SetLastWriteTimeUtc(path, timestamp);
                        break;
                    case 3:
                        Write("Directory.Build.props", "<Project />");
                        File.Delete(Path.Combine(root, "build", "Compiler.props"));
                        break;
                    case 4:
                        Write("Directory.Build.props", import);
                        Write("build/Compiler.props", props);
                        break;
                    case 5:
                        Write("build/Compiler.props", props.Replace("<PropertyGroup>", "<PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\">"));
                        break;
                    case 6:
                        Write("build/Nested.props", props);
                        Write("build/Compiler.props", "<Project><Import Project=\"Nested.props\" /></Project>");
                        break;
                    case 7:
                        File.Delete(Path.Combine(root, "build", "Nested.props"));
                        File.Delete(Path.Combine(root, "build", "Compiler.props"));
                        File.WriteAllText(Path.Combine(runRoot, "External.props"), props);
                        Write("Directory.Build.props", new System.Xml.Linq.XElement("Project",
                            new System.Xml.Linq.XElement("Import", new System.Xml.Linq.XAttribute("Project", Path.Combine(runRoot, "External.props")))).ToString());
                        break;
                    case 8:
                        Write("Directory.Build.props", import);
                        Write("build/Compiler.props", props);
                        break;
                }
                var update = options with { IndexPath = Path.Combine(root, ".sharpdeps", $"update-{step}.sqlite"), AllowSemanticReuse = true };
                var incremental = await OracleProcess.RunAsync(new(update, previous.Snapshot, terms), runRoot, $"update-{step}", deadline.Token);
                var full = await OracleProcess.RunAsync(new(update with
                {
                    IndexPath = Path.Combine(root, ".sharpdeps", $"full-{step}.sqlite"),
                    AllowSemanticReuse = false
                }, previous.Snapshot, terms), runRoot, $"full-{step}", deadline.Token);
                Assert.NotEqual(incremental.ProcessId, full.ProcessId);
                Assert.Equal(step == 0 ? 1 : 0, Count(incremental, "cli.semanticGraphReused"));
                Assert.Equal(step == 0 ? 0 : 1, Count(incremental, "cli.projectCollectorsExtracted"));
                Assert.Equal(1, Count(full, "cli.projectCollectorsExtracted"));
                Assert.Equal(0, Count(full, "cli.semanticGraphReused"));
                Assert.Equal(step < 5 || step == 8, incremental.Snapshot.Files.Any(f => f.Reuse is not null));
                if (step == 2)
                {
                    Assert.Contains(incremental.Snapshot.Graph.Nodes, n => n.Name == "Demo.ProfileB");
                    Assert.DoesNotContain(incremental.Snapshot.Graph.Nodes, n => n.Name == "Demo.ProfileA");
                }
                else if (step == 3)
                    Assert.Contains(incremental.Snapshot.Graph.Nodes, n => n.Name == "Demo.DefaultProfile");
                var differences = FullBuildOracle.Differences(full, incremental);
                Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences.Take(30)) + "\nArtifacts: " + runRoot);
                output.WriteLine($"Step {step}: full oracle matched; semanticReused={Count(incremental, "cli.semanticGraphReused")}; " +
                    $"proof={incremental.Snapshot.Files.Any(f => f.Reuse is not null)}.");
                previous = incremental;
            }
            var rollback = options with { IndexPath = Path.Combine(root, ".sharpdeps", "update-8.sqlite"), AllowSemanticReuse = true };
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => OracleProcess.RunAsync(new(rollback, previous.Snapshot, terms)
                {
                    BeforeCommit = new("build/Compiler.props", props.Replace("PROFILE_A", "PROFILE_B"))
                }, runRoot, "publication-race", deadline.Token));
                using var failure = JsonDocument.Parse(File.ReadAllText(Path.Combine(runRoot, "publication-race.result.failure.json")));
                Assert.Equal("SOURCE_CHANGED_SINCE_STAGING", failure.RootElement.GetProperty("code").GetString());
                Assert.Equal(typeof(IndexStoreException).FullName, failure.RootElement.GetProperty("exceptionType").GetString());
                using var reader = IndexReader.Open(rollback.IndexPath, rollback.WorkspaceUuid);
                Assert.Equal(previous.Snapshot.Graph.Generation, reader.Snapshot().Graph.Generation);
                Assert.Equal(previous.Snapshot.Graph.SnapshotId, reader.Snapshot().Graph.SnapshotId);
            }
            finally
            {
                Write("build/Compiler.props", props);
            }
            Write("Directory.Build.props", "<Project><Import Project=\"Missing.props\" /></Project>");
            var missingOptions = rollback with { IndexPath = Path.Combine(root, ".sharpdeps", "missing-update.sqlite") };
            var missingUpdate = await OracleProcess.RunAsync(new(missingOptions, previous.Snapshot, terms), runRoot, "missing-update", deadline.Token);
            var missingFull = await OracleProcess.RunAsync(new(missingOptions with
            { IndexPath = Path.Combine(root, ".sharpdeps", "missing-full.sqlite"), AllowSemanticReuse = false }, previous.Snapshot, terms),
                runRoot, "missing-full", deadline.Token);
            Assert.NotEqual(missingUpdate.ProcessId, missingFull.ProcessId);
            foreach (var missing in new[] { missingUpdate, missingFull })
            {
                Assert.DoesNotContain(missing.Snapshot.Files, f => f.Reuse is not null);
                Assert.Equal(0, Count(missing, "cli.semanticGraphReused"));
                Assert.Equal(1, Count(missing, "cli.projectCollectorsExtracted"));
                Assert.Contains(missing.Snapshot.Graph.Nodes, n => n.Name == "Demo.DefaultProfile");
            }
            var missingDifferences = FullBuildOracle.Differences(missingFull, missingUpdate);
            Assert.True(missingDifferences.Count == 0, string.Join(Environment.NewLine, missingDifferences.Take(30)));
            Write("Directory.Build.props", import);
            using (var reader = IndexReader.Open(rollback.IndexPath, rollback.WorkspaceUuid))
                Assert.Equal(previous.Snapshot.Graph.SnapshotId, reader.Snapshot().Graph.SnapshotId);
            var recovered = await OracleProcess.RunAsync(new(rollback, previous.Snapshot, terms), runRoot, "recovered", deadline.Token);
            var recoveredFull = await OracleProcess.RunAsync(new(rollback with
            { IndexPath = Path.Combine(root, ".sharpdeps", "recovered-full.sqlite"), AllowSemanticReuse = false }, previous.Snapshot, terms),
                runRoot, "recovered-full", deadline.Token);
            Assert.Equal(1, Count(recovered, "cli.semanticGraphReused"));
            var recoveryDifferences = FullBuildOracle.Differences(recoveredFull, recovered);
            Assert.True(recoveryDifferences.Count == 0, string.Join(Environment.NewLine, recoveryDifferences.Take(30)));
            output.WriteLine("Imported property mutation was rejected at publication with the committed generation intact; missing imports stayed cold and recovery matched full.");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("Profile oracle artifacts: " + runRoot, error);
        }
        output.WriteLine("Profile oracle artifacts: " + runRoot);

        void Write(string relative, string text)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false, true));
        }
    }

    private static int Count(OracleObservation observation, string code)
        => observation.Snapshot.Graph.Diagnostics?.SingleOrDefault(d => d.Code == code)?.Count ?? 0;
}
