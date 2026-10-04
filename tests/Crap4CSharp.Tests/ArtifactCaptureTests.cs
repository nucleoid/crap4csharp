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

    private static FixtureData CreateFixture()
    {
        var source = ImmutableArray.Create<byte>(1, 2, 3);
        var coverage = ImmutableArray.Create(Encoding.UTF8.GetBytes(
            "<coverage><packages><package name=\"App\"><classes /></package></packages></coverage>"));
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
            new ManifestProducer("crap4csharp", "test", ComplexityRules.CallablesV1,
                ProjectAnalysisContext.ProtocolVersion, ManifestIdentity.CoverageProtocol, ManifestIdentity.PathProtocol),
            new ManifestCapture("completed", true, true, []),
            new ManifestRevision("none", "none", "workspace", null, null, null),
            [new ManifestRoot("workspace", "workspace", "sensitive")], [context],
            [new ManifestBuild("build", "ctx", "App", "dll", "mvid", "pdb", "portable-pdb")],
            [new ManifestExecution("test", "ctx", "build", true, 0, 1, 1, 0, 0)],
            [new ManifestArtifact("source", "source", "artifacts/source.bin", source.Length,
                    CanonicalIdentity.Sha256(source.AsSpan()), "ctx", null, null, null, null),
             new ManifestArtifact("coverage", "coverage", "artifacts/coverage.xml", coverage.Length,
                    CanonicalIdentity.Sha256(coverage.AsSpan()), "ctx", "build", "test", "cobertura", "line")],
            new ManifestEvaluationInputs("scope", "policy", null, null), null);
        return new FixtureData(manifest, new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal)
        {
            ["artifacts/source.bin"] = source,
            ["artifacts/coverage.xml"] = coverage
        });
    }

    private sealed record FixtureData(RunManifest Manifest,
        IReadOnlyDictionary<string, ImmutableArray<byte>> Bytes);
}
