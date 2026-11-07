namespace SharpDeps.Analysis.Roslyn.Symbols;

using System.Security.Cryptography;
using System.Text;
using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Identity;

/// <summary>A fail-closed package binding or registration failure, without local cache paths.</summary>
public sealed class PackageDocumentRegistrationException(string code, string message) : InvalidOperationException(message)
{
    public const string ProvenanceUnidentified = "semantic.packageDocumentProvenanceUnidentified";
    public const string IdentityAmbiguous = "semantic.packageDocumentIdentityAmbiguous";
    public const string BodyConflict = "semantic.packageDocumentBodyConflict";
    public const string PathCollision = "semantic.packageDocumentPathCollision";
    public string Code { get; } = code;
}

public sealed partial class SourceDocumentRegistry
{
    private readonly Dictionary<string, SourceDocument> _packageByIdentity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceDocument> _packageByTree = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _packagePaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers an already verified package input. Only stableVariantIdentity decides persistent identity;
    /// variantKey and fullPath are transient compilation lookup keys, never hashed into the document ID.</summary>
    public SourceDocument RegisterPackage(string variantKey, string projectName, string stableVariantIdentity,
        string packageId, string version, string packageRelativePath, string fullPath, string contentHash, long byteLength)
    {
        var label = $"{packageId}/{version}/{packageRelativePath}";
        if (string.IsNullOrWhiteSpace(stableVariantIdentity) || string.IsNullOrWhiteSpace(packageId)
            || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(packageRelativePath))
            throw new PackageDocumentRegistrationException(PackageDocumentRegistrationException.ProvenanceUnidentified,
                $"Package '{label}' at RegisterPackage has no identified provenance.");
        var identityKey = string.Join('\u001f', stableVariantIdentity, packageId.ToLowerInvariant(), version, packageRelativePath);
        var treeKey = TreeKey(variantKey, fullPath);
        _packageByIdentity.TryGetValue(identityKey, out var existing);
        if (_packageByTree.TryGetValue(treeKey, out var prior) && prior != existing)
            throw new PackageDocumentRegistrationException(PackageDocumentRegistrationException.IdentityAmbiguous,
                $"Package '{label}' at RegisterPackage claims a tree already bound to another package identity.");
        if (existing is not null)
        {
            if (existing.ContentHash != contentHash || existing.ByteLength != byteLength)
                throw new PackageDocumentRegistrationException(PackageDocumentRegistrationException.BodyConflict,
                    $"Package '{label}' at RegisterPackage has conflicting bodies for one identity.");
            _packageByTree[treeKey] = existing;
            return existing;
        }
        static string Segment(string text)
        {
            var value = new string(text.Select(c => char.IsAsciiLetterOrDigit(c)
                || c is '.' or '_' or '-' ? c : '_').ToArray());
            return value is "" or "." or ".." ? "document" : value;
        }
        var discriminator = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityKey))).ToLowerInvariant();
        var path = string.Join('/', new[] { "package", Segment(projectName), Segment(stableVariantIdentity),
            Segment(packageId.ToLowerInvariant()), Segment(version), discriminator }
            .Concat(packageRelativePath.Split('/').Select(Segment)));
        if (!_packagePaths.Add(path))
            throw new PackageDocumentRegistrationException(PackageDocumentRegistrationException.PathCollision,
                $"Package '{label}' at RegisterPackage collides with another virtual path.");
        var document = new SourceDocument(Identity.DocumentId(_packageRootId, path, "packageSource"), path,
            "packageSource", contentHash, byteLength, null);
        _packageByIdentity.Add(identityKey, document);
        _packageByTree.Add(treeKey, document);
        return document;
    }

    public bool IsPackageTree(string variantKey, Microsoft.CodeAnalysis.SyntaxTree tree)
        => !string.IsNullOrEmpty(tree.FilePath) && _packageByTree.ContainsKey(TreeKey(variantKey, tree.FilePath));
}
