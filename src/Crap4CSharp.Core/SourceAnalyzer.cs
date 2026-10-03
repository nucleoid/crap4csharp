using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Crap4CSharp.Core;

public sealed class SourceAnalyzer
{
    public IReadOnlyList<SourceMethod> AnalyzeFiles(IEnumerable<string> files)
    {
        var methods = new List<SourceMethod>();
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            var fullPath = Path.GetFullPath(file);
            var text = File.ReadAllText(fullPath);
            var tree = CSharpSyntaxTree.ParseText(text, path: fullPath);
            methods.AddRange(AnalyzeTree(tree));
        }

        return methods;
    }

    public IReadOnlyList<SourceMethod> AnalyzeText(string text, string logicalPath, CSharpParseOptions parseOptions) =>
        AnalyzeTree(CSharpSyntaxTree.ParseText(text, parseOptions, logicalPath));

    public IReadOnlyList<SourceMethod> AnalyzeTree(SyntaxTree tree)
    {
        var methods = new List<SourceMethod>();
        var fullPath = tree.FilePath;
            var errors = tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length > 0)
            {
                var first = errors[0];
                var position = first.Location.GetLineSpan().StartLinePosition;
                throw new InvalidDataException($"C# parse error in {fullPath}:{position.Line + 1}:{position.Character + 1}: {first.GetMessage()}");
        }

        var root = tree.GetRoot();
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (method.Body is null && method.ExpressionBody is null) continue;
                // DescendantNodes includes local functions separately, but never as MethodDeclarationSyntax.
                var lineSpan = tree.GetLineSpan(method.Span);
                var containingTypes = method.Ancestors().OfType<TypeDeclarationSyntax>()
                    .Reverse().Select(type => type.Identifier.ValueText);
                var typeName = string.Join(".", containingTypes);
                var namespaceName = string.Join(".", method.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                    .Reverse().Select(@namespace => @namespace.Name.ToString()));
                var coverageTypes = method.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(type =>
                    type.Identifier.ValueText + (type.TypeParameterList is { Parameters.Count: > 0 } parameters
                        ? $"`{parameters.Parameters.Count}" : string.Empty));
                var coverageTypeName = string.Join(".", coverageTypes);
                if (!string.IsNullOrWhiteSpace(namespaceName)) coverageTypeName = $"{namespaceName}.{coverageTypeName}";
                var parameterTypes = method.ParameterList.Parameters
                    .Select(parameter => parameter.Type?.ToString() ?? "?");
                var signature = $"{method.Identifier.ValueText}({string.Join(",", parameterTypes)})";
                var canonicalParameters = method.ParameterList.Parameters.Select(parameter =>
                {
                    var modifiers = string.Join(" ", parameter.Modifiers.Select(modifier => modifier.ValueText));
                    return $"{(modifiers.Length == 0 ? string.Empty : modifiers + " ")}{parameter.Type?.ToString() ?? "?"}";
                });
                var explicitInterface = method.ExplicitInterfaceSpecifier is null
                    ? string.Empty : method.ExplicitInterfaceSpecifier.Name + ".";
                var methodArity = method.TypeParameterList?.Parameters.Count ?? 0;
                var canonicalSignature = $"{coverageTypeName}.{explicitInterface}{method.Identifier.ValueText}`{methodArity}({string.Join(",", canonicalParameters)})";
                methods.Add(new SourceMethod(
                    fullPath,
                    typeName,
                    method.Identifier.ValueText,
                    $"{typeName}.{signature}",
                    signature,
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.EndLinePosition.Line + 1,
                    CalculateComplexity(method))
                {
                    CoverageTypeName = coverageTypeName,
                    CanonicalSignature = canonicalSignature
                });
        }

        return methods;
    }

    public static int CalculateComplexity(MethodDeclarationSyntax method)
    {
        var walker = new ComplexityWalker();
        walker.Visit(method.Body ?? (SyntaxNode?)method.ExpressionBody?.Expression);
        return walker.Complexity;
    }

    private sealed class ComplexityWalker : CSharpSyntaxWalker
    {
        public int Complexity { get; private set; } = 1;

        public override void VisitIfStatement(IfStatementSyntax node) { Complexity++; base.VisitIfStatement(node); }
        public override void VisitForStatement(ForStatementSyntax node) { Complexity++; base.VisitForStatement(node); }
        public override void VisitForEachStatement(ForEachStatementSyntax node) { Complexity++; base.VisitForEachStatement(node); }
        public override void VisitWhileStatement(WhileStatementSyntax node) { Complexity++; base.VisitWhileStatement(node); }
        public override void VisitDoStatement(DoStatementSyntax node) { Complexity++; base.VisitDoStatement(node); }
        public override void VisitCatchClause(CatchClauseSyntax node) { Complexity++; base.VisitCatchClause(node); }
        public override void VisitConditionalExpression(ConditionalExpressionSyntax node) { Complexity++; base.VisitConditionalExpression(node); }
        public override void VisitSwitchExpressionArm(SwitchExpressionArmSyntax node) { Complexity++; base.VisitSwitchExpressionArm(node); }
        public override void VisitCaseSwitchLabel(CaseSwitchLabelSyntax node) { Complexity++; base.VisitCaseSwitchLabel(node); }
        public override void VisitCasePatternSwitchLabel(CasePatternSwitchLabelSyntax node) { Complexity++; base.VisitCasePatternSwitchLabel(node); }
        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.LogicalAndExpression) || node.IsKind(SyntaxKind.LogicalOrExpression) ||
                node.IsKind(SyntaxKind.CoalesceExpression)) Complexity++;
            base.VisitBinaryExpression(node);
        }

        public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) { }
        public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) { }
        public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) { }
        public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) { }
    }
}
