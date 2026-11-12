namespace SharpDeps.Mcp.Tests;

using System.Text;
using System.Text.Json;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Core.Text;
using SharpDeps.Index;

internal sealed class ReplayFixture : IDisposable
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "sharpdeps-fixed-query-fixtures");
    private static readonly Guid Workspace = Guid.Parse("0fe0a91e-35b8-4880-a66a-57ce0986dcd2");
    private readonly Dictionary<string, (IndexFile File, string Text, byte[] Bytes)> sources = new(StringComparer.Ordinal);
    internal string Root { get; } = Path.Combine(Base, Guid.NewGuid().ToString("N"));
    internal string IndexPath => Path.Combine(Root, ".sharpdeps", "index.sqlite");
    internal string Project { get; } = HarnessIdentity.ProjectId(Workspace, "App.csproj");
    internal string Variant { get; }
    internal string Member { get; }
    internal string Huge { get; }
    private static readonly Guid DocumentUuid = Guid.Parse("8c737ea2-472d-45a1-b78e-8cdb887d2f24");
    internal string Document => HarnessIdentity.DocumentId(Workspace, DocumentUuid);
    internal string Section => HarnessIdentity.SectionId(Workspace, Document, "usage");
    internal const string Markdown = "# Usage\r\n\r\nRun accepts one value.\r\n";
    internal IndexSnapshot Snapshot { get; private set; }
    internal const string RunInt = "public int Run(int value) { return value + 7; }";
    internal const string RunString = "public int Run(string value) { return value.Length; }";
    internal const string Unrelated = "public void Unrelated() { throw new NotImplementedException(); }";

    internal ReplayFixture(int noise = 0, bool documents = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
        File.WriteAllText(Path.Combine(Root, ".sharpdeps", "workspace.json"), JsonSerializer.Serialize(
            new { schemaVersion = 1, workspaceUuid = Workspace, cursorKey = Convert.ToBase64String(new byte[32]) }));
        var workspace = HarnessIdentity.WorkspaceId(Workspace);
        var nodes = new List<HarnessNode> { new(workspace, HarnessNodeKind.Workspace, "Replay", null, null) };
        var variants = new List<HarnessVariant>();
        var occurrences = new List<HarnessSymbolOccurrence>();
        var scopes = new[] { ("App.csproj", "net10.0", "Debug"), ("App.csproj", "net9.0", "Debug"),
            ("Other.csproj", "net10.0", "Debug"), ("Third.csproj", "net10.0", "Release") };
        var huge = "public string Huge() { return \"" + string.Concat(Enumerable.Repeat("検証😀 \\\" \\\\ ", 2200)) + "\"; }";
        const string corePath = "src/Service.cs";
        AddFile(corePath, "// shared physical source\r\npartial class Service\r\n{\r\n" + RunInt + "\r\n" + RunString
            + "\r\n" + huge + "\r\n" + Unrelated + "\r\n}\r\n", new UTF8Encoding(false));
        const string partialPath = "src/Service.Partial.cs";
        AddFile(partialPath, "partial class Service { public void Reset() { } }\r\n", new UTF8Encoding(false));
        foreach (var (projectPath, tfm, configuration) in scopes)
        {
            var project = HarnessIdentity.ProjectId(Workspace, projectPath);
            var variant = HarnessIdentity.VariantId(Workspace, project, tfm, configuration);
            if (nodes.All(node => node.Id != project))
            {
                nodes.Add(new(project, HarnessNodeKind.Project, Path.GetFileNameWithoutExtension(projectPath), workspace, null));
            }
            variants.Add(new(variant, project, tfm, configuration, null, null, null));
            var type = Symbol(project, "type", "T:Service");
            if (nodes.All(node => node.Id != type))
            {
                nodes.Add(new(type, HarnessNodeKind.Type, "Service", project, null, "T:Service"));
            }
            var core = sources[corePath].Text;
            var start = core.IndexOf("partial class", StringComparison.Ordinal);
            AddOccurrence(type, variant, [Location(corePath, new(start, core.Length - start - 2)),
                Location(partialPath, new(0, sources[partialPath].Text.Length - 2))]);
            AddMember("Run", "M:Service.Run(System.Int32)", RunInt);
            AddMember("Run", "M:Service.Run(System.String)", RunString);
            AddMember("Huge", "M:Service.Huge", huge);
            AddMember("Unrelated", "M:Service.Unrelated", Unrelated);
            void AddMember(string name, string signature, string body)
            {
                var member = Symbol(project, "method", signature);
                if (nodes.All(node => node.Id != member))
                {
                    nodes.Add(new(member, HarnessNodeKind.Member, name, type, null, signature));
                }
                AddOccurrence(member, variant, [Location(corePath, new(core.IndexOf(body, StringComparison.Ordinal), body.Length))]);
            }
        }
        Variant = variants[0].Id;
        Member = Occurrence(Symbol(Project, "method", "M:Service.Run(System.Int32)"), Variant);
        Huge = Occurrence(Symbol(Project, "method", "M:Service.Huge"), Variant);
        AddType("Utf8", "enc/Utf8.cs", new UTF8Encoding(false));
        AddType("Utf8Bom", "enc/Utf8Bom.cs", new UTF8Encoding(true));
        AddType("Utf16Le", "enc/Utf16Le.cs", new UnicodeEncoding(false, true));
        AddType("Utf16Be", "enc/Utf16Be.cs", new UnicodeEncoding(true, true));
        AddType("Generated", "generated/Generated.g.cs", new UTF8Encoding(false), IndexFileKinds.GeneratedSource);
        AddType("Package", "package/Package.cs", new UTF8Encoding(false), IndexFileKinds.PackageSource);
        for (var index = 0; index < noise; index++)
        {
            var signature = "T:Noise" + index;
            nodes.Add(new(Symbol(Project, "type", signature), HarnessNodeKind.Type, "Noise" + index, Project, null, signature));
        }
        var edges = new List<HarnessEdge>();
        var documentIdentities = new List<IndexDocumentIdentity>();
        HarnessMarkdownIdentity? markdownIdentity = null;
        if (documents)
        {
            const string documentPath = "docs/usage.md";
            AddFile(documentPath, Markdown, new UTF8Encoding(false), "markdown");
            var documentLocation = Location(documentPath, new(0, Markdown.Length));
            nodes.Add(new(Document, HarnessNodeKind.Document, documentPath, workspace, documentLocation));
            nodes.Add(new(Section, HarnessNodeKind.Section, "Usage", Document, documentLocation));
            var memberNode = occurrences.Single(value => value.Id == Member).LogicalSymbolId;
            edges.Add(new("replay-document-link", Section, memberNode, null, Member, Variant,
                "documents_symbol", HarnessCertainty.Candidate, "markdown-explicit", documentLocation));
            var optionsHash = FixedQueryReplay.Hash(Encoding.UTF8.GetBytes("options"));
            markdownIdentity = new("1.0.0", sources[documentPath].File.ContentHash, "parser-1", "contract-1", "profile-1", optionsHash);
            documentIdentities.Add(new(DocumentUuid, documentPath, null, sources[documentPath].File.ContentHash, "scope", "parser-1", "contract-1", "profile-1",
                optionsHash, [new("usage", "usage", "1:Usage", sources[documentPath].File.ContentHash)]));
        }
        var graph = new HarnessGraphEnvelope(HarnessGraphContract.Format, HarnessGraphContract.SchemaVersion,
            HarnessGraphContract.IdentityVersion, workspace, "fixed-query-fixture-1", 1, HarnessCoverage.Partial,
            variants, nodes, occurrences, edges, [], markdownIdentity);
        Snapshot = new(graph, sources.Values.Select(value => value.File).ToArray(), documentIdentities, [],
            nodes.Where(node => node.Kind is HarnessNodeKind.Type or HarnessNodeKind.Member)
                .Select(node => new IndexSearchText(node.Id, node.Name, node.Signature ?? "", "", "", "")).ToArray(),
            sources.Values.Where(value => Query.QueryWorkspace.IsGenerated(value.File)).Select(value => new IndexGeneratedBody(value.File.SourceId, value.Text)).ToArray(),
            sources.Values.Where(value => Query.QueryWorkspace.IsPackage(value.File)).Select(value => new IndexPackageBody(value.File.SourceId, value.Text)).ToArray());
        Publish(1);

        void AddType(string name, string path, Encoding encoding, string kind = "source")
        {
            var declaration = "public class " + name + " { public string Text => \"日本語😀 \\\" \\\\ token\"; }";
            AddFile(path, "// encoding fixture\r\n" + declaration + "\r\n", encoding, kind);
            var type = Symbol(Project, "type", "T:" + name);
            nodes.Add(new(type, HarnessNodeKind.Type, name, Project, null, "T:" + name));
            AddOccurrence(type, Variant, [Location(path, new(sources[path].Text.IndexOf(declaration, StringComparison.Ordinal), declaration.Length))]);
        }
        void AddOccurrence(string symbol, string variant, IReadOnlyList<HarnessLocation> declarations)
            => occurrences.Add(new(Occurrence(symbol, variant), symbol, variant, declarations[0], declarations));
    }

    internal void Publish(long generation)
    {
        Snapshot = Snapshot with { Graph = Snapshot.Graph with { Generation = generation, SnapshotId = "fixed-query-fixture-" + generation } };
        using var writer = IndexWriter.Open(Root, IndexPath, Workspace, true);
        writer.Commit(IndexStage.Create(Snapshot));
    }

    internal void ChangeSource()
    {
        var source = sources["src/Service.cs"];
        File.WriteAllText(Path.Combine(Root, "src", "Service.cs"), source.Text.Replace("value + 7", "value + 8", StringComparison.Ordinal), new UTF8Encoding(false));
    }

    private void AddFile(string path, string text, Encoding encoding, string kind = "source")
    {
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        var decoded = SourceTextEncoding.Decode(bytes);
        sources.Add(path, (new(path, path, kind, FixedQueryReplay.Hash(bytes), bytes.Length, decoded.Length), decoded, bytes));
        if (kind is "source" or "markdown")
        {
            var full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
    }
    private HarnessLocation Location(string path, HarnessRawSpan span) => new(path, null, sources[path].File.ContentHash, span);
    private static string Symbol(string project, string kind, string signature) => HarnessIdentity.LogicalSymbolId(Workspace, project, kind, signature);
    private static string Occurrence(string symbol, string variant) => HarnessIdentity.SymbolOccurrenceId(Workspace, symbol, variant);

    public void Dispose()
    {
        var expected = Path.GetFullPath(Base) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Replay fixture cleanup escaped its dedicated directory.");
        }
        Directory.Delete(Root, recursive: true);
    }
}
