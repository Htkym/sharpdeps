namespace SharpDeps.Analysis.Roslyn.Symbols;

using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Identity;

/// <summary>
/// A generated document cannot be registered under one stable identity. The code
/// distinguishes an unidentified generator from an ambiguous identity (and from the
/// remaining registration conflicts) so a caller can report the cause, not just a
/// bare failure. It stays an <see cref="InvalidOperationException"/> so existing
/// callers keep treating it as an unsupported generated input.
/// </summary>
public sealed class GeneratedDocumentRegistrationException : InvalidOperationException
{
    /// <summary>The generator identity was empty, so the document cannot be keyed.</summary>
    public const string GeneratorUnidentified = "semantic.generatedDocumentGeneratorUnidentified";

    /// <summary>One workspace tree was claimed by two different generated identities.</summary>
    public const string IdentityAmbiguous = "semantic.generatedDocumentIdentityAmbiguous";

    /// <summary>One identity was reported with two different bodies.</summary>
    public const string BodyConflict = "semantic.generatedDocumentBodyConflict";

    /// <summary>Two distinct identities produced the same virtual path.</summary>
    public const string PathCollision = "semantic.generatedDocumentPathCollision";

    public GeneratedDocumentRegistrationException(string code, string message)
        : base(message)
        => Code = code;

    /// <summary>The diagnostic code naming the cause.</summary>
    public string Code { get; }
}

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
    /// A document whose generator could not be identified is refused: an empty
    /// identity would let two generators that share a hint and a body merge.
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
        if (string.IsNullOrWhiteSpace(generatorIdentity))
            throw new GeneratedDocumentRegistrationException(
                GeneratedDocumentRegistrationException.GeneratorUnidentified,
                $"The generator of the generated document '{hintName}' in '{projectName}' could not be identified"
                + (fullPath is null ? string.Empty : $" from its generated file path '{fullPath}'")
                + "; refusing to index it.");
        var identityKey = string.Join('\u001f', variantKey, projectName, generatorIdentity, hintName);
        if (_generatedByIdentity.TryGetValue(identityKey, out var existing))
        {
            if (existing.ContentHash != contentHash || existing.ByteLength != byteLength)
                throw new GeneratedDocumentRegistrationException(
                    GeneratedDocumentRegistrationException.BodyConflict,
                    $"The generated document '{hintName}' in '{projectName}' was reported with two different bodies under one identity; refusing to merge them.");
            return existing;
        }

        // One workspace tree belongs to one generated identity. A second identity
        // claiming the same tree is ambiguous, so it fails instead of overwriting.
        var treeKey = string.IsNullOrEmpty(fullPath) ? null : TreeKey(variantKey, fullPath);
        if (treeKey is not null && _generatedByTree.TryGetValue(treeKey, out var priorTree))
            throw new GeneratedDocumentRegistrationException(
                GeneratedDocumentRegistrationException.IdentityAmbiguous,
                $"The generated tree '{fullPath}' was reported for two different generated identities "
                + $"('{priorTree.RelativePath}' and '{projectName}/{generatorIdentity}/{hintName}'); refusing to merge them.");

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
        if (treeKey is not null)
        {
            _generatedByTree[treeKey] = document;
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
    /// project, variant, generator and hint, plus a discriminator hashed from the
    /// identity. The discriminator is unconditional, so the path (and the id derived
    /// from it) is a pure function of the identity: it never depends on registration
    /// order or on which other documents were registered.
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
        segments.AddRange(generatorIdentity.Split('/').Select(SanitizeSegment));

        var basePath = string.Join('/', segments) + "/" + SanitizeHint(hintName);
        var extension = Path.GetExtension(basePath);
        var stem = extension.Length > 0 ? basePath[..^extension.Length] : basePath;
        var discriminator = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identityKey)))[..8].ToLowerInvariant();
        var path = $"{stem}-{discriminator}{extension}";
        if (!_generatedPaths.Add(path))
        {
            throw new GeneratedDocumentRegistrationException(
                GeneratedDocumentRegistrationException.PathCollision,
                $"Generated document path '{path}' collides for distinct identities.");
        }

        return path;
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
