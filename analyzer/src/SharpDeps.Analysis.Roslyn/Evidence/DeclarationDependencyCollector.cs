// Declaration-based dependency collector (SD-009).
//
// Collects the relations that a declaration states about itself: inheritance,
// interface implementation, member signatures, generic constraints, and attributes.
// Only what the source writes is collected: implicit bases (System.Object), compiler
// generated interfaces (records), and enum underlying types are not emitted, so the
// graph is not filled with implicit dependencies.
//
// Type references are normalized so type arguments, tuple elements, and array
// elements each become their own reference with their own span (see
// docs/analysis-semantics.md). typeof/casts/patterns are SD-010's `typeUse`, not here.

namespace SharpDeps.Analysis.Roslyn.Evidence;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Roslyn.Symbols;

public sealed class DeclarationDependencyCollector
{
    private readonly SymbolResolver _resolver;
    private readonly SourceDocumentRegistry _documents;
    private readonly SymbolIndex _index;
    private readonly string _profileHash;
    private readonly Dictionary<string, IndexedType> _typesById;
    private readonly Dictionary<string, string> _contentHashByDocument;
    private readonly HashSet<string> _seen;

    private readonly ExternalTypeRegistry? _externalTypes;

    public DeclarationDependencyCollector(
        SymbolResolver resolver,
        SourceDocumentRegistry documents,
        SymbolIndex index,
        string profileHash,
        ExternalTypeRegistry? externalTypes = null)
    {
        _externalTypes = externalTypes;
        _resolver = resolver;
        _documents = documents;
        _index = index;
        _profileHash = profileHash;
        _typesById = index.Types.ToDictionary(type => type.Id, StringComparer.Ordinal);
        _contentHashByDocument = index.Documents.ToDictionary(
            document => document.Id,
            document => document.ContentHash,
            StringComparer.Ordinal);
        _seen = [];
    }

