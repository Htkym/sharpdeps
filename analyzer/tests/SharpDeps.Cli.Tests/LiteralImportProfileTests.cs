namespace SharpDeps.Cli.Tests;

using System.Reflection;
using System.Text;
using System.Xml;
using Xunit;

public sealed class LiteralImportProfileTests
{
    [Fact]
    public void OnlyExplicitOneLevelStaticCompilerImportsAreAdmitted()
    {
        var root = Path.Combine(Path.GetTempPath(), "sharpdeps-literal-profile", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "build"));
        Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        const string properties = "<Project><PropertyGroup><Nullable>enable</Nullable><DefineConstants>PROFILE_A</DefineConstants></PropertyGroup></Project>";
        const string import = "<Project><Import Project=\"build/Compiler.props\" /></Project>";
        Write("Directory.Build.props", properties);
        Assert.True(Admitted());
        Write("Directory.Build.props", import);
        Write("build/Compiler.props", properties);
        Assert.True(Admitted());
        Write("Unused.props", properties);
        Assert.False(Admitted());
        File.Delete(Path.Combine(root, "Unused.props"));
        Assert.True(Admitted());
        Write("build/Nested.props", properties);
        Write("build/Compiler.props", "<Project><Import Project=\"Nested.props\" /></Project>");
        Assert.False(Admitted());
        File.Delete(Path.Combine(root, "build/Nested.props"));
        foreach (var contents in new[]
        {
            "<Project Sdk=\"Microsoft.NET.Sdk\" />",
            "<Project><PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\"><Nullable>enable</Nullable></PropertyGroup></Project>",
            "<Project><PropertyGroup><Nullable Condition=\"true\">enable</Nullable></PropertyGroup></Project>",
            "<Project><PropertyGroup><DefineConstants>$(DefineConstants);PROFILE_A</DefineConstants></PropertyGroup></Project>",
            "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>",
            "<Project><ItemGroup><Compile Include=\"Injected.cs\" /></ItemGroup></Project>",
            "<Project><Target Name=\"Injected\" /></Project>",
            "<!DOCTYPE Project [<!ENTITY value 'enable'>]><Project><PropertyGroup><Nullable>&value;</Nullable></PropertyGroup></Project>"
        })
        {
            Write("build/Compiler.props", contents);
            Assert.False(Admitted(), contents);
        }
        Write("build/Compiler.props", properties);
        foreach (var element in new[]
        {
            "<Import Project=\"build/Compiler.props\" Condition=\"true\" />",
            "<Import Project=\"build/Compiler.props\" Sdk=\"Custom.Sdk\" />",
            "<Import Project=\"build/Compiler.props\"><PropertyGroup /></Import>",
            "<Import Project=\"build/Compiler.props\">unexpected</Import>",
            "<Import Project=\"Missing.props\" />",
            "<Import Project=\"build/compiler.props\" />",
            "<Import Project=\"../External.props\" />",
            "<Import Project=\"/External.props\" />",
            "<Import Project=\"C:/External.props\" />",
            "<Import Project=\"https://example.invalid/External.props\" />",
            "<Import Project=\"$(CustomProps)\" />",
            "<Import Project=\"build/*.props\" />",
            "<Import Project=\"build/Compiler.props;Other.props\" />",
            "<Import Project=\"build/%43ompiler.props\" />",
            "<Import Project=\"build/../build/Compiler.props\" />",
            "<Import Project=\"build//Compiler.props\" />",
            "<Import Project=\" build/Compiler.props\" />",
            "<Import Project=\"build/Compiler.targets\" />",
            "<Import Project=\"obj/Compiler.props\" />",
            "<Import Project=\".sharpdeps/Compiler.props\" />"
        })
        {
            Write("Directory.Build.props", "<Project>" + element + "</Project>");
            Assert.False(Admitted(), element);
        }
        Write("Directory.Build.props", import);
        File.Delete(Path.Combine(root, "build/Compiler.props"));
        Assert.False(Admitted());
        Write("build/Compiler.props", properties);
        Assert.True(Admitted());

        bool Admitted()
        {
            try
            {
                return (bool)typeof(CliReuseProof).GetMethod("PlainProjects", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [root, root, new CliReuseProof.ProofBudget(default), false, null, null])!;
            }
            catch (TargetInvocationException error) when (error.InnerException is CliFailure or IOException or XmlException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        void Write(string relative, string contents) => File.WriteAllText(Path.Combine(root, relative), contents, new UTF8Encoding(false, true));
    }
}
