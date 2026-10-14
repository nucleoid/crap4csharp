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
        => PublishNewCancellable(manifest, artifacts, destination, CancellationToken.None);

    internal static string PublishNewCancellable(RunManifest manifest,
        IReadOnlyDictionary<string, ImmutableArray<byte>> artifacts, string destination, CancellationToken cancellationToken)
        => StageNewCancellable(manifest, artifacts, destination, cancellationToken).CommitVerifiedCapture(cancellationToken);

    internal static StagedArtifactPublication StageNewCancellable(RunManifest manifest,
        IReadOnlyDictionary<string, ImmutableArray<byte>> artifacts, string destination, CancellationToken cancellationToken,
        Action<string>? retainCreatedDirectory = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (artifacts.Keys.Any(locator => string.Equals(locator.Replace('\\', '/'), "manifest.json",
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The bundle entry name 'manifest.json' is reserved.");
        manifest = ManifestIdentity.Seal(manifest with { ManifestHash = null });
        var declared = manifest.Artifacts.Select(artifact => artifact.Locator)
            .Concat(manifest.Contexts.SelectMany(context => context.Inputs.Select(input => input.Locator)))
            .ToHashSet(StringComparer.Ordinal);
        if (!declared.SetEquals(artifacts.Keys))
            throw new InvalidDataException("Published artifact bytes must exactly match declared bundle locators.");
        var verification = ProvenanceVerifier.VerifyCaptureCancellable(manifest, artifacts, null, cancellationToken);
        if (verification.Status == ProvenanceStatus.Invalid)
            throw new InvalidDataException($"Cannot publish invalid capture: {string.Join(", ", verification.Reasons)}");
        var target = Path.GetFullPath(destination);
        if (ArtifactBundle.IsPublicationStagingRoot(target))
            throw new InvalidDataException("Artifact output uses a reserved publication-staging directory name.");
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"Artifact output already exists: {target}");
        var parent = Path.GetDirectoryName(target) ?? throw new IOException("Artifact output has no parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.incomplete");
        Directory.CreateDirectory(staging);
        retainCreatedDirectory?.Invoke(staging);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var pair in artifacts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locator = CanonicalIdentity.NormalizeLogicalPath(pair.Key);
            var path = Path.GetFullPath(locator.Replace('/', Path.DirectorySeparatorChar), staging);
            EnsureContained(staging, path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            for (var offset = 0; offset < pair.Value.Length; offset += 64 * 1024)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Write(pair.Value.AsSpan(offset, Math.Min(64 * 1024, pair.Value.Length - offset)));
            }
            stream.Flush(true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var manifestPath = Path.Combine(staging, "manifest.json");
        var manifestBytes = new System.Text.UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(manifest, Json) + "\n");
        using (var stream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(manifestBytes);
            stream.Flush(true);
        }
        var staged = ArtifactBundle.LoadStaging(manifestPath, staging);
        var stagedVerification = ProvenanceVerifier.VerifyCaptureCancellable(staged.Manifest, staged.Bytes, null, cancellationToken);
        if (stagedVerification.Status == ProvenanceStatus.Invalid)
            throw new InvalidDataException($"Staged capture verification failed: {string.Join(", ", stagedVerification.Reasons)}");
        cancellationToken.ThrowIfCancellationRequested();
        return new(staged, target);
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

// A verified capture is still prospective until the caller's terminal proof.
// Refusal leaves the requested destination absent; the nonce staging directory
// is retained rather than deleting paths that may have become consumer inputs.
internal sealed class StagedArtifactPublication(ArtifactBundle bundle, string target)
{
    internal ArtifactBundle Bundle { get; } = bundle;
    internal string Destination { get; } = target;
    internal string ManifestPath => Path.Combine(Destination, "manifest.json");
    private bool committed;
    private bool requiresCompletedProof;
    internal void RequireCompletedProof() => requiresCompletedProof = true;

    internal string Commit(CancellationToken token, IReadOnlyList<string>? protectedPaths = null)
    {
        if (requiresCompletedProof) throw ProvisionalRefusal();
        return CommitCore(token, protectedPaths ?? [], true);
    }

    internal string CommitCompleted(ArtifactBundle verifiedBundle, ProvenanceResult proof,
        CancellationToken token, IReadOnlyList<string> protectedPaths)
    {
        if (!requiresCompletedProof || !ReferenceEquals(Bundle, verifiedBundle) ||
            proof.Status != ProvenanceStatus.Verified || !proof.PostflightVerified || !proof.Reusable)
            throw ProvisionalRefusal();
        return CommitCore(token, protectedPaths, true);
    }

    private static PolicyException ProvisionalRefusal() => new("check.provisionalCaptureRequiresCompletedProof",
        "Provisional capture requires final and terminal completed proof before publication.");

    // Only the immediate publication adapters call this, directly after their
    // synchronous staged-byte verification. Deferred exports use Commit instead.
    internal string CommitVerifiedCapture(CancellationToken token)
    {
        if (requiresCompletedProof) throw new PolicyException("check.provisionalCaptureRequiresCompletedProof",
            "Provisional capture requires final and terminal completed proof before publication.");
        return CommitCore(token, [], false);
    }

    private string CommitCore(CancellationToken token, IReadOnlyList<string> protectedPaths, bool verifyFootprint)
    {
        token.ThrowIfCancellationRequested();
        if (committed) throw new InvalidOperationException("Artifact staging has already been published.");
        var parent = Path.GetDirectoryName(Destination)!;
        if (verifyFootprint) _ = SavedBundleFootprint.Validate(Bundle, parent, protectedPaths, token);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (protectedPaths.Any(path => PathIdentityPolicy.Current.Comparer.Equals(Path.GetFullPath(path), Destination) ||
            Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Destination) + Path.DirectorySeparatorChar, comparison)))
            throw new PolicyException("output.aliasesPolicyInput", "Artifact destination contains a protected consumer input.");
        for (var path = parent; path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Artifact destination traverses a reparse point.");
        if (File.Exists(Destination) || Directory.Exists(Destination))
            throw new IOException("Artifact destination already exists.");
        token.ThrowIfCancellationRequested();
        Directory.Move(Bundle.BundleRoot, Destination);
        committed = true;
        return ManifestPath;
    }
}

internal sealed record CompilerInputObservation(string Provider, string ContextId,
    IReadOnlyList<CompiledInputIdentity> Inputs, ImmutableArray<byte> AssemblyBytes,
    ImmutableArray<byte> PdbBytes, string ModuleIdentity, string Mvid, bool Complete, string? FailureReason);

internal sealed record CapturePhaseValidation(bool ActualBindingComplete, bool Reusable, string? Reason);
