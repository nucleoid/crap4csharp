using Crap4CSharp.Core;

// Exact loaded files, never a directory-name exemption. Call only after saved proof.
internal static class SavedBundleFootprint
{
    internal static IReadOnlySet<string> Validate(ArtifactBundle bundle, string workspace,
        IReadOnlyList<string> protectedPaths, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var comparer = PathIdentityPolicy.Current.Comparer;
        var root = Path.GetFullPath(bundle.BundleRoot);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Beneath(string path, string directory) => path.StartsWith(
            Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, comparison);
        if (comparer.Equals(root, Path.GetFullPath(workspace)) || Beneath(workspace, root) ||
            protectedPaths.Any(path => comparer.Equals(root, Path.GetFullPath(path)) || Beneath(Path.GetFullPath(path), root)))
            throw Refuse();
        // Check ancestors too: a root reached through a directory link is not owned.
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw Refuse();
        var expected = new HashSet<string>(bundle.ResolvedInputs.Select(Path.GetFullPath), comparer);
        var directories = new HashSet<string>(comparer) { root };
        foreach (var path in expected)
        {
            if (!Beneath(path, root)) throw Refuse();
            for (var parent = Path.GetDirectoryName(path); parent is not null && Beneath(parent, root); parent = Path.GetDirectoryName(parent))
                directories.Add(parent);
        }
        var found = new HashSet<string>(comparer);
        var pending = new Stack<string>(); pending.Push(root);
        var count = 0;
        while (pending.TryPop(out var directory))
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            if (++count > 20000) throw Refuse();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw Refuse();
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!directories.Contains(path)) throw Refuse();
                pending.Push(path);
            }
            else
            {
                if (!expected.Contains(path) || !ArtifactBundle.IsRegularInput(path) || !found.Add(path)) throw Refuse();
            }
        }
        if (!found.SetEquals(expected) || bundle.LoadedManifestSha256 is null) throw Refuse();
        var current = ArtifactBundle.Load(bundle.ManifestPath, workspace);
        if (current.LoadedManifestSha256 != bundle.LoadedManifestSha256 || current.Manifest.ManifestHash != bundle.Manifest.ManifestHash ||
            current.Bytes.Count != bundle.Bytes.Count || !expected.SetEquals(current.ResolvedInputs)) throw Refuse();
        foreach (var entry in bundle.Bytes)
        {
            token.ThrowIfCancellationRequested();
            if (!current.Bytes.TryGetValue(entry.Key, out var bytes) || !entry.Value.AsSpan().SequenceEqual(bytes.AsSpan())) throw Refuse();
        }
        token.ThrowIfCancellationRequested();
        return expected;
    }

    private static PolicyException Refuse() => new("provenance.savedBundleFootprintUnproven",
        "Loaded evidence bundle membership, bytes or input separation changed or cannot be proven.");
}
