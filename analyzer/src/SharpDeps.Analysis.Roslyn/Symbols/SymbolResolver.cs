// Symbol-to-id resolution across compilations (SD-008).
//
// A type declared in a referenced project must resolve to the id of the *defining*
// project variant, not to a copy owned by the referencing project. Project variants
// are matched by assembly name through the resolved reference closure, which keeps
// multi-targeted projects apart ("Domain" exists as net10.0 and netstandard2.0).
//
// Types the solution does not declare are external: they are keyed by assembly
// identity so the same referenced type is one node and no NuGet package is guessed.

namespace SharpDeps.Analysis.Roslyn.Symbols;

using Microsoft.CodeAnalysis;

public sealed class SymbolResolver
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _definingVariantByAssembly;

    /// <param name="definingVariantByAssembly">
    /// For every project variant key: the assembly name of the variant itself and of
    /// every project it references (direct or transitive) mapped to that project's
    /// variant key. Built by <see cref="SemanticReferenceMap"/>.
    /// </param>
    public SymbolResolver(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> definingVariantByAssembly)
    {
        _definingVariantByAssembly = definingVariantByAssembly;
    }

    /// <summary>
    /// Resolves the id of a type, or null when the type cannot be determined
    /// (type parameters, error types, anonymous types). Callers record null as
    /// unresolved instead of fabricating an edge.
    /// </summary>
    public string? ResolveTypeId(ITypeSymbol? type, string ownerVariantKey)
    {
        var target = Normalize(type);
        if (target is null)
        {
            return null;
        }

        if (DeclaringVariantOf(target, ownerVariantKey) is { } variantKey)
        {
            return Core.Identity.Identity.TypeId(variantKey, SymbolIndexBuilder.TypeKeyOf(target));
        }

        return Core.Identity.Identity.ExternalTypeId(AssemblyIdentityOf(target), SymbolIndexBuilder.TypeKeyOf(target));
    }

    /// <summary>Resolves the id of a member, or null when it cannot be determined.</summary>
    public string? ResolveMemberId(ISymbol? member, string ownerVariantKey)
    {
        if (member is null || member is INamedTypeSymbol || member is ITypeSymbol)
        {
            return null;
        }

        var containingType = Normalize(member.ContainingType);
        if (containingType is null)
        {
            return null;
        }

        var key = Core.Identity.Identity.MemberKey(
            member.GetDocumentationCommentId(),
            SymbolIndexBuilder.TypeKeyOf(containingType),
            member.Name,
            member is IMethodSymbol method ? SymbolIndexBuilder.ParameterTypeKeys(method) : null);

        return DeclaringVariantOf(containingType, ownerVariantKey) is { } variantKey
            ? Core.Identity.Identity.MemberId(variantKey, key)
            : Core.Identity.Identity.MemberId(AssemblyIdentityOf(containingType), key);
    }

    /// <summary>True when the type is defined outside the analyzed projects.</summary>
    public bool IsExternal(ITypeSymbol? type, string ownerVariantKey)
        => Normalize(type) is { } target && DeclaringVariantOf(target, ownerVariantKey) is null;

    public bool TryResolveTypeId(ITypeSymbol? type, string ownerVariantKey, out string typeId)
    {
        var resolved = ResolveTypeId(type, ownerVariantKey);
        typeId = resolved ?? string.Empty;
        return resolved is not null;
    }

    private string? DeclaringVariantOf(INamedTypeSymbol type, string ownerVariantKey)
    {
        var assemblyName = type.ContainingAssembly?.Name;
        if (string.IsNullOrEmpty(assemblyName))
        {
            return null;
        }

        return _definingVariantByAssembly.TryGetValue(ownerVariantKey, out var map)
            && map.TryGetValue(assemblyName, out var variantKey)
                ? variantKey
                : null;
    }

    /// <summary>
    /// Reduces a type to a definition symbol: constructed generics resolve to their
    /// definition (type arguments are collected separately), and arrays/pointers
    /// resolve to their element type.
    /// </summary>
    private static INamedTypeSymbol? Normalize(ITypeSymbol? type) => type switch
    {
        null => null,
        IArrayTypeSymbol array => Normalize(array.ElementType),
        IPointerTypeSymbol pointer => Normalize(pointer.PointedAtType),
        ITypeParameterSymbol => null,
        IDynamicTypeSymbol => null,
        IErrorTypeSymbol => null,
        INamedTypeSymbol { IsAnonymousType: true } => null,
        INamedTypeSymbol named => named.OriginalDefinition,
        _ => null
    };

    private static string AssemblyIdentityOf(INamedTypeSymbol type)
        => type.ContainingAssembly?.Identity.ToString() ?? "unknown, Version=0.0.0.0";
}

/// <summary>
/// Builds the per-variant "assembly name → defining variant" map from the resolved
/// project references (direct and transitive), matching how the loader compiles.
/// </summary>
public static class SemanticReferenceMap
{
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Build(
        Solution solution,
        Func<Project, string?> variantKeyOf)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
            {
                continue;
            }

            var ownerVariant = variantKeyOf(project);
            if (string.IsNullOrEmpty(ownerVariant))
            {
                continue;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(project.AssemblyName))
            {
                map[project.AssemblyName] = ownerVariant;
            }

            var queue = new Queue<ProjectId>(project.ProjectReferences.Select(reference => reference.ProjectId));
            var seen = new HashSet<ProjectId>(queue);
            while (queue.Count > 0)
            {
                var target = solution.GetProject(queue.Dequeue());
                if (target is null || target.Language != LanguageNames.CSharp)
                {
                    continue;
                }

                var targetVariant = variantKeyOf(target);
                if (!string.IsNullOrEmpty(targetVariant) && !string.IsNullOrEmpty(target.AssemblyName))
                {
                    map[target.AssemblyName] = targetVariant;
                }

                foreach (var reference in target.ProjectReferences)
                {
                    if (seen.Add(reference.ProjectId))
                    {
                        queue.Enqueue(reference.ProjectId);
                    }
                }
            }

            result[ownerVariant] = map;
        }

        return result;
    }
}
