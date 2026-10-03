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

    public string NormalizeExisting(string path, string? baseDirectory = null)
    {
        var normalized = Normalize(path, baseDirectory);
        if (IsCaseSensitive) return normalized;

        var root = Path.GetPathRoot(normalized)
            ?? throw new ArgumentException($"Path has no root: {path}", nameof(path));
        var current = root;
        foreach (var segment in normalized[root.Length..].Split(
                     Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(current)) return normalized;
            var matches = Directory.EnumerateFileSystemEntries(current)
                .Where(entry => Comparer.Equals(Path.GetFileName(entry), segment))
                .Order(StringComparer.Ordinal).ToArray();
            var exact = matches.FirstOrDefault(entry =>
                string.Equals(Path.GetFileName(entry), segment, StringComparison.Ordinal));
            if (exact is not null)
            {
                current = exact;
                continue;
            }
            if (matches.Length > 1)
                throw new InvalidDataException(
                    $"Local path identity collision while resolving '{path}': {string.Join(", ", matches.Select(Path.GetFileName))}.");
            current = matches.Length == 1 ? matches[0] : Path.Combine(current, segment);
        }
        return Normalize(current);
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
