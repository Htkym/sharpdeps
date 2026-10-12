// Source index for the Quick v2 model (SD-006).
//
// The Quick analysis already reads every C# file and every project file; this
// index records what it read (documents, declaration and using positions, declared
// ProjectReference items) so the report v2 mapper can attach evidence without a
// second pass over the sources.

namespace SharpDeps.Analysis.Quick;

using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Identity;

public sealed record QuickDocument(
    string Id,
    string FullPath,
    string RelativePath,
    string ContentHash,
    string Origin,
    long ByteLength);

public sealed record QuickSpan(
    int Start,
    int Length,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter)
{
    /// <summary>Stable key used in evidence ids: line, character, and length.</summary>
    public string SpanKey => $"{StartLine}:{StartCharacter}:{Length}";

    public PhysicalSpan ToPhysicalSpan()
        => new(Start, Length, StartLine, StartCharacter, EndLine, EndCharacter);
}

public sealed record QuickUsing(string NamespaceName, QuickSpan Span);

public sealed record QuickNamespaceDeclaration(
    string ProjectPath,
    string DocumentId,
    string NamespaceName,
    QuickSpan Span);

public sealed record QuickFileUsage(
    string ProjectPath,
    string DocumentId,
    IReadOnlyList<string> DeclaredNamespaces,
    IReadOnlyList<QuickUsing> Usings);

public sealed record QuickProjectReference(
    string SourceProjectPath,
    string TargetFullPath,
    string IncludePath,
    bool IsConditional,
    string DocumentId,
    QuickSpan? Span);

/// <summary>A project-level `global using` directive, which applies to every file of the project.</summary>
public sealed record QuickGlobalUsing(
    string ProjectPath,
    string NamespaceName,
    string DocumentId,
    QuickSpan Span);

/// <summary>A file that was not analyzed, with the reason (never silently dropped).</summary>
public sealed record QuickFileSkip(string RelativePath, string Reason);

public sealed record QuickTypeSegment(string Name, int Arity);

public sealed record QuickTypeDeclaration(
    string ProjectPath, string DocumentId, IReadOnlyList<string> NamespaceSegments, string SourceNamespaceName,
    IReadOnlyList<QuickTypeSegment> TypeSegments, IReadOnlyList<QuickSpan> ContainingSpans,
    string SyntaxKind, string Kind, string Accessibility, bool IsPartial, bool IsFileScoped,
    bool HasSyntaxErrors, QuickSpan Span);

public sealed record QuickTypeBaseReference(
    string ProjectPath, string DocumentId, int DeclarationStart, int DeclarationLength, string Shape,
    IReadOnlyList<string> NameSegments, QuickSpan Span);

public sealed record QuickTypeLookupBarrier(string ProjectPath, string? DocumentId);

public sealed record QuickSourceIndex(
    IReadOnlyList<QuickDocument> Documents,
    IReadOnlyList<QuickNamespaceDeclaration> NamespaceDeclarations,
    IReadOnlyList<QuickFileUsage> FileUsages,
    IReadOnlyList<QuickProjectReference> ProjectReferences,
    IReadOnlyList<QuickGlobalUsing> GlobalUsings,
    IReadOnlyList<QuickFileSkip> Skips,
    IReadOnlyList<QuickTypeDeclaration>? TypeDeclarations = null,
    IReadOnlyList<QuickTypeBaseReference>? TypeBaseReferences = null,
    IReadOnlyList<QuickTypeLookupBarrier>? TypeLookupBarriers = null,
    int TypeDeclarationsOmitted = 0,
    int TypeReferencesOmitted = 0);

