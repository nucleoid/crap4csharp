using System.Collections.Immutable;
using System.Text;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ProvenanceTests
{
    private const string CompiledSource = "tests/Crap4CSharp.Tests/ProvenanceCompiledFixture.cs";
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
    public void CanonicalTuplesDistinguishNullEmptyAndFormerDelimiterCollisions()
    {
        Assert.NotEqual(CanonicalIdentity.Tuple("tuple", (string?)null),
            CanonicalIdentity.Tuple("tuple", ""));
        Assert.NotEqual(CanonicalIdentity.Tuple("tuple", "a\nb", "c"),
            CanonicalIdentity.Tuple("tuple", "a", "b\nc"));
        Assert.Throws<ArgumentException>(() => CanonicalIdentity.NormalizeLogicalPath("src/a\nb.cs"));
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
        var expected = manifest.Contexts[0].Inputs[0];
        var current = EvidenceFrom(manifest) with
        {
            Inputs = [new CurrentInputEvidence(expected.Role, expected.LogicalPath, expected.Length,
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
    public void CaptureDerivesBuildIdentityFromActualPeAndPortablePdb()
    {
        var manifest = Fixture();
        manifest = ManifestIdentity.Seal(manifest with
        {
            Builds = [manifest.Builds[0] with { Mvid = Guid.Empty.ToString("D") }],
            ManifestHash = null
        });

        Assert.Contains(ProvenanceReasonCodes.ActualBindingIncomplete,
            ProvenanceVerifier.VerifyCapture(manifest, FixtureBytes()).Reasons);
    }

    [Fact]
    public void CaptureRejectsSourceBytesNotNamedByThePortablePdb()
    {
        var manifest = Fixture();
        var bytes = FixtureBytes();
        var changed = ImmutableArray.Create(Encoding.UTF8.GetBytes("namespace Other; internal class Different { }"));
        bytes["artifacts/source.bin"] = changed;
        var context = manifest.Contexts[0];
        var input = context.Inputs[0] with
        { Length = changed.Length, Sha256 = CanonicalIdentity.Sha256(changed.AsSpan()) };
        var artifacts = manifest.Artifacts.Select(item => item.Kind == "source"
            ? item with { Length = changed.Length, Sha256 = input.Sha256 } : item).ToArray();
        manifest = ManifestIdentity.Seal(manifest with
        {
            Contexts = [context with { Inputs = [input] }],
            Artifacts = artifacts,
            ManifestHash = null
        });

        Assert.Contains(ProvenanceReasonCodes.ActualBindingIncomplete,
            ProvenanceVerifier.VerifyCapture(manifest, bytes).Reasons);
    }

    [Fact]
    public void CaptureDerivesExecutionCountsFromActualTrx()
    {
        var manifest = Fixture();
        var bytes = FixtureBytes();
        var malformed = ImmutableArray.Create(Encoding.UTF8.GetBytes("<TestRun><broken>"));
        bytes["artifacts/results.trx"] = malformed;
        var artifacts = manifest.Artifacts.Select(item => item.Kind == "test-result"
            ? item with { Length = malformed.Length, Sha256 = CanonicalIdentity.Sha256(malformed.AsSpan()) }
            : item).ToArray();
        manifest = ManifestIdentity.Seal(manifest with { Artifacts = artifacts, ManifestHash = null });

        Assert.Contains(ProvenanceReasonCodes.TestExecutionIncomplete,
            ProvenanceVerifier.VerifyCapture(manifest, bytes).Reasons);
    }

    [Fact]
    public void CaptureRejectsAFailedTrxEvenWhenItsCountersLookSuccessful()
    {
        var manifest = Fixture();
        var bytes = FixtureBytes();
        var failed = ImmutableArray.Create(Encoding.UTF8.GetBytes("""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Failed"><Counters total="1" executed="1" passed="1" failed="0"
                error="0" timeout="0" aborted="0" disconnected="0" passedButRunAborted="0"
                notRunnable="0" inconclusive="0" notExecuted="0" /></ResultSummary>
            </TestRun>
            """));
        bytes["artifacts/results.trx"] = failed;
        var artifacts = manifest.Artifacts.Select(item => item.Kind == "test-result"
            ? item with { Length = failed.Length, Sha256 = CanonicalIdentity.Sha256(failed.AsSpan()) }
            : item).ToArray();
        manifest = ManifestIdentity.Seal(manifest with { Artifacts = artifacts, ManifestHash = null });

        Assert.Contains(ProvenanceReasonCodes.TestExecutionIncomplete,
            ProvenanceVerifier.VerifyCapture(manifest, bytes).Reasons);
    }

    [Fact]
    public void CaptureDerivesCoverageFormatAndCoordinatesFromTheXml()
    {
        var manifest = Fixture();
        manifest = ManifestIdentity.Seal(manifest with
        {
            Artifacts = manifest.Artifacts.Select(item => item.Kind == "coverage"
                ? item with { Format = "opencover", CoordinateKind = "sequence-point" } : item).ToArray(),
            ManifestHash = null
        });

        Assert.Contains(ProvenanceReasonCodes.IncompatiblePointRepresentation,
            ProvenanceVerifier.VerifyCapture(manifest, FixtureBytes()).Reasons);
    }

    [Fact]
    public void CanonicalManifestIdentityMatchesPublishedGoldenVector()
    {
        var manifest = Fixture();
        var golden = ManifestIdentity.Seal(manifest with
        {
            Builds = [manifest.Builds[0] with
            {
                ModuleIdentity = "module", AssemblySha256 = CanonicalIdentity.Sha256([7]), Mvid = "mvid",
                PdbSha256 = CanonicalIdentity.Sha256([8]), DebugIdentity = "portable-pdb"
            }],
            Artifacts = manifest.Artifacts.Select(item => item.Kind switch
            {
                "assembly" => item with { Length = 1, Sha256 = CanonicalIdentity.Sha256([7]) },
                "pdb" => item with { Length = 1, Sha256 = CanonicalIdentity.Sha256([8]) },
                _ => item
            }).ToArray(),
            ManifestHash = null
        });

        Assert.Equal("2fcf2db1d70b98d6d21703f0e4f2dc1653a69c815127ea1c4ffc08faac6b57ea",
            manifest.Contexts[0].SourceSetHash);
        Assert.Equal("7d898082e4d73b0a846d0e36bb0c0e9f253383b6da17cb8927b0d08445dfd254",
            manifest.Contexts[0].InputClosureHash);
        Assert.Equal("bdf3277f889c3bd5c1d9b949d281e9a8ad551b9052f9fa2a67499747a681cc66",
            manifest.Contexts[0].ContextHash);
        Assert.Equal("ccf42f44a8c89cefcb302316363bd2ca5d303e13eeadea6b48ce97f6bc0f9195",
            golden.ManifestHash);
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

    private static Dictionary<string, ImmutableArray<byte>> FixtureBytes() => FixtureParts().Bytes;

    private static RunManifest Fixture()
    {
        var parts = FixtureParts();
        var source = parts.Bytes["artifacts/source.bin"];
        var sourceHash = CanonicalIdentity.Sha256(source.AsSpan());
        var manifest = new RunManifest("1.0", "sha256-canonical-v1",
            new ManifestProducer("crap4csharp", "0.1.0", ComplexityRules.CallablesV1,
                ProjectAnalysisContext.ProtocolVersion, "coverage-v1", "paths-v1"),
            new ManifestCapture("completed", true, true, []),
            new ManifestRevision("git", "repo", "worktree", "abc", null, null),
            [new ManifestRoot("workspace", "workspace", "sensitive")],
            [new ManifestContext("ctx", "App.csproj", "net10.0", "Debug", "AnyCPU",
                "sources", "context", "closure", true, true,
                [new ManifestInput("source", CompiledSource, "artifacts/source.bin", source.Length,
                    sourceHash, "utf-8", false)])
            {
                ParseOptions = new ManifestParseOptions("preview", "Regular", ["DEBUG"],
                    new Dictionary<string, string>(StringComparer.Ordinal)),
                PathPolicy = new ManifestPathPolicy("sensitive", [])
            }],
            [new ManifestBuild("build", "ctx", parts.Build.ModuleIdentity,
                CanonicalIdentity.Sha256(parts.Bytes["artifacts/app.dll"].AsSpan()), parts.Build.Mvid,
                CanonicalIdentity.Sha256(parts.Bytes["artifacts/app.pdb"].AsSpan()), parts.Build.DebugIdentity)],
            [new ManifestExecution("test", "ctx", "build", true, 0, 1, 1, 0, 0)],
            [new ManifestArtifact("source", "source", "artifacts/source.bin", source.Length, sourceHash,
                    "ctx", null, null, null, null),
             new ManifestArtifact("coverage", "coverage", "artifacts/coverage.xml",
                    parts.Bytes["artifacts/coverage.xml"].Length,
                    CanonicalIdentity.Sha256(parts.Bytes["artifacts/coverage.xml"].AsSpan()),
                    "ctx", "build", "test", "cobertura", "line"),
             new ManifestArtifact("assembly", "assembly", "artifacts/app.dll", parts.Bytes["artifacts/app.dll"].Length,
                    CanonicalIdentity.Sha256(parts.Bytes["artifacts/app.dll"].AsSpan()),
                    "ctx", "build", null, null, null),
             new ManifestArtifact("pdb", "pdb", "artifacts/app.pdb", parts.Bytes["artifacts/app.pdb"].Length,
                    CanonicalIdentity.Sha256(parts.Bytes["artifacts/app.pdb"].AsSpan()),
                    "ctx", "build", null, null, null),
             new ManifestArtifact("test-result", "test-result", "artifacts/results.trx",
                    parts.Bytes["artifacts/results.trx"].Length,
                    CanonicalIdentity.Sha256(parts.Bytes["artifacts/results.trx"].AsSpan()),
                    "ctx", "build", "test", "trx", null),
             new ManifestArtifact("scope", "scope", "artifacts/scope.json",
                    parts.Bytes["artifacts/scope.json"].Length,
                    CanonicalIdentity.Sha256(parts.Bytes["artifacts/scope.json"].AsSpan()),
                    null, null, null, "json", null),
             new ManifestArtifact("policy", "policy", "artifacts/policy.json",
                    parts.Bytes["artifacts/policy.json"].Length,
                    CanonicalIdentity.Sha256(parts.Bytes["artifacts/policy.json"].AsSpan()),
                    null, null, null, "json", null)],
            new ManifestEvaluationInputs(
                CanonicalIdentity.Sha256(parts.Bytes["artifacts/scope.json"].AsSpan()),
                CanonicalIdentity.Sha256(parts.Bytes["artifacts/policy.json"].AsSpan()), null, null), null);
        return ManifestIdentity.Seal(manifest);
    }

    private static (Dictionary<string, ImmutableArray<byte>> Bytes, InspectedBuildEvidence Build) FixtureParts()
    {
        var assemblyPath = typeof(ProvenanceCompiledFixture).Assembly.Location;
        var assembly = ImmutableArray.Create(File.ReadAllBytes(assemblyPath));
        var pdb = ImmutableArray.Create(File.ReadAllBytes(Path.ChangeExtension(assemblyPath, ".pdb")));
        var trx = ImmutableArray.Create(Encoding.UTF8.GetBytes("""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0"
                error="0" timeout="0" aborted="0" inconclusive="0" notExecuted="0" /></ResultSummary>
            </TestRun>
            """));
        var bytes = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal)
        {
            ["artifacts/source.bin"] = ImmutableArray.Create(CompiledSourceBytes()),
            ["artifacts/coverage.xml"] = ImmutableArray.Create(Encoding.UTF8.GetBytes(
                "<coverage><packages><package name=\"Crap4CSharp.Tests\"><classes><class name=\"Crap4CSharp.Tests.ProvenanceCompiledFixture\" filename=\"tests/Crap4CSharp.Tests/ProvenanceCompiledFixture.cs\"><methods><method name=\"M\" signature=\"()\"><lines><line number=\"7\" hits=\"1\" /></lines></method></methods></class></classes></package></packages></coverage>")),
            ["artifacts/app.dll"] = assembly,
            ["artifacts/app.pdb"] = pdb,
            ["artifacts/results.trx"] = trx,
            ["artifacts/scope.json"] = ImmutableArray.Create(
                Encoding.UTF8.GetBytes($$"""{"version":1,"sources":["{{CompiledSource}}"]}""")),
            ["artifacts/policy.json"] = ImmutableArray.Create(
                Encoding.UTF8.GetBytes("""{"version":1,"threshold":8,"allowMissingCoverage":false}"""))
        };
        return (bytes, ArtifactEvidenceInspector.InspectBuild(assembly, pdb));
    }

    private static byte[] CompiledSourceBytes()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            var candidate = Path.Combine(root.FullName, CompiledSource.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
            root = root.Parent;
        }
        throw new FileNotFoundException($"Could not locate {CompiledSource}.");
    }

    private static CurrentEvidence EvidenceFrom(RunManifest manifest) => new(
        manifest.Revision.RepositoryIdentity, manifest.Revision.WorkspaceIdentity, manifest.Revision.Head,
        manifest.Contexts.SelectMany(context => context.Inputs.Where(input => !input.Generated)
            .Select(input => new CurrentInputEvidence(input.Role, input.LogicalPath, input.Length, input.Sha256)))
            .OrderBy(input => input.LogicalPath, StringComparer.Ordinal).ToArray(),
        manifest.Contexts.ToDictionary(context => context.Id, context => context.ContextHash, StringComparer.Ordinal),
        true);
}
