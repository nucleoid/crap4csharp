using System.Collections.Immutable;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ProvenanceTests
{
    [Fact]
    public void CanonicalIdentityBindsExactBytesAndLogicalIdentity()
    {
        var value = CanonicalIdentity.Content("source", "src/C.cs", [1, 2, 3]);
        Assert.Equal(value, CanonicalIdentity.Content("source", "src/C.cs", [1, 2, 3]));
        Assert.NotEqual(value, CanonicalIdentity.Content("source", "src/D.cs", [1, 2, 3]));
        Assert.NotEqual(value, CanonicalIdentity.Content("source", "src/C.cs", [1, 2, 4]));
        Assert.Equal(64, value.Length);
    }

    [Fact]
    public void CapturedManifestRejectsTamperedAndDanglingArtifacts()
    {
        var manifest = Fixture();
        var bytes = FixtureBytes();
        Assert.Equal(ProvenanceStatus.Captured, ProvenanceVerifier.VerifyCapture(manifest, bytes).Status);

        bytes["artifacts/coverage.xml"] = ImmutableArray.Create<byte>(4, 5, 7);
        Assert.Contains(ProvenanceReasonCodes.ArtifactChanged,
            ProvenanceVerifier.VerifyCapture(manifest, bytes).Reasons);

        var dangling = manifest with { Builds = [manifest.Builds[0] with { ContextId = "missing" }] };
        Assert.Contains(ProvenanceReasonCodes.DanglingReference,
            ProvenanceVerifier.VerifyCapture(dangling, FixtureBytes()).Reasons);
    }

    [Theory]
    [InlineData("0.9", "sha256-canonical-v1", "manifest.schemaUnsupported")]
    [InlineData("1.0", "sha256-other", "manifest.identityAlgorithmUnsupported")]
    public void UnsupportedIdentitySemanticsFailClosed(string schema, string algorithm, string reason)
    {
        var manifest = Fixture() with { ManifestSchemaVersion = schema, IdentityAlgorithm = algorithm };
        Assert.Contains(reason, ProvenanceVerifier.VerifyCapture(manifest, FixtureBytes()).Reasons);
    }

    [Fact]
    public void RulesetsAreNotTreatedAsEquivalent()
    {
        var manifest = Fixture();
        manifest = manifest with
        {
            Producer = manifest.Producer with { ComplexityRuleset = ComplexityRules.OrdinaryMethodsV1 }
        };
        Assert.Contains(ProvenanceReasonCodes.RulesetMismatch,
            ProvenanceVerifier.VerifyCapture(manifest, FixtureBytes(), ComplexityRules.CallablesV1).Reasons);
    }

    [Fact]
    public void FreshBindingMayBeVerifiedWithoutBeingReusable()
    {
        var manifest = Fixture();
        manifest = manifest with
        {
            Contexts = [manifest.Contexts[0] with { ReuseRecipeComplete = false }]
        };
        var current = CurrentEvidence.FromManifest(manifest);
        var fresh = ProvenanceVerifier.VerifyCurrent(manifest, FixtureBytes(), current, false);
        Assert.Equal(ProvenanceStatus.Verified, fresh.Status);
        Assert.False(fresh.Reusable);
        Assert.Contains(ProvenanceReasonCodes.ReuseRecipeIncomplete, fresh.Reasons);

        var reuse = ProvenanceVerifier.VerifyCurrent(manifest, FixtureBytes(), current, true);
        Assert.Equal(ProvenanceStatus.Invalid, reuse.Status);
        Assert.Contains(ProvenanceReasonCodes.ContextNotRevalidated, reuse.Reasons);
    }

    [Fact]
    public void SameLineByteChangeInvalidatesCurrentWorkspaceEvidence()
    {
        var manifest = Fixture();
        var current = CurrentEvidence.FromManifest(manifest) with
        {
            Inputs = [new CurrentInputEvidence("source", "src/C.cs", 3,
                CanonicalIdentity.Sha256([1, 2, 4]))]
        };
        var result = ProvenanceVerifier.VerifyCurrent(manifest, FixtureBytes(), current, false);
        Assert.Equal(ProvenanceStatus.Invalid, result.Status);
        Assert.Contains(ProvenanceReasonCodes.SourceChanged, result.Reasons);
    }

    [Fact]
    public void IncompatibleReportCoordinateKindsNeverUnion()
    {
        var manifest = Fixture();
        manifest = manifest with
        {
            Artifacts = [manifest.Artifacts[0], manifest.Artifacts[1], manifest.Artifacts[1] with
            {
                Id = "coverage-2", Locator = "artifacts/coverage-2.xml", CoordinateKind = "line",
                Sha256 = CanonicalIdentity.Sha256([7, 8, 9])
            }]
        };
        var bytes = FixtureBytes();
        bytes["artifacts/coverage-2.xml"] = ImmutableArray.Create<byte>(7, 8, 9);
        Assert.Contains(ProvenanceReasonCodes.IncompatiblePointRepresentation,
            ProvenanceVerifier.VerifyCapture(manifest, bytes).Reasons);
    }

    private static Dictionary<string, ImmutableArray<byte>> FixtureBytes() => new(StringComparer.Ordinal)
    {
        ["artifacts/source.bin"] = ImmutableArray.Create<byte>(1, 2, 3),
        ["artifacts/coverage.xml"] = ImmutableArray.Create<byte>(4, 5, 6)
    };

    private static RunManifest Fixture()
    {
        var sourceHash = CanonicalIdentity.Sha256([1, 2, 3]);
        return new RunManifest("1.0", "sha256-canonical-v1",
            new ManifestProducer("crap4csharp", "test", ComplexityRules.CallablesV1,
                ProjectAnalysisContext.ProtocolVersion, "coverage-v1", "paths-v1"),
            new ManifestCapture("completed", true, true, []),
            new ManifestRevision("git", "repo", "worktree", "abc", null, null),
            [new ManifestRoot("workspace", "workspace", "sensitive")],
            [new ManifestContext("ctx", "App.csproj", "net10.0", "Debug", "AnyCPU",
                "sources", "context", "closure", true, true,
                [new ManifestInput("source", "src/C.cs", "artifacts/source.bin", 3, sourceHash, "utf-8", false)])],
            [new ManifestBuild("build", "ctx", "module", "dll", "mvid", "pdb", "portable-pdb")],
            [new ManifestExecution("test", "ctx", "build", true, 0, 1, 1, 0, 0)],
            [new ManifestArtifact("source", "source", "artifacts/source.bin", 3, sourceHash,
                    "ctx", null, null, null, null),
             new ManifestArtifact("coverage", "coverage", "artifacts/coverage.xml", 3,
                    CanonicalIdentity.Sha256([4, 5, 6]), "ctx", "build", "test", "opencover", "sequence-point")],
            new ManifestEvaluationInputs("scope", "policy", null, null), null);
    }
}
