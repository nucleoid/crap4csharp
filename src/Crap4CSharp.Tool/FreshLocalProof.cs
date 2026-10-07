using Crap4CSharp.Core;

// Finite supported SDK authored membership, not a universal filesystem tracer.
// Actual compiled sources/imports/resources/references are independently regated by
// the retained compiler proof, including inputs outside this workspace inventory.
internal static class FreshLocalProof
{
    private static readonly HashSet<string> Extensions = new(
        [".cs", ".csproj", ".props", ".targets", ".sln", ".slnx", ".json", ".projitems", ".shproj", ".rsp", ".resx", ".editorconfig", ".globalconfig"],
        StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyDictionary<string, string> Capture(string root, CancellationToken token)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(Path.GetFullPath(root));
        long total = 0;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new PolicyException("provenance.localSnapshotUnsupported", "Local workspace membership must not traverse reparse points.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // Same authored-membership boundary as ProjectContextLoader;
                    // compiled inputs from these directories remain compiler-proof inputs.
                    if (Path.GetFileName(path) is not ("bin" or "obj" or ".git" or "TestResults" or "node_modules")) pending.Push(path);
                    continue;
                }
                if (!Extensions.Contains(Path.GetExtension(path)) &&
                    !Path.GetFileName(path).Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase)) continue;
                var length = new FileInfo(path).Length;
                if (length > 16 * 1024 * 1024 || (total += length) > 256 * 1024 * 1024 || values.Count >= 20000)
                    throw new PolicyException("provenance.localSnapshotUnsupported", "Local authored workspace fingerprint exceeds bounded capture limits.");
                var bytes = ArtifactBundle.ReadBounded(path, 16 * 1024 * 1024, length, "local authored input");
                if (bytes.Length != length)
                    throw new PolicyException("provenance.localWorkspaceChanged", "Local authored input changed during fingerprint capture.");
                var hash = CanonicalIdentity.Sha256(bytes);
                values.Add(Path.GetRelativePath(root, path).Replace('\\', '/'), hash);
            }
        }
        return values;
    }

    internal static string Identity(IReadOnlyDictionary<string, string> values) => CanonicalIdentity.Set(
        "fresh-local-authored-membership-v1", values.Select(p => CanonicalIdentity.Tuple("authored-input", p.Key, p.Value)));

    internal static void Validate(FreshCheckPreflight preflight, CancellationToken token)
    {
        if (preflight.Trusted.Trust != "local-unreviewed" || preflight.Policy.Scope != "all" ||
            preflight.Request.BaseRef is not null || preflight.Scope is not null || preflight.LocalSnapshot is null)
            throw new PolicyException("provenance.localSnapshotUnsupported", "Local fresh binding requires explicit all-scope local policy and no Git selection.");
        if (Identity(preflight.LocalSnapshot) != Identity(Capture(preflight.Root, token)))
            throw new PolicyException("provenance.localWorkspaceChanged", "Local authored workspace membership or content changed after preflight.");
        foreach (var input in preflight.Trusted.ContentHashes)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(input.Key, preflight.Root);
            var bytes = ArtifactBundle.ReadBounded(path, 16 * 1024 * 1024, null, "local policy input");
            if (CanonicalIdentity.Sha256(bytes) != input.Value)
                throw new PolicyException("provenance.localPolicyChanged", "Local policy, baseline or exemption content changed after acquisition.");
        }
    }
}
