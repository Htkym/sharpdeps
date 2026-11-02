namespace SharpDeps.Analysis.Roslyn.Symbols;

using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Identity;

/// <summary>
/// Registers the source documents a symbol index refers to. Document ids match the
/// Quick analyzer's rule (workspace root + relative path + origin), so a file has one
/// id whichever analyzer produced it.
/// </summary>
public sealed class SourceDocumentRegistry
{
    private readonly string _rootId;
    private readonly string _rootDirectory;
    private readonly Dictionary<string, SourceDocument> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _generatedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SourceDocument> _generatedByIdentity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceDocument> _generatedByTree = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SourceDocument> _generated = [];

    public SourceDocumentRegistry(string rootId, string rootDirectory)
    {
        _rootId = rootId;
        _rootDirectory = rootDirectory;
    }

    public IReadOnlyList<SourceDocument> Documents => _byPath.Values
        .Concat(_generated)
        .OrderBy(document => document.RelativePath, StringComparer.Ordinal)
        .ToArray();

    public SourceDocument Register(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath);
        if (_byPath.TryGetValue(normalized, out var existing))
        {
            return existing;
        }

        var relativePath = Identity.NormalizeRelativePath(Path.GetRelativePath(_rootDirectory, normalized));
        var id = Identity.DocumentId(_rootId, relativePath, "userSource");
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(normalized);
        }
        catch
        {
            bytes = [];
        }

        var hash = bytes.Length == 0
            ? "unavailable"
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        var document = new SourceDocument(id, relativePath, "userSource", hash, bytes.LongLength, null);
        _byPath[normalized] = document;
        return document;
    }

    /// <summary>Retains #line coordinates without replacing physical evidence positions.</summary>
    public MappedLocation? MappedLocationFor(Microsoft.CodeAnalysis.SyntaxNode syntax)
    {
        var mapped = syntax.SyntaxTree.GetMappedLineSpan(syntax.Span);
        var physical = syntax.SyntaxTree.GetLineSpan(syntax.Span);
        if (!mapped.IsValid || (!mapped.HasMappedPath && mapped.StartLinePosition == physical.StartLinePosition)) return null;
        var directory = Path.GetDirectoryName(Path.GetFullPath(syntax.SyntaxTree.FilePath)) ?? _rootDirectory;
        string fullPath;
        try { fullPath = Path.GetFullPath(mapped.Path, directory); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return new MappedLocation(Identity.NormalizeRelativePath(Path.GetRelativePath(_rootDirectory, fullPath)),
            mapped.StartLinePosition.Line, mapped.StartLinePosition.Character);
    }

    /// <summary>
    /// Registers a source-generated document under a stable identity: project variant,
    /// generator identity and hint name. The relative path is virtual:
    /// the workspace's obj path never reaches the report, and the generated content is
    /// read from the analysis result, not from the user's repository.
    /// </summary>
    /// <param name="fullPath">
    /// The workspace path of the generated document, used only to match compilation
    /// trees of the same variant to this document; null means the document is only
    /// listed in the manifest. It never decides identity, so the same hint emitted
    /// for several variants or by several generators stays a separate document, and
    /// two different bodies under one identity fail loudly instead of merging.
    /// </param>
    public SourceDocument RegisterGenerated(
        string variantKey,
        string projectName,
        string generatorIdentity,
        string hintName,
        string? fullPath,
        string contentHash,
        long byteLength)
    {
        ArgumentNullException.ThrowIfNull(variantKey);
        ArgumentNullException.ThrowIfNull(projectName);
        ArgumentNullException.ThrowIfNull(generatorIdentity);
        ArgumentNullException.ThrowIfNull(hintName);
        var identityKey = string.Join('\u001f', variantKey, projectName, generatorIdentity, hintName);
        if (_generatedByIdentity.TryGetValue(identityKey, out var existing))
        {
            if (existing.ContentHash != contentHash || existing.ByteLength != byteLength)
                throw new InvalidOperationException(
                    $"The generated document '{hintName}' in '{projectName}' was reported with two different bodies under one identity; refusing to merge them.");
            return existing;
        }

        var relativePath = UniqueGeneratedPath(projectName, variantKey, generatorIdentity, hintName, identityKey);
        var document = new SourceDocument(
            Identity.DocumentId(_rootId, relativePath, "generatedSource"),
            relativePath,
            "generatedSource",
            contentHash,
            byteLength,
            null);
        _generatedByIdentity.Add(identityKey, document);
        _generated.Add(document);
        if (!string.IsNullOrEmpty(fullPath))
        {
            _generatedByTree[TreeKey(variantKey, fullPath)] = document;
        }

        return document;
    }

    /// <summary>
    /// Resolves the document a compilation tree belongs to: the pre-registered
    /// generated document of this variant when the tree is one of its generated
    /// trees, otherwise the physical file. Null when the tree has no path.
    /// </summary>
    public SourceDocument? ResolveTreeDocument(string variantKey, Microsoft.CodeAnalysis.SyntaxTree tree)
    {
        if (string.IsNullOrEmpty(tree.FilePath))
        {
            return null;
        }

        return _generatedByTree.TryGetValue(TreeKey(variantKey, tree.FilePath), out var generated)
            ? generated
            : Register(tree.FilePath);
    }

    /// <summary>True when the tree is a pre-registered generated tree of this variant.</summary>
    public bool IsGeneratedTree(string variantKey, Microsoft.CodeAnalysis.SyntaxTree tree)
        => !string.IsNullOrEmpty(tree.FilePath)
            && _generatedByTree.ContainsKey(TreeKey(variantKey, tree.FilePath));

    private static string TreeKey(string variantKey, string fullPath)
        => variantKey + "\u001f" + Path.GetFullPath(fullPath);

    /// <summary>
    /// A virtual path under <c>generated/</c> built from the identity itself:
    /// project, variant, generator and hint. Distinct identities that sanitize to
    /// the same path are separated by a discriminator hashed from the identity,
    /// never by registration order or the workspace's absolute path.
    /// </summary>
    private string UniqueGeneratedPath(
        string projectName,
        string variantKey,
        string generatorIdentity,
        string hintName,
        string identityKey)
    {
        var segments = new List<string>
        {
            "generated",
            SanitizeSegment(projectName),
            SanitizeVariant(variantKey),
        };
        if (generatorIdentity.Length > 0)
        {
            segments.AddRange(generatorIdentity.Split('/').Select(SanitizeSegment));
        }
        else
        {
            segments.Add("unknown-generator");
        }

        var basePath = string.Join('/', segments) + "/" + SanitizeHint(hintName);
        if (_generatedPaths.Add(basePath))
        {
            return basePath;
        }

        var extension = Path.GetExtension(basePath);
        var stem = extension.Length > 0 ? basePath[..^extension.Length] : basePath;
        var discriminator = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identityKey)))[..8].ToLowerInvariant();
        var candidate = $"{stem}-{discriminator}{extension}";
        if (_generatedPaths.Add(candidate))
        {
            return candidate;
        }

        throw new InvalidOperationException(
            $"Generated document path '{basePath}' collides for distinct identities.");
    }

    /// <summary>The variant part of a variant key ("path|tfm|configuration|platform"), path-safe.</summary>
    private static string SanitizeVariant(string variantKey)
    {
        var parts = variantKey.Split('|');
        var suffix = parts.Length > 1 ? string.Join('_', parts[1..]) : variantKey;
        var cleaned = new string(suffix.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_').ToArray()).Trim('_');
        return cleaned.Length == 0 ? "variant" : cleaned;
    }

    private static string SanitizeSegment(string value)
    {
        var cleaned = value.Replace('\\', '_').Replace('/', '_').Trim();
        return cleaned.Length == 0 ? "project" : cleaned;
    }

    private static string SanitizeHint(string hintName)
    {
        var segments = hintName
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment is not "." and not "..")
            .ToArray();
        var relative = string.Join('/', segments);
        return relative.Length == 0 ? "document.cs" : relative;
    }
}

