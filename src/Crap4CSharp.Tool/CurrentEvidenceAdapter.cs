using Crap4CSharp.Core;

internal static class CurrentEvidenceAdapter
{
    public static CurrentEvidence Capture(RunManifest manifest, string workspaceRoot,
        string repositoryIdentity, string workspaceIdentity, string? head)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var inputs = new List<CurrentInputEvidence>();
        foreach (var input in manifest.Contexts.SelectMany(context => context.Inputs).Where(input => !input.Generated)
            .OrderBy(input => input.LogicalPath, StringComparer.Ordinal))
        {
            var logical = CanonicalIdentity.NormalizeLogicalPath(input.LogicalPath);
            var path = Path.GetFullPath(logical.Replace('/', Path.DirectorySeparatorChar), root);
            EnsureContained(root, path);
            var info = new FileInfo(path);
            if (!info.Exists) { inputs.Add(new(input.Role, input.LogicalPath, -1, "missing")); continue; }
            var target = info.ResolveLinkTarget(true);
            if (target is not null) EnsureContained(root, target.FullName);
            if (info.Length > 100 * 1024 * 1024) throw new InvalidDataException($"Workspace input exceeds 100 MB: {logical}");
            var bytes = File.ReadAllBytes(path);
            inputs.Add(new(input.Role, input.LogicalPath, bytes.Length, CanonicalIdentity.Sha256(bytes)));
        }
        var contexts = manifest.Contexts.ToDictionary(context => context.Id, context => context.ContextHash,
            StringComparer.Ordinal);
        return new CurrentEvidence(repositoryIdentity, workspaceIdentity, head, inputs, contexts);
    }

    private static void EnsureContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative)) throw new InvalidDataException("Workspace input escapes the declared root.");
    }
}
