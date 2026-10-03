using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Crap4CSharp.Core;

public enum CallableKind
{
    Method, Constructor, Destructor, PropertyGet, PropertySet, PropertyInit,
    IndexerGet, IndexerSet, IndexerInit, EventAdd, EventRemove, Operator, Conversion,
    LocalFunction, Lambda, AnonymousMethod, TopLevel, FieldInitializer, EventInitializer,
    PropertyInitializer, PrimaryConstructorBaseArguments
}

public enum CallableApplicability { Applicable, NotApplicable }

public sealed record CallableAnalysisContext(
    string Project,
    string TargetFramework,
    string Configuration,
    string Platform,
    string ContextId,
    CSharpParseOptions ParseOptions);

public sealed record CallableSourceSpan(int StartLine, int StartColumn, int EndLine, int EndColumn, int Start, int Length);

public sealed record CallableParameterIdentity(string RefKind, string Type);

public sealed record CallableSemanticIdentity(
    string TypeName,
    string MetadataName,
    int GenericArity,
    IReadOnlyList<CallableParameterIdentity> Parameters,
    string ReturnType,
    bool IsStatic,
    string ReportSignature,
    string StableKey);

public sealed record CallableEntry(
    string CallableId,
    string ObservationId,
    CallableKind Kind,
    string Name,
    string? ParentId,
    string Path,
    CallableSourceSpan Span,
    int? Complexity,
    CallableApplicability Applicability,
    string? ApplicabilityReason,
    string Ruleset,
    string SemanticKey,
    string BodyChecksum,
    bool IdentityAmbiguous,
    string CoverageCapability,
    string? CoverageReason)
{
    public CallableSemanticIdentity? SemanticIdentity { get; init; }
}

public sealed record CallableInventoryResult(
    string Ruleset,
    string ContextId,
    string SourceContentIdentity,
    IReadOnlyList<CallableEntry> Callables);

public static class CallableInventory
{
    public static CallableInventoryResult Analyze(string text, string logicalPath, CallableAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        ArgumentNullException.ThrowIfNull(context);
        var tree = CSharpSyntaxTree.ParseText(text, context.ParseOptions, logicalPath);
        var errors = tree.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0) throw new InvalidDataException(errors[0].ToString());
        var root = tree.GetCompilationUnitRoot();
        var semanticModel = CSharpCompilation.Create("Crap4CSharp.CallableInventory", [tree]).GetSemanticModel(tree);
        var contentIdentity = ProjectAnalysisContext.ContentHash(text);
        var candidates = Discover(root, tree).OrderBy(item => item.Span.Start).ThenByDescending(item => item.Span.Length)
            .ThenBy(item => item.Kind).ToList();

        // A partial definition and implementation are one logical authored callable.
        var applicableKeys = candidates.Where(item => item.Applicable).Select(item => (item.Kind, item.SemanticKey))
            .ToHashSet();
        candidates.RemoveAll(item => !item.Applicable && item.Node is MethodDeclarationSyntax method &&
            method.Modifiers.Any(SyntaxKind.PartialKeyword) && applicableKeys.Contains((item.Kind, item.SemanticKey)));

