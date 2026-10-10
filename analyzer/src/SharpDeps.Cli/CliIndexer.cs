namespace SharpDeps.Cli;

using System.Security.Cryptography;
using System.Text;
using SharpDeps.Analysis.Contracts.Harness;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Markdown;
using SharpDeps.Analysis.Roslyn;
using SharpDeps.Analysis.Roslyn.Evidence;
using SharpDeps.Analysis.Roslyn.Symbols;
using SharpDeps.Index;

public sealed record CliIndexOptions(string Root, string Target, string IndexPath, Guid WorkspaceUuid,
    string Configuration = "Debug", string? Platform = null, IReadOnlyList<string>? ProjectPaths = null,
    string? TargetFramework = null, int TimeoutSeconds = 180);

public sealed class CliIndexException : Exception
{
    public CliIndexException(string code, int exitCode = 4) : this(code, code, exitCode) { }
    public CliIndexException(string code, string message, int exitCode = 4, Exception? inner = null)
        : base(message, inner) { Code = code; ExitCode = exitCode; }
    public string Code { get; }
    public int ExitCode { get; }
}

/// <summary>
/// Trusted full rebuild only. The caller validates trust and holds the writer lease from before
/// this call through commit. No restore, commit, renderer, watch or workspace UUID allocation occurs here.
/// </summary>
public static class CliIndexer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".sharpdeps", "bin", "obj", "node_modules" };
    private const int MaximumWalkEntries = 100_000;

    public static async Task<IndexSnapshot> BuildAsync(CliIndexOptions options, IndexSnapshot? previous,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = CliPaths.Root(options.Root);
        if (options.TimeoutSeconds is < 1 or > 3600
            || string.IsNullOrWhiteSpace(options.Configuration))
            throw new CliIndexException("CLI_INVALID_INDEX_OPTIONS");
        var workspaceId = HarnessIdentity.WorkspaceId(options.WorkspaceUuid);
        if (previous is not null)
        {
            IndexSnapshotValidator.Validate(previous);
            if (previous.Graph.WorkspaceId != workspaceId)
                throw new CliIndexException("CLI_WORKSPACE_MISMATCH");
        }
        var target = InsideRoot(root, options.Target);
        if (!File.Exists(target) || !new[] { ".csproj", ".sln", ".slnx" }
                .Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase))
            throw new CliIndexException("CLI_TARGET_NOT_SUPPORTED");
        var selectedPaths = (options.ProjectPaths ?? []).Select(path => InsideRoot(root, path))
            .ToHashSet(PathComparer);
        if (selectedPaths.Any(path => !File.Exists(path)
                || !path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            throw new CliIndexException("CLI_PROJECT_NOT_FOUND");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        token = timeout.Token;
        var snapshotId = Guid.NewGuid().ToString("N");
        var generation = previous is null ? 1 : checked(previous.Graph.Generation + 1);
        var manifest = new Dictionary<string, InputText>(StringComparer.OrdinalIgnoreCase);
        long inputBytes = 0;

        // Capture selected documentation/configuration before evaluation. The configuration inventory is
        // deliberately not claimed complete: SDK imports and generator inputs are not exposed by the loader.
        foreach (var path in WalkInputs(root, token))
            await Capture(path, IsMarkdown(path) ? "markdown" : "configuration");
        await Capture(target, "configuration");
        foreach (var path in selectedPaths) await Capture(path, "configuration");

        if (!SemanticEnvironment.TryRegister(Path.GetDirectoryName(target)!, out var registrationFailure))
            throw new CliIndexException("CLI_TARGET_SDK_UNAVAILABLE",
                registrationFailure ?? "No target MSBuild SDK is available.");
        IReadOnlyList<ProjectVariantSelection>? selections = null;
        if (!string.IsNullOrWhiteSpace(options.TargetFramework))
        {
            string[] requested = selectedPaths.Count > 0 ? selectedPaths.ToArray()
                : target.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? [target] : [];
            var loaderRoot = Path.GetDirectoryName(target)!;
            selections = requested.Select(path => new ProjectVariantSelection(
                Identity.ProjectLogicalId(Identity.WorkspaceRootId(loaderRoot), Path.GetRelativePath(loaderRoot, path)),
                options.TargetFramework!)).ToArray();
        }
        var loaded = await SemanticLoader.LoadAsync(new(target, options.Configuration, options.Platform,
            options.TimeoutSeconds, selections), token);
        var variants = loaded.Report.Variants.Where(v => selectedPaths.Count == 0
                || selectedPaths.Contains(Path.GetFullPath(v.ProjectPath)))
            .Where(v => options.TargetFramework is null || v.TargetFramework == options.TargetFramework).ToArray();
        if (variants.Length == 0 || variants.Any(v => v.LoadState != "loaded" || !v.CompilationObtained
                || !loaded.Compilations.ContainsKey(v.VariantKey) || string.IsNullOrWhiteSpace(v.TargetFramework)))
            throw new CliIndexException("CLI_SEMANTIC_LOAD_INCOMPLETE");
        if (selectedPaths.Any(path => !variants.Any(v => PathComparer.Equals(path, Path.GetFullPath(v.ProjectPath)))))
            throw new CliIndexException("CLI_SELECTED_VARIANT_NOT_AVAILABLE");
        if (variants.Any(v => v.GeneratedDocumentCount != 0 || v.GeneratedDocumentError is not null))
            throw new CliIndexException("CLI_GENERATED_INPUT_UNSUPPORTED",
                "Virtual generated input cannot be verified against the live-file storage contract.");
        var projectByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projects = new List<HarnessProjectVariant>();
        foreach (var variant in variants)
        {
            var path = InsideRoot(root, variant.ProjectPath);
            var input = await Capture(path, "project");
            if (!projectByPath.TryGetValue(path, out var projectId))
            {
                projectId = previous?.Files.FirstOrDefault(f => f.Kind == "project"
                    && StringComparer.OrdinalIgnoreCase.Equals(f.RelativePath, input.File.RelativePath))?.SourceId
                    ?? HarnessIdentity.ProjectId(options.WorkspaceUuid, input.File.RelativePath);
                projectByPath.Add(path, projectId);
                manifest[input.File.RelativePath] = input with { File = input.File with { SourceId = projectId, Kind = "project" } };
            }
            projects.Add(new(variant.VariantKey, projectId, Path.GetFileNameWithoutExtension(path),
                variant.TargetFramework!, variant.Configuration, options.Platform));
        }
        var inputs = variants.Select(v => new SymbolIndexInput(v.VariantKey, v.ProjectName,
            loaded.Compilations[v.VariantKey])).ToArray();
        var documents = new SourceDocumentRegistry(Identity.WorkspaceRootId(root), root);
        var sourceOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            foreach (var tree in input.Compilation.SyntaxTrees)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(tree.FilePath))
                    throw new CliIndexException("CLI_GENERATED_INPUT_UNSUPPORTED");
                var sourcePath = InsideRoot(root, tree.FilePath);
                if (!File.Exists(sourcePath)) throw new CliIndexException("CLI_GENERATED_INPUT_UNSUPPORTED",
                    "Every syntax input must be a verifiable physical file inside the selected root.");
                var captured = await Capture(sourcePath, "source");
                var compilationText = tree.GetText(token).ToString();
                var offset = compilationText == captured.Text ? 0
                    : captured.HasUtf8Bom && compilationText == captured.Text[1..] ? 1 : -1;
                if (offset < 0)
                    throw new CliIndexException("SOURCE_CHANGED_SINCE_STAGING", "Compilation text differs from the selected live input.");
                var registered = documents.Register(captured.AbsolutePath);
                if (sourceOffsets.TryGetValue(registered.Id, out var priorOffset) && priorOffset != offset)
                    throw new CliIndexException("CLI_SOURCE_COORDINATES_INCONSISTENT");
                sourceOffsets[registered.Id] = offset;
                // Empty source files carry no declarations/evidence; the registry's historical unavailable
                // marker is not copied into the new manifest. Any later location using it fails validation.
                if (registered.ByteLength != captured.File.ByteLength
                    || (registered.ContentHash != captured.File.ContentHash && captured.File.ByteLength != 0))
                    throw new CliIndexException("SOURCE_CHANGED_SINCE_STAGING");
                manifest[captured.File.RelativePath] = captured with
                    { File = captured.File with { SourceId = registered.Id, Kind = "source" } };
            }
        }
        var symbols = SymbolIndexBuilder.Build(documents, inputs, token);
        var resolver = new SymbolResolver(loaded.DefiningVariantByAssembly);
        var external = new ExternalTypeRegistry();
        var declaration = new DeclarationDependencyCollector(resolver, documents, symbols, loaded.Report.Profile.ProfileHash, external);
        var operation = new OperationDependencyCollector(resolver, documents, symbols, loaded.Report.Profile.ProfileHash, external);
        var collected = operation.Collect(inputs, token);
        var evidence = declaration.Collect(inputs, token).Concat(collected.HarnessEvidence ?? collected.Evidence).ToArray();
        var graph = HarnessGraphProjector.Project(options.WorkspaceUuid, snapshotId, generation,
            HarnessCoverage.Partial, projects, symbols, evidence, collected.Stats, token);
        graph = AlignCompilationLocations(graph, sourceOffsets);
        var diagnostics = (graph.Diagnostics ?? []).ToDictionary(d => d.Code, d => d.Count, StringComparer.Ordinal);
        diagnostics["cli.configurationInventoryUnverified"] = 1;
        if (loaded.Report.Diagnostics.Count != 0) diagnostics["cli.loaderDiagnostics"] = loaded.Report.Diagnostics.Count;
        if (loaded.Report.Limitations.Count != 0) diagnostics["cli.loaderLimitations"] = loaded.Report.Limitations.Count;
        var compilerErrors = variants.Sum(v => v.ErrorDiagnosticCount);
        if (compilerErrors != 0) diagnostics["cli.compilerErrors"] = compilerErrors;
        graph = graph with { Diagnostics = Array.AsReadOnly(diagnostics.OrderBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => new HarnessGraphDiagnostic(d.Key, d.Value)).ToArray()) };

        // SourceFile nodes expose actual manifest metadata and retain project-path to persisted-ID binding.
        var sourceNodes = manifest.Values.Select(input => new HarnessNode("cli-file:" + input.File.SourceId,
            HarnessNodeKind.SourceFile, input.File.RelativePath,
            input.File.Kind == "project" ? input.File.SourceId : workspaceId,
            new(input.File.SourceId, null, input.File.ContentHash, new(0, input.Text.Length)))).ToArray();
        graph = graph with { Nodes = Array.AsReadOnly(graph.Nodes.Concat(sourceNodes).ToArray()) };
        var catalogSymbols = MakeCatalog(graph, symbols, evidence, manifest.Values.Where(i => i.File.Kind == "project")
            .ToDictionary(i => i.File.SourceId, i => i.File.RelativePath, StringComparer.Ordinal));
        var adapter = new LithoSharpMarkdownAdapter();
        var markdown = new List<(InputText Input, MarkdownGraphProjection Projection, string? ExplicitId)>();
        foreach (var input in manifest.Values.Where(i => i.File.Kind == "markdown").OrderBy(i => i.File.RelativePath, StringComparer.Ordinal))
        {
            var request = new MarkdownGraphRequest(input.Text, options.WorkspaceUuid, Guid.NewGuid(), workspaceId,
                input.File.SourceId, input.File.ContentHash, snapshotId, generation);
            var projection = adapter.Analyze(request, isTrusted: true, token);
            var ids = projection.Attributes.Where(a => a.Key == "sharpdeps.id").ToArray();
            if (ids.Length > 1 || (ids.Length == 1 && string.IsNullOrWhiteSpace(ids[0].Source.Scalar.Text)))
                throw new CliIndexException("CLI_MARKDOWN_ID_INVALID");
            markdown.Add((input, projection, ids.FirstOrDefault()?.Source.Scalar.Text));
        }
        var oldDocuments = (previous?.Documents ?? []).Select(d => new MarkdownDocumentIdentity(d.DocumentUuid,
            d.RelativePath, d.ExplicitId, d.ContentHash)).ToArray();
        var matches = MarkdownIdentityMatcher.MatchDocuments(oldDocuments, markdown.Select(m =>
            new MarkdownDocumentMatchInput(m.Input.File.RelativePath, m.ExplicitId, m.Input.File.ContentHash)).ToArray(), token);
        var identities = new List<IndexDocumentIdentity>();
        var projections = new List<(InputText Input, MarkdownGraphProjection Projection)>();
        foreach (var item in markdown)
        {
            token.ThrowIfCancellationRequested();
            var match = matches.Single(m => m.RelativePath == item.Input.File.RelativePath);
            if (match.Reason == "duplicate-explicit-id") throw new CliIndexException("CLI_MARKDOWN_ID_AMBIGUOUS");
            var uuid = match.DocumentUuid ?? Guid.NewGuid();
            var old = previous?.Documents.SingleOrDefault(d => d.DocumentUuid == uuid);
            var scope = old?.ScopeId ?? workspaceId;
            var request = new MarkdownGraphRequest(item.Input.Text, options.WorkspaceUuid, uuid, scope,
                item.Input.File.SourceId, item.Input.File.ContentHash, snapshotId, generation);
            var facts = scope == workspaceId ? item.Projection.Facts : adapter.Analyze(request, true, token).Facts;
            var oldSections = old is null ? null : new MarkdownSectionIdentitySnapshot(
                HarnessIdentity.DocumentId(options.WorkspaceUuid, uuid), old.ScopeId, old.ContentHash,
                old.ParserVersion, old.ContractVersion, old.ProfileId, old.OptionsHash,
                old.Sections.Select(s => new MarkdownSectionIdentity(s.LocalKey, s.Token, s.HeadingKey, s.BodyHash)).ToArray());
            var sections = MarkdownIdentityMatcher.MatchSections(options.WorkspaceUuid, uuid, item.Input.Text,
                facts, oldSections, () => Guid.NewGuid().ToString("N"), token);
            var projection = adapter.Analyze(request with { SectionTokens = sections.SectionTokens,
                SectionIdentityTextHash = sections.TextHash }, true, token);
            identities.Add(new(uuid, item.Input.File.RelativePath, item.ExplicitId, item.Input.File.ContentHash,
                scope, sections.Snapshot.ParserVersion, sections.Snapshot.ContractVersion, sections.Snapshot.ProfileId,
                sections.Snapshot.OptionsHash, sections.Snapshot.Sections.Select(s =>
                    new IndexSectionIdentity(s.LocalKey, s.Token, s.HeadingKey, s.BodyHash)).ToArray()));
            graph = Merge(graph, projection.Graph);
            projections.Add((item.Input, projection));
            if (match.DocumentUuid is null && match.Candidates.Count != 0)
                diagnostics["cli.documentIdentityAmbiguous"] = diagnostics.GetValueOrDefault("cli.documentIdentityAmbiguous") + 1;
        }
        foreach (var (input, projection) in projections)
        {
            var aliases = CodeAliases(Path.GetDirectoryName(input.AbsolutePath)!, manifest.Values.Where(i => i.File.Kind == "source"));
            var resolution = MarkdownSymbolResolver.Resolve(projection, input.Text, new(graph, catalogSymbols, aliases),
                cancellationToken: token);
            graph = MarkdownSymbolResolver.ProjectGraph(graph, resolution);
            foreach (var reason in projection.ProjectionReasons.Concat(resolution.Reasons).Distinct(StringComparer.Ordinal))
                diagnostics["markdown." + reason] = diagnostics.GetValueOrDefault("markdown." + reason) + 1;
        }
        graph = graph with { Diagnostics = Array.AsReadOnly(diagnostics.OrderBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => new HarnessGraphDiagnostic(d.Key, d.Value)).ToArray()) };
        var search = MakeSearch(graph, manifest, projections.Select(p => p.Projection));
        foreach (var input in manifest.Values) await Verify(input, root, token);
        var snapshot = new IndexSnapshot(graph, manifest.Values.Select(i => i.File).OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray(),
            identities.ToArray(), [], search);
        IndexSnapshotValidator.Validate(snapshot);
        token.ThrowIfCancellationRequested();
        return snapshot;

        async Task<InputText> Capture(string path, string kind)
        {
            path = InsideRoot(root, path);
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            IndexSnapshotValidator.ValidateRelativePath(relative);
            if (manifest.TryGetValue(relative, out var old))
            {
                if (!PathComparer.Equals(old.AbsolutePath, path)) throw new CliIndexException("CLI_PATH_ALIAS");
                return old;
            }
            var text = await ReadInput(path, token);
            inputBytes = checked(inputBytes + text.Bytes);
            if (inputBytes > IndexSnapshotValidator.MaximumInputTotalBytes)
                throw new CliIndexException("CLI_INPUT_BUDGET_EXCEEDED");
            var input = new InputText(path, new("cli-input:" + relative, relative, kind, text.Hash,
                text.Bytes, text.Text.Length), text.Text, text.HasUtf8Bom);
            manifest.Add(relative, input);
            return input;
        }
    }

    private sealed record InputText(string AbsolutePath, IndexFile File, string Text, bool HasUtf8Bom);
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static IEnumerable<string> WalkInputs(string root, CancellationToken token)
    {
        var directories = new Stack<string>(); directories.Push(root);
        var count = 0;
        while (directories.TryPop(out var directory))
        {
            RejectLinks(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++count > MaximumWalkEntries) throw new CliIndexException("CLI_INPUT_BUDGET_EXCEEDED");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0 && ExcludedDirectories.Contains(Path.GetFileName(path))) continue;
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new CliIndexException("CLI_LINK_INPUT_REJECTED");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(path);
                else if (IsMarkdown(path) || IsConfiguration(path)) yield return path;
            }
        }
    }

    private static bool IsMarkdown(string path) => Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    private static bool IsConfiguration(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".csproj" or ".fsproj" or ".vbproj" or ".sln" or ".slnx" or ".props" or ".targets" or ".editorconfig"
        || Path.GetFileName(path).ToLowerInvariant() is "global.json" or "nuget.config" or "packages.lock.json";

    private static async Task<(string Text, string Hash, long Bytes, bool HasUtf8Bom)> ReadInput(string path, CancellationToken token)
    {
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        if (stream.Length > IndexSnapshotValidator.MaximumInputFileBytes)
            throw new CliIndexException("CLI_INPUT_BUDGET_EXCEEDED");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token);
        if (await stream.ReadAsync(new byte[1], token) != 0) throw new CliIndexException("SOURCE_CHANGED_SINCE_STAGING");
        var hasUtf8Bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException error)
        { throw new CliIndexException("CLI_ENCODING_UNSUPPORTED", "Index inputs require strict UTF-8.", inner: error); }
        if (text.Contains('\0')) throw new CliIndexException("CLI_ENCODING_UNSUPPORTED", "NUL/binary and UTF-16 inputs are unsupported.");
        RejectLinks(path);
        return (text, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength, hasUtf8Bom);
    }

    private static async Task Verify(InputText input, string root, CancellationToken token)
    {
        var current = await ReadInput(InsideRoot(root, input.AbsolutePath), token);
        if (current.Hash != input.File.ContentHash || current.Bytes != input.File.ByteLength || current.Text != input.Text)
            throw new CliIndexException("SOURCE_CHANGED_SINCE_STAGING");
    }

    private static string InsideRoot(string root, string path) => CliPaths.InRoot(root, path);
    private static void RejectLinks(string path) => CliPaths.RejectLinks(path);

    private static HarnessGraphEnvelope Merge(HarnessGraphEnvelope left, HarnessGraphEnvelope right)
    {
        if (left.WorkspaceId != right.WorkspaceId || left.SnapshotId != right.SnapshotId || left.Generation != right.Generation
            || (left.Markdown is not null && right.Markdown is not null && left.Markdown != right.Markdown))
            throw new CliIndexException("CLI_GRAPH_MERGE_CONFLICT");
        return left with { Nodes = MergeById(left.Nodes, right.Nodes, n => n.Id),
            Variants = MergeById(left.Variants, right.Variants, v => v.Id),
            SymbolOccurrences = MergeById(left.SymbolOccurrences, right.SymbolOccurrences, o => o.Id),
            Edges = MergeById(left.Edges, right.Edges, e => e.Id),
            LegacyReferences = Array.AsReadOnly(left.LegacyReferences.Concat(right.LegacyReferences).Distinct().ToArray()),
            Markdown = left.Markdown ?? right.Markdown,
            Coverage = left.Coverage == HarnessCoverage.Failed || right.Coverage == HarnessCoverage.Failed ? HarnessCoverage.Failed
                : left.Coverage == HarnessCoverage.Partial || right.Coverage == HarnessCoverage.Partial ? HarnessCoverage.Partial : HarnessCoverage.CompleteWithinScope };
    }

    private static HarnessGraphEnvelope AlignCompilationLocations(HarnessGraphEnvelope graph, IReadOnlyDictionary<string, int> offsets)
    {
        // Roslyn may consume a real UTF-8 BOM, whereas the saved-source reader retains U+FEFF.
        // Only the exact byte-BOM + compilation-text comparison above permits this one-code-unit shift.
        return graph with { Nodes = Array.AsReadOnly(graph.Nodes.Select(n => n with { Location = Align(n.Location) }).ToArray()),
            SymbolOccurrences = Array.AsReadOnly(graph.SymbolOccurrences.Select(o => o with {
                Location = Align(o.Location), Declarations = o.Declarations is null ? null
                    : Array.AsReadOnly(o.Declarations.Select(l => Align(l)!).ToArray()) }).ToArray()),
            Edges = Array.AsReadOnly(graph.Edges.Select(AlignEdge).ToArray()) };

        HarnessLocation? Align(HarnessLocation? location) => location is { RawSpan: { } span }
            && offsets.TryGetValue(location.SourceId, out var offset) && offset == 1
                ? location with { RawSpan = new(checked(span.Start + 1), span.Length) } : location;
        HarnessEdge AlignEdge(HarnessEdge edge)
        {
            var evidence = Align(edge.Evidence);
            if (evidence == edge.Evidence) return edge;
            if (edge.SourceOccurrenceId is null || edge.TargetOccurrenceId is null || evidence?.ContentHash is null)
                throw new CliIndexException("CLI_SOURCE_COORDINATES_INCONSISTENT");
            return edge with { Evidence = evidence, Id = HarnessIdentity.EdgeId(Guid.ParseExact(graph.WorkspaceId[3..], "N"),
                edge.SourceOccurrenceId, edge.TargetOccurrenceId, edge.Kind, evidence.SourceId, evidence.ContentHash,
                evidence.RawSpan, edge.Producer) };
        }
    }

    private static IReadOnlyList<T> MergeById<T>(IEnumerable<T> left, IEnumerable<T> right, Func<T, string> id) where T : notnull
    {
        var merged = left.ToDictionary(id, StringComparer.Ordinal);
        foreach (var item in right)
        {
            if (merged.TryGetValue(id(item), out var old) && !EqualityComparer<T>.Default.Equals(old, item))
                throw new CliIndexException("CLI_GRAPH_MERGE_CONFLICT");
            merged[id(item)] = item;
        }
        return Array.AsReadOnly(merged.Values.OrderBy(id, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<MarkdownSymbolCandidate> MakeCatalog(HarnessGraphEnvelope graph, SymbolIndex index,
        IReadOnlyList<CollectedEvidence> evidence, IReadOnlyDictionary<string, string> projectPaths)
    {
        var legacy = graph.LegacyReferences.GroupBy(l => l.LegacyId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var occurrences = graph.SymbolOccurrences.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var projects = graph.Variants.ToDictionary(v => v.Id, v => projectPaths[v.ProjectId], StringComparer.Ordinal);
        var types = index.Types.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var namespaces = index.Namespaces.ToDictionary(n => n.Id, n => n.Name, StringComparer.Ordinal);
        var result = new List<MarkdownSymbolCandidate>();
        foreach (var type in index.Types)
            Add(type.Id, type.FullName, type.Name, type.DocumentationId, type.NamespaceId is { } ns ? namespaces.GetValueOrDefault(ns) : null);
        foreach (var member in index.Members)
        {
            var type = types[member.TypeId];
            Add(member.Id, type.FullName + "." + member.Name, member.Name, member.DocumentationId,
                type.NamespaceId is { } ns ? namespaces.GetValueOrDefault(ns) : null);
        }
        foreach (var symbol in evidence.Where(e => e.TargetIsExternal && e.TargetSymbol is not null).Select(e => e.TargetSymbol!).Distinct())
        {
            var typeName = symbol.TypeName.StartsWith("global::", StringComparison.Ordinal) ? symbol.TypeName[8..] : symbol.TypeName;
            var shortName = typeName.Split('.').Last().Split('<')[0];
            // A member call also creates a proven type node, even without its own occurrence/legacy row.
            var typeId = HarnessIdentity.ExternalSymbolId(Guid.ParseExact(graph.WorkspaceId[3..], "N"),
                symbol.AssemblyIdentity, "type", symbol.TypeCanonicalSignature);
            if (graph.Nodes.Any(n => n.Id == typeId && n.Kind == HarnessNodeKind.ExternalSymbol)
                && !(legacy.GetValueOrDefault(symbol.LegacyTypeId)?.Length > 0))
                result.Add(new(typeId, typeName, shortName,
                    symbol.TypeCanonicalSignature.StartsWith("doc:", StringComparison.Ordinal) ? symbol.TypeCanonicalSignature[4..] : null));
            Add(symbol.LegacyTypeId, typeName, shortName,
                symbol.TypeCanonicalSignature.StartsWith("doc:", StringComparison.Ordinal) ? symbol.TypeCanonicalSignature[4..] : null, null);
            if (symbol.LegacyMemberId is { } member)
            {
                // HarnessSignatureOf keeps the real documentation ID as its first field.
                var documentationId = symbol.CanonicalSignature.Split('|', 2)[0];
                Add(member, typeName + "." + symbol.Name, symbol.Name,
                    documentationId.Length > 2 && documentationId[1] == ':' ? documentationId : null, null);
            }
        }
        return Array.AsReadOnly(result.Distinct().ToArray());

        void Add(string oldId, string fullName, string shortName, string? documentationId, string? ns)
        {
            foreach (var reference in legacy.GetValueOrDefault(oldId) ?? [])
            {
                if (reference.OccurrenceId is null) continue;
                var occurrence = occurrences[reference.OccurrenceId];
                var locations = occurrence.Declarations ?? (occurrence.Location is { } primary ? [primary] : []);
                if (locations.Count == 0) result.Add(new(reference.NodeId, fullName, shortName, documentationId,
                    projects[occurrence.VariantId], ns, occurrence.VariantId));
                else foreach (var location in locations)
                    result.Add(new(reference.NodeId, fullName, shortName, documentationId,
                        projects[occurrence.VariantId], ns, occurrence.VariantId, location.SourceId));
            }
        }
    }

    private static IReadOnlyDictionary<string, string> CodeAliases(string documentDirectory, IEnumerable<InputText> sources)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in sources)
        {
            // The shared link target is relative to this document, not implicitly to workspace root.
            var local = Path.GetRelativePath(documentDirectory, input.AbsolutePath).Replace('\\', '/');
            Add(local); if (!local.StartsWith("../", StringComparison.Ordinal)) Add("./" + local);
            void Add(string alias)
            {
                if (result.TryGetValue(alias, out var old) && old != input.File.SourceId)
                    throw new CliIndexException("CLI_CODE_ALIAS_AMBIGUOUS");
                result[alias] = input.File.SourceId;
            }
        }
        return result;
    }

    private static IReadOnlyList<IndexSearchText> MakeSearch(HarnessGraphEnvelope graph,
        IReadOnlyDictionary<string, InputText> manifest, IEnumerable<MarkdownGraphProjection> markdown)
    {
        var filePaths = manifest.Values.ToDictionary(i => i.File.SourceId, i => i.File.RelativePath, StringComparer.Ordinal);
        var locations = graph.SymbolOccurrences.GroupBy(o => o.LogicalSymbolId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(o => o.Declarations ?? (o.Location is { } l ? [l] : []))
                .Select(l => filePaths[l.SourceId]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault(), StringComparer.Ordinal);
        var fields = markdown.SelectMany(m => m.SearchFields).GroupBy(s => s.NodeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var result = new List<IndexSearchText>();
        foreach (var node in graph.Nodes)
        {
            var selected = fields.GetValueOrDefault(node.Id) ?? [];
            var heading = string.Join("\n", selected.Where(f => f.Kind == "heading").Select(f => f.Content.Text ?? "").Distinct());
            var body = string.Join("\n", selected.Where(f => f.Kind != "heading").Select(f => f.Content.Text ?? "").Distinct());
            var path = node.Location is { } location ? filePaths[location.SourceId] : locations.GetValueOrDefault(node.Id) ?? "";
            result.Add(new(node.Id, node.Name, node.Signature ?? "", heading, body, path));
        }
        // Reject over-large selected text instead of silently truncating a searchable document.
        if (result.Any(r => new[] { r.Name, r.Signature, r.Heading, r.Body, r.Path }.Any(s => s.Length > IndexSnapshotValidator.MaximumSearchFieldUtf16))
            || result.Sum(r => (long)r.Name.Length + r.Signature.Length + r.Heading.Length + r.Body.Length + r.Path.Length)
                > IndexSnapshotValidator.MaximumSearchTextUtf16)
            throw new CliIndexException("CLI_SEARCH_BUDGET_EXCEEDED");
        return Array.AsReadOnly(result.ToArray());
    }
}