    public IReadOnlyList<CollectedEvidence> Collect(
        IReadOnlyList<SymbolIndexInput> inputs,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CollectedEvidence>();

        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var tree in input.Compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var model = input.Compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    switch (node)
                    {
                        case BaseTypeDeclarationSyntax typeDeclaration:
                            CollectTypeDeclaration(model, input, typeDeclaration, results);
                            break;
                        case DelegateDeclarationSyntax delegateDeclaration:
                            CollectDelegate(model, input, delegateDeclaration, results);
                            break;
                    }
                }
            }
        }

        return results;
    }

    private void CollectTypeDeclaration(
        SemanticModel model,
        SymbolIndexInput input,
        BaseTypeDeclarationSyntax declaration,
        List<CollectedEvidence> results)
    {
        if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type)
        {
            return;
        }

        var sourceTypeId = Identity.TypeId(input.VariantId, SymbolIndexBuilder.TypeKeyOf(type));
        if (!_typesById.ContainsKey(sourceTypeId))
        {
            // Not part of the index (implicit or generated): declarations about it are
            // not part of the model either.
            return;
        }

        var publicSurface = PublicSurface.IsExternallyVisible(type);

        if (declaration.BaseList is not null)
        {
            foreach (var baseType in declaration.BaseList.Types)
            {
                // Interfaces are implemented, everything else is inherited. Only the
                // written base list is considered, so implicit object bases stay out.
                var target = model.GetTypeInfo(baseType.Type).Type;
                var kind = target?.TypeKind == TypeKind.Interface ? "implements" : "inherits";
                CollectTypeSyntax(model, input, baseType.Type, sourceTypeId, null, publicSurface, kind, results);
            }
        }

        CollectAttributes(model, input, declaration.AttributeLists, sourceTypeId, null, publicSurface, results);

        if (declaration is not TypeDeclarationSyntax typeDeclaration)
        {
            return;
        }

        CollectConstraints(model, input, typeDeclaration.ConstraintClauses, sourceTypeId, null, publicSurface, results);

        if (declaration is RecordDeclarationSyntax record && record.ParameterList is not null)
        {
            // Positional record parameters become properties; their types are part of
            // the type's surface, attributed to the primary constructor.
            var constructorId = record.ParameterList.Parameters.Count > 0
                ? _resolver.ResolveMemberId(model.GetDeclaredSymbol(record.ParameterList.Parameters[0]), input.VariantId)
                : null;
            foreach (var parameter in record.ParameterList.Parameters)
            {
                if (parameter.Type is not null)
                {
                    CollectTypeSyntax(
                        model,
                        input,
                        parameter.Type,
                        sourceTypeId,
                        constructorId,
                        publicSurface,
                        "signature",
                        results);
                }
            }
        }

        foreach (var member in typeDeclaration.Members)
        {
            CollectMember(model, input, member, sourceTypeId, results);
        }
    }

    private void CollectDelegate(
        SemanticModel model,
        SymbolIndexInput input,
        DelegateDeclarationSyntax declaration,
        List<CollectedEvidence> results)
    {
        if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type)
        {
            return;
        }

        var sourceTypeId = Identity.TypeId(input.VariantId, SymbolIndexBuilder.TypeKeyOf(type));
        if (!_typesById.ContainsKey(sourceTypeId))
        {
            return;
        }

        var publicSurface = PublicSurface.IsExternallyVisible(type);
        CollectTypeSyntax(model, input, declaration.ReturnType, sourceTypeId, null, publicSurface, "signature", results);
        foreach (var parameter in declaration.ParameterList.Parameters)
        {
            if (parameter.Type is not null)
            {
                CollectTypeSyntax(model, input, parameter.Type, sourceTypeId, null, publicSurface, "signature", results);
            }
        }

        CollectConstraints(model, input, declaration.ConstraintClauses, sourceTypeId, null, publicSurface, results);
        CollectAttributes(model, input, declaration.AttributeLists, sourceTypeId, null, publicSurface, results);
    }

    private void CollectMember(
        SemanticModel model,
        SymbolIndexInput input,
        MemberDeclarationSyntax member,
        string sourceTypeId,
        List<CollectedEvidence> results)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
            {
                var symbol = model.GetDeclaredSymbol(method);
                Emit(
                    model,
                    input,
                    [method.ReturnType],
                    method.ParameterList.Parameters,
                    sourceTypeId,
                    symbol,
                    method.ConstraintClauses,
                    method.AttributeLists,
                    results);
                return;
            }

            case ConstructorDeclarationSyntax constructor:
            {
                Emit(
                    model,
                    input,
                    [],
                    constructor.ParameterList.Parameters,
                    sourceTypeId,
                    model.GetDeclaredSymbol(constructor),
                    SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    constructor.AttributeLists,
                    results);
                return;
            }

            case PropertyDeclarationSyntax property:
            {
                var symbol = model.GetDeclaredSymbol(property);
                Emit(
                    model,
                    input,
                    [property.Type],
                    null,
                    sourceTypeId,
                    symbol,
                    SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    property.AttributeLists,
                    results);
                return;
            }

            case IndexerDeclarationSyntax indexer:
            {
                Emit(
                    model,
                    input,
                    [indexer.Type],
                    indexer.ParameterList.Parameters,
                    sourceTypeId,
                    model.GetDeclaredSymbol(indexer),
                    SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    indexer.AttributeLists,
                    results);
                return;
            }

            case EventDeclarationSyntax eventDeclaration:
            {
                Emit(
                    model,
                    input,
                    [eventDeclaration.Type],
                    null,
                    sourceTypeId,
                    model.GetDeclaredSymbol(eventDeclaration),
                    SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    eventDeclaration.AttributeLists,
                    results);
                return;
            }

            case FieldDeclarationSyntax field:
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    var symbol = model.GetDeclaredSymbol(variable);
                    Emit(
                        model,
                        input,
                        [field.Declaration.Type],
                        null,
                        sourceTypeId,
                        symbol,
                        SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                        field.AttributeLists,
                        results);
                }

                return;
            }

            case EventFieldDeclarationSyntax eventField:
            {
                foreach (var variable in eventField.Declaration.Variables)
                {
                    Emit(
                        model,
                        input,
                        [eventField.Declaration.Type],
                        null,
                        sourceTypeId,
                        model.GetDeclaredSymbol(variable),
                        SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                        eventField.AttributeLists,
                        results);
                }

                return;
            }

            case OperatorDeclarationSyntax @operator:
            {
                Emit(
                    model,
                    input,
                    [@operator.ReturnType],
                    @operator.ParameterList.Parameters,
                    sourceTypeId,
                    model.GetDeclaredSymbol(@operator),
                    SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    @operator.AttributeLists,
                    results);
                return;
            }

            case ConversionOperatorDeclarationSyntax conversion:
            {
                Emit(
                    model,
                    input,
                    [conversion.Type],
                    conversion.ParameterList.Parameters,
                    sourceTypeId,
                    model.GetDeclaredSymbol(conversion),
                    SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    conversion.AttributeLists,
                    results);
                return;
            }

            default:
                return;
        }
    }

    /// <summary>Collects return type, parameters, constraints, and attributes of one member.</summary>
    private void Emit(
        SemanticModel model,
        SymbolIndexInput input,
        IReadOnlyList<TypeSyntax> returnTypes,
        IReadOnlyList<ParameterSyntax>? parameters,
        string sourceTypeId,
        ISymbol? symbol,
        SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses,
        SyntaxList<AttributeListSyntax> attributes,
        List<CollectedEvidence> results)
    {
        var publicSurface = PublicSurface.IsExternallyVisible(symbol);
        var memberId = symbol is null ? null : _resolver.ResolveMemberId(symbol, input.VariantId);

        foreach (var returnType in returnTypes)
        {
            CollectTypeSyntax(model, input, returnType, sourceTypeId, memberId, publicSurface, "signature", results);
        }

        if (parameters is not null)
        {
            foreach (var parameter in parameters)
            {
                if (parameter.Type is not null)
                {
                    CollectTypeSyntax(
                        model,
                        input,
                        parameter.Type,
                        sourceTypeId,
                        memberId,
                        publicSurface,
                        "signature",
                        results);
                }
            }
        }

        CollectConstraints(model, input, constraintClauses, sourceTypeId, memberId, publicSurface, results);
        CollectAttributes(model, input, attributes, sourceTypeId, memberId, publicSurface, results);
    }

    private void CollectConstraints(
        SemanticModel model,
        SymbolIndexInput input,
        SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses,
        string sourceTypeId,
        string? sourceMemberId,
        bool publicSurface,
        List<CollectedEvidence> results)
    {
        foreach (var constraint in constraintClauses
                     .SelectMany(clause => clause.Constraints)
                     .OfType<TypeConstraintSyntax>())
        {
            CollectTypeSyntax(
                model,
                input,
                constraint.Type,
                sourceTypeId,
                sourceMemberId,
                publicSurface,
                "constraint",
                results);
        }
    }

    private void CollectAttributes(
        SemanticModel model,
        SymbolIndexInput input,
        SyntaxList<AttributeListSyntax> attributeLists,
        string sourceTypeId,
        string? sourceMemberId,
        bool publicSurface,
        List<CollectedEvidence> results)
    {
        foreach (var attribute in attributeLists.SelectMany(list => list.Attributes))
        {
            if (model.GetSymbolInfo(attribute).Symbol is not IMethodSymbol constructor)
            {
                continue;
            }

            var attributeType = constructor.ContainingType;
            AddEvidence(
                input,
                attributeType,
                attribute.Name,
                sourceTypeId,
                sourceMemberId,
                publicSurface,
                "attribute",
                results);
        }
    }

    /// <summary>
    /// Adds a reference for the written type and, recursively, for its type arguments,
    /// tuple elements, array/nullable/pointer element types. Each nested type keeps its
    /// own span so the evidence points at what was written.
    /// </summary>
    private void CollectTypeSyntax(
        SemanticModel model,
        SymbolIndexInput input,
        TypeSyntax syntax,
        string sourceTypeId,
        string? sourceMemberId,
        bool publicSurface,
        string kind,
        List<CollectedEvidence> results)
    {
        var type = model.GetTypeInfo(syntax).Type;

        // Arrays/pointers/nullable wrappers are normalized away: their element type is
        // collected from its own syntax below.
        var primary = NormalizeDefinition(type);
        if (primary is not null)
        {
            AddEvidence(input, primary, syntax, sourceTypeId, sourceMemberId, publicSurface, kind, results);
        }

        foreach (var nested in NestedTypeSyntaxes(syntax))
        {
            CollectTypeSyntax(model, input, nested, sourceTypeId, sourceMemberId, publicSurface, kind, results);
        }
    }

    private static INamedTypeSymbol? NormalizeDefinition(ITypeSymbol? type) => type switch
    {
        null => null,
        INamedTypeSymbol { IsAnonymousType: true } => null,
        INamedTypeSymbol named => named.OriginalDefinition,
        _ => null
    };

    private static IEnumerable<TypeSyntax> NestedTypeSyntaxes(TypeSyntax syntax)
    {
        switch (syntax)
        {
            case GenericNameSyntax generic:
                foreach (var argument in generic.TypeArgumentList.Arguments)
                {
                    yield return argument;
                }

                break;
            case QualifiedNameSyntax qualified when qualified.Right is GenericNameSyntax generic:
                foreach (var argument in generic.TypeArgumentList.Arguments)
                {
                    yield return argument;
                }
                break;
            case AliasQualifiedNameSyntax alias when alias.Name is GenericNameSyntax generic:
                foreach (var argument in generic.TypeArgumentList.Arguments)
                {
                    yield return argument;
                }
                break;
            case ArrayTypeSyntax array:
                yield return array.ElementType;
                break;
            case NullableTypeSyntax nullable:
                yield return nullable.ElementType;
                break;
            case PointerTypeSyntax pointer:
                yield return pointer.ElementType;
                break;
            case TupleTypeSyntax tuple:
                foreach (var element in tuple.Elements)
                {
                    yield return element.Type;
                }

                break;
        }
    }

    private void AddEvidence(
        SymbolIndexInput input,
        ITypeSymbol target,
        SyntaxNode syntax,
        string sourceTypeId,
        string? sourceMemberId,
        bool publicSurface,
        string kind,
        List<CollectedEvidence> results)
    {
        var targetTypeId = _resolver.ResolveTypeId(target, input.VariantId);
        if (targetTypeId is null)
        {
            return;
        }

        if (!_typesById.ContainsKey(targetTypeId))
        {
            _externalTypes?.Register(targetTypeId, target.ToDisplayString());
        }

        var tree = syntax.SyntaxTree;
        if (string.IsNullOrEmpty(tree.FilePath))
        {
            return;
        }

        var document = _documents.Register(tree.FilePath);
        var span = tree.GetLineSpan(syntax.Span);
        var physicalSpan = new PhysicalSpan(
            syntax.Span.Start,
            syntax.Span.Length,
            span.StartLinePosition.Line,
            span.StartLinePosition.Character,
            span.EndLinePosition.Line,
            span.EndLinePosition.Character);
        var spanKey = $"{physicalSpan.StartLine}:{physicalSpan.StartCharacter}:{physicalSpan.Length}";

        var relationId = Identity.RelationId("symbolResolved", sourceTypeId, targetTypeId, _profileHash);
        var evidenceId = Identity.EvidenceId(relationId, kind, document.Id, spanKey);
        if (!_seen.Add(evidenceId))
        {
            return;
        }

        var contentHash = _contentHashByDocument.TryGetValue(document.Id, out var hash)
            ? hash
            : document.ContentHash;

        results.Add(new CollectedEvidence(
            new EvidenceRecord(
                evidenceId,
                relationId,
                sourceTypeId,
                targetTypeId,
                sourceTypeId,
                sourceMemberId,
                targetTypeId,
                null,
                kind,
                document.Origin,
                document.Id,
                physicalSpan,
                _documents.MappedLocationFor(syntax),
                contentHash,
                "resolved",
                publicSurface,
                null),
            SourceVariantId: input.VariantId,
            SourceNamespaceId: NamespaceOf(sourceTypeId),
            TargetVariantId: TargetVariantOf(targetTypeId),
            TargetNamespaceId: NamespaceOf(targetTypeId),
            TargetIsExternal: !_typesById.ContainsKey(targetTypeId)));
    }

    private string? NamespaceOf(string typeId)
        => _typesById.TryGetValue(typeId, out var type) ? type.NamespaceId : null;

    private string? TargetVariantOf(string typeId)
        => _typesById.TryGetValue(typeId, out var type) ? type.ProjectVariantId : null;
}