        var built = new List<(Candidate Candidate, CallableEntry Entry)>();
        foreach (var candidate in candidates)
        {
            var parent = built.Where(item => item.Candidate != candidate && item.Candidate.OwnershipSpan.Contains(candidate.Span) &&
                    IsExecutableParent(item.Candidate.Kind))
                .OrderBy(item => item.Candidate.OwnershipSpan.Length).FirstOrDefault();
            var anonymous = IsAnonymous(candidate.Kind);
            var semanticIdentity = SemanticIdentity(candidate, semanticModel);
            var semanticKey = anonymous
                ? $"{parent.Entry?.CallableId ?? "<root>"}:{candidate.Kind}:{candidate.SemanticKey}:{candidate.BodyFingerprint}"
                : semanticIdentity?.StableKey ?? candidate.SemanticKey;
            var callableId = Hash(context.Project.Replace('\\', '/'), context.TargetFramework, context.Configuration,
                context.Platform, ComplexityRules.CallablesV1, candidate.Kind.ToString(), semanticKey);
            var observationId = Hash(callableId, context.ContextId, contentIdentity, logicalPath.Replace('\\', '/'),
                candidate.Span.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                candidate.Span.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var coverage = Coverage(candidate);
            var entry = new CallableEntry(callableId, observationId, candidate.Kind, candidate.Name,
                parent.Entry?.CallableId, logicalPath.Replace('\\', '/'), ToSpan(tree, candidate.Span),
                candidate.Applicable ? candidate.Complexity : null,
                candidate.Applicable ? CallableApplicability.Applicable : CallableApplicability.NotApplicable,
                candidate.Applicable ? null : "callable.noAuthoredBody", ComplexityRules.CallablesV1,
                semanticKey, candidate.BodyFingerprint, false, coverage.Capability, coverage.Reason);
            entry = entry with { SemanticIdentity = semanticIdentity };
            built.Add((candidate, entry));
        }

        var ambiguous = built.Where(item => IsAnonymous(item.Entry.Kind))
            .GroupBy(item => item.Entry.CallableId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group).Select(item => item.Entry.ObservationId).ToHashSet(StringComparer.Ordinal);
        var entries = built.Select(item => ambiguous.Contains(item.Entry.ObservationId)
                ? item.Entry with { IdentityAmbiguous = true, CoverageCapability = "unsupported", CoverageReason = "coverage.ambiguousCallableOwnership" }
                : item.Entry)
            .OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Span.Start)
            .ThenBy(item => item.Kind).ThenBy(item => item.CallableId, StringComparer.Ordinal).ToArray();
        return new CallableInventoryResult(ComplexityRules.CallablesV1, context.ContextId, contentIdentity, entries);
    }

    private static List<Candidate> Discover(CompilationUnitSyntax root, SyntaxTree tree)
    {
        var output = new List<Candidate>();
        var globals = root.Members.OfType<GlobalStatementSyntax>().ToArray();
        if (globals.Length > 0)
        {
            var span = TextSpan.FromBounds(globals[0].SpanStart, globals[^1].Span.End);
            var complexity = 1 + globals.Sum(item => ComplexityRules.Modern.Calculate(item.Statement) - 1);
            output.Add(Candidate.Create(root, span, span, CallableKind.TopLevel, "<top-level>", "<top-level>", true,
                complexity, Fingerprint(globals.SelectMany(item => item.DescendantTokens(descendIntoTrivia: false)))));
        }

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    AddDeclaration(output, method, method.Body ?? (SyntaxNode?)method.ExpressionBody?.Expression,
                        CallableKind.Method, method.Identifier.ValueText, MethodKey(method));
                    break;
                case ConstructorDeclarationSyntax constructor:
                    AddDeclaration(output, constructor, constructor.Body ?? (SyntaxNode?)constructor.ExpressionBody?.Expression,
                        CallableKind.Constructor, constructor.Identifier.ValueText, MemberPrefix(constructor) + ".ctor(" + Parameters(constructor.ParameterList) + ")",
                        constructor);
                    break;
                case DestructorDeclarationSyntax destructor:
                    AddDeclaration(output, destructor, destructor.Body ?? (SyntaxNode?)destructor.ExpressionBody?.Expression,
                        CallableKind.Destructor, "~" + destructor.Identifier.ValueText, MemberPrefix(destructor) + ".dtor()", destructor);
                    break;
                case OperatorDeclarationSyntax op:
                    AddDeclaration(output, op, op.Body ?? (SyntaxNode?)op.ExpressionBody?.Expression, CallableKind.Operator,
                        "operator " + op.OperatorToken.ValueText, MemberPrefix(op) + ".operator " + op.OperatorToken.ValueText + "(" + Parameters(op.ParameterList) + ")", op);
                    break;
                case ConversionOperatorDeclarationSyntax conversion:
                    AddDeclaration(output, conversion, conversion.Body ?? (SyntaxNode?)conversion.ExpressionBody?.Expression,
                        CallableKind.Conversion, "operator " + conversion.Type,
                        MemberPrefix(conversion) + ".operator " + conversion.ImplicitOrExplicitKeyword.ValueText + " " + NormalizeTokens(conversion.Type) + "(" + Parameters(conversion.ParameterList) + ")", conversion);
                    break;
                case LocalFunctionStatementSyntax local:
                    AddDeclaration(output, local, local.Body ?? (SyntaxNode?)local.ExpressionBody?.Expression,
                        CallableKind.LocalFunction, local.Identifier.ValueText,
                        local.Identifier.ValueText + "`" + (local.TypeParameterList?.Parameters.Count ?? 0) + "(" + Parameters(local.ParameterList) + ")");
                    break;
                case AccessorDeclarationSyntax accessor:
                    var accessorKind = AccessorKind(accessor);
                    AddDeclaration(output, accessor, accessor.Body ?? (SyntaxNode?)accessor.ExpressionBody?.Expression,
                        accessorKind, accessor.Keyword.ValueText, AccessorKey(accessor, accessorKind));
                    break;
                case PropertyDeclarationSyntax property when property.ExpressionBody is not null:
                    AddExpression(output, property.ExpressionBody.Expression, property.Span, CallableKind.PropertyGet,
                        property.Identifier.ValueText + ".get", MemberPrefix(property) + ".property " + property.Identifier.ValueText + ".get");
                    break;
                case IndexerDeclarationSyntax indexer when indexer.ExpressionBody is not null:
                    AddExpression(output, indexer.ExpressionBody.Expression, indexer.Span, CallableKind.IndexerGet,
                        "this.get", MemberPrefix(indexer) + ".indexer(" + Parameters(indexer.ParameterList) + ").get");
                    break;
                case SimpleLambdaExpressionSyntax lambda:
                    AddAnonymous(output, lambda, lambda.Body, CallableKind.Lambda, "lambda");
                    break;
                case ParenthesizedLambdaExpressionSyntax lambda:
                    AddAnonymous(output, lambda, lambda.Body, CallableKind.Lambda, "lambda");
                    break;
                case AnonymousMethodExpressionSyntax anonymous:
                    AddAnonymous(output, anonymous, anonymous.Block, CallableKind.AnonymousMethod, "anonymous");
                    break;
                case VariableDeclaratorSyntax variable when variable.Initializer is not null:
                    var declaration = variable.Parent?.Parent;
                    var kind = declaration is EventFieldDeclarationSyntax ? CallableKind.EventInitializer : CallableKind.FieldInitializer;
                    AddExpression(output, variable.Initializer.Value, variable.Initializer.Span, kind, variable.Identifier.ValueText,
                        $"{MemberPrefix(variable)}:{kind}:{variable.Identifier.ValueText}", anonymousIdentity: true);
                    break;
                case PropertyDeclarationSyntax property when property.Initializer is not null:
                    AddExpression(output, property.Initializer.Value, property.Initializer.Span, CallableKind.PropertyInitializer,
                        property.Identifier.ValueText, $"{MemberPrefix(property)}:propertyInitializer:{property.Identifier.ValueText}", anonymousIdentity: true);
                    break;
                case PrimaryConstructorBaseTypeSyntax primary when primary.ArgumentList is not null:
                    AddExpression(output, primary.ArgumentList, primary.ArgumentList.Span, CallableKind.PrimaryConstructorBaseArguments,
                        "primary-base", $"{MemberPrefix(primary)}:primaryBase", anonymousIdentity: true);
                    break;
            }
        }
        return output;
    }

    private static void AddDeclaration(List<Candidate> output, SyntaxNode declaration, SyntaxNode? body,
        CallableKind kind, string name, string key, SyntaxNode? complexityRegion = null)
    {
        var applicable = body is not null;
        var region = complexityRegion ?? body;
        output.Add(Candidate.Create(declaration, declaration.Span, declaration.Span, kind, name, key, applicable,
            applicable ? ComplexityRules.Modern.Calculate(region) : 0,
            Fingerprint((body ?? declaration).DescendantTokens(descendIntoTrivia: false))));
    }

    private static void AddExpression(List<Candidate> output, SyntaxNode expression, TextSpan ownershipSpan,
        CallableKind kind, string name, string key, bool anonymousIdentity = false) =>
        output.Add(Candidate.Create(expression, expression.Span, ownershipSpan, kind, name,
            anonymousIdentity ? "anonymous:" + key : key, true, ComplexityRules.Modern.Calculate(expression),
            Fingerprint(expression.DescendantTokens(descendIntoTrivia: false))));

    private static void AddAnonymous(List<Candidate> output, SyntaxNode declaration, CSharpSyntaxNode body,
        CallableKind kind, string name) => output.Add(Candidate.Create(declaration, declaration.Span, declaration.Span,
            kind, name, name, true, ComplexityRules.Modern.Calculate(body),
            Fingerprint(body.DescendantTokens(descendIntoTrivia: false))));

    private static (string Capability, string? Reason) Coverage(Candidate candidate)
    {
        if (!candidate.Applicable) return ("not-applicable", "callable.noAuthoredBody");
        if (candidate.Kind is CallableKind.LocalFunction or CallableKind.Lambda or CallableKind.AnonymousMethod or CallableKind.TopLevel)
            return ("unsupported", "coverage.unsupportedGeneratedMapping");
        if (candidate.Kind is CallableKind.FieldInitializer or CallableKind.EventInitializer or CallableKind.PropertyInitializer or CallableKind.PrimaryConstructorBaseArguments)
            return ("unsupported", "scope.unsupportedCallable");
        if (candidate.Node.DescendantTokens().Any(token => token.IsKind(SyntaxKind.AsyncKeyword)) ||
            candidate.Node.DescendantNodes().Any(node => node is YieldStatementSyntax))
            return ("portable-pdb-required", "coverage.unsupportedGeneratedMapping");
        return ("semantic-ordinary", null);
    }

    private static CallableSemanticIdentity? SemanticIdentity(Candidate candidate, SemanticModel model)
    {
        IMethodSymbol? symbol = candidate.Node switch
        {
            MethodDeclarationSyntax method => model.GetDeclaredSymbol(method),
            ConstructorDeclarationSyntax constructor => model.GetDeclaredSymbol(constructor),
            DestructorDeclarationSyntax destructor => model.GetDeclaredSymbol(destructor),
            OperatorDeclarationSyntax op => model.GetDeclaredSymbol(op),
            ConversionOperatorDeclarationSyntax conversion => model.GetDeclaredSymbol(conversion),
            LocalFunctionStatementSyntax local => model.GetDeclaredSymbol(local),
            AccessorDeclarationSyntax accessor => model.GetDeclaredSymbol(accessor),
            _ => candidate.Node.AncestorsAndSelf().OfType<PropertyDeclarationSyntax>().FirstOrDefault() is { } property
                ? model.GetDeclaredSymbol(property)?.GetMethod
                : candidate.Node.AncestorsAndSelf().OfType<IndexerDeclarationSyntax>().FirstOrDefault() is { } indexer
                    ? model.GetDeclaredSymbol(indexer)?.GetMethod
                    : null
        };
        if (symbol is null || candidate.Kind is CallableKind.LocalFunction) return symbol is null ? null : FromSymbol(symbol);
        return FromSymbol(symbol);
    }

    private static CallableSemanticIdentity FromSymbol(IMethodSymbol symbol)
    {
        var typeName = MetadataTypeName(symbol.ContainingType);
        var parameters = symbol.Parameters.Select(parameter => new CallableParameterIdentity(
            parameter.RefKind.ToString().ToLowerInvariant(), TypeName(parameter.Type))).ToArray();
        var returnType = TypeName(symbol.ReturnType);
        var reportSignature = $"{returnType} {typeName}::{symbol.MetadataName}({string.Join(",", parameters.Select(item => item.Type))})";
        var stable = $"{typeName}::{symbol.MetadataName}`{symbol.Arity}({string.Join(",", parameters.Select(item => item.RefKind + ":" + item.Type))})->{returnType}:{(symbol.IsStatic ? "static" : "instance")}";
        return new CallableSemanticIdentity(typeName, symbol.MetadataName, symbol.Arity, parameters, returnType,
            symbol.IsStatic, reportSignature, stable);
    }

    private static string MetadataTypeName(INamedTypeSymbol? type)
    {
        if (type is null) return "<global>";
        var types = new Stack<string>();
        for (var current = type; current is not null; current = current.ContainingType) types.Push(current.MetadataName);
        var prefix = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." : string.Empty;
        return prefix + string.Join("+", types);
    }

    private static string TypeName(ITypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None)
            return type.SpecialType.ToString().Replace("System_", "System.", StringComparison.Ordinal);
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal).TrimEnd('?');
    }

    private static bool IsAnonymous(CallableKind kind) => kind is CallableKind.Lambda or CallableKind.AnonymousMethod or
        CallableKind.FieldInitializer or CallableKind.EventInitializer or CallableKind.PropertyInitializer or
        CallableKind.PrimaryConstructorBaseArguments;
    private static bool IsExecutableParent(CallableKind kind) => true;

    private static CallableKind AccessorKind(AccessorDeclarationSyntax accessor)
    {
        var indexer = accessor.Parent?.Parent is IndexerDeclarationSyntax;
        var @event = accessor.Parent?.Parent is EventDeclarationSyntax;
        return accessor.Keyword.Kind() switch
        {
            SyntaxKind.GetKeyword => indexer ? CallableKind.IndexerGet : CallableKind.PropertyGet,
            SyntaxKind.SetKeyword => indexer ? CallableKind.IndexerSet : CallableKind.PropertySet,
            SyntaxKind.InitKeyword => indexer ? CallableKind.IndexerInit : CallableKind.PropertyInit,
            SyntaxKind.AddKeyword when @event => CallableKind.EventAdd,
            SyntaxKind.RemoveKeyword when @event => CallableKind.EventRemove,
            _ => throw new InvalidDataException($"Unsupported accessor '{accessor.Keyword.ValueText}'.")
        };
    }

    private static string AccessorKey(AccessorDeclarationSyntax accessor, CallableKind kind)
    {
        var owner = accessor.Parent?.Parent;
        return owner switch
        {
            PropertyDeclarationSyntax property => $"{MemberPrefix(property)}.property {property.Identifier.ValueText}.{kind}",
            IndexerDeclarationSyntax indexer => $"{MemberPrefix(indexer)}.indexer({Parameters(indexer.ParameterList)}).{kind}",
            EventDeclarationSyntax @event => $"{MemberPrefix(@event)}.event {@event.Identifier.ValueText}.{kind}",
            _ => $"{MemberPrefix(accessor)}.{kind}"
        };
    }

    private static string MethodKey(MethodDeclarationSyntax method) =>
        $"{MemberPrefix(method)}.{(method.ExplicitInterfaceSpecifier is null ? "" : NormalizeTokens(method.ExplicitInterfaceSpecifier.Name) + ".")}" +
        $"{method.Identifier.ValueText}`{method.TypeParameterList?.Parameters.Count ?? 0}({Parameters(method.ParameterList)})";
    private static string Parameters(ParameterListSyntax parameters) => string.Join(",", parameters.Parameters.Select(parameter =>
        string.Join(" ", parameter.Modifiers.Select(item => item.ValueText).Append(NormalizeTokens(parameter.Type)))));
    private static string Parameters(BracketedParameterListSyntax parameters) => string.Join(",", parameters.Parameters.Select(parameter =>
        string.Join(" ", parameter.Modifiers.Select(item => item.ValueText).Append(NormalizeTokens(parameter.Type)))));
    private static string NormalizeTokens(SyntaxNode? node) => node is null ? "?" : string.Concat(node.DescendantTokens().Select(token => token.ValueText));

    private static string MemberPrefix(SyntaxNode node)
    {
        var namespaces = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(item => NormalizeTokens(item.Name));
        var types = node.Ancestors().OfType<TypeDeclarationSyntax>().Reverse()
            .Select(item => item.Identifier.ValueText + "`" + (item.TypeParameterList?.Parameters.Count ?? 0));
        return string.Join(".", namespaces.Concat(types));
    }

    private static string Fingerprint(IEnumerable<SyntaxToken> tokens) => Hash(tokens.Select(token =>
        $"{token.RawKind}:{token.ValueText}").ToArray());
    private static string Hash(params string[] values) => Hash((IEnumerable<string>)values);
    private static string Hash(IEnumerable<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static CallableSourceSpan ToSpan(SyntaxTree tree, TextSpan span)
    {
        var lines = tree.GetLineSpan(span);
        return new CallableSourceSpan(lines.StartLinePosition.Line + 1, lines.StartLinePosition.Character + 1,
            lines.EndLinePosition.Line + 1, lines.EndLinePosition.Character + 1, span.Start, span.Length);
    }

    private sealed record Candidate(SyntaxNode Node, TextSpan Span, TextSpan OwnershipSpan, CallableKind Kind,
        string Name, string SemanticKey, bool Applicable, int Complexity, string BodyFingerprint)
    {
        public static Candidate Create(SyntaxNode node, TextSpan span, TextSpan ownershipSpan, CallableKind kind,
            string name, string semanticKey, bool applicable, int complexity, string fingerprint) =>
            new(node, span, ownershipSpan, kind, name, semanticKey, applicable, complexity, fingerprint);
    }
}
