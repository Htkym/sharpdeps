// Symbol index for the semantic model (SD-008).
//
// Collects namespace/type/member declarations with their owning project variant,
// containment, flags (partial, nested, file-local, generic, top-level), declaration
// locations, and the source documents they live in. The position index answers
// "which type is at this offset" for editor-driven lookups (SD-019).

namespace SharpDeps.Analysis.Roslyn.Symbols;

using Microsoft.CodeAnalysis;
using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Identity;

public sealed record SymbolDeclarationLocation(
    string DocumentId,
    string RelativePath,
    int Start,
    int Length,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter)
{
    public PhysicalSpan ToPhysicalSpan()
        => new(Start, Length, StartLine, StartCharacter, EndLine, EndCharacter);
}

public sealed record IndexedNamespace(
    string Id,
    string ProjectVariantId,
    string Name,
    int TypeCount,
    string? RepresentativeDocumentId);

public sealed record IndexedType(
    string Id,
    string ProjectVariantId,
    string? NamespaceId,
    string? ContainingTypeId,
    string Name,
    string FullName,
    string? DocumentationId,
    string Kind,
    string Accessibility,
    bool IsPartial,
    bool IsNested,
    bool IsFileLocal,
    bool IsExternal,
    bool IsGeneric,
    int Arity,
    IReadOnlyList<SymbolDeclarationLocation> Declarations,
    string? AssemblyIdentity,
    string SymbolKey);

public sealed record IndexedMember(
    string Id,
    string ProjectVariantId,
    string TypeId,
    string Kind,
    string Name,
    string Accessibility,
    string? DocumentationId,
    int Arity,
    string Signature,
    IReadOnlyList<SymbolDeclarationLocation> Declarations);

public sealed record SymbolIndexSummary(
    int ProjectVariantCount,
    int NamespaceCount,
    int TypeCount,
    int MemberCount,
    int PartialTypeCount,
    int NestedTypeCount,
    int FileLocalTypeCount,
    int GenericTypeCount,
    bool HasTopLevelStatements);

public sealed record SymbolIndex(
    IReadOnlyList<IndexedNamespace> Namespaces,
    IReadOnlyList<IndexedType> Types,
    IReadOnlyList<IndexedMember> Members,
    IReadOnlyList<SourceDocument> Documents,
    SymbolPositionIndex PositionIndex,
    SymbolIndexSummary Summary);

/// <summary>One project variant and the compilation that represents it.</summary>
public sealed record SymbolIndexInput(string VariantId, string ProjectName, Compilation Compilation);

public static class SymbolIndexBuilder
{
    /// <summary>
    /// Canonical type display for keys and signatures: fully qualified CLR names, so
    /// "int" and "System.Int32" can never produce two different keys.
    /// </summary>
    public static readonly SymbolDisplayFormat DisplayFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>
    /// Builds the index. The document registry is shared with the evidence collectors
    /// so a file has exactly one document id per analysis.
    /// </summary>
    public static SymbolIndex Build(
        string rootId,
        string rootDirectory,
        IReadOnlyList<SymbolIndexInput> inputs,
        CancellationToken cancellationToken = default)
        => Build(new SourceDocumentRegistry(rootId, rootDirectory), inputs, cancellationToken);

    public static SymbolIndex Build(
        SourceDocumentRegistry documents,
        IReadOnlyList<SymbolIndexInput> inputs,
        CancellationToken cancellationToken = default)
    {
        var namespaces = new List<IndexedNamespace>();
        var types = new List<IndexedType>();
        var members = new List<IndexedMember>();
        var typeCountByNamespace = new Dictionary<string, int>(StringComparer.Ordinal);
        var representativeByNamespace = new Dictionary<string, string>(StringComparer.Ordinal);
        var namespaceNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var hasTopLevelStatements = false;

        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ContainsTopLevelStatements(input.Compilation))
            {
                hasTopLevelStatements = true;
            }

