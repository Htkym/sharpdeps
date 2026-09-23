// Supporting structures for the symbol index (SD-008).

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

    public SourceDocumentRegistry(string rootId, string rootDirectory)
    {
        _rootId = rootId;
        _rootDirectory = rootDirectory;
    }

    public IReadOnlyList<SourceDocument> Documents => _byPath.Values
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
