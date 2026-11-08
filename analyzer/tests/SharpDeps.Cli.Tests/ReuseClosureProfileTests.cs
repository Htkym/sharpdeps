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
    public async Task StaticDirectoryBuildPropsStayClosedAcrossEditsAndFallBackForConditionalImports()
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
        Write("Directory.Build.props", props);
        Write("Service.cs", source);
        Write("guide.md", "# Guide\n`Demo.Service.Run`\n");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
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
            for (var step = 0; step < 7; step++)
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
                        Write("Directory.Build.props", props.Replace("PROFILE_A", "PROFILE_B"));
                        break;
                    case 3:
                        File.Delete(Path.Combine(root, "Directory.Build.props"));
                        break;
                    case 4:
                        Write("Directory.Build.props", props);
                        break;
                    case 5:
                        Write("Directory.Build.props", props.Replace("<PropertyGroup>", "<PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\">"));
                        break;
                    case 6:
                        Write("Directory.Build.props", "<Project><Import Project=\"Absent.props\" Condition=\"Exists('Absent.props')\" /></Project>");
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
                Assert.Equal(step < 5, incremental.Snapshot.Files.Any(f => f.Reuse is not null));
                if (step == 2)
                {
                    Assert.Contains(incremental.Snapshot.Graph.Nodes, n => n.Name == "Demo.ProfileB");
                    Assert.DoesNotContain(incremental.Snapshot.Graph.Nodes, n => n.Name == "Demo.ProfileA");
                }
                else if (step is 3 or 6)
                    Assert.Contains(incremental.Snapshot.Graph.Nodes, n => n.Name == "Demo.DefaultProfile");
                var differences = FullBuildOracle.Differences(full, incremental);
                Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences.Take(30)) + "\nArtifacts: " + runRoot);
                output.WriteLine($"Step {step}: full oracle matched; semanticReused={Count(incremental, "cli.semanticGraphReused")}; " +
                    $"proof={incremental.Snapshot.Files.Any(f => f.Reuse is not null)}.");
                previous = incremental;
            }
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
