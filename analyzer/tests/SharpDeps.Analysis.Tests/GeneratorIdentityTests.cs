using SharpDeps.Analysis.Roslyn;
using Xunit;

namespace SharpDeps.Analysis.Tests;

/// <summary>
/// Reads the generator identity from the generated file path shape
/// .../&lt;assembly&gt;/&lt;type&gt;/&lt;hint&gt;, including hints that contain '/'.
/// </summary>
public sealed class GeneratorIdentityTests
{
    [Theory]
    [InlineData("C:/work/obj/Debug/net10.0/Generator/SampleGeneration.GeneratedOrderFactory/OrderFactory.g.cs",
        "OrderFactory.g.cs", "Generator/SampleGeneration.GeneratedOrderFactory")]
    [InlineData("C:/work/obj/Debug/net10.0/Generator/SampleGeneration.GeneratedOrderFactory/Nested/OrderFactory.g.cs",
        "Nested/OrderFactory.g.cs", "Generator/SampleGeneration.GeneratedOrderFactory")]
    [InlineData("C:\\work\\obj\\Debug\\net10.0\\Generator\\SampleGeneration.GeneratedOrderFactory\\Nested\\OrderFactory.g.cs",
        "Nested\\OrderFactory.g.cs", "Generator/SampleGeneration.GeneratedOrderFactory")]
    [InlineData("C:/work/obj/Debug/net10.0/Generator/SampleGeneration.GeneratedOrderFactory/orderfactory.g.cs",
        "OrderFactory.g.cs", "Generator/SampleGeneration.GeneratedOrderFactory")]
    public void ReadsTheGeneratorFromTheGeneratedFilePathTail(string filePath, string hintName, string expected)
        => Assert.Equal(expected, SemanticLoader.GeneratorIdentityOf(filePath, hintName));

    [Theory]
    [InlineData(null, "OrderFactory.g.cs")]
    [InlineData("C:/OrderFactory.g.cs", "OrderFactory.g.cs")]
    [InlineData("C:/work/obj/net10.0/Generator/Factory/Other.g.cs", "OrderFactory.g.cs")]
    [InlineData("C:/work/obj/net10.0/Generator/Factory/Nested/Other.g.cs", "Nested/OrderFactory.g.cs")]
    [InlineData("C:/work/obj/net10.0/Generator/Factory/OrderFactory.g.cs", "Nested/OrderFactory.g.cs")]
    public void LeavesPathsOutsideTheGeneratedShapeUnidentified(string? filePath, string hintName)
        => Assert.Equal(string.Empty, SemanticLoader.GeneratorIdentityOf(filePath, hintName));
}
