namespace Crap4CSharp.Core;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public sealed record ScopeLimitation(string Code, string Path, string SyntaxKind, LineRange Range);

public sealed record ChangedMethodSelection(
    IReadOnlyList<SourceMethod> Methods,
    IReadOnlyList<SourceMethod> Removals,
    bool ConservativelyWidened,
    string? WideningReason,
    IReadOnlyList<ScopeLimitation>? Limitations = null)
{
    public IReadOnlyList<ScopeLimitation> ScopeLimitations => Limitations ?? [];
}

public static class ChangedMethodSelector
{
    public static ChangedMethodSelection Select(ChangedFile change, IReadOnlyList<SourceMethod> oldMethods,
        IReadOnlyList<SourceMethod> newMethods, ScopeGranularity granularity)
    {
        var limitations = FindUnsupportedExecutableChanges(change);
        if (granularity == ScopeGranularity.File)
            return new ChangedMethodSelection(newMethods, RemovedMethods(oldMethods, newMethods), false, null, limitations);

        if (change.Kind is ScopeChangeKind.Added or ScopeChangeKind.Copied)
            return new ChangedMethodSelection(newMethods, [], false, null, limitations);
        if (change.Kind == ScopeChangeKind.Deleted)
            return new ChangedMethodSelection([], oldMethods, false, null, limitations);
        if (change.Kind == ScopeChangeKind.Renamed && change.OldIdentity == change.NewIdentity &&
            change.AddedRanges.Count == 0 && change.DeletedRanges.Count == 0)
            return new ChangedMethodSelection([], [], false, null, limitations);

        var selected = newMethods.Where(method => change.AddedRanges.Any(range => range.Intersects(method.StartLine, method.EndLine)))
            .ToList();
        var oldTouched = oldMethods.Where(method => change.DeletedRanges.Any(range => range.Intersects(method.StartLine, method.EndLine))).ToArray();
        var removals = new List<SourceMethod>();
        var widened = false;

        foreach (var oldMethod in oldTouched)
        {
            var matches = newMethods.Where(method => method.CanonicalSignature == oldMethod.CanonicalSignature).ToArray();
            if (matches.Length == 1)
            {
                if (!selected.Contains(matches[0])) selected.Add(matches[0]);
            }
            else if (matches.Length == 0)
            {
                removals.Add(oldMethod);
            }
            else
            {
                widened = true;
            }
        }

        if (widened)
            selected = newMethods.ToList();
        return new ChangedMethodSelection(selected.Distinct().OrderBy(method => method.StartLine).ToArray(),
            removals.Distinct().OrderBy(method => method.StartLine).ToArray(), widened,
            widened ? "scope.ambiguousCallableMapping" : null, limitations);
    }

    private static IReadOnlyList<SourceMethod> RemovedMethods(IReadOnlyList<SourceMethod> oldMethods,
        IReadOnlyList<SourceMethod> newMethods)
    {
        var current = newMethods.Select(method => method.CanonicalSignature).ToHashSet(StringComparer.Ordinal);
        return oldMethods.Where(method => !current.Contains(method.CanonicalSignature)).ToArray();
    }

    private static IReadOnlyList<ScopeLimitation> FindUnsupportedExecutableChanges(ChangedFile change)
    {
        var limitations = new List<ScopeLimitation>();
        AddLimitations(change.NewSource, change.AddedRanges, limitations);
        AddLimitations(change.OldSource, change.DeletedRanges, limitations);
        return limitations.Distinct().OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Range.StartLine).ThenBy(item => item.SyntaxKind, StringComparer.Ordinal).ToArray();
    }

    private static void AddLimitations(CapturedSource? source, IReadOnlyList<LineRange> ranges, List<ScopeLimitation> output)
    {
        if (source is null || ranges.Count == 0) return;
        var tree = CSharpSyntaxTree.ParseText(source.Text, path: source.LogicalPath);
        foreach (var node in tree.GetRoot().DescendantNodes().Where(IsUnsupportedExecutable))
        {
            if (node.Ancestors().OfType<MethodDeclarationSyntax>().Any()) continue;
            var span = tree.GetLineSpan(node.Span);
            var range = new LineRange(span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
            if (ranges.Any(changed => changed.Intersects(range.StartLine, range.EndLine)))
                output.Add(new ScopeLimitation("scope.unsupportedChangedCallable", source.LogicalPath, node.Kind().ToString(), range));
        }
    }

    private static bool IsUnsupportedExecutable(SyntaxNode node) => node is
        ConstructorDeclarationSyntax or DestructorDeclarationSyntax or OperatorDeclarationSyntax or
        ConversionOperatorDeclarationSyntax or AccessorDeclarationSyntax or GlobalStatementSyntax or
        SimpleLambdaExpressionSyntax or ParenthesizedLambdaExpressionSyntax or AnonymousMethodExpressionSyntax ||
        node is PropertyDeclarationSyntax { ExpressionBody: not null } ||
        node is IndexerDeclarationSyntax { ExpressionBody: not null } ||
        node is VariableDeclaratorSyntax { Initializer: not null };
}
