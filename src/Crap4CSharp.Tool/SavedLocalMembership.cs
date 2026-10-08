using System.Text.Json;
using Crap4CSharp.Core;

// Hash-only original finite membership, not a universal input or runtime tracer.
internal static class SavedLocalMembership
{
    internal const string Kind = "original-local-membership";
    internal const int MaximumBytes = 8 * 1024 * 1024;
    internal static byte[] Capture(IReadOnlyDictionary<string, string> original, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { provider = "finite-local-membership-v1",
            entries = original.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { path = p.Key, sha256 = p.Value }) });
        _ = Parse(bytes, FreshLocalProof.Identity(original), token);
        return bytes;
    }

    internal static IReadOnlyDictionary<string, string> Parse(byte[] bytes, string? originalStateHash, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw Refuse();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            Fields(root, "provider", "entries");
            if (root.GetProperty("provider").GetString() != "finite-local-membership-v1") throw Refuse();
            var entries = root.GetProperty("entries");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 20000) throw Refuse();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var physicalNames = new HashSet<string>(PathIdentityPolicy.Current.Comparer);
            string? previous = null;
            foreach (var entry in entries.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                Fields(entry, "path", "sha256");
                var path = entry.GetProperty("path").GetString();
                var hash = entry.GetProperty("sha256").GetString();
                if (string.IsNullOrEmpty(path) || path.Length > 4096 || path.Any(char.IsControl) ||
                    path.Contains('\\') || path.Contains(':') || Path.IsPathRooted(path) ||
                    path.Split('/').Any(p => p is "" or "." or "..") || CanonicalIdentity.NormalizeLogicalPath(path) != path ||
                    (previous is not null && StringComparer.Ordinal.Compare(previous, path) >= 0) || !physicalNames.Add(path) ||
                    hash is null || hash.Length != 64 || hash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))) throw Refuse();
                values.Add(path, hash); previous = path;
            }
            if (FreshLocalProof.Identity(values) != originalStateHash) throw Refuse();
            token.ThrowIfCancellationRequested();
            return values;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw Refuse(); }
    }

    // Caller must first complete ALL independently current compiler/test/coverage gates.
    internal static string Observe(ArtifactBundle bundle, FreshCheckPreflight preflight,
        IReadOnlyList<string> provenOutputs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var artifacts = bundle.Manifest.Artifacts.Where(a => a.Kind == Kind).ToArray();
        if (bundle.Manifest.Revision.Kind != "none" || artifacts.Length != 1) throw Refuse();
        var artifact = artifacts[0];
        if (artifact.Id != Kind || artifact.Format != "json" || artifact.ContextId is not null ||
            artifact.BuildId is not null || artifact.ExecutionId is not null || artifact.CoordinateKind is not null ||
            !bundle.Bytes.TryGetValue(artifact.Locator, out var bytes) || bytes.Length != artifact.Length ||
            CanonicalIdentity.Sha256(bytes.AsSpan()) != artifact.Sha256) throw Refuse();
        var original = Parse(bytes.ToArray(), bundle.Manifest.Revision.StateHash, token);
        return FreshLocalProof.Identity(FreshLocalProof.NormalizeCurrent(preflight, original,
            preflight.LocalSnapshot ?? throw Refuse(), provenOutputs, token));
    }

    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Refuse();
        var fields = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!fields.Remove(property.Name)) throw Refuse();
        if (fields.Count != 0) throw Refuse();
    }
    private static PolicyException Refuse() => new("provenance.savedLocalMembershipUnproven",
        "Original local membership recipe is malformed, unsupported or not bound to the captured revision.");
}
