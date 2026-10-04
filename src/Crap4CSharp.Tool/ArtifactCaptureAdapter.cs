using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using Crap4CSharp.Core;

internal static class ArtifactCaptureAdapter
{
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    public static string PublishNew(RunManifest manifest,
        IReadOnlyDictionary<string, ImmutableArray<byte>> artifacts, string destination)
    {
        var target = Path.GetFullPath(destination);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"Artifact output already exists: {target}");
        var parent = Path.GetDirectoryName(target) ?? throw new IOException("Artifact output has no parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.incomplete");
        Directory.CreateDirectory(staging);
        foreach (var pair in artifacts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var locator = CanonicalIdentity.NormalizeLogicalPath(pair.Key);
            var path = Path.GetFullPath(locator.Replace('/', Path.DirectorySeparatorChar), staging);
            EnsureContained(staging, path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(pair.Value.AsSpan());
            stream.Flush(true);
        }
        var verification = ProvenanceVerifier.VerifyCapture(manifest, artifacts, manifest.Producer.ComplexityRuleset);
        if (verification.Status == ProvenanceStatus.Invalid)
            throw new InvalidDataException($"Cannot publish invalid capture: {string.Join(", ", verification.Reasons)}");
        var manifestPath = Path.Combine(staging, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, Json) + "\n", new System.Text.UTF8Encoding(false));
        Directory.Move(staging, target);
        return Path.Combine(target, "manifest.json");
    }

    private static void EnsureContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative)) throw new InvalidDataException("Artifact locator escapes owned staging.");
    }
}
