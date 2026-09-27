// Operation-based dependency collector (SD-010).
//
// Collects what the code actually uses: object creation, calls, member access, type
// use, and compile-time names. Each body is scanned exactly once (the operation tree
// of that body is visited once, not every descendant root), so a `new Foo()` is one
// piece of evidence instead of a syntax duplicate.
//
// References that cannot be resolved are never promoted to confirmed edges: invalid
// operations, dynamic receivers, and candidate-only symbols are counted and reported
// separately.

namespace SharpDeps.Analysis.Roslyn.Evidence;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Roslyn.Symbols;

/// <summary>Reference kinds that the operation collector does not turn into edges.</summary>
public sealed record OperationCollectionStats(
    int BodiesScanned,
    int UnresolvedOperations,
    int DynamicReferences,
    int CandidateOnlySymbols);

public sealed record OperationCollectionResult(
    IReadOnlyList<CollectedEvidence> Evidence,
    OperationCollectionStats Stats);

public sealed class OperationDependencyCollector
{
    private readonly SymbolResolver _resolver;
    private readonly SourceDocumentRegistry _documents;
    private readonly Dictionary<string, IndexedType> _typesById;
    private readonly string _profileHash;
    private readonly HashSet<string> _seen = [];

    private readonly ExternalTypeRegistry? _externalTypes;

    public OperationDependencyCollector(
        SymbolResolver resolver,
        SourceDocumentRegistry documents,
        SymbolIndex index,
        string profileHash,
        ExternalTypeRegistry? externalTypes = null)
    {
        _externalTypes = externalTypes;
        _resolver = resolver;
        _documents = documents;
        _typesById = index.Types.ToDictionary(type => type.Id, StringComparer.Ordinal);
        _profileHash = profileHash;
    }

