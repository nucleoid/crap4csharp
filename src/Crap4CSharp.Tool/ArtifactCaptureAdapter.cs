using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using Crap4CSharp.Core;

internal static class ArtifactCaptureAdapter
{
    public const string CompilerEvidenceProvider = "validated-csc-inputs-v1";
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    public static string PublishNew(RunManifest manifest,
        IReadOnlyDictionary<string, ImmutableArray<byte>> artifacts, string destination)
    {
        if (artifacts.Keys.Any(locator => string.Equals(locator.Replace('\\', '/'), "manifest.json",
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The bundle entry name 'manifest.json' is reserved.");
        manifest = ManifestIdentity.Seal(manifest with { ManifestHash = null });
        var declared = manifest.Artifacts.Select(artifact => artifact.Locator)
            .Concat(manifest.Contexts.SelectMany(context => context.Inputs.Select(input => input.Locator)))
            .ToHashSet(StringComparer.Ordinal);
        if (!declared.SetEquals(artifacts.Keys))
            throw new InvalidDataException("Published artifact bytes must exactly match declared bundle locators.");
        var verification = ProvenanceVerifier.VerifyCapture(manifest, artifacts);
        if (verification.Status == ProvenanceStatus.Invalid)
            throw new InvalidDataException($"Cannot publish invalid capture: {string.Join(", ", verification.Reasons)}");
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
        var manifestPath = Path.Combine(staging, "manifest.json");
        var manifestBytes = new System.Text.UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(manifest, Json) + "\n");
        using (var stream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(manifestBytes);
            stream.Flush(true);
        }
        var staged = ArtifactBundle.Load(manifestPath, staging);
        var stagedVerification = ProvenanceVerifier.VerifyCapture(staged.Manifest, staged.Bytes);
        if (stagedVerification.Status == ProvenanceStatus.Invalid)
            throw new InvalidDataException($"Staged capture verification failed: {string.Join(", ", stagedVerification.Reasons)}");
        Directory.Move(staging, target);
        return Path.Combine(target, "manifest.json");
    }

    private static void EnsureContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative)) throw new InvalidDataException("Artifact locator escapes owned staging.");
    }

    public static CapturePhaseValidation ValidatePhases(
        string contextId,
        IReadOnlyList<ManifestInput> authoredBefore,
        IReadOnlyList<ManifestInput> authoredAfter,
        IReadOnlyList<ManifestInput> generatedAfter,
        CompilerInputObservation compilerObservation,
        bool futureReuseRecipeComplete)
    {
        if (compilerObservation.Provider != CompilerEvidenceProvider || !compilerObservation.Complete ||
            compilerObservation.ContextId != contextId)
            return new(false, false, compilerObservation.FailureReason ?? "provenance.compilerEvidenceUnsupported");
        var before = Identities(authoredBefore.Where(input => !input.Generated));
        var after = Identities(authoredAfter.Where(input => !input.Generated));
        if (!before.SequenceEqual(after, StringComparer.Ordinal))
            return new(false, false, "provenance.authoredInputDrift");
        var expected = Identities(authoredAfter.Concat(generatedAfter).Where(input => input.Role == "source"));
        var observed = compilerObservation.Inputs.Select(input =>
                $"source\n{input.LogicalPath}\n{input.ContentIdentity}\n{input.IsGenerated}")
            .Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(observed, StringComparer.Ordinal))
            return new(false, false, "provenance.compiledInputBindingIncomplete");
        if (compilerObservation.AssemblyBytes.IsDefaultOrEmpty || compilerObservation.PdbBytes.IsDefaultOrEmpty ||
            string.IsNullOrWhiteSpace(compilerObservation.ModuleIdentity) ||
            string.IsNullOrWhiteSpace(compilerObservation.Mvid))
            return new(false, false, "provenance.compiledOutputBindingIncomplete");
        try
        {
            var inspected = ArtifactEvidenceInspector.InspectBuild(compilerObservation.AssemblyBytes,
                compilerObservation.PdbBytes);
            if (inspected.ModuleIdentity != compilerObservation.ModuleIdentity ||
                inspected.Mvid != compilerObservation.Mvid)
                return new(false, false, "provenance.compiledOutputBindingIncomplete");
        }
        catch (InvalidDataException)
        {
            return new(false, false, "provenance.compiledOutputBindingIncomplete");
        }
        return new(true, futureReuseRecipeComplete,
            futureReuseRecipeComplete ? null : ProvenanceReasonCodes.ReuseRecipeIncomplete);
    }

    private static string[] Identities(IEnumerable<ManifestInput> values) => values.Select(input =>
            $"{input.Role}\n{input.LogicalPath}\n{input.Sha256}\n{input.Generated}")
        .Order(StringComparer.Ordinal).ToArray();
}

internal sealed record CompilerInputObservation(string Provider, string ContextId,
    IReadOnlyList<CompiledInputIdentity> Inputs, ImmutableArray<byte> AssemblyBytes,
    ImmutableArray<byte> PdbBytes, string ModuleIdentity, string Mvid, bool Complete, string? FailureReason);

internal sealed record CapturePhaseValidation(bool ActualBindingComplete, bool Reusable, string? Reason);
