using System.Collections.Immutable;
using System.Text;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ArtifactCaptureTests
{
    [Fact]
    public void PublicationSealsVerifiesAndNeverOverwritesADestination()
    {
        using var directory = TestDirectory.Create("crap4csharp-publish");
        var fixture = CreateFixture();
        var target = Path.Combine(directory.Path, "bundle");

        var locator = ArtifactCaptureAdapter.PublishNew(fixture.Manifest, fixture.Bytes, target);
        var loaded = ArtifactBundle.Load(locator, directory.Path);

        Assert.NotNull(loaded.Manifest.ManifestHash);
        Assert.Equal(ProvenanceStatus.Captured,
            ProvenanceVerifier.VerifyCapture(loaded.Manifest, loaded.Bytes).Status);
        Assert.Throws<IOException>(() => ArtifactCaptureAdapter.PublishNew(fixture.Manifest, fixture.Bytes, target));
    }

    [Fact]
    public void PublicationReservesManifestLocatorBeforeCreatingStaging()
    {
        using var directory = TestDirectory.Create("crap4csharp-reserved-manifest");
        var fixture = CreateFixture();
        var bytes = fixture.Bytes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        bytes["manifest.json"] = ImmutableArray.Create<byte>(1);

        Assert.Throws<InvalidDataException>(() => ArtifactCaptureAdapter.PublishNew(
            fixture.Manifest, bytes, Path.Combine(directory.Path, "bundle")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void SavedInputListCannotMasqueradeAsRevalidatedCurrentContext()
    {
        using var directory = TestDirectory.Create("crap4csharp-current-evidence");
        Directory.CreateDirectory(Path.Combine(directory.Path, "src"));
        File.WriteAllBytes(Path.Combine(directory.Path, "src", "C.cs"), [1, 2, 3]);
        var fixture = CreateFixture();
        var manifest = ManifestIdentity.Seal(fixture.Manifest with
        {
            Revision = new ManifestRevision("none", "none",
                CanonicalIdentity.Set("local-workspace-v1", [Path.GetFullPath(directory.Path)]), null, null, null),
            ManifestHash = null
        });

        var current = CurrentEvidenceAdapter.Capture(manifest, directory.Path);
        var result = ProvenanceVerifier.VerifyCurrent(manifest, fixture.Bytes, current, true);

        Assert.False(current.MembershipRecipeRevalidated);
        Assert.Empty(current.ContextHashes);
        Assert.Equal(ProvenanceStatus.Invalid, result.Status);
        Assert.Contains(ProvenanceReasonCodes.ContextNotRevalidated, result.Reasons);
    }

    [Fact]
    public void LoaderRejectsASymbolicLinkInAnyLocatorComponent()
    {
        if (OperatingSystem.IsWindows()) return; // Creation requires a Windows symlink privilege; Linux exercises the policy.
        using var directory = TestDirectory.Create("crap4csharp-bundle-link");
        var fixture = CreateFixture();
        var locator = ArtifactCaptureAdapter.PublishNew(fixture.Manifest, fixture.Bytes,
            Path.Combine(directory.Path, "bundle"));
        var artifactDirectory = Path.Combine(directory.Path, "bundle", "artifacts");
        var outside = Path.Combine(directory.Path, "outside");
        Directory.Move(artifactDirectory, outside);
        Directory.CreateSymbolicLink(artifactDirectory, outside);

        Assert.Throws<InvalidDataException>(() => ArtifactBundle.Load(locator, directory.Path));
    }

    [Fact]
    public void ThreePhaseValidationAcceptsExpectedGeneratedTransitionButNotAuthoredDrift()
    {
        var authored = new ManifestInput("source", "src/C.cs", "artifacts/source.bin", 3,
            CanonicalIdentity.Sha256([1, 2, 3]), "utf-8", false);
        var generated = new ManifestInput("source", "obj/G.g.cs", "artifacts/generated.bin", 2,
            CanonicalIdentity.Sha256([4, 5]), "utf-8", true);
        var observed = new CompilerInputObservation(ArtifactCaptureAdapter.CompilerEvidenceProvider, "ctx",
            [new CompiledInputIdentity(authored.LogicalPath, authored.Sha256, false),
             new CompiledInputIdentity(generated.LogicalPath, generated.Sha256, true)],
            ImmutableArray.Create<byte>(6), ImmutableArray.Create<byte>(7), "App", "mvid", true, null);

        var fresh = ArtifactCaptureAdapter.ValidatePhases("ctx", [authored], [authored], [generated], observed, false);
        var reusable = ArtifactCaptureAdapter.ValidatePhases("ctx", [authored], [authored], [generated], observed, true);
        var drifted = ArtifactCaptureAdapter.ValidatePhases("ctx", [authored],
            [authored with { Sha256 = CanonicalIdentity.Sha256([1, 2, 4]) }], [generated], observed, true);

        Assert.True(fresh.ActualBindingComplete);
        Assert.False(fresh.Reusable);
        Assert.Equal(ProvenanceReasonCodes.ReuseRecipeIncomplete, fresh.Reason);
        Assert.True(reusable.ActualBindingComplete);
        Assert.True(reusable.Reusable);
        Assert.False(drifted.ActualBindingComplete);
        Assert.Equal("provenance.authoredInputDrift", drifted.Reason);
    }

    [Fact]
    public async Task ConcurrentPublicationHasExactlyOneWinnerAndNeverOverwrites()
    {
        using var directory = TestDirectory.Create("crap4csharp-concurrent-publish");
        var fixture = CreateFixture();
        var target = Path.Combine(directory.Path, "bundle");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await Task.Yield();
            try
            {
                return (Success: true, Error: (Exception?)null,
                    Locator: ArtifactCaptureAdapter.PublishNew(fixture.Manifest, fixture.Bytes, target));
            }
            catch (Exception exception)
            {
                return (Success: false, Error: exception, Locator: (string?)null);
            }
        }));

        Assert.Single(attempts, attempt => attempt.Success);
        Assert.Single(attempts, attempt => !attempt.Success && attempt.Error is IOException);
        var loaded = ArtifactBundle.Load(Path.Combine(target, "manifest.json"), directory.Path);
        Assert.Equal(ProvenanceStatus.Captured, ProvenanceVerifier.VerifyCapture(loaded.Manifest, loaded.Bytes).Status);
    }

    private static FixtureData CreateFixture()
    {
        var source = ImmutableArray.Create<byte>(1, 2, 3);
        var coverage = ImmutableArray.Create(Encoding.UTF8.GetBytes(
            "<coverage><packages><package name=\"App\"><classes /></package></packages></coverage>"));
        var assembly = ImmutableArray.Create<byte>(7);
        var pdb = ImmutableArray.Create<byte>(8);
        var trx = ImmutableArray.Create<byte>(9);
        var scope = ImmutableArray.Create<byte>(10);
        var policy = ImmutableArray.Create<byte>(11);
        var context = new ManifestContext("ctx", "App.csproj", "net10.0", "Debug", "AnyCPU",
            "", "", "", true, true,
            [new ManifestInput("source", "src/C.cs", "artifacts/source.bin", source.Length,
                CanonicalIdentity.Sha256(source.AsSpan()), "utf-8", false)])
        {
            ParseOptions = new ManifestParseOptions("preview", "Regular", [],
                new Dictionary<string, string>(StringComparer.Ordinal)),
            PathPolicy = new ManifestPathPolicy("sensitive", [])
        };
        var manifest = new RunManifest("1.0", CanonicalIdentity.Algorithm,
            new ManifestProducer("crap4csharp", "0.1.0", ComplexityRules.CallablesV1,
                ProjectAnalysisContext.ProtocolVersion, ManifestIdentity.CoverageProtocol, ManifestIdentity.PathProtocol),
            new ManifestCapture("completed", true, true, []),
            new ManifestRevision("none", "none", "workspace", null, null, null),
            [new ManifestRoot("workspace", "workspace", "sensitive")], [context],
            [new ManifestBuild("build", "ctx", "App", CanonicalIdentity.Sha256(assembly.AsSpan()), "mvid",
                CanonicalIdentity.Sha256(pdb.AsSpan()), "portable-pdb")],
            [new ManifestExecution("test", "ctx", "build", true, 0, 1, 1, 0, 0)],
            [new ManifestArtifact("source", "source", "artifacts/source.bin", source.Length,
                    CanonicalIdentity.Sha256(source.AsSpan()), "ctx", null, null, null, null),
             new ManifestArtifact("coverage", "coverage", "artifacts/coverage.xml", coverage.Length,
                    CanonicalIdentity.Sha256(coverage.AsSpan()), "ctx", "build", "test", "cobertura", "line"),
             new ManifestArtifact("assembly", "assembly", "artifacts/app.dll", assembly.Length,
                    CanonicalIdentity.Sha256(assembly.AsSpan()), "ctx", "build", null, null, null),
             new ManifestArtifact("pdb", "pdb", "artifacts/app.pdb", pdb.Length,
                    CanonicalIdentity.Sha256(pdb.AsSpan()), "ctx", "build", null, null, null),
             new ManifestArtifact("test-result", "test-result", "artifacts/results.trx", trx.Length,
                    CanonicalIdentity.Sha256(trx.AsSpan()), "ctx", "build", "test", "trx", null),
             new ManifestArtifact("scope", "scope", "artifacts/scope.json", scope.Length,
                    CanonicalIdentity.Sha256(scope.AsSpan()), null, null, null, "json", null),
             new ManifestArtifact("policy", "policy", "artifacts/policy.json", policy.Length,
                    CanonicalIdentity.Sha256(policy.AsSpan()), null, null, null, "json", null)],
            new ManifestEvaluationInputs(CanonicalIdentity.Sha256(scope.AsSpan()),
                CanonicalIdentity.Sha256(policy.AsSpan()), null, null), null);
        return new FixtureData(manifest, new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal)
        {
            ["artifacts/source.bin"] = source,
            ["artifacts/coverage.xml"] = coverage,
            ["artifacts/app.dll"] = assembly,
            ["artifacts/app.pdb"] = pdb,
            ["artifacts/results.trx"] = trx,
            ["artifacts/scope.json"] = scope,
            ["artifacts/policy.json"] = policy
        });
    }

    private sealed record FixtureData(RunManifest Manifest,
        IReadOnlyDictionary<string, ImmutableArray<byte>> Bytes);
}