/// <summary>Collects the source index while the normal Quick analysis runs.</summary>
public sealed class QuickSourceIndexCollector
{
    private readonly string _rootId;
    private readonly string _solutionDirectory;
    private readonly Dictionary<string, QuickDocument> _documents = new(StringComparer.Ordinal);
    private readonly List<QuickNamespaceDeclaration> _declarations = [];
    private readonly List<QuickFileUsage> _fileUsages = [];
    private readonly List<QuickProjectReference> _projectReferences = [];
    private readonly List<QuickGlobalUsing> _globalUsings = [];
    private readonly List<QuickFileSkip> _skips = [];
    // Per analysis, independent of display budgets. Omission disables name binding.
    public const int MaxTypeDeclarations = 40000;
    public const int MaxTypeReferences = 80000;
    public const int MaxTypeDepth = 32;
    public const int MaxNamespaceParts = 64;
    public const int MaxQualifiedParts = MaxNamespaceParts + MaxTypeDepth;
    public const long MaxRetainedTypeNameUnits = 8L * 1024 * 1024;
    private readonly List<QuickTypeDeclaration> _types = [];
    private readonly List<QuickTypeBaseReference> _typeReferences = [];
    private readonly HashSet<QuickTypeLookupBarrier> _lookupBarriers = [];
    private int _typeDeclarationsOmitted;
    private int _typeReferencesOmitted;
    private long _retainedTypeNameUnits;

    public QuickSourceIndexCollector(string rootId, string solutionDirectory)
    {
        _rootId = rootId;
        _solutionDirectory = solutionDirectory;
    }

    public static string ComputeContentHash(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    public QuickDocument AddDocument(string fullPath, byte[] bytes, string contentHash, string origin = "userSource")
    {
        var relativePath = ToRelativePath(fullPath);
        var id = Identity.DocumentId(_rootId, relativePath, origin);
        if (_documents.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var document = new QuickDocument(id, fullPath, relativePath, contentHash, origin, bytes.LongLength);
        _documents[id] = document;
        return document;
    }

    public void AddSkip(string fullPath, string reason)
        => _skips.Add(new QuickFileSkip(ToRelativePath(fullPath), reason));

    public void AddDeclaration(QuickNamespaceDeclaration declaration)
        => _declarations.Add(declaration);

    public void AddFileUsage(QuickFileUsage usage)
        => _fileUsages.Add(usage);

    public void AddProjectReference(QuickProjectReference reference)
        => _projectReferences.Add(reference);

    public void AddGlobalUsing(QuickGlobalUsing globalUsing)
        => _globalUsings.Add(globalUsing);

    public void AddTypeDeclaration(QuickTypeDeclaration declaration)
    {
        var units = declaration.NamespaceSegments.Sum(name => (long)name.Length)
            + declaration.SourceNamespaceName.Length
            + declaration.TypeSegments.Sum(segment => (long)segment.Name.Length)
            + (declaration.IsFileScoped && _documents.TryGetValue(declaration.DocumentId, out var document) ? document.RelativePath.Length : 0);
        if (_types.Count < MaxTypeDeclarations && units <= MaxRetainedTypeNameUnits - _retainedTypeNameUnits)
        {
            _types.Add(declaration);
            _retainedTypeNameUnits += units;
        }
        else OmitTypeDeclaration();
    }

    public void AddTypeReference(QuickTypeBaseReference reference)
    {
        var units = reference.NameSegments.Sum(name => (long)name.Length);
        if (_typeReferences.Count < MaxTypeReferences && units <= MaxRetainedTypeNameUnits - _retainedTypeNameUnits)
        {
            _typeReferences.Add(reference);
            _retainedTypeNameUnits += units;
        }
        else _typeReferencesOmitted++;
    }

    public void OmitTypeDeclaration() => _typeDeclarationsOmitted++;

    public void AddTypeLookupBarrier(string projectPath, string? documentId)
        => _lookupBarriers.Add(new QuickTypeLookupBarrier(projectPath, documentId));

    public QuickSourceIndex Build() => new(
        [.. _documents.Values.OrderBy(document => document.RelativePath, StringComparer.Ordinal)],
        _declarations,
        _fileUsages,
        _projectReferences,
        _globalUsings,
        _skips,
        _types,
        _typeReferences,
        [.. _lookupBarriers],
        _typeDeclarationsOmitted,
        _typeReferencesOmitted);

    private string ToRelativePath(string fullPath)
        => Identity.NormalizeRelativePath(Path.GetRelativePath(_solutionDirectory, fullPath));
}
