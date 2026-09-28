// A tiny source generator that only exists in memory: it writes no file to obj/ and no
// file to the repository, so the semantic loader can only see its output through the
// compilation's generated documents.

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SampleGeneration;

[Generator]
public sealed class GeneratedOrderFactory : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static context =>
        {
            const string source = """
                namespace Generated;

                /// <summary>Created by an in-memory generator, never written to disk.</summary>
                public static class OrderFactory
                {
                    public static Domain.Order Create(string id) => new() { Id = id };
                }
                """;

            context.AddSource("OrderFactory.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }
}
