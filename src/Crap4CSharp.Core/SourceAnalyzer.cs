using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;

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

    public IReadOnlyList<SourceMethod> AnalyzeCaptured(string logicalPath, ImmutableArray<byte> bytes,
        CSharpParseOptions? parseOptions = null) =>
        AnalyzeText(CapturedSource.Create(logicalPath, bytes.AsSpan()).Text, logicalPath,
            parseOptions ?? CSharpParseOptions.Default);

    public IReadOnlyList<SourceMethod> AnalyzeLogicalText(string logicalPath, string text) =>
        AnalyzeText(text, logicalPath, CSharpParseOptions.Default);

    public static int CalculateComplexity(MethodDeclarationSyntax method)
        => ComplexityRules.Legacy.Calculate(method.Body ?? (SyntaxNode?)method.ExpressionBody?.Expression);
}
