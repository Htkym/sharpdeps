// Evidence collected from declarations (SD-009).
//
// A collected record carries the v2 evidence (ids, span, confidence, public surface)
// plus the classification SD-012 needs for aggregation: which project variant and
// namespace the two ends belong to, and whether the target is external. Relations are
// never built here; the aggregator groups evidence into relations so the counting
// rules stay in one place (docs/analysis-semantics.md).

namespace SharpDeps.Analysis.Roslyn.Evidence;

using Microsoft.CodeAnalysis;
using SharpDeps.Analysis.Core.Graph;
using SharpDeps.Analysis.Contracts;

public sealed record CollectedEvidence(
    EvidenceRecord Evidence,
    string SourceVariantId,
    string? SourceNamespaceId,
    string? TargetVariantId,
    string? TargetNamespaceId,
    bool TargetIsExternal,
    string? CanonicalSourceMemberId = null,
    string? CanonicalTargetMemberId = null,
    string? Access = null,
    HarnessTargetSymbol? TargetSymbol = null,
    string? Producer = null,
    IReadOnlyList<HarnessDeclarationOwner>? HarnessDeclarationOwners = null)
{
    /// <summary>A type that depends on itself must not be reported as a cycle.</summary>
    public bool IsSelfReference => Evidence.SourceEntityId == Evidence.TargetEntityId;

    /// <summary>Same namespace: aggregation must not turn this into a namespace self-edge.</summary>
    public bool SameNamespace
        => SourceNamespaceId is not null && SourceNamespaceId == TargetNamespaceId;

    /// <summary>Same project variant: aggregation must not turn this into a project self-edge.</summary>
    public bool SameProjectVariant
        => SourceVariantId == TargetVariantId;

    /// <summary>
    /// Projects the collected record onto the shared graph model (SD-012). One
    /// evidence record is one occurrence; the aggregator groups them into relations.
    /// </summary>
    public GraphEvidence ToGraphEvidence(string basis = "symbolResolved")
        => new(
            Basis: basis,
            SourceEntityId: Evidence.SourceEntityId,
            TargetEntityId: Evidence.TargetEntityId,
            Kind: Evidence.Kind,
            SourceMemberId: Evidence.SourceMemberId,
            DocumentId: Evidence.DocumentId,
            PublicSurface: Evidence.PublicSurface,
            Generated: string.Equals(Evidence.Origin, "generatedSource", StringComparison.Ordinal),
            Confidence: Evidence.Confidence,
            SourceNamespaceId: SourceNamespaceId,
            TargetNamespaceId: TargetNamespaceId,
            SourceProjectId: SourceVariantId,
            TargetProjectId: TargetVariantId,
            TargetIsExternal: TargetIsExternal);
}

/// <summary>Owner-specific declaration provenance, separate from legacy type aggregation.</summary>
public sealed record HarnessDeclarationOwner(string? SourceMemberId, bool PublicSurface);

/// <summary>Normalized external identity inputs; legacy hashes are lookup keys, not signatures.</summary>
public sealed record HarnessTargetSymbol(
    string LegacyTypeId,
    string? LegacyMemberId,
    string Kind,
    string Name,
    string CanonicalSignature,
    string AssemblyIdentity,
    string TypeCanonicalSignature,
    string TypeName);

/// <summary>Effective accessibility, including the containing types.</summary>
public static class PublicSurface
{    public static bool IsExternallyVisible(ISymbol? symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (!IsExternallyVisibleAccessibility(current))
            {
                return false;
            }
        }

        return symbol is not null;
    }

    private static bool IsExternallyVisibleAccessibility(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Microsoft.CodeAnalysis.Accessibility.Public => true,
        Microsoft.CodeAnalysis.Accessibility.Protected => true,
        Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => true,
        // A file-local type reports NotApplicable; everything else here is internal,
        // private, or private-protected and therefore not external surface.
        _ => false
    };
}
