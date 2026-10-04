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
        manifest = ManifestIdentity.Seal(manifest with
        {
            Contexts = [manifest.Contexts[0] with { ReuseRecipeComplete = false }],
            ManifestHash = null
        });
        var current = EvidenceFrom(manifest);
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
        var current = EvidenceFrom(manifest) with
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

    [Fact]
    public void CaptureRequiresAHashBoundSuccessfulBuildAndExecutionGraph()
    {
        var original = Fixture();
        var manifest = original with
        {
            Builds = [],
            Executions = [],
            Artifacts = [original.Artifacts[0], original.Artifacts[1] with
            {
                BuildId = null,
                ExecutionId = null
            }],
            ManifestHash = null
        };

        var reasons = ProvenanceVerifier.VerifyCapture(manifest, FixtureBytes()).Reasons;

        Assert.Contains("manifest.hashMissing", reasons);
        Assert.Contains("provenance.buildEvidenceMissing", reasons);
        Assert.Contains(ProvenanceReasonCodes.TestExecutionIncomplete, reasons);
        Assert.Contains(ProvenanceReasonCodes.DanglingReference, reasons);
    }

    [Theory]
    [InlineData("foo-v9", "1.0", "coverage-v1", "paths-v1")]
    [InlineData("callables-v1", "0.0", "coverage-v1", "paths-v1")]
    [InlineData("callables-v1", "1.0", "coverage-v9", "paths-v1")]
    [InlineData("callables-v1", "1.0", "coverage-v1", "paths-v9")]
    public void CaptureRejectsUnsupportedProducerAndProtocolIdentities(
        string ruleset, string contextProtocol, string coverageProtocol, string pathProtocol)
    {
        var manifest = Fixture();
        manifest = manifest with
        {
            Producer = manifest.Producer with
            {
                ComplexityRuleset = ruleset,
                ContextProtocol = contextProtocol,
                CoverageProtocol = coverageProtocol,
                PathProtocol = pathProtocol
            }
        };

        Assert.Contains("manifest.producerUnsupported",
            ProvenanceVerifier.VerifyCapture(manifest, FixtureBytes()).Reasons);
    }

    [Fact]
    public void CaptureRejectsAnUnsupportedProducerVersion()
    {
        var manifest = Fixture();
        manifest = manifest with { Producer = manifest.Producer with { ToolVersion = "99.0.0" } };

        Assert.Contains(ProvenanceReasonCodes.ProducerUnsupported,
            ProvenanceVerifier.VerifyCapture(manifest, FixtureBytes()).Reasons);
    }

    [Fact]
    public void CanonicalManifestIdentityMatchesPublishedGoldenVector()
    {
        var manifest = Fixture();

        Assert.Equal("352c257a192f9041fefd856032835e63731386e369e327be772c2580a375f928",
            manifest.Contexts[0].SourceSetHash);
        Assert.Equal("cde09b11c2e1f8023c5fac5f8468a48a42e2fae1fbcc253972ae49a24bb6f80b",
            manifest.Contexts[0].InputClosureHash);
        Assert.Equal("7ac8c7682d111361ff8bed3ed526c5b24c71b564f633d81e7ea603bcd7eee3ab",
            manifest.Contexts[0].ContextHash);
        Assert.Equal("37381bd2241d6593aac0040bf4d84cdd80a6bcde7689d90633b7f6c917cd43f0",
            manifest.ManifestHash);
    }

    [Fact]
    public void ParseSymbolsAndLogicalCasePolicyArePartOfContextIdentity()
    {
        var manifest = Fixture();
        var changedSymbols = ManifestIdentity.Seal(manifest with
        {
            Contexts = [manifest.Contexts[0] with
            {
                ParseOptions = manifest.Contexts[0].ParseOptions! with
                { PreprocessorSymbols = ["DEBUG", "FEATURE"] }
            }],
            ManifestHash = null
        });
        var changedCase = ManifestIdentity.Seal(manifest with
        {
            Contexts = [manifest.Contexts[0] with
            {
                PathPolicy = manifest.Contexts[0].PathPolicy! with { CasePolicy = "insensitive" }
            }],
            ManifestHash = null
        });

        Assert.NotEqual(manifest.Contexts[0].ContextHash, changedSymbols.Contexts[0].ContextHash);
        Assert.NotEqual(manifest.Contexts[0].ContextHash, changedCase.Contexts[0].ContextHash);
        Assert.NotEqual(manifest.ManifestHash, changedSymbols.ManifestHash);
        Assert.NotEqual(manifest.ManifestHash, changedCase.ManifestHash);
    }

    [Fact]
    public void WindowsCapturedPathResolvesLogicallyWithoutTheOriginalDrive()
    {
        var result = CapturedLogicalPathResolver.Resolve(@"C:\agent\repo\src\C.cs", ["src/C.cs"],
            new CapturedPathPolicy(false,
                [new ManifestReportRootMapping(@"C:\agent\repo", "")]));

        Assert.Equal("src/C.cs", result);
    }

    private static Dictionary<string, ImmutableArray<byte>> FixtureBytes() => new(StringComparer.Ordinal)
    {
        ["artifacts/source.bin"] = ImmutableArray.Create<byte>(1, 2, 3),
        ["artifacts/coverage.xml"] = ImmutableArray.Create<byte>(4, 5, 6),
        ["artifacts/app.dll"] = ImmutableArray.Create<byte>(7),
        ["artifacts/app.pdb"] = ImmutableArray.Create<byte>(8),
        ["artifacts/results.trx"] = ImmutableArray.Create<byte>(9),
        ["artifacts/scope.json"] = ImmutableArray.Create<byte>(10),
        ["artifacts/policy.json"] = ImmutableArray.Create<byte>(11)
    };

    private static RunManifest Fixture()
    {
        var sourceHash = CanonicalIdentity.Sha256([1, 2, 3]);
        var manifest = new RunManifest("1.0", "sha256-canonical-v1",
            new ManifestProducer("crap4csharp", "0.1.0", ComplexityRules.CallablesV1,
                ProjectAnalysisContext.ProtocolVersion, "coverage-v1", "paths-v1"),
            new ManifestCapture("completed", true, true, []),
            new ManifestRevision("git", "repo", "worktree", "abc", null, null),
            [new ManifestRoot("workspace", "workspace", "sensitive")],
            [new ManifestContext("ctx", "App.csproj", "net10.0", "Debug", "AnyCPU",
                "sources", "context", "closure", true, true,
                [new ManifestInput("source", "src/C.cs", "artifacts/source.bin", 3, sourceHash, "utf-8", false)])
            {
                ParseOptions = new ManifestParseOptions("preview", "Regular", ["DEBUG"],
                    new Dictionary<string, string>(StringComparer.Ordinal)),
                PathPolicy = new ManifestPathPolicy("sensitive", [])
            }],
            [new ManifestBuild("build", "ctx", "module", CanonicalIdentity.Sha256([7]), "mvid",
                CanonicalIdentity.Sha256([8]), "portable-pdb")],
            [new ManifestExecution("test", "ctx", "build", true, 0, 1, 1, 0, 0)],
            [new ManifestArtifact("source", "source", "artifacts/source.bin", 3, sourceHash,
                    "ctx", null, null, null, null),
             new ManifestArtifact("coverage", "coverage", "artifacts/coverage.xml", 3,
                    CanonicalIdentity.Sha256([4, 5, 6]), "ctx", "build", "test", "opencover", "sequence-point"),
             new ManifestArtifact("assembly", "assembly", "artifacts/app.dll", 1, CanonicalIdentity.Sha256([7]),
                    "ctx", "build", null, null, null),
             new ManifestArtifact("pdb", "pdb", "artifacts/app.pdb", 1, CanonicalIdentity.Sha256([8]),
                    "ctx", "build", null, null, null),
             new ManifestArtifact("test-result", "test-result", "artifacts/results.trx", 1, CanonicalIdentity.Sha256([9]),
                    "ctx", "build", "test", "trx", null),
             new ManifestArtifact("scope", "scope", "artifacts/scope.json", 1, CanonicalIdentity.Sha256([10]),
                    null, null, null, "json", null),
             new ManifestArtifact("policy", "policy", "artifacts/policy.json", 1, CanonicalIdentity.Sha256([11]),
                    null, null, null, "json", null)],
            new ManifestEvaluationInputs(CanonicalIdentity.Sha256([10]), CanonicalIdentity.Sha256([11]), null, null), null);
        return ManifestIdentity.Seal(manifest);
    }

    private static CurrentEvidence EvidenceFrom(RunManifest manifest) => new(
        manifest.Revision.RepositoryIdentity, manifest.Revision.WorkspaceIdentity, manifest.Revision.Head,
        manifest.Contexts.SelectMany(context => context.Inputs.Where(input => !input.Generated)
            .Select(input => new CurrentInputEvidence(input.Role, input.LogicalPath, input.Length, input.Sha256)))
            .OrderBy(input => input.LogicalPath, StringComparer.Ordinal).ToArray(),
        manifest.Contexts.ToDictionary(context => context.Id, context => context.ContextHash, StringComparer.Ordinal),
        true);
}