    public OperationCollectionResult Collect(
        IReadOnlyList<SymbolIndexInput> inputs,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CollectedEvidence>();
        var bodies = 0;
        var unresolved = 0;
        var dynamic = 0;
        var candidateOnly = 0;

        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var tree in input.Compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var model = input.Compilation.GetSemanticModel(tree);

                foreach (var root in BodyRoots(tree))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (model.GetOperation(root) is not { } operation)
                    {
                        continue;
                    }

                    var owner = model.GetEnclosingSymbol(root.SpanStart);
                    bodies++;

                    foreach (var node in new[] { operation }.Concat(operation.Descendants()))
                    {
                        CollectOperation(
                            model,
                            node,
                            input,
                            owner,
                            results,
                            ref unresolved,
                            ref dynamic,
                            ref candidateOnly);
                    }

                    // These written types have no dedicated operation (for example a
                    // generic method's type arguments or a local declaration's type).
                    foreach (var syntax in root.DescendantNodesAndSelf())
                    {
                        IEnumerable<TypeSyntax> writtenTypes = syntax switch
                        {
                            TypeArgumentListSyntax arguments => arguments.Arguments,
                            VariableDeclarationSyntax local when local.Parent is not FieldDeclarationSyntax
                                and not EventFieldDeclarationSyntax && !local.Type.IsVar => [local.Type],
                            DeclarationPatternSyntax pattern => [pattern.Type],
                            TypePatternSyntax pattern => [pattern.Type],
                            RecursivePatternSyntax { Type: not null } pattern => [pattern.Type],
                            _ => []
                        };
                        foreach (var written in writtenTypes)
                        {
                            var type = model.GetTypeInfo(written, cancellationToken).Type;
                            if (type is not null)
                            {
                                AddEvidence(input, owner, type, written, "typeUse", results);
                            }
                        }
                    }
                }
            }
        }

        return new OperationCollectionResult(
            results,
            new OperationCollectionStats(bodies, unresolved, dynamic, candidateOnly));
    }

    /// <summary>
    /// Syntax nodes whose operation tree covers a body exactly once. Lambdas and local
    /// functions are not roots: they are reached from the body that contains them.
    /// </summary>
    private static IEnumerable<SyntaxNode> BodyRoots(SyntaxTree tree)
    {
        foreach (var node in tree.GetRoot().DescendantNodes(descendIntoChildren: _ => true))
        {
            switch (node)
            {
                case BaseMethodDeclarationSyntax method:
                    if (method.Body is not null)
                    {
                        yield return method.Body;
                    }

                    if (method.ExpressionBody is not null)
                    {
                        yield return method.ExpressionBody;
                    }

                    if (method is ConstructorDeclarationSyntax { Initializer: not null } constructor)
                    {
                        yield return constructor.Initializer;
                    }

                    break;

                case AccessorDeclarationSyntax accessor:
                    if (accessor.Body is not null)
                    {
                        yield return accessor.Body;
                    }

                    if (accessor.ExpressionBody is not null)
                    {
                        yield return accessor.ExpressionBody;
                    }

                    break;

                case ArrowExpressionClauseSyntax arrow:
                    // Property/indexer expression bodies; method arrows are covered above.
                    if (arrow.Parent is PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax)
                    {
                        yield return arrow;
                    }

                    break;

                case VariableDeclaratorSyntax { Initializer.Value: not null } declarator
                    when declarator.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax:
                    yield return declarator.Initializer.Value;
                    break;

                case PropertyDeclarationSyntax { Initializer.Value: not null } property:
                    yield return property.Initializer.Value;
                    break;

                case GlobalStatementSyntax global:
                    // The statement is the operation root; the global statement node
                    // itself has no operation.
                    yield return global.Statement;
                    break;

                case AttributeArgumentSyntax argument:
                    yield return argument.Expression;
                    break;
            }
        }
    }

    private void CollectOperation(
        SemanticModel model,
        IOperation operation,
        SymbolIndexInput input,
        ISymbol? owner,
        List<CollectedEvidence> results,
        ref int unresolved,
        ref int dynamic,
        ref int candidateOnly)
    {
        switch (operation)
        {
            case IObjectCreationOperation creation:
                if (creation.Type is null || creation.Type.TypeKind is TypeKind.Error or TypeKind.Dynamic)
                {
                    unresolved++;
                    return;
                }

                AddEvidence(input, owner, creation.Type, creation.Syntax, "constructs", results, creation.Constructor);
                return;

            case IInvocationOperation invocation:
            {
                var target = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
                if (target.ContainingType is null
                    || target.ContainingType.TypeKind is TypeKind.Error or TypeKind.Dynamic)
                {
                    unresolved++;
                    return;
                }

                // Virtual and interface calls are recorded against the declaration that
                // the compiler bound to; the runtime implementation is not guessed.
                if (HasCandidatesOnly(model, invocation.Syntax))
                {
                    candidateOnly++;
                    return;
                }

                AddEvidence(
                    input,
                    owner,
                    target.ContainingType,
                    invocation.Syntax,
                    "calls",
                    results,
                    member: target);
                return;
            }

            case IPropertyReferenceOperation property:
                if (property.Property.ContainingType is null)
                {
                    unresolved++;
                    return;
                }

                if (HasCandidatesOnly(model, property.Syntax))
                {
                    candidateOnly++;
                    return;
                }

                AddEvidence(
                    input,
                    owner,
                    property.Property.ContainingType,
                    property.Syntax,
                    "memberAccess",
                    results,
                    member: property.Property);
                return;

            case IFieldReferenceOperation field:
                if (field.Field.ContainingType is null)
                {
                    unresolved++;
                    return;
                }

                if (HasCandidatesOnly(model, field.Syntax))
                {
                    candidateOnly++;
                    return;
                }

                AddEvidence(
                    input,
                    owner,
                    field.Field.ContainingType,
                    field.Syntax,
                    "memberAccess",
                    results,
                    member: field.Field);
                return;

            case IEventReferenceOperation eventReference:
                if (eventReference.Event.ContainingType is null)
                {
                    unresolved++;
                    return;
                }

                AddEvidence(
                    input,
                    owner,
                    eventReference.Event.ContainingType,
                    eventReference.Syntax,
                    "memberAccess",
                    results,
                    member: eventReference.Event);
                return;

            case ITypeOfOperation typeOf:
                if (typeOf.TypeOperand is null)
                {
                    unresolved++;
                    return;
                }

                AddEvidence(input, owner, typeOf.TypeOperand, typeOf.Syntax, "typeUse", results);
                return;

            case IDefaultValueOperation defaultValue:
                if (defaultValue.Type is null)
                {
                    unresolved++;
                    return;
                }

                AddEvidence(input, owner, defaultValue.Type, defaultValue.Syntax, "typeUse", results);
                return;

            case IArrayCreationOperation array when array.Type is IArrayTypeSymbol arrayType:
                AddEvidence(input, owner, arrayType.ElementType, array.Syntax, "typeUse", results);
                return;

            case IIsTypeOperation isType when isType.TypeOperand is not null:
                AddEvidence(input, owner, isType.TypeOperand, isType.Syntax, "typeUse", results);
                return;

            case IConversionOperation conversion when IsWrittenConversion(conversion):
                if (conversion.Type is null)
                {
                    unresolved++;
                    return;
                }

                AddEvidence(input, owner, conversion.Type, conversion.Syntax, "typeUse", results);
                return;

            case INameOfOperation nameOf:
            {
                // The child identifies the member or type whose name is used; a local or
                // parameter name has no type dependency.
                var referenced = ReferencedTypeOf(nameOf.ChildOperations.FirstOrDefault());
                if (referenced is null)
                {
                    return;
                }

                AddEvidence(input, owner, referenced, nameOf.Syntax, "compileTimeName", results);
                return;
            }

            case IInvalidOperation:
                unresolved++;
                return;

            case ILiteralOperation literal when literal.Type?.TypeKind == TypeKind.Dynamic:
                dynamic++;
                return;

            case IDynamicInvocationOperation or IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation:
                dynamic++;
                return;

            default:
                if (operation.Type?.TypeKind == TypeKind.Error)
                {
                    unresolved++;
                }

                return;
        }
    }

    private static bool IsWrittenConversion(IConversionOperation conversion)
        => !conversion.Conversion.IsImplicit
           && conversion.Syntax is CastExpressionSyntax or BinaryExpressionSyntax or ForEachStatementSyntax or
               VariableDeclarationSyntax;

    /// <summary>The type a referenced symbol belongs to, for compile-time names.</summary>
    private static ITypeSymbol? ReferencedTypeOf(IOperation? operation) => operation switch
    {
        null => null,
        IPropertyReferenceOperation property => property.Property.ContainingType,
        IFieldReferenceOperation field => field.Field.ContainingType,
        IEventReferenceOperation eventReference => eventReference.Event.ContainingType,
        IMethodReferenceOperation method => method.Method.ContainingType,
        IInvocationOperation invocation => invocation.TargetMethod.ContainingType,
        IObjectCreationOperation creation => creation.Type,
        ITypeOfOperation typeOf => typeOf.TypeOperand,
        // Locals and parameters are names, not type dependencies.
        ILocalReferenceOperation or IParameterReferenceOperation => null,
        _ => null
    };

    /// <summary>True when the syntax bound to candidates only (no confirmed symbol).</summary>
    private static bool HasCandidatesOnly(SemanticModel model, SyntaxNode? syntax)
    {
        if (syntax is null)
        {
            return false;
        }

        var info = model.GetSymbolInfo(syntax);
        return info.Symbol is null && info.CandidateSymbols.Length > 0;
    }

    private void AddEvidence(
        SymbolIndexInput input,
        ISymbol? owner,
        ITypeSymbol target,
        SyntaxNode? syntax,
        string kind,
        List<CollectedEvidence> results,
        ISymbol? member = null)
    {
        if (syntax is null)
        {
            return;
        }

        var sourceTypeId = SourceTypeIdOf(owner, input);
        if (sourceTypeId is null)
        {
            return;
        }

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

        var sourceMemberId = _resolver.ResolveMemberId(owner, input.VariantId);
        var targetMemberId = member is null ? null : _resolver.ResolveMemberId(member, input.VariantId);
        var contentHash = document.ContentHash;

        results.Add(new CollectedEvidence(
            new EvidenceRecord(
                evidenceId,
                relationId,
                sourceTypeId,
                targetTypeId,
                sourceTypeId,
                sourceMemberId,
                targetTypeId,
                targetMemberId,
                kind,
                document.Origin,
                document.Id,
                physicalSpan,
                _documents.MappedLocationFor(syntax),
                contentHash,
                "resolved",
                PublicSurface.IsExternallyVisible(owner),
                null),
            SourceVariantId: input.VariantId,
            SourceNamespaceId: NamespaceOf(sourceTypeId),
            TargetVariantId: TargetVariantOf(targetTypeId),
            TargetNamespaceId: NamespaceOf(targetTypeId),
            TargetIsExternal: !_typesById.ContainsKey(targetTypeId)));

    }

    /// <summary>
    /// Owner of the reference: accessors are attributed to the property or event they
    /// belong to, lambdas and local functions to the member that contains them.
    /// </summary>
    private static string? SourceTypeIdOf(ISymbol? owner, SymbolIndexInput input)
    {
        var containingType = owner switch
        {
            INamedTypeSymbol type => type,
            IMethodSymbol { AssociatedSymbol: not null } accessor => accessor.AssociatedSymbol!.ContainingType,
            _ => owner?.ContainingType
        };

        return containingType is null
            ? null
            : Identity.TypeId(input.VariantId, SymbolIndexBuilder.TypeKeyOf(containingType));
    }

    private string? NamespaceOf(string typeId)
        => _typesById.TryGetValue(typeId, out var type) ? type.NamespaceId : null;

    private string? TargetVariantOf(string typeId)
        => _typesById.TryGetValue(typeId, out var type) ? type.ProjectVariantId : null;
}
