namespace SharpDeps.Cli.Tests;

using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SharpDeps.Analysis.Markdown;
using Syntamark;
using Xunit;

public sealed class ParserFullOracleTests
{
    [Fact]
    public void RoslynChangedTextMatchesIndependentFullParseThroughEditsAndUndo()
    {
        const string original = "#define FEATURE\r\n#if FEATURE\r\nclass C { string Text = \"日本語😀\"; int M() => 1; }\r\n#endif\r\n";
        var options = new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Diagnose);
        var tree = CSharpSyntaxTree.ParseText(original, options, "source.cs");
        foreach (var next in new[] { original.Replace("=> 1", "=> Missing("), original.Replace("#if FEATURE", "#if OTHER"), original })
        {
            var text = tree.GetText();
            var before = text.ToString();
            var prefix = 0;
            while (prefix < Math.Min(before.Length, next.Length) && before[prefix] == next[prefix])
                prefix++;
            var suffix = 0;
            while (suffix < Math.Min(before.Length, next.Length) - prefix
                && before[^(suffix + 1)] == next[^(suffix + 1)])
                suffix++;
            var changed = text.WithChanges(new TextChange(TextSpan.FromBounds(prefix, before.Length - suffix),
                next[prefix..(next.Length - suffix)]));
            var candidate = tree.WithChangedText(changed);
            var full = CSharpSyntaxTree.ParseText(next, options, "source.cs");
            Assert.Equal(Syntax(full), Syntax(candidate));
            tree = candidate;
        }
    }

    private static string Syntax(SyntaxTree tree)
    {
        var root = tree.GetRoot();
        return JsonSerializer.Serialize(new
        {
            Text = root.ToFullString(),
            NodesAndTokens = root.DescendantNodesAndTokensAndSelf(descendIntoTrivia: true).Select(n => new
            {
                n.RawKind, n.Span, n.FullSpan, n.IsMissing, n.ContainsDiagnostics
            }),
            Tokens = root.DescendantTokens(descendIntoTrivia: true).Select(t => new
            {
                t.RawKind, t.Text, t.ValueText, t.Span, t.FullSpan, t.IsMissing
            }),
            Trivia = root.DescendantTrivia(descendIntoTrivia: true).Select(t => new
            {
                t.RawKind, Text = t.ToFullString(), t.Span, t.FullSpan, t.HasStructure
            }),
            Diagnostics = tree.GetDiagnostics().Select(d => new
            {
                d.Id, d.Severity, d.WarningLevel, d.IsSuppressed,
                Message = d.GetMessage(System.Globalization.CultureInfo.InvariantCulture), d.Location.SourceSpan
            })
        });
    }

    [Theory]
    [InlineData("# 日本語😀\r\n\r\n[Service](./code.cs) &amp; `Demo.Service`\r\n", 10000)]
    [InlineData("---\r\ntitle: Example\r\n---\r\n# Heading\r\n\r\n- item\r\n\r\n```cs\r\nclass C {}\r\n```\r\n", 10000)]
    [InlineData("# Heading\n\nIReadOnlyState<T>\n\n[ref]: https://example.test\n", 10000)]
    [InlineData("# Heading\n\n- one\n- two\n- three\n", 2)]
    public void PreparedMarkdownFactsMatchIndependentCanonicalFullParse(string text, int maxItems)
    {
        var options = new MarkdownParseOptions(maxOutputItems: maxItems);
        var request = new MarkdownGraphRequest(text, Guid.Parse("11111111-2222-4333-8444-555555555555"),
            Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"), "scope", "guide.md", "v1", "snapshot", 1, Options: options);
        var candidate = new LithoSharpMarkdownAdapter().Prepare(request, true);
        var full = MarkdownParser.Parse(text, request.ScopeId, request.SourceId, request.SourceVersion, options);
        Assert.Equal(JsonSerializer.Serialize(Canonical(full)), JsonSerializer.Serialize(Canonical(candidate.Facts)));
    }

    // Include nonpublic source-map state as well as public facts; no diagnostics, coverage or TextHash exclusions.
    private static object? Canonical(object? value)
    {
        if (value is null || value is string || value.GetType().IsPrimitive || value is decimal)
            return value;
        if (value.GetType().IsEnum)
            return value.ToString();
        if (value is IEnumerable sequence)
            return sequence.Cast<object?>().Select(Canonical).ToArray();
        var properties = value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.GetMethod is not null && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal);
        return properties.ToDictionary(p => p.Name, p => Canonical(p.GetValue(value)), StringComparer.Ordinal);
    }
}