/// <summary>
/// Maps a document offset to the type declared there. Declarations are kept sorted,
/// and the innermost (smallest enclosing) type wins so a nested type is found inside
/// its outer type.
/// </summary>
public sealed class SymbolPositionIndex
{
    private readonly Dictionary<string, IReadOnlyList<TypeSpan>> _byDocument;

    private SymbolPositionIndex(Dictionary<string, IReadOnlyList<TypeSpan>> byDocument)
    {
        _byDocument = byDocument;
    }

    public int DocumentCount => _byDocument.Count;

    public int EntryCount => _byDocument.Values.Sum(spans => spans.Count);

    public static SymbolPositionIndex Build(IReadOnlyList<IndexedType> types)
    {
        var byDocument = new Dictionary<string, List<TypeSpan>>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            foreach (var declaration in type.Declarations)
            {
                if (!byDocument.TryGetValue(declaration.DocumentId, out var spans))
                {
                    spans = [];
                    byDocument[declaration.DocumentId] = spans;
                }

                spans.Add(new TypeSpan(
                    declaration.Start,
                    declaration.Start + declaration.Length,
                    type.Id,
                    type.FullName,
                    type.ProjectVariantId));
            }
        }

        return new SymbolPositionIndex(byDocument.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<TypeSpan>)[.. entry.Value
                .OrderBy(span => span.Start)
                .ThenBy(span => span.End)],
            StringComparer.Ordinal));
    }

    /// <summary>The innermost type containing the offset, or null when there is none.</summary>
    public string? FindTypeIdAt(string documentId, int offset)
        => FindTypesAt(documentId, offset).FirstOrDefault()?.TypeId;

    /// <summary>
    /// The innermost type per project variant at the offset. The same file is indexed
    /// once per variant it belongs to, so the caller picks the variant of the active
    /// analysis profile instead of one being chosen here.
    /// </summary>
    public IReadOnlyList<TypeSpan> FindTypesAt(string documentId, int offset)
    {
        if (!_byDocument.TryGetValue(documentId, out var spans))
        {
            return [];
        }

        return spans
            .Where(span => offset >= span.Start && offset < span.End)
            .GroupBy(span => span.ProjectVariantId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(span => span.End - span.Start).First())
            .OrderBy(span => span.ProjectVariantId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>A declared type inside one document.</summary>
    public sealed record TypeSpan(int Start, int End, string TypeId, string FullName, string ProjectVariantId);
}
