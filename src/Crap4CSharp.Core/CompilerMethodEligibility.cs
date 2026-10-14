using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Crap4CSharp.Core;

// A generated metadata flag alone cannot remove an authored risk unit. Require
// independent Roslyn synthesis or an explicitly unsupported authored callable,
// and exact source-document/sequence-point containment. This does not attribute
// generated coverage, particularly MoveNext, to an authored method.
internal static class CompilerMethodEligibility
{
    internal static IReadOnlySet<int> Classify(CSharpCompilation compilation, InspectedBuildEvidence build,
        IReadOnlyDictionary<string, string> documents, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = new HashSet<int>();
        var trees = compilation.SyntaxTrees.ToDictionary(tree => tree.FilePath, StringComparer.Ordinal);
        foreach (var method in build.Methods)
        {
            token.ThrowIfCancellationRequested();
            if (method.SequencePoints.Count == 0 || (!method.CompilerGenerated && method.MethodName != ".ctor")) continue;
            if (method.SequencePoints.Any(point => !documents.TryGetValue(point.Document, out var path) || !trees.ContainsKey(path)))
                continue;
            var type = compilation.GetTypeByMetadataName(method.TypeName.Replace('/', '+'));
            if (type is { IsRecord: true })
            {
                var members = type.GetMembers().OfType<IMethodSymbol>()
                    .Concat(type.GetMembers().OfType<IPropertySymbol>().SelectMany(property =>
                        new[] { property.GetMethod, property.SetMethod }.OfType<IMethodSymbol>()))
                    .Where(member => member.MetadataName == method.MethodName && method.ParameterTypes is not null &&
                        member.Parameters.Select(parameter => ParameterType(parameter.Type) +
                            (parameter.RefKind == RefKind.None ? "" : "&")).SequenceEqual(method.ParameterTypes, StringComparer.Ordinal)).ToArray();
                var declarations = type.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax(token))
                    .OfType<RecordDeclarationSyntax>().ToArray();
                var autoAccessors = members.SelectMany(member => member.DeclaringSyntaxReferences)
                    .Select(reference => reference.GetSyntax(token)).OfType<AccessorDeclarationSyntax>()
                    .Where(accessor => accessor.Body is null && accessor.ExpressionBody is null &&
                        accessor.SemicolonToken.IsKind(SyntaxKind.SemicolonToken)).ToArray();
                // Roslyn does not mark declared auto-accessors implicitly declared.
                // Their semicolon-only declarations have no authored executable
                // body; the property initializer remains in the authored inventory.
                if (method.CompilerGenerated && members.Length > 0 &&
                    members.All(member => member.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet) &&
                    autoAccessors.Length == members.Length &&
                    method.SequencePoints.All(point => autoAccessors.Any(accessor => Contains(accessor, point, documents))))
                {
                    result.Add(method.MetadataToken);
                    continue;
                }
                var primary = members.Length > 0 && members.All(member => member.MethodKind == MethodKind.Constructor &&
                    member.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax(token) is RecordDeclarationSyntax { ParameterList: not null }));
                if (members.Length > 0 && (members.All(member => member.IsImplicitlyDeclared) || primary) &&
                    method.SequencePoints.All(point => declarations.Any(declaration => Contains(declaration, point, documents))))
                {
                    result.Add(method.MetadataToken);
                    continue;
                }
            }
            if (!method.CompilerGenerated) continue;
            // Missing collector data for an anonymous/local body must leave the
            // corresponding authored callable unknown. Its inventory remains intact.
            var unsupported = method.SequencePoints.Select(point => trees[documents[point.Document]])
                .Distinct().SelectMany(tree => tree.GetRoot(token).DescendantNodes())
                .Where(node => node is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax or LocalFunctionStatementSyntax);
            if (unsupported.Any(node => method.SequencePoints.All(point => Contains(node, point, documents))))
                result.Add(method.MetadataToken);
        }
        return result;
    }

    private static string ParameterType(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol parameter)
        {
            var ordinal = parameter.Ordinal;
            if (parameter.TypeParameterKind == TypeParameterKind.Type)
                for (var parent = parameter.DeclaringType?.ContainingType; parent is not null; parent = parent.ContainingType)
                    ordinal += parent.Arity;
            return (parameter.TypeParameterKind == TypeParameterKind.Method ? "!!" : "!") + ordinal;
        }
        if (type is IArrayTypeSymbol array) return ParameterType(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]";
        if (type is IPointerTypeSymbol pointer) return ParameterType(pointer.PointedAtType) + "*";
        if (type is IDynamicTypeSymbol) return "System.Object";
        if (type is not INamedTypeSymbol named) return "<unsupported>";
        var chain = new Stack<INamedTypeSymbol>();
        for (var current = named; current is not null; current = current.ContainingType) chain.Push(current);
        var ns = named.ContainingNamespace.IsGlobalNamespace ? "" : named.ContainingNamespace.ToDisplayString() + ".";
        var name = ns + string.Join("+", chain.Select(part => part.MetadataName));
        var arguments = chain.SelectMany(part => part.TypeArguments).Select(ParameterType).ToArray();
        return arguments.Length == 0 ? name : name + "<" + string.Join(",", arguments) + ">";
    }

    private static bool Contains(SyntaxNode declaration, InspectedSequencePoint point,
        IReadOnlyDictionary<string, string> documents)
    {
        if (!documents.TryGetValue(point.Document, out var path) || declaration.SyntaxTree.FilePath != path) return false;
        var span = declaration.SyntaxTree.GetLineSpan(declaration.Span).Span;
        var start = new Microsoft.CodeAnalysis.Text.LinePosition(point.StartLine - 1, point.StartColumn - 1);
        var end = new Microsoft.CodeAnalysis.Text.LinePosition(point.EndLine - 1, point.EndColumn - 1);
        return start >= span.Start && end <= span.End && end >= start;
    }
}
