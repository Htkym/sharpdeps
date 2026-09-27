using SharpDeps.Analysis.Core.Xml;
using System.Xml.Linq;
using Xunit;

namespace SharpDeps.Analysis.Tests;

public sealed class ProjectKindDetectionTests
{
    [Theory]
    [InlineData("<Project Sdk='Microsoft.NET.Sdk.Web' />", "web")]
    [InlineData("<Project><Sdk Name='Microsoft.NET.Sdk.Web' /></Project>", "web")]
    [InlineData("<Project Sdk='Microsoft.NET.Sdk'><Sdk Name='Microsoft.NET.Sdk.Web' /></Project>", "web")]
    [InlineData("<Project><Import Project='Sdk.props' Sdk='Microsoft.NET.Sdk.Web' /></Project>", "web")]
    [InlineData("<Project Sdk='Microsoft.NET.Sdk.BlazorWebAssembly' />", "web")]
    [InlineData("<Project Sdk='Microsoft.NET.Sdk.Razor' />", "library")]
    [InlineData("<Project Sdk='Microsoft.NET.Sdk'><ItemGroup><FrameworkReference Include='Microsoft.AspNetCore.App' /></ItemGroup></Project>", "library")]
    public void DetectsSdkDeclarationsWithoutTreatingRazorLibrariesAsWebApps(string xml, string expected)
        => Assert.Equal(expected, ProjectKindDetection.FromProjectXml("Sample", XElement.Parse(xml)));

    [Theory]
    [InlineData("App.Tests")]
    [InlineData("AppTests")]
    [InlineData("AppTest")]
    public void DetectsTestProjectsByName(string name)
    {
        var kind = ProjectKindDetection.DetermineProjectKind(name, "Microsoft.NET.Sdk", [], null, null, null, null);

        Assert.Equal("test", kind);
    }

    [Fact]
    public void DetectsTestProjectsByPropertyAndPackages()
    {
        var byProperty = ProjectKindDetection.DetermineProjectKind(
            "App",
            "Microsoft.NET.Sdk",
            [],
            "true",
            null,
            null,
            null);
        var byPackage = ProjectKindDetection.DetermineProjectKind(
            "App",
            "Microsoft.NET.Sdk",
            ["xunit.runner.visualstudio"],
            null,
            null,
            null,
            null);

        Assert.Equal("test", byProperty);
        Assert.Equal("test", byPackage);
    }

    [Fact]
    public void DetectsWebDesktopAndAppProjects()
    {
        Assert.Equal(
            "web",
            ProjectKindDetection.DetermineProjectKind("Web", "Microsoft.NET.Sdk.Web", [], null, null, null, null));
        Assert.Equal(
            "desktop",
            ProjectKindDetection.DetermineProjectKind("Desktop", "Microsoft.NET.Sdk", [], null, null, "true", null));
        Assert.Equal(
            "desktop",
            ProjectKindDetection.DetermineProjectKind("Desktop", "Microsoft.NET.Sdk", [], null, null, null, "true"));
        Assert.Equal(
            "app",
            ProjectKindDetection.DetermineProjectKind("Console", "Microsoft.NET.Sdk", [], null, "Exe", null, null));
    }

    [Fact]
    public void FallsBackToLibrary()
    {
        var kind = ProjectKindDetection.DetermineProjectKind(
            "Domain",
            "Microsoft.NET.Sdk",
            [],
            null,
            null,
            null,
            null);

        Assert.Equal("library", kind);
    }
}