            foreach (var typeSymbol in EnumerateTypes(input.Compilation.Assembly.GlobalNamespace))
            {
                // Implicit types stay out of the model, except the synthesized entry
                // point that owns top-level statements: those statements are real code
                // and must not disappear from the graph.
                if ((typeSymbol.IsImplicitlyDeclared || IsCompilerGenerated(typeSymbol))
                    && !IsTopLevelEntryPoint(typeSymbol))
                {
                    continue;
                }

                var namespaceName = NamespaceOf(typeSymbol);
                var namespaceId = namespaceName.Length == 0
                    ? null
                    : Identity.NamespaceId(input.VariantId, namespaceName);
                var containingType = typeSymbol.ContainingType;
                var containingTypeId = containingType is null
                    ? null
                    : Identity.TypeId(input.VariantId, TypeKeyOf(containingType));
                var declarations = CollectDeclarations(typeSymbol, documents, cancellationToken);
                var isFileLocal = typeSymbol.IsFileLocal;
                var type = new IndexedType(
                    Id: Identity.TypeId(input.VariantId, TypeKeyOf(typeSymbol)),
                    ProjectVariantId: input.VariantId,
                    NamespaceId: namespaceId,
                    ContainingTypeId: containingTypeId,
                    Name: typeSymbol.Name,
                    FullName: FullNameOf(typeSymbol),
                    DocumentationId: typeSymbol.GetDocumentationCommentId(),
                    Kind: KindOf(typeSymbol),
                    Accessibility: AccessibilityOf(typeSymbol),
                    IsPartial: IsPartial(typeSymbol),
                    IsNested: containingType is not null,
                    IsFileLocal: isFileLocal,
                    IsExternal: false,
                    IsGeneric: typeSymbol.Arity > 0,
                    Arity: typeSymbol.Arity,
                    Declarations: declarations,
                    AssemblyIdentity: typeSymbol.ContainingAssembly?.Identity.ToString(),
                    SymbolKey: TypeKeyOf(typeSymbol));

                types.Add(type);

                if (namespaceId is not null)
                {
                    namespaceNames[namespaceId] = namespaceName;
                    typeCountByNamespace[namespaceId] = typeCountByNamespace.GetValueOrDefault(namespaceId) + 1;
                    representativeByNamespace.TryAdd(namespaceId, declarations.FirstOrDefault()?.DocumentId ?? string.Empty);
                }

                foreach (var member in typeSymbol.GetMembers())
                {
                    if (member.IsImplicitlyDeclared || member is INamedTypeSymbol || member is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove })
                    {
                        continue;
                    }

                    var memberDeclarations = CollectDeclarations(member, documents, cancellationToken);
                    if (memberDeclarations.Count == 0)
                    {
                        continue;
                    }

                    var parameterTypes = member is IMethodSymbol method
                        ? ParameterTypeKeys(method)
                        : [];

                    members.Add(new IndexedMember(
                        Id: Identity.MemberId(
                            input.VariantId,
                            Identity.MemberKey(
                                member.GetDocumentationCommentId(),
                                TypeKeyOf(typeSymbol),
                                member.Name,
                                parameterTypes)),
                        ProjectVariantId: input.VariantId,
                        TypeId: type.Id,
                        Kind: MemberKindOf(member),
                        Name: member.Name,
                        Accessibility: AccessibilityOf(member),
                        DocumentationId: member.GetDocumentationCommentId(),
                        Arity: member is IMethodSymbol named ? named.Arity : 0,
                        Signature: MemberSignatureOf(member),
                        Declarations: memberDeclarations));
                }
            }

            // Namespaces are derived from the declared types so the set matches what
            // the model can actually contain.
            foreach (var namespaceName in types
                         .Where(type => type.ProjectVariantId == input.VariantId)
                         .Select(type => type.NamespaceId is not null ? namespaceNames[type.NamespaceId] : string.Empty)
                         .Where(name => name.Length > 0)
                         .Distinct(StringComparer.Ordinal))
            {
                var namespaceId = Identity.NamespaceId(input.VariantId, namespaceName);
                namespaces.Add(new IndexedNamespace(
                    namespaceId,
                    input.VariantId,
                    namespaceName,
                    typeCountByNamespace.GetValueOrDefault(namespaceId),
                    representativeByNamespace.GetValueOrDefault(namespaceId)));
            }
        }

        var positionIndex = SymbolPositionIndex.Build(types);
        var summary = new SymbolIndexSummary(
            ProjectVariantCount: inputs.Count,
            NamespaceCount: namespaces.Count,
            TypeCount: types.Count,
            MemberCount: members.Count,
            PartialTypeCount: types.Count(type => type.IsPartial),
            NestedTypeCount: types.Count(type => type.IsNested),
            FileLocalTypeCount: types.Count(type => type.IsFileLocal),
            GenericTypeCount: types.Count(type => type.IsGeneric),
            HasTopLevelStatements: hasTopLevelStatements);

        return new SymbolIndex(
            namespaces.OrderBy(node => node.ProjectVariantId, StringComparer.Ordinal)
                .ThenBy(node => node.Name, StringComparer.Ordinal)
                .ToArray(),
            types.OrderBy(type => type.Id, StringComparer.Ordinal).ToArray(),
            members.OrderBy(member => member.Id, StringComparer.Ordinal).ToArray(),
            documents.Documents,
            positionIndex,
            summary);
    }

    /// <summary>Stable declaration key: documentation id first, structural key second.</summary>
    public static string TypeKeyOf(INamedTypeSymbol type)
    {
        var key = Identity.TypeKey(
            type.GetDocumentationCommentId(),
            NamespaceOf(type),
            ContainingTypeNames(type),
            type.Name,
            type.Arity);
        for (var owner = type; owner is not null; owner = owner.ContainingType)
        {
            if (owner.IsFileLocal)
            {
                var file = owner.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
                return key + "|file:" + Identity.NormalizeRelativePath(file ?? owner.MetadataName);
            }
        }
        return key;
    }

    private static IReadOnlyList<string> ContainingTypeNames(INamedTypeSymbol type)
    {
        var names = new List<string>();
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            names.Insert(0, outer.Name);
        }

        return names;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol root)
    {
        foreach (var namespaceSymbol in root.GetNamespaceMembers().OrderBy(node => node.Name, StringComparer.Ordinal))
        {
            foreach (var nested in EnumerateTypes(namespaceSymbol))
            {
                yield return nested;
            }
        }

        foreach (var type in root.GetTypeMembers().OrderBy(node => node.Name, StringComparer.Ordinal))
        {
            yield return type;
            foreach (var nested in EnumerateNestedTypes(type))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers().OrderBy(node => node.Name, StringComparer.Ordinal))
        {
            yield return nested;
            foreach (var deeper in EnumerateNestedTypes(nested))
            {
                yield return deeper;
            }
        }
    }

    private static IReadOnlyList<SymbolDeclarationLocation> CollectDeclarations(
        ISymbol symbol,
        SourceDocumentRegistry documents,
        CancellationToken cancellationToken)
    {
        var locations = new List<SymbolDeclarationLocation>();
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tree = reference.SyntaxTree;
            if (string.IsNullOrEmpty(tree.FilePath))
            {
                continue;
            }

            var document = documents.Register(tree.FilePath);
            var span = reference.Span;
            var lineSpan = tree.GetLineSpan(span);
            locations.Add(new SymbolDeclarationLocation(
                document.Id,
                document.RelativePath,
                span.Start,
                span.Length,
                lineSpan.StartLinePosition.Line,
                lineSpan.StartLinePosition.Character,
                lineSpan.EndLinePosition.Line,
                lineSpan.EndLinePosition.Character));
        }

        return locations
            .OrderBy(location => location.DocumentId, StringComparer.Ordinal)
            .ThenBy(location => location.Start)
            .ToArray();
    }

    private static bool ContainsTopLevelStatements(Compilation compilation)
        => compilation.SyntaxTrees.Any(tree =>
            tree.GetRoot().ChildNodes().Any(node => node is Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax));

    /// <summary>The compiler-generated type that owns top-level statements.</summary>
    internal static bool IsTopLevelEntryPoint(INamedTypeSymbol type)
        => type.GetMembers().OfType<IMethodSymbol>()
            .Any(method => method.Name is "<Main>$" && method.IsImplicitlyDeclared);

    private static bool IsCompilerGenerated(INamedTypeSymbol type)
        => type.Name.StartsWith('<')
            || type.Name.Contains("AnonymousType", StringComparison.Ordinal)
            || type.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.Name is "CompilerGeneratedAttribute" or "GeneratedCodeAttribute");

    /// <summary>Partial declarations merge into one type with several locations.</summary>
    private static bool IsPartial(INamedTypeSymbol type)
        => type.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax declaration
            && declaration.Modifiers.Any(modifier => modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)));

    private static string NamespaceOf(INamedTypeSymbol type)
    {
        var name = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return name == "<global namespace>" ? string.Empty : name;
    }

    private static string FullNameOf(INamedTypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));

    private static string KindOf(INamedTypeSymbol type) => type switch
    {
        { TypeKind: TypeKind.Interface } => "interface",
        { TypeKind: TypeKind.Enum } => "enum",
        { TypeKind: TypeKind.Delegate } => "delegate",
        { TypeKind: TypeKind.Struct } => type.IsRecord ? "record" : "struct",
        { TypeKind: TypeKind.Class } => type.IsRecord ? "record" : "class",
        _ => "unknown"
    };

    private static string AccessibilityOf(ISymbol symbol)
    {
        // File-local types report internal accessibility but are visible only in their
        // own file, so they are reported separately.
        if (symbol is INamedTypeSymbol { IsFileLocal: true })
        {
            return "file";
        }

        return symbol.DeclaredAccessibility switch
    {
            Microsoft.CodeAnalysis.Accessibility.Public => "public",
            Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
            Microsoft.CodeAnalysis.Accessibility.Protected => "protected",
            Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private",
            Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "protected",
            Microsoft.CodeAnalysis.Accessibility.Private => "private",
            Microsoft.CodeAnalysis.Accessibility.NotApplicable => "unknown",
            _ => "unknown"
        };
    }

    private static string MemberKindOf(ISymbol member) => member switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "constructor",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => "other"
    };

    /// <summary>
    /// Parameter types in the same fully qualified format the member key hashes, so
    /// the key and the display string never disagree.
    /// </summary>
    public static string[] ParameterTypeKeys(IMethodSymbol method)
        => method.Parameters
            .Select(parameter => parameter.Type.ToDisplayString(DisplayFormat))
            .ToArray();

    private static string MemberSignatureOf(ISymbol member) => member switch
    {
        IMethodSymbol method =>
            $"({string.Join(", ", ParameterTypeKeys(method))})"
            + $" -> {method.ReturnType.ToDisplayString(DisplayFormat)}",
        IPropertySymbol property => property.Type.ToDisplayString(DisplayFormat),
        IFieldSymbol field => field.Type.ToDisplayString(DisplayFormat),
        IEventSymbol @event => @event.Type.ToDisplayString(DisplayFormat),
        _ => string.Empty
    };
}
