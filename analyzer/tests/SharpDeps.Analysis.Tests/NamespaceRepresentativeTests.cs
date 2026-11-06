namespace SharpDeps.Analysis.Tests;

using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Quick;
using Xunit;

public sealed class NamespaceRepresentativeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepresentativeIsOrdinalMinimumRegardlessOfProjectAndCreationOrder(bool reverse)
    {
        var root = Path.Combine(Path.GetTempPath(), "sharpdeps-namespace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var names = new[] { "A", "Z" };
            foreach (var name in reverse ? names.Reverse() : names)
            {
                var directory = Path.Combine(root, name);
                Directory.CreateDirectory(directory);
                var files = new[] { "A.cs", "Z.cs" };
                foreach (var file in reverse ? files.Reverse() : files)
                    await File.WriteAllTextAsync(Path.Combine(directory, file), "namespace Shared; public class " + name + Path.GetFileNameWithoutExtension(file) + " { }");
            }

            var projects = names.Select(name => new LoadedProject(name, Path.Combine(root, name, name + ".csproj"),
                name + ".csproj", "", name, "library", "net10.0", [], [])).ToArray();
            var graph = await NamespaceAnalyzer.AnalyzeAsync(reverse ? projects.Reverse().ToArray() : projects, 60, 200);

            var node = Assert.Single(graph.DiagramNodes);
            Assert.Equal(Path.Combine(root, "A", "A.cs"), node.RepresentativeFile);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
