namespace Crap4CSharp.Core;

/// <summary>Defines identity for paths that belong to the local captured source inventory.</summary>
public sealed class PathIdentityPolicy
{
    public static PathIdentityPolicy Sensitive { get; } = new(true);
    public static PathIdentityPolicy Insensitive { get; } = new(false);
    public static PathIdentityPolicy Current { get; } = OperatingSystem.IsWindows()
        ? Insensitive
        : Sensitive;

    public PathIdentityPolicy(bool caseSensitive)
    {
        IsCaseSensitive = caseSensitive;
        Comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        Comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }

    public bool IsCaseSensitive { get; }
    public StringComparer Comparer { get; }
    public StringComparison Comparison { get; }

    public string Normalize(string path, string? baseDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = baseDirectory is null ? Path.GetFullPath(path) : Path.GetFullPath(path, baseDirectory);
        var root = Path.GetPathRoot(fullPath);
        if (root is null) throw new ArgumentException($"Path has no root: {path}", nameof(path));
        while (fullPath.Length > root.Length && Path.EndsInDirectorySeparator(fullPath))
            fullPath = fullPath[..^1];
        return fullPath;
    }

    public bool Equals(string left, string right) =>
        Comparer.Equals(Normalize(left), Normalize(right));

    public bool Contains(string root, string candidate)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);
        if (Comparer.Equals(normalizedRoot, normalizedCandidate)) return true;
        if (!normalizedCandidate.StartsWith(normalizedRoot, Comparison)) return false;
        return normalizedRoot.Length == 0 || Path.EndsInDirectorySeparator(normalizedRoot) ||
            (normalizedCandidate.Length > normalizedRoot.Length &&
             (normalizedCandidate[normalizedRoot.Length] == Path.DirectorySeparatorChar ||
              normalizedCandidate[normalizedRoot.Length] == Path.AltDirectorySeparatorChar));
    }

    internal IReadOnlyList<string> DistinctOrThrow(IEnumerable<string> paths, string description)
    {
        var identities = new Dictionary<string, string>(Comparer);
        foreach (var path in paths.Select(path => Normalize(path)).Order(StringComparer.Ordinal))
        {
            if (identities.TryGetValue(path, out var existing))
            {
                if (!string.Equals(existing, path, StringComparison.Ordinal))
                    throw new InvalidDataException($"Local path identity collision in {description}: '{existing}' and '{path}'.");
                continue;
            }
            identities.Add(path, path);
        }
        return identities.Values.Order(StringComparer.Ordinal).ToArray();
    }
}
