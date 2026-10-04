using System.Collections.Immutable;
using System.Text.Json;
using Crap4CSharp.Core;

internal sealed record ArtifactBundle(RunManifest Manifest,
    IReadOnlyDictionary<string, ImmutableArray<byte>> Bytes, string ManifestPath)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static ArtifactBundle Load(string manifestPath, string workingDirectory)
    {
        var fullManifest = Path.GetFullPath(manifestPath, workingDirectory);
        var info = new FileInfo(fullManifest);
        if (!info.Exists) throw new FileNotFoundException($"Artifact manifest not found: {fullManifest}", fullManifest);
        if (info.Length > 4 * 1024 * 1024) throw new InvalidDataException("Artifact manifest exceeds the 4 MB limit.");
        var root = info.DirectoryName ?? throw new InvalidDataException("Artifact manifest has no bundle root.");
        var manifestBytes = File.ReadAllBytes(fullManifest);
        var manifest = JsonSerializer.Deserialize<RunManifest>(manifestBytes, Json)
            ?? throw new InvalidDataException("Artifact manifest is empty.");
        var locators = manifest.Artifacts.Select(item => item.Locator)
            .Concat(manifest.Contexts.SelectMany(context => context.Inputs.Select(input => input.Locator)))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var bytes = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        long total = manifestBytes.Length;
        foreach (var locator in locators)
        {
            var normalized = CanonicalIdentity.NormalizeLogicalPath(locator);
            var path = Path.GetFullPath(normalized.Replace('/', Path.DirectorySeparatorChar), root);
            EnsureContained(root, path);
            var artifact = new FileInfo(path);
            if (!artifact.Exists) continue;
            var target = artifact.ResolveLinkTarget(true);
            if (target is not null) EnsureContained(root, target.FullName);
            if (artifact.Length > 100 * 1024 * 1024) throw new InvalidDataException($"Artifact exceeds the 100 MB limit: {locator}");
            total += artifact.Length;
            if (total > 512L * 1024 * 1024) throw new InvalidDataException("Artifact bundle exceeds the 512 MB limit.");
            bytes.Add(locator, ImmutableArray.Create(File.ReadAllBytes(path)));
        }
        return new ArtifactBundle(manifest, bytes, fullManifest);
    }

    private static void EnsureContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative)) throw new InvalidDataException("Artifact locator escapes the bundle root.");
    }
}
