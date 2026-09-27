using SharpDeps.Analysis.Quick;
using Xunit;

namespace SharpDeps.Analysis.Tests;

public sealed class SolutionDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sharpdeps-discovery-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Write(string relativePath, string content)
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    private static string ProjectXml(string? extra = null)
        => $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              {extra}
            </Project>
            """;

    [Fact]
    public async Task ParsesSlnProjectsAndSolutionFolders()
    {
        var solution = Write(
            "Sample.sln",
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{AAAA0000-0000-0000-0000-000000000000}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "A", "src\A\A.csproj", "{11110000-0000-0000-0000-000000000000}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "B", "B.csproj", "{22220000-0000-0000-0000-000000000000}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Ignored", "Ignored.txt", "{33330000-0000-0000-0000-000000000000}"
            EndProject
            Global
            	GlobalSection(NestedProjects) = preSolution
            		{11110000-0000-0000-0000-000000000000} = {AAAA0000-0000-0000-0000-000000000000}
            	EndGlobalSection
            EndGlobal
            """);
        Write("src/A/A.csproj", ProjectXml());
        Write("B.csproj", ProjectXml());

        var parsed = await SolutionDiscovery.ParseSlnAsync(solution);

        Assert.Equal("Sample", parsed.SolutionName);
        Assert.Equal(2, parsed.Projects.Count);

        var projectA = parsed.Projects.Single(project => project.Name == "A");
        Assert.Equal("src", projectA.GroupPath);
        Assert.Equal(Path.Combine("src", "A", "A.csproj"), projectA.RelativePath);

        var projectB = parsed.Projects.Single(project => project.Name == "B");
        Assert.Equal("(solution root)", projectB.GroupPath);
    }

    [Fact]
    public async Task ParsesSlnxWithFolders()
    {
        var solution = Write(
            "Sample.slnx",
            """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/A/A.csproj" />
              </Folder>
              <Project Path="B.csproj" />
            </Solution>
            """);
        Write("src/A/A.csproj", ProjectXml());
        Write("B.csproj", ProjectXml());

        var parsed = await SolutionDiscovery.ParseSlnxAsync(solution);

        Assert.Equal(2, parsed.Projects.Count);
        // .slnx folders are path-like ("/src/"), so Quick currently reports the folder
        // name verbatim. Normalizing it is part of the SD-006 Quick model work; this
        // assertion pins today's behavior so the change is deliberate.
        Assert.Equal("/src/", parsed.Projects.Single(project => project.Name == "A").GroupPath);
    }

    [Fact]
    public async Task FollowsTransitiveProjectReferencesForAProjectScope()
    {
        var root = Write(
            "Root/Root.csproj",
            ProjectXml("""<ItemGroup><ProjectReference Include="..\Middle\Middle.csproj" /></ItemGroup>"""));
        Write(
            "Middle/Middle.csproj",
            ProjectXml("""<ItemGroup><ProjectReference Include="..\Leaf\Leaf.csproj" /></ItemGroup>"""));
        Write("Leaf/Leaf.csproj", ProjectXml());

        var parsed = await SolutionDiscovery.ParseProjectClosureAsync(root, 40, []);

        Assert.Equal(3, parsed.Projects.Count);
        Assert.EndsWith("(project scope)", parsed.SolutionName, StringComparison.Ordinal);
        Assert.Contains(parsed.Projects, project => project.Name == "Leaf");
    }

    [Fact]
    public async Task WarnsAboutMissingReferencesAndTraversalCaps()
    {
        var root = Write(
            "Root/Root.csproj",
            ProjectXml(
                """
                <ItemGroup>
                  <ProjectReference Include="..\Missing\Missing.csproj" />
                  <ProjectReference Include="..\Second\Second.csproj" />
                  <ProjectReference Include="..\Third\Third.csproj" />
                </ItemGroup>
                """));
        Write("Second/Second.csproj", ProjectXml());
        Write("Third/Third.csproj", ProjectXml());

        var warnings = new List<string>();
        var parsed = await SolutionDiscovery.ParseProjectClosureAsync(root, 2, warnings);

        Assert.Equal(2, parsed.Projects.Count);
        Assert.Contains(warnings, warning => warning.Contains("was not found", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("safety limit of 2 project", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadsPackageReferencesAndConditionsFromTheDeclaredXml()
    {
        var project = Write(
            "A/A.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net10.0;net8.0</TargetFrameworks>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <ProjectReference Include="..\B\B.csproj" Condition="'$(Configuration)' == 'Release'" />
              </ItemGroup>
            </Project>
            """);
        Write("B/B.csproj", ProjectXml());

        var entry = new SharpDeps.Analysis.Contracts.SolutionProjectEntry(
            "A",
            "A",
            project,
            Path.Combine("A", "A.csproj"),
            "A folder");
        var loaded = await ProjectLoader.LoadProjectAsync(entry, _root);

        Assert.Equal("test", loaded.Kind);
        Assert.Equal("net10.0", loaded.TargetFramework);
        Assert.Single(loaded.PackageReferences);
        var reference = Assert.Single(loaded.ProjectReferences);
        Assert.True(reference.IsConditional);
        Assert.EndsWith("B.csproj", reference.FullPath, StringComparison.Ordinal);
    }
}
