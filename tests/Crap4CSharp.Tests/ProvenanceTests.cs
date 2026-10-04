using System.Collections.Immutable;
using System.Text;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ProvenanceTests
{
    private const string CompiledSource = "tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs";
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
    public void CaptureRejectsCoverageLinesThatDoNotMatchThePortablePdb()
    {
        var manifest = Fixture();
        var bytes = FixtureBytes();
        var stale = ImmutableArray.Create(Encoding.UTF8.GetBytes(
            "<coverage><packages><package name=\"Crap4CSharp.ProvenanceFixture\"><classes><class name=\"Crap4CSharp.ProvenanceFixture.CompiledEvidence\" filename=\"tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs\"><methods><method name=\"M\" signature=\"()\"><lines><line number=\"6\" hits=\"1\" /></lines></method></methods></class></classes></package></packages></coverage>"));
        bytes["artifacts/coverage.xml"] = stale;
        var artifacts = manifest.Artifacts.Select(item => item.Kind == "coverage"
            ? item with { Length = stale.Length, Sha256 = CanonicalIdentity.Sha256(stale.AsSpan()) } : item).ToArray();
        manifest = ManifestIdentity.Seal(manifest with { Artifacts = artifacts, ManifestHash = null });

        Assert.Contains(ProvenanceReasonCodes.IncompatiblePointRepresentation,
            ProvenanceVerifier.VerifyCapture(manifest, bytes).Reasons);
    }

    [Fact]
    public void CaptureRejectsLineCoverageThatOmitsAStatementLine()
    {
        var manifest = Fixture();
        var bytes = FixtureBytes();
        var incomplete = "<coverage><packages><package name=\"Crap4CSharp.ProvenanceFixture\"><classes>" +
            "<class name=\"Crap4CSharp.ProvenanceFixture.CompiledEvidence\" " +
            "filename=\"tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs\"><methods>" +
            "<method name=\"M\" signature=\"()\"><lines><line number=\"9\" hits=\"1\" /></lines></method>" +
            "</methods></class></classes></package></packages></coverage>";
        ReplaceCoverage(ref manifest, bytes, incomplete, "cobertura", "line");

        Assert.Contains(ProvenanceReasonCodes.IncompatiblePointRepresentation,
            ProvenanceVerifier.VerifyCapture(manifest, bytes).Reasons);
    }

    [Fact]
    public void CapturedPdbPathMapIsAppliedWithoutHostSuffixGuessing()
    {
        var parts = FixtureParts();
        Assert.All(parts.Build.Documents.Keys, path => Assert.StartsWith("/_/", path));
        Assert.Equal(ProvenanceStatus.Captured,
            ProvenanceVerifier.VerifyCapture(Fixture(), parts.Bytes).Status);
    }

    [Fact]
    public void CapturedPdbPathWithoutAnExplicitMappingIsRejected()
    {
        var manifest = Fixture();
        manifest = ManifestIdentity.Seal(manifest with
        {
            Contexts = [manifest.Contexts[0] with
            {
                PathPolicy = new ManifestPathPolicy("sensitive", [])
            }],
            ManifestHash = null
        });

        Assert.Contains(ProvenanceReasonCodes.ActualBindingIncomplete,
            ProvenanceVerifier.VerifyCapture(manifest, FixtureBytes()).Reasons);
    }

    [Fact]
    public void LogicalMappingsDistinguishRootAndNestedSameNamedFiles()
    {
        string[] sources = ["Helpers.cs", "Sub/Helpers.cs"];
        var policy = new CapturedPathPolicy(true, [new ManifestReportRootMapping("/repo", "")]);

        Assert.Equal("Helpers.cs", CapturedLogicalPathResolver.Resolve("/repo/Helpers.cs", sources, policy));
        Assert.Equal("Sub/Helpers.cs", CapturedLogicalPathResolver.Resolve("/repo/Sub/Helpers.cs", sources, policy));
    }

    [Fact]
    public void CoverletOpenCoverLineProjectionMatchesPortablePdb()
    {
        var manifest = Fixture();
        var bytes = FixtureBytes();
        var build = FixtureParts().Build;
        ReplaceCoverage(ref manifest, bytes, CoverletOpenCover(build), "opencover", "sequence-point");

        Assert.Equal(ProvenanceStatus.Captured, ProvenanceVerifier.VerifyCapture(manifest, bytes).Status);
    }

    [Fact]
    public void RealCoverletOpenCoverReportMatchesItsPortablePdb()
    {
        var assemblyPath = typeof(Fixture.Scorer).Assembly.Location;
        var assembly = ImmutableArray.Create(File.ReadAllBytes(assemblyPath));
        var pdb = ImmutableArray.Create(File.ReadAllBytes(Path.ChangeExtension(assemblyPath, ".pdb")));
        var reportPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Provenance",
            "coverlet-fixture.opencover.xml");
        var report = ImmutableArray.Create(File.ReadAllBytes(reportPath));

        var build = ArtifactEvidenceInspector.InspectBuild(assembly, pdb);
        string[] sources = ["samples/Fixture/Fixture/CallableSamples.cs", "samples/Fixture/Fixture/Scorer.cs"];
        var scorerDocument = build.Documents.Keys.Single(path => path.Replace('\\', '/').EndsWith(sources[1],
            StringComparison.Ordinal));
        var pdbRoot = scorerDocument.Replace('\\', '/')[..^sources[1].Length].TrimEnd('/');
        var policy = new CapturedPathPolicy(true, [new ManifestReportRootMapping(pdbRoot, "")]);

        Assert.True(ArtifactEvidenceInspector.CoverageMatchesBuild(report, reportPath, build, sources, policy));
    }

    [Fact]
    public void ForeignModulesInCoverletReportDoNotInvalidateTheBoundBuild()
    {
        var manifest = Fixture();
        var bytes = FixtureBytes();
        var build = FixtureParts().Build;
        ReplaceCoverage(ref manifest, bytes, CoverletOpenCover(build, includeForeignModule: true),
            "opencover", "sequence-point");

        Assert.Equal(ProvenanceStatus.Captured, ProvenanceVerifier.VerifyCapture(manifest, bytes).Status);
    }

    [Fact]
    public void ConstructorCoverageMayExcludeOnlyLinesAssignedToAnotherMethod()
    {
        var build = new InspectedBuildEvidence("App", "mvid", "debug",
            new Dictionary<string, string> { ["src/C.cs"] = "hash" }, "preview", [],
            [new InspectedMethodEvidence(1, "App.C", ".ctor",
                [new("src/C.cs", 0, 10, 5, 10, 20), new("src/C.cs", 1, 11, 5, 11, 20),
                 new("src/C.cs", 2, 20, 5, 20, 20)]),
             new InspectedMethodEvidence(2, "App.C", "get_Value", [new("src/C.cs", 0, 20, 5, 20, 20)])]);
        var complete = "<CoverageSession><Modules><Module><ModuleName>App</ModuleName>" +
            "<Files><File uid=\"1\" fullPath=\"src/C.cs\" /></Files><Classes><Class><FullName>App.C</FullName><Methods>" +
            "<Method><Name>System.Void App.C::.ctor()</Name><FileRef uid=\"1\" /><SequencePoints>" +
            "<SequencePoint vc=\"1\" sl=\"10\" sc=\"1\" el=\"10\" ec=\"2\" fileid=\"1\" />" +
            "<SequencePoint vc=\"1\" sl=\"11\" sc=\"1\" el=\"11\" ec=\"2\" fileid=\"1\" />" +
            "</SequencePoints></Method><Method><Name>System.Int32 App.C::get_Value()</Name><FileRef uid=\"1\" />" +
            "<SequencePoints><SequencePoint vc=\"1\" sl=\"20\" sc=\"1\" el=\"20\" ec=\"2\" fileid=\"1\" />" +
            "</SequencePoints></Method></Methods></Class></Classes></Module></Modules></CoverageSession>";
        var policy = new CapturedPathPolicy(true, []);
        var completeBytes = ImmutableArray.Create(Encoding.UTF8.GetBytes(complete));
        var incompleteBytes = ImmutableArray.Create(Encoding.UTF8.GetBytes(complete.Replace(
            "<SequencePoint vc=\"1\" sl=\"11\" sc=\"1\" el=\"11\" ec=\"2\" fileid=\"1\" />", "")));

        Assert.True(ArtifactEvidenceInspector.CoverageMatchesBuild(completeBytes, "coverage.xml", build,
            ["src/C.cs"], policy));
        Assert.False(ArtifactEvidenceInspector.CoverageMatchesBuild(incompleteBytes, "coverage.xml", build,
            ["src/C.cs"], policy));
    }

    [Fact]
    public void CoberturaSourceRootsAreAlternativesForOneObservation()
    {
        var manifest = Fixture();
        var bytes = FixtureBytes();
        var build = FixtureParts().Build;
        var document = build.Documents.Keys.Single(path => path.Replace('\\', '/').EndsWith(CompiledSource,
            StringComparison.Ordinal));
        var root = document.Replace('\\', '/')[..^CompiledSource.Length].TrimEnd('/');
        var xml = $$"""
            <coverage><sources><source>/definitely/wrong</source><source>{{root}}</source></sources><packages>
              <package name="{{build.ModuleIdentity}}"><classes><class name="Crap4CSharp.ProvenanceFixture.CompiledEvidence" filename="{{CompiledSource}}"><methods>
                <method name="M" signature="()"><lines><line number="9" hits="1" /><line number="10" hits="1" /></lines></method>
              </methods></class></classes></package>
            </packages></coverage>
            """;
        ReplaceCoverage(ref manifest, bytes, xml, "cobertura", "line");

        Assert.Equal(ProvenanceStatus.Captured, ProvenanceVerifier.VerifyCapture(manifest, bytes).Status);
    }

    [Fact]
    public void CaptureRejectsParseOptionsThatDifferFromTheCompilerRecord()
    {
        var manifest = Fixture();
        manifest = ManifestIdentity.Seal(manifest with
        {
            Contexts = [manifest.Contexts[0] with
            {
                ParseOptions = manifest.Contexts[0].ParseOptions! with { PreprocessorSymbols = [] }
            }],
            ManifestHash = null
        });

        Assert.Contains(ProvenanceReasonCodes.ActualBindingIncomplete,
            ProvenanceVerifier.VerifyCapture(manifest, FixtureBytes()).Reasons);
    }

    [Fact]
    public void CaptureRejectsACompletedRunWithNoPassedTests()
    {
        var manifest = Fixture();
        var bytes = FixtureBytes();
        var skipped = ImmutableArray.Create(Encoding.UTF8.GetBytes("""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <TestDefinitions><UnitTest storage="Crap4CSharp.ProvenanceFixture.dll" /></TestDefinitions>
              <ResultSummary outcome="Completed"><Counters total="1" executed="0" passed="0" failed="0"
                error="0" timeout="0" aborted="0" disconnected="0" passedButRunAborted="0"
                notRunnable="0" inconclusive="0" notExecuted="1" /></ResultSummary>
            </TestRun>
            """));
        bytes["artifacts/results.trx"] = skipped;
        var artifacts = manifest.Artifacts.Select(item => item.Kind == "test-result"
            ? item with { Length = skipped.Length, Sha256 = CanonicalIdentity.Sha256(skipped.AsSpan()) } : item).ToArray();
        manifest = ManifestIdentity.Seal(manifest with
        {
            Executions = [manifest.Executions[0] with { PassedTests = 0, SkippedTests = 1 }],
            Artifacts = artifacts,
            ManifestHash = null
        });

        Assert.Contains(ProvenanceReasonCodes.TestExecutionIncomplete,
            ProvenanceVerifier.VerifyCapture(manifest, bytes).Reasons);
    }

    [Fact]
    public void CanonicalManifestIdentityMatchesPublishedGoldenVector()
    {
        var sourceHash = CanonicalIdentity.Sha256([1, 2, 3]);
        var context = new ManifestContext("ctx", "App.csproj", "net10.0", "Debug", "AnyCPU",
            "", "", "", true, true,
            [new ManifestInput("source", "src/C.cs", "artifacts/source.bin", 3, sourceHash, "utf-8", false)])
        {
            ParseOptions = new ManifestParseOptions("preview", "Regular", ["DEBUG"],
                new Dictionary<string, string>(StringComparer.Ordinal)),
            PathPolicy = new ManifestPathPolicy("sensitive", [])
        };
        var golden = ManifestIdentity.Seal(new RunManifest("1.0", CanonicalIdentity.Algorithm,
            new ManifestProducer("crap4csharp", "0.1.0", ComplexityRules.CallablesV1,
                ProjectAnalysisContext.ProtocolVersion, "coverage-v1", "paths-v1"),
            new ManifestCapture("completed", true, true, []),
            new ManifestRevision("git", "repo", "worktree", "abc", null, null),
            [new ManifestRoot("workspace", "workspace", "sensitive")], [context],
            [new ManifestBuild("build", "ctx", "module", CanonicalIdentity.Sha256([7]), "mvid",
                CanonicalIdentity.Sha256([8]), "portable-pdb")],
            [new ManifestExecution("test", "ctx", "build", true, 0, 1, 1, 0, 0)
            {
                TestModuleIdentity = "module", TestAssemblySha256 = CanonicalIdentity.Sha256([7]),
                TestMvid = "mvid", TestPdbSha256 = CanonicalIdentity.Sha256([8]),
                TestDebugIdentity = "portable-pdb"
            }],
            [new ManifestArtifact("source", "source", "artifacts/source.bin", 3, sourceHash,
                    "ctx", null, null, null, null),
             new ManifestArtifact("coverage", "coverage", "artifacts/coverage.xml", 3,
                    CanonicalIdentity.Sha256([4, 5, 6]), "ctx", "build", "test", "opencover", "sequence-point"),
             new ManifestArtifact("assembly", "assembly", "artifacts/app.dll", 1, CanonicalIdentity.Sha256([7]),
                    "ctx", "build", null, null, null),
             new ManifestArtifact("pdb", "pdb", "artifacts/app.pdb", 1, CanonicalIdentity.Sha256([8]),
                    "ctx", "build", null, null, null),
             new ManifestArtifact("test-result", "test-result", "artifacts/results.trx", 1,
                    CanonicalIdentity.Sha256([9]), "ctx", "build", "test", "trx", null),
             new ManifestArtifact("test-assembly", "test-assembly", "artifacts/app.dll", 1,
                    CanonicalIdentity.Sha256([7]), "ctx", "build", "test", null, null),
             new ManifestArtifact("test-pdb", "test-pdb", "artifacts/app.pdb", 1,
                    CanonicalIdentity.Sha256([8]), "ctx", "build", "test", null, null),
             new ManifestArtifact("scope", "scope", "artifacts/scope.json", 1, CanonicalIdentity.Sha256([10]),
                    null, null, null, "json", null),
             new ManifestArtifact("policy", "policy", "artifacts/policy.json", 1, CanonicalIdentity.Sha256([11]),
                    null, null, null, "json", null)],
            new ManifestEvaluationInputs(CanonicalIdentity.Sha256([10]), CanonicalIdentity.Sha256([11]), null, null),
            null));

        Assert.Equal("864f42d22774118aa30c2be59188cf321335366c5322a78565ba3e39b7d71a70",
            golden.Contexts[0].SourceSetHash);
        Assert.Equal("e281b69e9bae9ddabafdfad7e60ded5039e610cbf82aa373bf6c4c64bc730e21",
            golden.Contexts[0].InputClosureHash);
        Assert.Equal("27e8ee19bc0a8943eb757235bf53b9c1aee69b42eb5b5310c3cef54c946e20b9",
            golden.Contexts[0].ContextHash);
        Assert.Equal("149010ba02d88ba95e606ec5375e6fd55d02f660eb1cd77b4429a144ad6ce730",
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
                ParseOptions = new ManifestParseOptions(parts.Build.LanguageVersion, "Regular",
                    parts.Build.PreprocessorSymbols,
                    new Dictionary<string, string>(StringComparer.Ordinal)),
                PathPolicy = new ManifestPathPolicy("sensitive", [new ManifestReportRootMapping("/_/", "")])
            }],
            [new ManifestBuild("build", "ctx", parts.Build.ModuleIdentity,
                CanonicalIdentity.Sha256(parts.Bytes["artifacts/app.dll"].AsSpan()), parts.Build.Mvid,
                CanonicalIdentity.Sha256(parts.Bytes["artifacts/app.pdb"].AsSpan()), parts.Build.DebugIdentity)],
            [new ManifestExecution("test", "ctx", "build", true, 0, 1, 1, 0, 0)
            {
                TestModuleIdentity = parts.Build.ModuleIdentity,
                TestAssemblySha256 = CanonicalIdentity.Sha256(parts.Bytes["artifacts/app.dll"].AsSpan()),
                TestMvid = parts.Build.Mvid,
                TestPdbSha256 = CanonicalIdentity.Sha256(parts.Bytes["artifacts/app.pdb"].AsSpan()),
                TestDebugIdentity = parts.Build.DebugIdentity
            }],
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
             new ManifestArtifact("test-assembly", "test-assembly", "artifacts/app.dll",
                    parts.Bytes["artifacts/app.dll"].Length,
                    CanonicalIdentity.Sha256(parts.Bytes["artifacts/app.dll"].AsSpan()),
                    "ctx", "build", "test", null, null),
             new ManifestArtifact("test-pdb", "test-pdb", "artifacts/app.pdb",
                    parts.Bytes["artifacts/app.pdb"].Length,
                    CanonicalIdentity.Sha256(parts.Bytes["artifacts/app.pdb"].AsSpan()),
                    "ctx", "build", "test", null, null),
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
        var assemblyPath = typeof(Crap4CSharp.ProvenanceFixture.CompiledEvidence).Assembly.Location;
        var assembly = ImmutableArray.Create(File.ReadAllBytes(assemblyPath));
        var pdb = ImmutableArray.Create(File.ReadAllBytes(Path.ChangeExtension(assemblyPath, ".pdb")));
        var trx = ImmutableArray.Create(Encoding.UTF8.GetBytes("""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <TestDefinitions><UnitTest storage="Crap4CSharp.ProvenanceFixture.dll" /></TestDefinitions>
              <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0"
                error="0" timeout="0" aborted="0" inconclusive="0" notExecuted="0" /></ResultSummary>
            </TestRun>
            """));
        var bytes = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal)
        {
            ["artifacts/source.bin"] = ImmutableArray.Create(CompiledSourceBytes()),
            ["artifacts/coverage.xml"] = ImmutableArray.Create(Encoding.UTF8.GetBytes(
                "<coverage><packages><package name=\"Crap4CSharp.ProvenanceFixture\"><classes><class name=\"Crap4CSharp.ProvenanceFixture.CompiledEvidence\" filename=\"tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs\"><methods><method name=\"M\" signature=\"()\"><lines><line number=\"9\" hits=\"1\" /><line number=\"10\" hits=\"1\" /></lines></method></methods></class></classes></package></packages></coverage>")),
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

    private static void ReplaceCoverage(ref RunManifest manifest,
        Dictionary<string, ImmutableArray<byte>> bytes, string xml, string format, string coordinateKind)
    {
        var coverage = ImmutableArray.Create(Encoding.UTF8.GetBytes(xml));
        bytes["artifacts/coverage.xml"] = coverage;
        manifest = ManifestIdentity.Seal(manifest with
        {
            Artifacts = manifest.Artifacts.Select(item => item.Kind == "coverage"
                ? item with
                {
                    Length = coverage.Length, Sha256 = CanonicalIdentity.Sha256(coverage.AsSpan()),
                    Format = format, CoordinateKind = coordinateKind
                }
                : item).ToArray(),
            ManifestHash = null
        });
    }

    private static string CoverletOpenCover(InspectedBuildEvidence build, bool includeForeignModule = false)
    {
        var method = build.Methods.Single(item => item.TypeName == "Crap4CSharp.ProvenanceFixture.CompiledEvidence" &&
            item.MethodName == "M");
        var points = method.SequencePoints.Where(point => point.StartLine != point.EndLine ||
            point.EndColumn != point.StartColumn + 1).ToArray();
        var document = points.Select(point => point.Document).Distinct(StringComparer.Ordinal).Single();
        var projectedPoints = string.Concat(points.SelectMany(point =>
            Enumerable.Range(point.StartLine, point.EndLine - point.StartLine + 1)).Distinct().Order()
            .Select(line => $"<SequencePoint vc=\"1\" sl=\"{line}\" sc=\"1\" el=\"{line}\" ec=\"2\" fileid=\"1\" />"));
        var foreign = includeForeignModule
            ? """
              <Module><ModuleName>Foreign.Project</ModuleName><Files><File uid="2" fullPath="foreign.cs" /></Files>
                <Classes><Class><FullName>Foreign.Type</FullName><Methods><Method><Name>System.Void Foreign.Type::M()</Name>
                  <FileRef uid="2" /><SequencePoints><SequencePoint vc="1" sl="1" sc="1" el="1" ec="2" fileid="2" /></SequencePoints>
                </Method></Methods></Class></Classes></Module>
              """
            : string.Empty;
        return $$"""
            <CoverageSession><Modules>
              <Module><ModuleName>{{build.ModuleIdentity}}</ModuleName><Files><File uid="1" fullPath="{{document}}" /></Files>
                <Classes><Class><FullName>{{method.TypeName}}</FullName><Methods><Method><Name>System.Int32 {{method.TypeName}}::M()</Name>
                  <FileRef uid="1" /><SequencePoints>{{projectedPoints}}</SequencePoints>
                </Method></Methods></Class></Classes></Module>
              {{foreign}}
            </Modules></CoverageSession>
            """;
    }

    private static CurrentEvidence EvidenceFrom(RunManifest manifest) => new(
        manifest.Revision.RepositoryIdentity, manifest.Revision.WorkspaceIdentity, manifest.Revision.Head,
        manifest.Contexts.SelectMany(context => context.Inputs.Where(input => !input.Generated)
            .Select(input => new CurrentInputEvidence(input.Role, input.LogicalPath, input.Length, input.Sha256)))
            .OrderBy(input => input.LogicalPath, StringComparer.Ordinal).ToArray(),
        manifest.Contexts.ToDictionary(context => context.Id, context => context.ContextHash, StringComparer.Ordinal),
        true);
}
