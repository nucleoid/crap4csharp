namespace Crap4CSharp.Core;

public sealed record ChangedMethodSelection(
    IReadOnlyList<SourceMethod> Methods,
    IReadOnlyList<SourceMethod> Removals,
    bool ConservativelyWidened,
    string? WideningReason);

public static class ChangedMethodSelector
{
    public static ChangedMethodSelection Select(ChangedFile change, IReadOnlyList<SourceMethod> oldMethods,
        IReadOnlyList<SourceMethod> newMethods, ScopeGranularity granularity)
    {
        if (granularity == ScopeGranularity.File)
            return new ChangedMethodSelection(newMethods, RemovedMethods(oldMethods, newMethods), false, null);

        if (change.Kind == ScopeChangeKind.Added)
            return new ChangedMethodSelection(newMethods, [], false, null);
        if (change.Kind == ScopeChangeKind.Deleted)
            return new ChangedMethodSelection([], oldMethods, false, null);
        if (change.Kind == ScopeChangeKind.Renamed && change.OldIdentity == change.NewIdentity &&
            change.AddedRanges.Count == 0 && change.DeletedRanges.Count == 0)
            return new ChangedMethodSelection([], [], false, null);

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
            widened ? "scope.ambiguousCallableMapping" : null);
    }

    private static IReadOnlyList<SourceMethod> RemovedMethods(IReadOnlyList<SourceMethod> oldMethods,
        IReadOnlyList<SourceMethod> newMethods)
    {
        var current = newMethods.Select(method => method.CanonicalSignature).ToHashSet(StringComparer.Ordinal);
        return oldMethods.Where(method => !current.Contains(method.CanonicalSignature)).ToArray();
    }
}
