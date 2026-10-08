using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class FreshCheckExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ColdPublicFreshCheckRestoresBuildsAndTestsOnceAndRetainsBoundArtifacts(bool failing)
        => RunCase(failing, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ColdExecutionCoordinatorPreservesActualOnceBuildsAndRawFailedTests(bool failing)
        => RunCase(failing, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NoChangedIncrementalScopeStillExecutesRequiredTestsAndReportsCrapNAOnlyAfterSuccess(bool failing)
        => RunCase(failing, false, scopedEmpty: true);

    [Fact]
    public Task SemanticEmptyProductionStillRunsRequiredTestsAndReportsExplicitNA()
        => RunCase(false, false, true);

    [Fact]
    public Task NonGitAllScopeFreshCheckRunsRequiredContextsWithLocalUnreviewedTrustAndNoGitRevision()
        => RunCase(false, false, policyScope: "all", local: true);

    [Theory]
    [InlineData("artifacts")]
    [InlineData("intermediate")]
    public Task LocalColdFreshCheckAcceptsEvaluatedCustomOutputWithoutRebaseliningAuthoredInputs(string outputLayout)
        => RunCase(false, false, policyScope: "all", local: true, outputLayout: outputLayout);

    [Theory]
    [InlineData("source")]
    [InlineData("additional")]
    public Task LocalCustomOutputStillRejectsTestSideAuthoredInputDriftWithPreservedTimestamp(string inputRole)
        => RunCase(false, false, policyScope: "all", local: true, outputLayout: "intermediate", outputMutation: inputRole);

    [Fact]
    public Task FailedTestsRetainUsableBoundPartialMetricsWithoutCompletedOrReusableSuccess()
        => RunCase(true, false, policyScope: "all", local: true, partial: true);

    [Fact]
    public Task FailedTestsWithPreservedTimestampSourceDriftCannotExposeStalePartialMetrics()
        => RunCase(true, false, policyScope: "all", local: true, partialDrift: true);

    [Fact]
    public Task FailedTestsCannotOverwritePriorResultNewlySelectedAsConsumerInputDuringTest()
        => RunCase(false, false, policyScope: "all", local: true, staleRawOutput: true);

    [Fact]
    public Task FailedTestsRetainActualOutcomesWhenTestReplacesResultWithConsumerBytes()
        => RunCase(false, false, policyScope: "all", local: true, staleRawOutput: true, replaceRawOutput: true);

    [Fact]
    public Task ValidatedFreshBuildFailureReplacesPriorPassButNeverNewlySelectedConsumerInput()
        => RunCase(false, false, policyScope: "all", local: true, staleOutput: true);

    [Fact]
    public Task ExplicitPriorBuildRevalidatesCapturedGraphWithoutRecompilingAndRunsFreshTestsOnce()
        => RunCase(false, false, policyScope: "all", local: true, priorBuild: true);

    [Fact]
    public Task ExplicitGeneratedInclusionRetainsGeneratedClassificationAndScoresItsCallable()
        => RunCase(false, false, false, true);

    [Theory]
    [InlineData("all", "method", true)]
    [InlineData("all", "file", true)]
    [InlineData("base", "file", true)]
    [InlineData("all", "method", false)]
    [InlineData("base", "method", false)]
    [InlineData("base", "file", false)]
    public Task GeneratedEligibilityUsesTrustedScopeAndGranularityWithoutChangingPhysicalClassification(
        string scope, string granularity, bool includeGenerated)
        => RunCase(false, false, includeGenerated: includeGenerated, generatedFixture: true,
            policyScope: scope, granularity: granularity);

    [Theory]
    [InlineData("Library/Code.cs", true)]
    [InlineData("Library/Other.cs", false)]
    [InlineData("Library/Code.cs/sibling", false)]
    public Task FailedTestsPartialFindingsRespectTrustedProductionExclusion(string exclusion, bool excluded)
        => RunCase(true, false, policyScope: "all", local: true, partial: true, partialExclusion: exclusion,
            partialExpectedExcluded: excluded);

    [Fact]
    public Task ColdOrdinaryConsumerMeasuresCompositeBoundariesWithoutDroppingProofOrRepeatingTests()
        => RunCase(false, true, policyScope: "all", local: true, measure: true);

    [Fact]
    public Task PublicStrictMetricViolationRetainsSuccessfulOnceTestsAndCompleteNonReusableReplay()
        => RunCase(false, false, policyScope: "all", local: true, thresholdViolation: true);

    [Fact]
    public Task CompletedFreshEvidenceReusesRequiredTestsWithoutRestoreCompileOrTestAndPreservesDecisions()
        => RunCase(false, false, policyScope: "all", completedReuse: true);

    [Fact]
    public Task CaptureRetainsRequiredTestWithoutReachableScoredProduction()
        => RunCase(false, true, standaloneTest: true);

    [Fact]
    public Task SavedRequiredResultsBindEveryActualTestArtifactAgainstCurrentCompilerWithoutNewLaunches()
        => RunCase(false, true, standaloneTest: true, savedResults: true);

    [Fact]
    public Task SavedNormalizedCoverageBindsCurrentReachabilityModulePathsAndPdbPointsWithoutNewLaunches()
        => RunCase(false, true, standaloneTest: true, savedCoverage: true);

    [Fact]
    public Task SavedSharedCoverageCannotUseAnotherRequiredInvocationToExcuseMissingModuleEvidence()
        => RunCase(false, true, standaloneTest: true, savedCoverage: true, sharedCoverage: true);

    [Fact]
    public Task SavedProductionExecutionsBindExactCurrentReachabilityAndRequiredArtifactBytes()
        => RunCase(false, true, standaloneTest: true, savedCoverage: true, productionAgreement: true);

    [Fact]
    public Task SavedProductionExecutionsRetainBothSharedRequiredTestReachabilityPairs()
        => RunCase(false, true, standaloneTest: true, savedCoverage: true, sharedCoverage: true, productionAgreement: true);

    [Fact]
    public Task SavedCompilerContextsBindAllCurrentSourcesReferencesParsePathsAndMetadata()
        => RunCase(false, true, standaloneTest: true, savedCoverage: true, contextAgreement: true);

    [Fact]
    public Task SavedSharedCompilerContextsRetainEveryExcludedRequiredTestInput()
        => RunCase(false, true, standaloneTest: true, savedCoverage: true, sharedCoverage: true, contextAgreement: true);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public Task SavedEligibilityContextsBindActualSemanticEmptyAndGeneratedInclusiveCompilerEvidence(bool empty, bool generated)
        => RunCase(false, true, emptyEligible: empty, includeGenerated: generated, contextOnly: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SavedCompleteBoundaryRechecksTwoRequiredContextsAndRejectsBetweenGateDrift(bool shared)
        => RunCase(false, true, standaloneTest: true, savedCoverage: true, sharedCoverage: shared, compositeProof: true);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public Task SavedCompleteBoundaryRetainsSemanticEmptyAndGeneratedInclusiveEvidence(bool empty, bool generated)
        => RunCase(false, true, emptyEligible: empty, includeGenerated: generated, contextOnly: true, compositeProof: true);

    [Fact]
    public Task SavedCompleteLocalBoundaryRetainsOriginalMembershipAcrossTwoRequiredContexts()
        => RunCase(false, true, policyScope: "all", local: true, standaloneTest: true, savedCoverage: true, compositeProof: true);

    [Fact]
    public Task SavedCompleteBoundaryRechecksExcludedRunnerAndPhysicalOutputsAfterIntermediateGates()
        => RunCase(false, true, policyScope: "all", local: true, standaloneTest: true, savedCoverage: true, compositeProof: true, physicalComposite: true);

    private static async Task RunCase(bool failing, bool coordinatorOnly, bool emptyEligible = false, bool includeGenerated = false,
        bool generatedFixture = false, string policyScope = "base", string granularity = "method", bool local = false, bool priorBuild = false, bool partial = false, bool partialDrift = false, bool scopedEmpty = false, bool staleOutput = false, bool staleRawOutput = false, bool replaceRawOutput = false, string? outputLayout = null, string? outputMutation = null, string? partialExclusion = null, bool partialExpectedExcluded = false, bool measure = false, bool thresholdViolation = false, bool completedReuse = false, bool standaloneTest = false, bool savedResults = false, bool savedCoverage = false, bool sharedCoverage = false, bool productionAgreement = false, bool contextAgreement = false, bool contextOnly = false, bool compositeProof = false, bool physicalComposite = false)
    {
        var hasGenerated = includeGenerated || generatedFixture;
        using var fixture = TestDirectory.Create("crap4csharp-public-cold-check");
        if (outputLayout is not null)
            fixture.Write("Directory.Build.props", outputLayout == "artifacts"
                ? "<Project><PropertyGroup><UseArtifactsOutput>true</UseArtifactsOutput><ArtifactsPath>$(MSBuildThisFileDirectory)artifacts</ArtifactsPath></PropertyGroup></Project>"
                : "<Project><PropertyGroup><BaseIntermediateOutputPath>$(MSBuildProjectDirectory)/compiler-cache/</BaseIntermediateOutputPath></PropertyGroup></Project>");
        const string counter = """
            <Target Name="CountActualCompile" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-count.txt" Lines="once" Overwrite="false" /></Target>
            """;
        const string privateCanary = "fresh-private-property-never-export-7264c8e";
        fixture.Write("Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            (coordinatorOnly ? "<PrivateCanary>" + privateCanary + "</PrivateCanary>" : "") + "</PropertyGroup>" + counter +
            (outputMutation is null ? "" : outputMutation == "source"
                ? "<ItemGroup><Compile Include=\"compiler-cache/Authored.cs\" /></ItemGroup>"
                : "<ItemGroup><AdditionalFiles Include=\"compiler-cache/Rules.json\" /></ItemGroup>") +
            (hasGenerated ? "<Target Name=\"AddGenerated\" BeforeTargets=\"CoreCompile\"><ItemGroup><Compile Include=\"obj/Extra.cs\" /></ItemGroup></Target>" : "") + "</Project>");
        if (outputMutation is not null)
            fixture.Write(outputMutation == "source" ? "Library/compiler-cache/Authored.cs" : "Library/compiler-cache/Rules.json",
                outputMutation == "source" ? "public static class Authored { public static int Value => 42; }" : "{\"value\":42}");
        fixture.Write("Library/Code.cs", emptyEligible ? "public static class Library {}" : "public static class Library { public static int Value => 42; }");
        if (hasGenerated) fixture.Write("Library/obj/Extra.cs", "public static class Extra { public static int Value => 42; }");
        if (coordinatorOnly) fixture.Write("Library/NoRuntime.cs", "#if NOT_DEFINED\npublic class Inactive {}\n#endif\n");
        fixture.Write("Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
            <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" PrivateAssets="all" />
            <PackageReference Include="xunit.v3" Version="3.2.2" PrivateAssets="all" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
            <PackageReference Include="coverlet.collector" Version="6.0.4" PrivateAssets="all" />
            <ProjectReference Include="../Library/Library.csproj" /></ItemGroup>
            COUNTER</Project>
            """.Replace("COUNTER", counter, StringComparison.Ordinal));
        fixture.Write("Tests/Tests.cs", "using Xunit; public class Once { [Fact] public void Test() { " +
            (outputMutation is null ? "" : "var p = System.IO.Path.GetFullPath(\"../../../../Library/compiler-cache/" +
                (outputMutation == "source" ? "Authored.cs" : "Rules.json") +
                "\", System.AppContext.BaseDirectory); var time = System.IO.File.GetLastWriteTimeUtc(p); System.IO.File.WriteAllText(p, System.IO.File.ReadAllText(p).Replace(\"42\", \"43\")); System.IO.File.SetLastWriteTimeUtc(p, time); ") +
            (partialDrift ? "var p = System.IO.Path.GetFullPath(\"../../../../Library/Code.cs\", System.AppContext.BaseDirectory); var time = System.IO.File.GetLastWriteTimeUtc(p); System.IO.File.WriteAllText(p, System.IO.File.ReadAllText(p).Replace(\"42\", \"43\")); System.IO.File.SetLastWriteTimeUtc(p, time); " : "") +
            (priorBuild || measure || thresholdViolation || completedReuse || savedResults || savedCoverage || contextOnly ? "System.IO.File.AppendAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, \"test-count.txt\"), \"once\\n\"); " : "") +
            "Assert.Equal(" + (failing ? "99" : "42") + ", " + (emptyEligible ? "42" : hasGenerated ? "Extra.Value" : "Library.Value") + "); } }");
        fixture.Write("policy.json", """
            {"schemaVersion":"repository-policy-v1","mode":"strict","productionProjects":["Library/Library.csproj"],
            "testProjects":["Tests/Tests.csproj"],"configuration":"Debug","targetFrameworks":["net10.0"],"scope":"base",
            "threshold":8,"missingCoverage":"fail","requiredChecks":["tests","coverage","crap"],"exclusions":[],"ruleset":"callables-v1"}
            """.Replace("\"ruleset\":\"callables-v1\"", includeGenerated ?
                "\"ruleset\":\"callables-v1\",\"allowedOverrides\":{\"includeGenerated\":true}" : "\"ruleset\":\"callables-v1\"", StringComparison.Ordinal)
                .Replace("\"scope\":\"base\"", "\"scope\":\"" + policyScope + "\"", StringComparison.Ordinal));
        if (thresholdViolation)
        {
            var path = Path.Combine(fixture.Path, "policy.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"threshold\":8", "\"threshold\":0.5", StringComparison.Ordinal));
        }
        if (partialExclusion is not null)
        {
            var path = Path.Combine(fixture.Path, "policy.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"threshold\":8", "\"threshold\":0.5", StringComparison.Ordinal)
                .Replace("\"exclusions\":[]", "\"exclusions\":[\"" + partialExclusion + "\"]", StringComparison.Ordinal));
        }
        if (scopedEmpty)
        {
            var policyPath = Path.Combine(fixture.Path, "policy.json");
            var text = File.ReadAllText(policyPath).Replace("\"mode\":\"strict\"", "\"mode\":\"incremental\",\"baseline\":\"baseline.json\"", StringComparison.Ordinal);
            File.WriteAllText(policyPath, text);
            var parsed = RepositoryPolicyParser.Parse(System.Text.Encoding.UTF8.GetBytes(text), "policy.json");
            var compatibility = TrustedPolicyLoader.BoundCompatibilityHash(parsed, new Dictionary<string, byte[]>());
            File.WriteAllBytes(Path.Combine(fixture.Path, "baseline.json"), BaselineDocument.Serialize(
                new BaselineDocument(BaselineDocument.Version, ComplexityRules.CallablesV1, compatibility,
                    new string('a', 64), "fixture-adoption", [])));
        }
        if (standaloneTest)
        {
            fixture.Write("Independent/Independent.csproj", File.ReadAllText(Path.Combine(fixture.Path, "Tests/Tests.csproj"))
                .Replace("<ProjectReference Include=\"../Library/Library.csproj\" />", sharedCoverage ? "<ProjectReference Include=\"../Library/Library.csproj\" />" : "", StringComparison.Ordinal));
            fixture.Write("Independent/Tests.cs", "using Xunit; public class IndependentTests { [Fact] public void Test() { " +
                (savedResults || savedCoverage ? "System.IO.File.AppendAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, \"test-count.txt\"), \"once\\n\"); " : "") + (sharedCoverage ? "Assert.Equal(42, Library.Value); } }" : "Assert.Equal(42, 42); } }"));
            fixture.Write("Root.slnx", "<Solution><Configurations><Platform Name=\"AnyCPU\" /></Configurations><Project Path=\"Tests/Tests.csproj\" /><Project Path=\"Independent/Independent.csproj\" /></Solution>");
            var policyPath = Path.Combine(fixture.Path, "policy.json");
            File.WriteAllText(policyPath, File.ReadAllText(policyPath).Replace("\"testProjects\":[\"Tests/Tests.csproj\"]",
                "\"testProjects\":[\"Tests/Tests.csproj\",\"Independent/Independent.csproj\"]", StringComparison.Ordinal));
        }
        fixture.Write(".gitignore", coordinatorOnly ? "build-count.txt\nbundle/\n" : "bin/\nobj/\nbuild-count.txt\nbundle/\n");
        var token = TestContext.Current.CancellationToken;
        if (!local)
            foreach (var args in new[] { new[] { "init", "-q" }, new[] { "config", "user.name", "Fixture" },
                new[] { "config", "user.email", "fixture@example.invalid" }, new[] { "add", "." }, new[] { "commit", "-qm", "cold fixture" } })
                Assert.Equal(0, (await ProcessRunner.RunAsync("git", args, fixture.Path, TimeSpan.FromSeconds(30), token)).ExitCode);
        var original = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).Where(p =>
            !p.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(p => p, File.ReadAllBytes);
        if (hasGenerated)
        {
            Assert.Single(Directory.GetDirectories(fixture.Path, "obj", SearchOption.AllDirectories));
            Assert.Single(Directory.GetFiles(Path.Combine(fixture.Path, "Library", "obj")));
            Assert.Empty(Directory.GetFiles(fixture.Path, "*.dll", SearchOption.AllDirectories));
        }
        else Assert.Empty(Directory.GetDirectories(fixture.Path, "obj", SearchOption.AllDirectories));
        if (coordinatorOnly)
        {
            // Measure only these API boundaries, not guessed internal build/test phases.
            var timings = new Dictionary<string, double>(StringComparer.Ordinal);
            async Task<T> MeasureAsync<T>(string name, Func<Task<T>> operation)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var value = await operation();
                timings.Add(name, clock.Elapsed.TotalMilliseconds);
                return value;
            }
            T Measure<T>(string name, Func<T> operation)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var value = operation();
                timings.Add(name, clock.Elapsed.TotalMilliseconds);
                return value;
            }
            var request = new FreshCheckRequest(standaloneTest ? "Root.slnx" : "Tests/Tests.csproj", "policy.json", local ? null : "HEAD", local ? "all" : null, null, "worktree", "method",
                null, "bundle", false, false, null, new PolicyOverrides(IncludeGenerated: includeGenerated ? true : null), TimeSpan.FromMinutes(3));
            var preflight = await MeasureAsync("trustedPreflight", () => FreshCheckPreflightLoader.LoadAsync(request, fixture.Path, token));
            var evidence = await MeasureAsync("executionComposite", () => FreshCheckExecution.RunAsync(preflight, token));
            Assert.Equal(standaloneTest ? 3 : 2, evidence.Compiler.Observations.Count);
            Assert.Equal(standaloneTest ? 2 : 1, evidence.Tests.Count);
            Assert.Equal(standaloneTest ? 2 : 1, evidence.Tests.Outcomes.Count);
            FreshCapturedBundle? measuredCapture = null;
            if (failing)
            {
                Assert.NotEqual(0, evidence.Tests[0].Process.ExitCode);
                Assert.Null(evidence.Coverage); Assert.Empty(evidence.SafeChildren);
                var rawFailure = FreshFailureResult.Create(evidence, DateTimeOffset.UnixEpoch, TimeSpan.Zero);
                Assert.Equal(1, rawFailure.Run.ExitCode);
                Assert.Contains(rawFailure.Evaluation.Checks, c => c.Name == "testExecution" && c.Status == "fail");
                var outcome = Assert.Single(rawFailure.Run.TestOutcomes!);
                Assert.Equal("failed", outcome.Status); Assert.Equal(evidence.Tests[0].Process.ExitCode, outcome.ExitCode);
                Assert.All(rawFailure.Run.Artifacts, a => { Assert.False(a.Complete); Assert.False(a.Reusable); });
                Assert.DoesNotContain(rawFailure.Run.Artifacts, a => a.Kind == "manifest");
                var partialCoverage = CoverageGraphBinding.ValidateFailedInvocation(evidence.Tests[0], evidence.Compiler, token);
                Assert.NotEmpty(partialCoverage.Modules);
                Assert.Throws<ProjectContextException>(() => CoverageGraphBinding.ValidateInvocation(evidence.Tests[0], evidence.Compiler, token));
                Assert.Throws<ProjectContextException>(() => CoverageGraphBinding.ValidateAggregate(evidence.Tests, evidence.Compiler, token));
                var partialResult = await FreshPartialAnalysis.AttachAsync(evidence, rawFailure, token);
                // HEAD was captured after committing this fixture: base scope is
                // genuinely empty. Required failed tests still run; no all-scope
                // metrics may leak into the selected changed scope.
                Assert.Empty(partialResult.Evaluation.Metrics);
                Assert.Contains(partialResult.Evaluation.Checks, c => c.Name == "coverage" && c.Reason == "coverage.boundPartialObservations");
                Assert.Equal(1, partialResult.Run.ExitCode);
                Assert.False(partialResult.Evaluation.Decision.Completed);
                var trxPath = Path.Combine(evidence.Tests[0].ResultsDirectory, "tests.trx");
                var trxBytes = File.ReadAllBytes(trxPath); var trxTime = File.GetLastWriteTimeUtc(trxPath);
                try
                {
                    var document = System.Xml.Linq.XDocument.Load(trxPath);
                    document.Descendants().Single(e => e.Name.LocalName == "Counters").SetAttributeValue("failed", "0");
                    document.Save(trxPath); File.SetLastWriteTimeUtc(trxPath, trxTime);
                    Assert.Throws<ProjectContextException>(() => CoverageGraphBinding.ValidateFailedInvocation(evidence.Tests[0], evidence.Compiler, token));
                }
                finally { File.WriteAllBytes(trxPath, trxBytes); File.SetLastWriteTimeUtc(trxPath, trxTime); }
                var attachmentPath = partialCoverage.Attachment.Path;
                var attachmentTime = File.GetLastWriteTimeUtc(attachmentPath);
                try
                {
                    var document = System.Xml.Linq.XDocument.Load(attachmentPath);
                    document.Descendants("ModuleName").First().Value = "UnrelatedModule";
                    document.Save(attachmentPath); File.SetLastWriteTimeUtc(attachmentPath, attachmentTime);
                    Assert.Throws<ProjectContextException>(() => CoverageGraphBinding.ValidateFailedInvocation(evidence.Tests[0], evidence.Compiler, token));
                }
                finally { File.WriteAllBytes(attachmentPath, partialCoverage.Attachment.Bytes.ToArray()); File.SetLastWriteTimeUtc(attachmentPath, attachmentTime); }
                using var rawJson = JsonDocument.Parse(ResultWriter.Serialize(rawFailure));
                Assert.Equal("failed", rawJson.RootElement.GetProperty("run").GetProperty("testOutcomes")[0].GetProperty("status").GetString());
            }
            else
            {
                Assert.Equal(0, evidence.Tests[0].Process.ExitCode); Assert.Null(evidence.Tests.Failure);
                Assert.NotNull(evidence.Coverage); Assert.Equal(standaloneTest ? 2 : 1, evidence.SafeChildren.Count);
                var captured = await MeasureAsync("captureWithRegates", () => FreshManifestCapture.CaptureAsync(evidence, token));
                measuredCapture = captured;
                var planArtifact = captured.Manifest.Artifacts.Single(a => a.Kind == CompletedTestPlanRecipe.Kind);
                var selectedRoot = ProjectTargetSelector.Select(fixture.Path, request.Project);
                Assert.Equal(selectedRoot, CompletedTestPlanRecipe.ResolveSelectedRoot(
                    captured.Bytes[planArtifact.Locator].ToArray(), fixture.Path, token));
                var reloadRequest = new ProjectContextLoadRequest(selectedRoot, preflight.Policy.Configuration, "AnyCPU",
                    preflight.Policy.TargetFrameworks.ToArray(), false, includeGenerated, request.Timeout);
                var currentGraph = await ProjectContextLoader.LoadGraphAsync(reloadRequest, token);
                var currentRunners = await ProjectContextLoader.DiscoverTestRunnersAsync(reloadRequest, token);
                Assert.True(currentGraph.Success); Assert.True(currentRunners.Success);
                var currentPlan = CheckExecutionPlan.Create(currentRunners, currentGraph, token);
                CompletedTestPlanRecipe.Validate(captured.Bytes[planArtifact.Locator].ToArray(), currentPlan,
                    fixture.Path, selectedRoot, token);
                Assert.False(captured.Manifest.Contexts.Single().ReuseRecipeComplete); // plan alone never enables reuse
                var requiredInventory = captured.Manifest.Artifacts.Single(a => a.Kind == "required-test-inventory");
                var parsedRequired = SavedRequiredTestInventory.Parse(captured.Bytes[requiredInventory.Locator].ToArray(),
                    currentPlan.Tests.Select(t => t.GraphId).ToArray(), token);
                Assert.Equal(currentPlan.Tests.Select(t => t.GraphId), parsedRequired.Select(t => t.GraphId));
                Assert.Equal("provenance.savedRequiredInventoryUnproven", Assert.Throws<PolicyException>(() =>
                    SavedRequiredTestInventory.Parse(captured.Bytes[requiredInventory.Locator].ToArray(),
                        currentPlan.Tests.Select(t => t.GraphId + "-wrong").ToArray(), token)).Code);
                if (standaloneTest)
                {
                    var independent = currentPlan.Tests.Single(t => Path.GetFileName(t.ProjectPath) == "Independent.csproj");
                    if (sharedCoverage) Assert.Contains(captured.Manifest.Executions, e => e.TestProject == "Independent/Independent.csproj");
                    else Assert.DoesNotContain(captured.Manifest.Executions, e => e.TestProject == "Independent/Independent.csproj");
                    Assert.Contains(captured.Manifest.Artifacts, a => a.Id == "required-test-" + independent.GraphId + "-trx");
                    Assert.Equal("once", File.ReadAllText(Path.Combine(fixture.Path, "Independent/build-count.txt")).Trim());
                }
                using (var saved = JsonDocument.Parse(captured.Bytes[requiredInventory.Locator].AsMemory()))
                {
                    var required = saved.RootElement.GetProperty("tests").EnumerateArray().ToArray();
                    Assert.Equal(currentPlan.Tests.Select(t => t.GraphId), required.Select(t => t.GetProperty("graphId").GetString()));
                    foreach (var item in required)
                    {
                        var graphId = item.GetProperty("graphId").GetString()!;
                        var binding = currentGraph.GraphContexts.Single(b => b.GraphId == graphId);
                        var requiredTrx = captured.Manifest.Artifacts.Single(a => a.Id == item.GetProperty("trx").GetString());
                        var requiredPe = captured.Manifest.Artifacts.Single(a => a.Id == item.GetProperty("assembly").GetString());
                        var requiredPdb = captured.Manifest.Artifacts.Single(a => a.Id == item.GetProperty("pdb").GetString());
                        var requiredChild = captured.Manifest.Artifacts.Single(a => a.Id == item.GetProperty("child").GetString());
                        Assert.Equal("required-test-result", requiredTrx.Kind);
                        Assert.Equal("required-test-assembly", requiredPe.Kind);
                        Assert.Equal("required-test-pdb", requiredPdb.Kind);
                        Assert.Equal("required-test-child", requiredChild.Kind);
                        Assert.Equal(1, TestResultBinding.ValidateDocument(captured.Bytes[requiredTrx.Locator], binding.Context.EvaluatedTargetPath!, token));
                        TestChildEvidenceExport.ValidateSaved(captured.Bytes[requiredChild.Locator], graphId, token);
                        var testBuild = ArtifactEvidenceInspector.InspectBuild(captured.Bytes[requiredPe.Locator], captured.Bytes[requiredPdb.Locator]);
                        Assert.Equal(item.GetProperty("mvid").GetString(), testBuild.Mvid);
                        Assert.Equal(item.GetProperty("moduleIdentity").GetString(), testBuild.ModuleIdentity);
                    }
                    Assert.False(saved.RootElement.GetProperty("reusableRecipe").GetBoolean());
                }

                var sourceInputs = captured.Manifest.Contexts.Single().Inputs.Where(i => i.Role is "source" or "compiler-source").ToArray();
                Assert.Equal(evidence.Compiler.FinalGraph.GraphContexts.Single(b => !b.IsTestProject).Context.CompilerDocuments.Count, sourceInputs.Length);
                var capturedPe = captured.Manifest.Artifacts.Single(a => a.Kind == "assembly");
                var capturedPdb = captured.Manifest.Artifacts.Single(a => a.Kind == "pdb");
                var actual = ArtifactEvidenceInspector.InspectBuild(captured.Bytes[capturedPe.Locator], captured.Bytes[capturedPdb.Locator]);
                Assert.Equal(actual.Documents.Count, sourceInputs.Count(i => i.Role == "source"));
                Assert.Equal(sourceInputs.Length - actual.Documents.Count, sourceInputs.Count(i => i.Role == "compiler-source"));
                Assert.False(captured.Manifest.Contexts.Single().ReuseRecipeComplete);
                Assert.Contains(captured.Manifest.Artifacts, a => a.Kind == "test-child-build");
                Assert.DoesNotContain(captured.Manifest.Artifacts, a => a.Kind.Contains("binlog", StringComparison.Ordinal));
                Assert.All(captured.Bytes.Values, b => Assert.True(b.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(privateCanary)) < 0));
                Assert.Contains(captured.Manifest.Artifacts, a => a.Kind == "compiler-input-closure");
                Assert.Contains(captured.Manifest.Artifacts, a => a.Kind == "test-compiler-input-closure");
                Assert.Contains(captured.Manifest.Artifacts, a => a.Kind == "test-compiler-source");
                var alias = await Assert.ThrowsAsync<PolicyException>(() => FreshManifestCapture.PublishAsync(evidence, captured,
                    Path.Combine(fixture.Path, "Library", "Code.cs"), token));
                Assert.Equal("output.aliasesPolicyInput", alias.Code);
                var manifest = await MeasureAsync("privatePublishWithRegate", () => FreshManifestCapture.PublishAsync(evidence, captured,
                    Path.Combine(evidence.PrivateDirectory, "safe-export"), token));
                if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.GetDirectoryName(manifest)!));
                var bundle = Measure("bundleLoad", () => ArtifactBundle.Load(manifest, evidence.PrivateDirectory));
                Assert.Equal(ProvenanceStatus.Captured, ProvenanceVerifier.VerifyCapture(bundle.Manifest, bundle.Bytes).Status);
                if (compositeProof)
                {
                    var gate = typeof(App).Assembly.GetType("SavedCompletedProof")?.GetMethod("ValidateAsync",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    Assert.NotNull(gate);
                    async Task Agree(Func<CancellationToken, Task>? mutate = null)
                    {
                        try { await (Task)gate.Invoke(null, [bundle, preflight, token, mutate])!; }
                        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
                        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
                    }
                    await Agree();
                    var mutationProject = standaloneTest ? "Independent" : "Tests";
                    var sourcePath = Path.Combine(fixture.Path, mutationProject, "Tests.cs");
                    var originalBytes = File.ReadAllBytes(sourcePath); var originalTime = File.GetLastWriteTimeUtc(sourcePath);
                    var callbacks = 0;
                    try
                    {
                        var refused = await Record.ExceptionAsync(() => Agree(ct =>
                        {
                            ct.ThrowIfCancellationRequested();
                            callbacks++;
                            File.WriteAllText(sourcePath, File.ReadAllText(sourcePath).Replace("42", "43", StringComparison.Ordinal));
                            File.SetLastWriteTimeUtc(sourcePath, originalTime);
                            return Task.CompletedTask;
                        }));
                        Assert.Equal(1, callbacks); // mutation reached after all intermediate agreement gates
                        // The independently reloaded context hashes authored source bytes, so the final
                        // saved-plan comparison rejects this drift before the retained physical guard.
                        Assert.Equal("provenance.completedTestPlanMismatch", Assert.IsType<PolicyException>(refused).Code);
                        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(sourcePath));
                        Assert.False(originalBytes.AsSpan().SequenceEqual(File.ReadAllBytes(sourcePath)));
                    }
                    finally { File.WriteAllBytes(sourcePath, originalBytes); File.SetLastWriteTimeUtc(sourcePath, originalTime); }
                    if (local)
                    {
                        Assert.False(Directory.Exists(Path.Combine(fixture.Path, ".git")));
                        Assert.NotNull(preflight.LocalSnapshot);
                        Assert.Null(preflight.GitSnapshot);
                        // This owned resource is outside every compiler/output closure. Only
                        // comparison with the ORIGINAL authored membership can reject it.
                        var resource = Path.Combine(fixture.Path, "AddedAfterIntermediate.resx");
                        Assert.False(File.Exists(resource));
                        try
                        {
                            var refused = await Record.ExceptionAsync(() => Agree(ct =>
                            {
                                ct.ThrowIfCancellationRequested();
                                callbacks++;
                                File.WriteAllText(resource, "<root />");
                                return Task.CompletedTask;
                            }));
                            Assert.Equal(2, callbacks);
                            Assert.Equal("provenance.localWorkspaceChanged", Assert.IsType<PolicyException>(refused).Code);
                            Assert.True(File.Exists(resource));
                        }
                        finally { File.Delete(resource); }
                        await Agree();
                        Assert.False(Directory.Exists(Path.Combine(fixture.Path, ".git")));
                    }
                    else
                    {
                    // Restore this owned fixture's Git stat cache as well as bytes/time. Read-only
                    // diff-index otherwise retains an excluded Tests.cs M record after the mutation.
                    // Refresh cannot change the ORIGINAL logical staged entries; prove that explicitly.
                    Assert.Equal(0, (await ProcessRunner.RunAsync("git", ["update-index", "--refresh", "--", mutationProject + "/Tests.cs"],
                        fixture.Path, TimeSpan.FromSeconds(30), token)).ExitCode);
                    var restoredStage = await ProcessRunner.RunAsync("git", ["ls-files", "--stage", "-z", "--"],
                        fixture.Path, TimeSpan.FromSeconds(30), token);
                    Assert.Equal(0, restoredStage.ExitCode);
                    Assert.Equal(preflight.GitSnapshot!.Revision.IndexIdentity,
                        "sha256:" + CanonicalIdentity.Sha256(System.Text.Encoding.UTF8.GetBytes(restoredStage.StandardOutput)));
                    // A staged mode-only change leaves physical inputs and HEAD-to-worktree changes
                    // unchanged, but must still fail the ORIGINAL index identity comparison.
                    var indexPath = Path.Combine(fixture.Path, ".git", "index");
                    var originalIndex = File.ReadAllBytes(indexPath);
                    var indexTime = File.GetLastWriteTimeUtc(indexPath);
                    try
                    {
                        var refused = await Record.ExceptionAsync(() => Agree(async ct =>
                        {
                            callbacks++;
                            Assert.Equal(0, (await ProcessRunner.RunAsync("git", ["update-index", "--chmod=+x", mutationProject + "/Tests.cs"],
                                fixture.Path, TimeSpan.FromSeconds(30), ct)).ExitCode);
                            Assert.Equal(originalBytes, File.ReadAllBytes(sourcePath));
                        }));
                        Assert.Equal(2, callbacks);
                        Assert.Equal("provenance.gitStateChanged", Assert.IsType<PolicyException>(refused).Code);
                        Assert.False(originalIndex.AsSpan().SequenceEqual(File.ReadAllBytes(indexPath)));
                    }
                    finally { File.WriteAllBytes(indexPath, originalIndex); File.SetLastWriteTimeUtc(indexPath, indexTime); }
                    await Agree();
                    Assert.Equal(originalIndex, File.ReadAllBytes(indexPath));
                    }
                    if (physicalComposite)
                    {
                        Assert.Equal(2, currentPlan.Tests.Count);
                        var excluded = currentGraph.GraphContexts.Single(b => Path.GetFileName(b.Context.ResolvedProjectPath) == "Independent.csproj");
                        var production = currentGraph.GraphContexts.Single(b => !b.IsTestProject);
                        var productionObservation = evidence.Compiler.Observations.Single(o => o.ProjectPath == production.Context.ResolvedProjectPath);
                        var runnerPath = excluded.Context.ResolvedProjectPath!;
                        var runnerBytes = File.ReadAllBytes(runnerPath);
                        var runnerTime = File.GetLastWriteTimeUtc(runnerPath);
                        var beforeRunner = callbacks;
                        try
                        {
                            var refused = await Record.ExceptionAsync(() => Agree(ct =>
                            {
                                ct.ThrowIfCancellationRequested(); callbacks++;
                                File.WriteAllText(runnerPath, File.ReadAllText(runnerPath).Replace("</PropertyGroup>",
                                    "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup>", StringComparison.Ordinal));
                                File.SetLastWriteTimeUtc(runnerPath, runnerTime);
                                return Task.CompletedTask;
                            }));
                            Assert.Equal(beforeRunner + 1, callbacks);
                            // Runner discovery independently refuses the modified required graph.
                            Assert.Equal("test.planInvalid", Assert.IsType<ProjectContextException>(refused).Reason);
                            Assert.Equal(runnerTime, File.GetLastWriteTimeUtc(runnerPath));
                        }
                        finally { File.WriteAllBytes(runnerPath, runnerBytes); File.SetLastWriteTimeUtc(runnerPath, runnerTime); }
                        await Agree();
                        var physicalOutputs = new[]
                        {
                            (Path: excluded.Context.EvaluatedTargetPath!, Reason: "compiler.outputsUnproven"),
                            (Path: Path.ChangeExtension(excluded.Context.EvaluatedTargetPath!, ".pdb"), Reason: "compiler.outputsUnproven"),
                            (Path: productionObservation.ReferenceAssembly!.Path, Reason: "compiler.referenceOutputUnproven"),
                            (Path: production.Context.EvaluatedReferenceAssemblyPath!, Reason: "compiler.referenceOutputUnproven")
                        };
                        Assert.Equal(4, physicalOutputs.Select(o => o.Path).Distinct(PathIdentityPolicy.Current.Comparer).Count());
                        foreach (var physicalOutput in physicalOutputs)
                        {
                            var path = physicalOutput.Path;
                            Assert.StartsWith(fixture.Path + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
                            var bytesBefore = File.ReadAllBytes(path); var timeBefore = File.GetLastWriteTimeUtc(path);
                            Assert.NotEmpty(bytesBefore);
                            var beforeMutation = callbacks;
                            try
                            {
                                var refused = await Record.ExceptionAsync(() => Agree(ct =>
                                {
                                    ct.ThrowIfCancellationRequested(); callbacks++;
                                    var changed = bytesBefore.ToArray(); changed[^1] ^= 1;
                                    File.WriteAllBytes(path, changed); File.SetLastWriteTimeUtc(path, timeBefore);
                                    return Task.CompletedTask;
                                }));
                                Assert.Equal(beforeMutation + 1, callbacks);
                                Assert.Equal(physicalOutput.Reason, Assert.IsType<ProjectContextException>(refused).Reason);
                                Assert.Equal(bytesBefore.Length, new FileInfo(path).Length);
                                Assert.Equal(timeBefore, File.GetLastWriteTimeUtc(path));
                                Assert.False(bytesBefore.AsSpan().SequenceEqual(File.ReadAllBytes(path)));
                            }
                            finally { File.WriteAllBytes(path, bytesBefore); File.SetLastWriteTimeUtc(path, timeBefore); }
                            await Agree();
                            Assert.Equal(bytesBefore, File.ReadAllBytes(path));
                        }
                        Assert.Equal(beforeRunner + 5, callbacks);
                    }
                    foreach (var required in currentPlan.Tests)
                    {
                        var context = currentGraph.GraphContexts.Single(b => b.GraphId == required.GraphId).Context;
                        Assert.Single(File.ReadAllLines(Path.Combine(Path.GetDirectoryName(context.EvaluatedTargetPath!)!, "test-count.txt")));
                    }
                }
                if (contextOnly)
                {
                    var imported = FreshPriorBuild.Load(bundle, currentPlan.PreparedGraph, fixture.Path, token);
                    var currentCompiler = await CompilerGraphExecution.RunBoundAsync(reloadRequest, currentPlan.PreparedGraph,
                        (node, cancellation) => { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(imported[node.Id]); }, token);
                    ValidateEligibilityContexts(bundle, currentCompiler, fixture.Path, emptyEligible, includeGenerated, token);
                    foreach (var required in currentPlan.Tests)
                    {
                        var context = currentCompiler.FinalGraph.GraphContexts.Single(b => b.GraphId == required.GraphId).Context;
                        Assert.Single(File.ReadAllLines(Path.Combine(Path.GetDirectoryName(context.EvaluatedTargetPath!)!, "test-count.txt")));
                    }
                }
                if (savedCoverage)
                {
                    var method = typeof(CoverageGraphBinding).GetMethod("ValidateSavedNormalized",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    Assert.NotNull(method);
                    var imported = FreshPriorBuild.Load(bundle, currentPlan.PreparedGraph, fixture.Path, token);
                    var currentCompiler = await CompilerGraphExecution.RunBoundAsync(reloadRequest, currentPlan.PreparedGraph,
                        (node, cancellation) => { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(imported[node.Id]); }, token);
                    object Validate(ArtifactBundle candidate)
                    {
                        try { return method.Invoke(null, [candidate, currentPlan, currentCompiler, fixture.Path, token])!; }
                        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
                        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
                    }
                    if (contextAgreement)
                    {
                        var gate = typeof(App).Assembly.GetType("Crap4CSharp.Core.SavedCompilerContexts")?.GetMethod("Validate",
                            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                        Assert.NotNull(gate);
                        void Agree(ArtifactBundle candidate)
                        {
                            try { gate.Invoke(null, [candidate, currentCompiler, fixture.Path, token]); }
                            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
                            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
                        }
                        void Reject(ManifestContext changed)
                            => Assert.Equal("provenance.savedCompilerContextsUnproven", Assert.Throws<PolicyException>(() =>
                                Agree(bundle with { Manifest = ManifestIdentity.Seal(bundle.Manifest with { Contexts = [changed] }) })).Code);
                        Agree(bundle);
                        var savedContext = bundle.Manifest.Contexts.Single();
                        var source = savedContext.Inputs.First(i => i.Role == "source" && !i.Generated);
                        Reject(savedContext with { Inputs = savedContext.Inputs.Where(i => i != source).ToArray() });
                        Reject(savedContext with { Inputs = savedContext.Inputs.Select(i => i == source ? i with { Generated = true } : i).ToArray() });
                        Reject(savedContext with { Inputs = savedContext.Inputs.Select(i => i == source ? i with { RepositoryPath = "foreign.cs" } : i).ToArray() });
                        Reject(savedContext with { Inputs = savedContext.Inputs.Where(i => i.Role != "reference").ToArray() });
                        Reject(savedContext with { IncludeGenerated = !savedContext.IncludeGenerated });
                        Reject(savedContext with { CoverageApplicability = "semantic-empty" });
                        Reject(savedContext with { ParseOptions = savedContext.ParseOptions! with { SourceKind = "Script" } });
                        Reject(savedContext with { ParseOptions = savedContext.ParseOptions! with { PreprocessorSymbols = ["FOREIGN"] } });
                        Reject(savedContext with { ParseOptions = savedContext.ParseOptions! with { Features = new Dictionary<string, string> { ["foreign"] = "true" } } });
                        Reject(savedContext with { PathPolicy = savedContext.PathPolicy! with { CasePolicy = "foreign" } });
                        Reject(savedContext with { PathPolicy = savedContext.PathPolicy! with { ReportRootMappings = [] } });
                        foreach (var closure in bundle.Manifest.Artifacts.Where(a => a.Kind is "compiler-input-closure" or "test-compiler-input-closure"))
                        {
                            var altered = System.Collections.Immutable.ImmutableArray.Create(System.Text.Encoding.UTF8.GetBytes("{}"));
                            var hash = CanonicalIdentity.Sha256(altered.AsSpan()); var locator = "inputs/" + hash + ".bin";
                            var candidate = bundle with { Manifest = ManifestIdentity.Seal(bundle.Manifest with { Artifacts = bundle.Manifest.Artifacts.Select(a =>
                                a.Id == closure.Id ? a with { Locator = locator, Length = altered.Length, Sha256 = hash } : a).ToArray() }),
                                Bytes = new Dictionary<string, System.Collections.Immutable.ImmutableArray<byte>>(bundle.Bytes) { [locator] = altered } };
                            Assert.Equal("provenance.savedCompilerContextsUnproven", Assert.Throws<PolicyException>(() => Agree(candidate)).Code);
                        }
                        Agree(bundle);
                    }
                    if (productionAgreement)
                    {
                        var gate = typeof(App).Assembly.GetType("Crap4CSharp.Core.SavedProductionExecutions")?.GetMethod("Validate",
                            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                        Assert.NotNull(gate);
                        void Agree(ArtifactBundle candidate)
                        {
                            try { gate.Invoke(null, [candidate, currentPlan, currentCompiler, fixture.Path, token]); }
                            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
                            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
                        }
                        void Reject(RunManifest changed)
                            => Assert.Equal("provenance.savedProductionExecutionsUnproven", Assert.Throws<PolicyException>(() =>
                                Agree(bundle with { Manifest = ManifestIdentity.Seal(changed) })).Code);
                        Agree(bundle);
                        Assert.Equal(sharedCoverage ? 2 : 1, bundle.Manifest.Executions.Count);
                        var originalExecution = bundle.Manifest.Executions.First();
                        Reject(bundle.Manifest with { Executions = [] });
                        Reject(bundle.Manifest with { Executions = [originalExecution with { Id = "foreign-execution" }] });
                        Reject(bundle.Manifest with { Executions = [originalExecution with { TestProject = "Independent/Independent.csproj" }] });
                        Reject(bundle.Manifest with { Executions = [originalExecution with { TestMvid = Guid.NewGuid().ToString() }] });
                        Reject(bundle.Manifest with { Executions = [originalExecution with { PassedTests = originalExecution.PassedTests + 1 }] });
                        Reject(bundle.Manifest with { Builds = bundle.Manifest.Builds.Select(b => b with { Id = "foreign-build" }).ToArray() });
                        var coverage = bundle.Manifest.Artifacts.Single(a => a.Kind == "coverage" && a.ExecutionId == originalExecution.Id);
                        // Genuine same-shaped normalized module, but visits differ from
                        // the all-required saved report. Resealing is not agreement.
                        var xml = TestResultBinding.Parse(bundle.Bytes[coverage.Locator]);
                        xml.Descendants("SequencePoint").First().SetAttributeValue("vc", "999");
                        var changedBytes = System.Collections.Immutable.ImmutableArray.Create(System.Text.Encoding.UTF8.GetBytes(xml.ToString()));
                        var hash = CanonicalIdentity.Sha256(changedBytes.AsSpan()); var locator = "inputs/" + hash + ".bin";
                        var changedBundle = bundle with { Manifest = ManifestIdentity.Seal(bundle.Manifest with { Artifacts = bundle.Manifest.Artifacts.Select(a =>
                            a.Id == coverage.Id ? a with { Locator = locator, Length = changedBytes.Length, Sha256 = hash } : a).ToArray() }),
                            Bytes = new Dictionary<string, System.Collections.Immutable.ImmutableArray<byte>>(bundle.Bytes) { [locator] = changedBytes } };
                        Assert.Equal("provenance.savedProductionExecutionsUnproven", Assert.Throws<PolicyException>(() => Agree(changedBundle)).Code);
                        Assert.Equal(ProvenanceStatus.Captured, ProvenanceVerifier.VerifyCapture(changedBundle.Manifest, changedBundle.Bytes).Status);
                        // Coherently renamed associations still satisfy captured schema,
                        // but are not canonical IDs derived from the current graph.
                        var renamedId = "renamed-" + originalExecution.Id;
                        var renamed = bundle.Manifest with { Executions = bundle.Manifest.Executions.Select(e => e.Id == originalExecution.Id ? e with { Id = renamedId } : e).ToArray(),
                            Artifacts = bundle.Manifest.Artifacts.Select(a => a.ExecutionId == originalExecution.Id ? a with { ExecutionId = renamedId } : a).ToArray() };
                        renamed = ManifestIdentity.Seal(renamed);
                        Assert.Equal(ProvenanceStatus.Captured, ProvenanceVerifier.VerifyCapture(renamed, bundle.Bytes).Status);
                        Reject(renamed);
                        var renamedBuild = "renamed-" + originalExecution.BuildId;
                        var buildRenamed = ManifestIdentity.Seal(bundle.Manifest with {
                            Builds = bundle.Manifest.Builds.Select(b => b with { Id = renamedBuild }).ToArray(),
                            Executions = bundle.Manifest.Executions.Select(e => e with { BuildId = renamedBuild }).ToArray(),
                            Artifacts = bundle.Manifest.Artifacts.Select(a => a.BuildId is null ? a : a with { BuildId = renamedBuild }).ToArray() });
                        Assert.Equal(ProvenanceStatus.Captured, ProvenanceVerifier.VerifyCapture(buildRenamed, bundle.Bytes).Status);
                        Reject(buildRenamed);
                        Agree(bundle);
                    }
                    var accepted = Assert.IsType<BoundSavedGraphCoverage>(Validate(bundle));
                    Assert.Equal(currentPlan.Tests.Select(t => t.GraphId), accepted.Invocations.Select(i => i.GraphId));
                    Assert.Equal(evidence.Coverage.EligibleGraphIds, accepted.EligibleGraphIds);
                    var actualModules = evidence.Coverage.Invocations.SelectMany(i => i.Modules).ToArray();
                    var savedModules = accepted.Invocations.SelectMany(i => i.Modules).ToArray();
                    Assert.Equal(actualModules.Select(m => m.GraphId), savedModules.Select(m => m.GraphId));
                    foreach (var (fresh, saved) in actualModules.Zip(savedModules))
                    {
                        Assert.Equal(fresh.ActualMethodTokens, saved.ActualMethodTokens);
                        Assert.Equal(fresh.LineProjection, saved.LineProjection);
                    }
                    if (sharedCoverage)
                    {
                        Assert.Equal(2, savedModules.Length);
                        Assert.Single(savedModules.Select(m => m.GraphId).Distinct());
                        Assert.Single(savedModules.SelectMany(m => m.ActualMethodTokens).Distinct());
                    }
                    ArtifactBundle ChangeBytes(string id, byte[] content)
                    {
                        var bytes = System.Collections.Immutable.ImmutableArray.Create(content);
                        var hash = CanonicalIdentity.Sha256(bytes.AsSpan()); var locator = "inputs/" + hash + ".bin";
                        var manifestChanged = ManifestIdentity.Seal(bundle.Manifest with { Artifacts = bundle.Manifest.Artifacts.Select(a =>
                            a.Id == id ? a with { Locator = locator, Length = bytes.Length, Sha256 = hash } : a).ToArray() });
                        return bundle with { Manifest = manifestChanged,
                            Bytes = new Dictionary<string, System.Collections.Immutable.ImmutableArray<byte>>(bundle.Bytes) { [locator] = bytes } };
                    }
                    void Refused(ArtifactBundle candidate)
                        => Assert.Equal("provenance.savedCoverageUnproven", Assert.Throws<PolicyException>(() => Validate(candidate)).Code);
                    var module = parsedRequired.SelectMany(t => t.Modules).First();
                    var coverageArtifact = bundle.Manifest.Artifacts.Single(a => a.Id == module.Artifact);
                    void XmlChange(Action<System.Xml.Linq.XDocument> mutate)
                    {
                        var xml = TestResultBinding.Parse(bundle.Bytes[coverageArtifact.Locator]); mutate(xml);
                        Refused(ChangeBytes(module.Artifact, System.Text.Encoding.UTF8.GetBytes(xml.ToString())));
                    }
                    XmlChange(xml => xml.Descendants("ModuleName").Single().Value = "UnrelatedModule");
                    XmlChange(xml => xml.Descendants("File").First().SetAttributeValue("fullPath", Path.Combine(fixture.Path, "Independent/Tests.cs")));
                    XmlChange(xml => xml.Descendants("File").First().SetAttributeValue("fullPath", "Library/Code.cs"));
                    XmlChange(xml => xml.Descendants("SequencePoint").First().SetAttributeValue("sl", "100000"));
                    XmlChange(xml => xml.Descendants("SequencePoint").First().SetAttributeValue("vc", "-1"));
                    XmlChange(xml => xml.Descendants("SequencePoint").First().SetAttributeValue("sc", "1"));
                    XmlChange(xml => xml.Descendants("SequencePoint").First().SetAttributeValue("offset", "0"));
                    XmlChange(xml => xml.Descendants("MetadataToken").First().Value = "06000001");
                    XmlChange(xml => xml.Descendants("FileRef").First().SetAttributeValue("uid", "missing"));
                    XmlChange(xml => xml.Descendants("Modules").Single().Add(new System.Xml.Linq.XElement(xml.Descendants("Module").Single())));
                    XmlChange(xml => xml.Descendants("Method").First().Remove());
                    XmlChange(xml => xml.Descendants("Name").First().Value = "System.Int32 Library::get_Foreign()");
                    var inventory = System.Text.Json.Nodes.JsonNode.Parse(bundle.Bytes[requiredInventory.Locator].AsSpan())!;
                    var owner = parsedRequired.First(t => t.Modules.Count != 0);
                    var independent = sharedCoverage ? null : parsedRequired.Single(t => t.Modules.Count == 0);
                    inventory["tests"]!.AsArray().Single(t => t!["graphId"]!.GetValue<string>() == owner.GraphId)!["modules"] = new System.Text.Json.Nodes.JsonArray();
                    var missingModule = ChangeBytes(requiredInventory.Id, System.Text.Encoding.UTF8.GetBytes(inventory.ToJsonString()));
                    missingModule = missingModule with { Manifest = ManifestIdentity.Seal(missingModule.Manifest with { Artifacts = missingModule.Manifest.Artifacts
                        .Where(a => a.Id != module.Artifact).ToArray() }) };
                    // Another required invocation still has the same genuine production
                    // module. Full graph union alone must not waive this omission.
                    Refused(missingModule);
                    if (independent is not null)
                    {
                    inventory = System.Text.Json.Nodes.JsonNode.Parse(bundle.Bytes[requiredInventory.Locator].AsSpan())!;
                    var transferredId = "required-test-" + independent.GraphId + "-coverage-" + module.GraphId;
                    inventory["tests"]!.AsArray().Single(t => t!["graphId"]!.GetValue<string>() == owner.GraphId)!["modules"] = new System.Text.Json.Nodes.JsonArray();
                    inventory["tests"]!.AsArray().Single(t => t!["graphId"]!.GetValue<string>() == independent.GraphId)!["modules"] =
                        new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["graphId"] = module.GraphId, ["artifact"] = transferredId });
                    var transferred = ChangeBytes(requiredInventory.Id, System.Text.Encoding.UTF8.GetBytes(inventory.ToJsonString()));
                    transferred = transferred with { Manifest = ManifestIdentity.Seal(transferred.Manifest with { Artifacts = transferred.Manifest.Artifacts.Select(a =>
                        a.Id == module.Artifact ? a with { Id = transferredId } : a).ToArray() }) };
                    Refused(transferred);
                    }
                    Validate(bundle);
                    foreach (var required in currentPlan.Tests)
                    {
                        var context = currentCompiler.FinalGraph.GraphContexts.Single(b => b.GraphId == required.GraphId).Context;
                        Assert.Single(File.ReadAllLines(Path.Combine(Path.GetDirectoryName(context.EvaluatedTargetPath!)!, "test-count.txt")));
                    }
                    Assert.Equal("once", File.ReadAllText(Path.Combine(fixture.Path, "Independent/build-count.txt")).Trim());
                }
                if (savedResults)
                {
                    var type = typeof(App).Assembly.GetType("Crap4CSharp.Core.SavedRequiredTestResults");
                    Assert.NotNull(type);
                    var method = type.GetMethod("Validate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    Assert.NotNull(method);
                    var imported = FreshPriorBuild.Load(bundle, currentPlan.PreparedGraph, fixture.Path, token);
                    var currentCompiler = await CompilerGraphExecution.RunBoundAsync(reloadRequest, currentPlan.PreparedGraph,
                        (node, cancellation) => { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(imported[node.Id]); }, token);
                    void Validate(ArtifactBundle candidate)
                    {
                        try { method.Invoke(null, [candidate, currentPlan, currentCompiler, fixture.Path, token]); }
                        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
                        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
                    }
                    Validate(bundle);
                    Assert.Equal(2, parsedRequired.Count);
                    ArtifactBundle ChangeArtifact(string id, Func<ManifestArtifact, ManifestArtifact> change)
                        => bundle with { Manifest = ManifestIdentity.Seal(bundle.Manifest with
                            { Artifacts = bundle.Manifest.Artifacts.Select(a => a.Id == id ? change(a) : a).ToArray() }) };
                    ArtifactBundle ChangeBytes(string id, byte[] content)
                    {
                        var bytes = System.Collections.Immutable.ImmutableArray.Create(content);
                        var hash = CanonicalIdentity.Sha256(bytes.AsSpan());
                        var locator = "inputs/" + hash + ".bin";
                        var changed = ChangeArtifact(id, a => a with { Locator = locator, Length = bytes.Length, Sha256 = hash });
                        var dictionary = new Dictionary<string, System.Collections.Immutable.ImmutableArray<byte>>(bundle.Bytes) { [locator] = bytes };
                        return changed with { Bytes = dictionary };
                    }
                    void Refused(ArtifactBundle candidate)
                        => Assert.Equal("provenance.savedRequiredResultsUnproven", Assert.Throws<PolicyException>(() => Validate(candidate)).Code);
                    var savedInventoryId = requiredInventory.Id;
                    foreach (var savedTest in parsedRequired)
                    {
                        foreach (var id in new[] { savedTest.Trx, savedTest.Assembly, savedTest.Pdb, savedTest.Child })
                        {
                            var artifact = bundle.Manifest.Artifacts.Single(a => a.Id == id);
                            Refused(ChangeArtifact(id, a => a with { Kind = "unsupported-required-evidence" }));
                            Refused(ChangeArtifact(id, a => a with { Format = "unsupported" }));
                            Refused(ChangeArtifact(id, a => a with { CoordinateKind = "line" }));
                            Refused(ChangeArtifact(id, a => a with { ContextId = bundle.Manifest.Contexts[0].Id }));
                            Refused(ChangeArtifact(id, a => a with { BuildId = bundle.Manifest.Builds[0].Id }));
                            Refused(ChangeArtifact(id, a => a with { ExecutionId = bundle.Manifest.Executions[0].Id }));
                            var missing = new Dictionary<string, System.Collections.Immutable.ImmutableArray<byte>>(bundle.Bytes);
                            missing.Remove(artifact.Locator);
                            Refused(bundle with { Bytes = missing });
                        }
                        foreach (var field in new[] { "project", "moduleIdentity", "mvid", "debugIdentity", "passed" })
                        {
                            var inventory = System.Text.Json.Nodes.JsonNode.Parse(bundle.Bytes[requiredInventory.Locator].AsSpan())!;
                            var item = inventory["tests"]!.AsArray().Single(t => t!["graphId"]!.GetValue<string>() == savedTest.GraphId)!;
                            if (field == "passed") item[field] = 2; else item[field] = "unrelated";
                            Refused(ChangeBytes(savedInventoryId, System.Text.Encoding.UTF8.GetBytes(inventory.ToJsonString())));
                        }
                        var savedChild = bundle.Manifest.Artifacts.Single(a => a.Id == savedTest.Child);
                        var child = System.Text.Json.Nodes.JsonNode.Parse(bundle.Bytes[savedChild.Locator].AsSpan())!;
                        child["graphId"] = parsedRequired.Single(t => t.GraphId != savedTest.GraphId).GraphId;
                        Refused(ChangeBytes(savedTest.Child, System.Text.Encoding.UTF8.GetBytes(child.ToJsonString())));
                        child["graphId"] = savedTest.GraphId; child["rawExitCode"] = 1;
                        Refused(ChangeBytes(savedTest.Child, System.Text.Encoding.UTF8.GetBytes(child.ToJsonString())));
                        var trxArtifact = bundle.Manifest.Artifacts.Single(a => a.Id == savedTest.Trx);
                        var trxDocument = TestResultBinding.Parse(bundle.Bytes[trxArtifact.Locator]);
                        trxDocument.Descendants().First(e => e.Name.LocalName == "TestMethod").SetAttributeValue("codeBase",
                            Path.Combine(fixture.Path, "unrelated.dll"));
                        Refused(ChangeBytes(savedTest.Trx, System.Text.Encoding.UTF8.GetBytes(trxDocument.ToString())));
                        var other = parsedRequired.Single(t => t.GraphId != savedTest.GraphId);
                        Refused(ChangeBytes(savedTest.Assembly, bundle.Bytes[bundle.Manifest.Artifacts.Single(a => a.Id == other.Assembly).Locator].ToArray()));
                        Refused(ChangeBytes(savedTest.Pdb, bundle.Bytes[bundle.Manifest.Artifacts.Single(a => a.Id == other.Pdb).Locator].ToArray()));
                    }
                    Refused(bundle with { Manifest = ManifestIdentity.Seal(bundle.Manifest with { Artifacts = [.. bundle.Manifest.Artifacts,
                        bundle.Manifest.Artifacts.Single(a => a.Id == parsedRequired[0].Trx) with { Id = "required-test-orphan-trx" }] }) });
                    Refused(bundle with { Manifest = ManifestIdentity.Seal(bundle.Manifest with { Artifacts = bundle.Manifest.Artifacts
                        .Where(a => a.Id != parsedRequired[1].Trx).ToArray() }) });
                    var input = currentCompiler.FinalGraph.GraphContexts.Single(b => b.GraphId == parsedRequired[1].GraphId).Context.CompilerDocuments
                        .First(d => !d.IsGenerated).ResolvedPath!;
                    var originalInput = File.ReadAllBytes(input); var mtime = File.GetLastWriteTimeUtc(input);
                    try
                    {
                        File.AppendAllText(input, "\n// saved result drift"); File.SetLastWriteTimeUtc(input, mtime);
                        Refused(bundle);
                    }
                    finally { File.WriteAllBytes(input, originalInput); File.SetLastWriteTimeUtc(input, mtime); }
                    var observedPe = currentCompiler.Observations.Single(o => o.ProjectPath == currentPlan.Tests[0].ProjectPath).Assembly!.Path;
                    var originalPe = File.ReadAllBytes(observedPe); var peTime = File.GetLastWriteTimeUtc(observedPe);
                    try
                    {
                        var changedPe = originalPe.ToArray(); changedPe[^1] ^= 1;
                        File.WriteAllBytes(observedPe, changedPe); File.SetLastWriteTimeUtc(observedPe, peTime);
                        Refused(bundle);
                    }
                    finally { File.WriteAllBytes(observedPe, originalPe); File.SetLastWriteTimeUtc(observedPe, peTime); }
                    Validate(bundle);
                    foreach (var required in currentPlan.Tests)
                    {
                        var context = currentCompiler.FinalGraph.GraphContexts.Single(b => b.GraphId == required.GraphId).Context;
                        Assert.Single(File.ReadAllLines(Path.Combine(Path.GetDirectoryName(context.EvaluatedTargetPath!)!, "test-count.txt")));
                    }
                    Assert.Equal("once", File.ReadAllText(Path.Combine(fixture.Path, "Independent/build-count.txt")).Trim());
                }

                var replay = Measure("capturedReplay", () => AnalyzeCommand.Replay(bundle, fixture.Path, DateTimeOffset.UtcNow, TimeSpan.Zero, token, allSources: true));
                Assert.Equal(0, replay.Run.ExitCode);
                Assert.Equal("captured", replay.Evaluation.Provenance!.Status);
                var policy = Measure("purePolicy", () => FreshPolicyEvaluation.Evaluate(preflight, bundle.Manifest, replay));
                Assert.Equal(0, policy.Evaluation.ExitCode);
                Assert.Equal(emptyEligible ? 0 : 2, FreshPolicyEvaluation.Evaluate(preflight with
                    { Policy = preflight.Policy with { Threshold = .5 } }, bundle.Manifest, replay).Evaluation.ExitCode);
                Assert.Equal("captured", replay.Evaluation.Provenance.Status); // pure policy is not execution attestation
            }
            if (measure)
            {
                await MeasureAsync("finalRegate", async () => { await FreshManifestCapture.RegateAsync(evidence, token); return true; });
                var published = await MeasureAsync("requestedPublishWithRegate", () => FreshManifestCapture.PublishAsync(evidence,
                    measuredCapture!, "bundle", token));
                Assert.True(File.Exists(published));
                Assert.Equal(0, AnalyzeCommand.Replay(ArtifactBundle.Load(published, fixture.Path), fixture.Path,
                    DateTimeOffset.UtcNow, TimeSpan.Zero, token, allSources: true).Run.ExitCode);
                Assert.Equal(new[] { "trustedPreflight", "executionComposite", "captureWithRegates", "privatePublishWithRegate",
                    "bundleLoad", "capturedReplay", "purePolicy", "finalRegate", "requestedPublishWithRegate" }, timings.Keys);
                Assert.All(timings.Values, milliseconds => Assert.True(double.IsFinite(milliseconds) && milliseconds >= 0));
                Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Tests", "bin", "Debug", "net10.0", "test-count.txt")));
                Console.WriteLine("FRESH_COMPOSITE_TIMINGS " + JsonSerializer.Serialize(timings));
            }
            else Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
            Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Library", "build-count.txt")));
            Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Tests", "build-count.txt")));
            foreach (var (path, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(path));
            return;
        }
        var output = new StringWriter(); var errors = new StringWriter();
        string[] baseArgs = local ? [] : ["--base", "HEAD"];
        string[] publicArgs = ["check", "--project", "Tests/Tests.csproj", "--policy", "policy.json", .. baseArgs,
            "--artifact-output", "bundle", "--format", "json", "--timeout-seconds", "180", "--granularity", granularity, "--scope", policyScope];
        if (staleOutput || staleRawOutput) publicArgs = [.. publicArgs, "--output", "result.json"];
        if (includeGenerated) publicArgs = [.. publicArgs, "--include-generated"];
        var exit = await global::App.RunAsync(publicArgs, fixture.Path, output, errors, token);
        using var terminal = JsonDocument.Parse(output.ToString());
        AssertRetainedPrivateDiagnostics(terminal.RootElement, requireBuildLog: true);
        if (outputMutation is not null)
        {
            Assert.Equal(1, exit);
            Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
            Assert.Empty(terminal.RootElement.GetProperty("evaluation").GetProperty("metrics").EnumerateArray());
            Assert.False(terminal.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("completed").GetBoolean());
            Assert.True(output.ToString().Contains("compiler.inputsMutated", StringComparison.Ordinal) || output.ToString().Contains("provenance.localWorkspaceChanged", StringComparison.Ordinal), output.ToString());
            Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Library", "build-count.txt")));
            Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Tests", "build-count.txt")));
            var path = Path.Combine(fixture.Path, "Library/compiler-cache", outputMutation == "source" ? "Authored.cs" : "Rules.json");
            Assert.Contains("43", File.ReadAllText(path), StringComparison.Ordinal);
            File.WriteAllBytes(path, original[path]);
            foreach (var (input, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(input));
            return;
        }
        if (failing)
        {
            Assert.Equal(1, exit);
            if (partialDrift)
            {
                Assert.Empty(terminal.RootElement.GetProperty("evaluation").GetProperty("metrics").EnumerateArray());
                Assert.False(terminal.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("completed").GetBoolean());
                Assert.Contains(terminal.RootElement.GetProperty("run").GetProperty("testOutcomes").EnumerateArray(),
                    o => o.GetProperty("exitCode").GetInt32() == 1);
            }
            if (partial)
            {
                Assert.True(terminal.RootElement.GetProperty("evaluation").GetProperty("metrics").GetArrayLength() > 0, output.ToString());
                if (partialExclusion is not null)
                {
                    var findings = terminal.RootElement.GetProperty("evaluation").GetProperty("findings").EnumerateArray().ToArray();
                    if (partialExpectedExcluded) Assert.Empty(findings);
                    else Assert.Contains(findings, f => f.GetProperty("decision").GetString() == "fail" &&
                        f.GetProperty("path").GetString() == "Library/Code.cs");
                    Assert.Contains(terminal.RootElement.GetProperty("run").GetProperty("testOutcomes").EnumerateArray(),
                        o => o.GetProperty("exitCode").GetInt32() == 1);
                    Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
                }
                Assert.True(terminal.RootElement.GetProperty("evaluation").GetProperty("coverage").GetProperty("known").GetInt32() > 0);
                Assert.False(terminal.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("completed").GetBoolean());
                Assert.Equal("invalid", terminal.RootElement.GetProperty("evaluation").GetProperty("provenance").GetProperty("status").GetString());
                Assert.DoesNotContain(terminal.RootElement.GetProperty("run").GetProperty("artifacts").EnumerateArray(),
                    a => a.GetProperty("kind").GetString() == "manifest" || a.GetProperty("complete").GetBoolean() || a.GetProperty("reusable").GetBoolean());
            }
            Assert.Contains(terminal.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray(),
                c => c.GetProperty("name").GetString() == "testExecution" && c.GetProperty("status").GetString() == "fail");
        }
        else
        {
            Assert.True(exit == (thresholdViolation ? 2 : 0), errors + Environment.NewLine + output);
            Assert.True(File.Exists(Path.Combine(fixture.Path, "bundle", "manifest.json")));
            var bundle = ArtifactBundle.Load(Path.Combine(fixture.Path, "bundle", "manifest.json"), fixture.Path);
            Assert.Contains(bundle.Manifest.Artifacts, a => a.Kind == "test-child-build");
            Assert.DoesNotContain(bundle.Manifest.Artifacts, a => a.Kind == "binlog");
            if (thresholdViolation)
            {
                var evaluation = terminal.RootElement.GetProperty("evaluation");
                Assert.True(evaluation.GetProperty("decision").GetProperty("completed").GetBoolean());
                Assert.Equal("fail", evaluation.GetProperty("decision").GetProperty("policyDecision").GetString());
                var outcome = Assert.Single(terminal.RootElement.GetProperty("run").GetProperty("testOutcomes").EnumerateArray());
                Assert.Equal("completed", outcome.GetProperty("status").GetString());
                Assert.Equal(0, outcome.GetProperty("exitCode").GetInt32());
                var manifest = Assert.Single(terminal.RootElement.GetProperty("run").GetProperty("artifacts").EnumerateArray(),
                    a => a.GetProperty("kind").GetString() == "manifest");
                Assert.True(manifest.GetProperty("complete").GetBoolean());
                Assert.False(manifest.GetProperty("reusable").GetBoolean());
                Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Tests", "bin", "Debug", "net10.0", "test-count.txt")));
                var replay = AnalyzeCommand.Replay(bundle, fixture.Path, DateTimeOffset.UnixEpoch, TimeSpan.Zero, token, allSources: true);
                Assert.Equal(2, replay.Run.ExitCode);
                Assert.Equal("captured", replay.Evaluation.Provenance!.Status);
                using var replayJson = JsonDocument.Parse(ResultWriter.Serialize(replay));
                Assert.Equal(evaluation.GetProperty("metrics").GetRawText(), replayJson.RootElement.GetProperty("evaluation").GetProperty("metrics").GetRawText());
                // Captured engine findings use metric identities/category; trusted
                // policy findings use stable callable identities/category. Compare
                // source-level numeric decisions, not those intentional wrappers.
                string[] NormalizeFindings(JsonElement value) => value.EnumerateArray().Select(f =>
                    string.Join("|", new[] { "code", "severity", "contextId", "path", "span", "complexity",
                        "coverage", "crap", "threshold", "comparisonOperator", "decision" }
                        .Select(name => name == "path" ? JsonSerializer.Serialize(bundle.Manifest.Contexts
                            .Single(c => c.Id == f.GetProperty("contextId").GetString()).Inputs
                            .Where(i => i.Role == "source").Select(i => new { i.LogicalPath, RepositoryPath =
                                CapturedEvaluationInputs.DeclaredRepositorySourcePath(bundle.Manifest.Contexts
                                    .Single(c => c.Id == f.GetProperty("contextId").GetString()), i) })
                            .Single(i => i.LogicalPath == f.GetProperty("path").GetString() ||
                                i.RepositoryPath == f.GetProperty("path").GetString()).RepositoryPath)
                            : JsonSerializer.Serialize(f.GetProperty(name))))).Order(StringComparer.Ordinal).ToArray();
                Assert.Equal(NormalizeFindings(evaluation.GetProperty("findings")),
                    NormalizeFindings(replayJson.RootElement.GetProperty("evaluation").GetProperty("findings")));
                Assert.Equal("fail", replay.Evaluation.Decision.PolicyDecision);
            }
            if (scopedEmpty)
            {
                var checks = terminal.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray().ToArray();
                Assert.Contains(checks, c => c.GetProperty("name").GetString() == "testExecution" && c.GetProperty("status").GetString() == "pass");
                Assert.Contains(checks, c => c.GetProperty("name").GetString() == "coverage" && c.GetProperty("status").GetString() == "pass");
                Assert.Contains(checks, c => c.GetProperty("name").GetString() == "crap" && c.GetProperty("status").GetString() == "notApplicable" &&
                    c.GetProperty("reason").GetString() == "crap.noApplicableChangedCallables");
                Assert.True(terminal.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("completed").GetBoolean());
                // CRAP applicability is separate from the overall pure policy
                // decision: all required tests/evidence completed successfully.
                Assert.Equal("pass", terminal.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("policyDecision").GetString());
                Assert.NotEmpty(bundle.Manifest.Executions);
                Assert.Contains(bundle.Manifest.Artifacts, a => a.Kind == "coverage");
                var outcome = Assert.Single(terminal.RootElement.GetProperty("run").GetProperty("testOutcomes").EnumerateArray());
                Assert.Equal("completed", outcome.GetProperty("status").GetString());
                Assert.Equal(0, outcome.GetProperty("exitCode").GetInt32());
            }
            if (local)
            {
                Assert.False(Directory.Exists(Path.Combine(fixture.Path, ".git")));
                Assert.Equal("none", bundle.Manifest.Revision.Kind);
                Assert.Null(bundle.Manifest.Revision.Head);
                Assert.NotEmpty(bundle.Manifest.Revision.StateHash!);
                Assert.Equal("local-unreviewed", terminal.RootElement.GetProperty("evaluation").GetProperty("policyTrust").GetProperty("trust").GetString());
                Assert.Contains(terminal.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray(),
                    c => c.GetProperty("name").GetString() == "testExecution" && c.GetProperty("status").GetString() == "pass");
            }
            if (staleRawOutput)
            {
                var resultPath = Path.Combine(fixture.Path, "result.json");
                var saved = File.ReadAllBytes(resultPath);
                var testPath = Path.Combine(fixture.Path, "Tests/Tests.cs");
                File.WriteAllText(testPath, """
                    using Xunit;
                    public class Once { [Fact] public void Test() {
                        var project = System.IO.Path.GetFullPath("../../../../Library/Library.csproj", System.AppContext.BaseDirectory);
                        var original = System.IO.File.ReadAllText(project);
                        System.IO.File.WriteAllText(project, original.Replace("</Project>",
                            "<ItemGroup><AdditionalFiles Include=\"../result.json\" /></ItemGroup></Project>"));
                        Assert.Equal(99, Library.Value);
                    } }
                    """);
                if (replaceRawOutput)
                {
                    File.WriteAllText(testPath, """
                        using Xunit;
                        public class Once { [Fact] public void Test() {
                            var result = System.IO.Path.GetFullPath("../../../../result.json", System.AppContext.BaseDirectory);
                            System.IO.File.WriteAllText(result, "owned consumer bytes written during failed test");
                            Assert.Equal(99, Library.Value);
                        } }
                        """);
                    saved = System.Text.Encoding.UTF8.GetBytes("owned consumer bytes written during failed test");
                }
                var retryArgs = publicArgs.Where((_, index) => index != Array.IndexOf(publicArgs, "--artifact-output") &&
                    index != Array.IndexOf(publicArgs, "--artifact-output") + 1).ToArray();
                var text = new StringWriter(); var diagnostic = new StringWriter();
                Assert.Equal(1, await global::App.RunAsync(retryArgs, fixture.Path, text, diagnostic, token));
                using var failed = JsonDocument.Parse(text.ToString());
                AssertRetainedPrivateDiagnostics(failed.RootElement, requireBuildLog: true);
                Assert.Contains(failed.RootElement.GetProperty("run").GetProperty("testOutcomes").EnumerateArray(),
                    o => o.GetProperty("exitCode").GetInt32() == 1);
                Assert.False(failed.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("completed").GetBoolean());
                Assert.DoesNotContain(failed.RootElement.GetProperty("run").GetProperty("artifacts").EnumerateArray(),
                    a => a.GetProperty("kind").GetString() == "manifest");
                Assert.Equal(saved, File.ReadAllBytes(resultPath));
                Assert.Contains(failed.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray(),
                    c => c.GetProperty("reason").GetString() == "output.notWritten");
                return;
            }
            if (staleOutput)
            {
                var resultPath = Path.Combine(fixture.Path, "result.json");
                Assert.Equal(output.ToString(), File.ReadAllText(resultPath));
                var projectPath = Path.Combine(fixture.Path, "Library/Library.csproj");
                var projectText = File.ReadAllText(projectPath);
                var failureProject = projectText.Replace("</Project>",
                    "<Target Name=\"FailActualBuild\" BeforeTargets=\"CoreCompile\" Condition=\"'$(DesignTimeBuild)' != 'true'\"><Error Text=\"owned actual build failure\" /></Target></Project>", StringComparison.Ordinal);
                File.WriteAllText(projectPath, failureProject);
                var retryArgs = publicArgs.Where((_, index) => index != Array.IndexOf(publicArgs, "--artifact-output") &&
                    index != Array.IndexOf(publicArgs, "--artifact-output") + 1).ToArray();
                async Task<string> Retry(bool allocatedDiagnostics = true)
                {
                    var text = new StringWriter(); var diagnostic = new StringWriter();
                    Assert.Equal(1, await global::App.RunAsync(retryArgs, fixture.Path, text, diagnostic, token));
                    using var failed = JsonDocument.Parse(text.ToString());
                    Assert.False(failed.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("completed").GetBoolean());
                    if (allocatedDiagnostics) AssertRetainedPrivateDiagnostics(failed.RootElement, requireBuildLog: true);
                    else Assert.DoesNotContain(failed.RootElement.GetProperty("run").GetProperty("artifacts").EnumerateArray(),
                        a => a.GetProperty("kind").GetString() == "private-diagnostics");
                    return text.ToString();
                }
                var failedText = await Retry();
                Assert.Equal(failedText, File.ReadAllText(resultPath));
                using var failedResult = JsonDocument.Parse(failedText);
                Assert.DoesNotContain(failedResult.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray(),
                    c => c.GetProperty("reason").GetString() == "output.notWritten");
                // A schema-valid previous result is still a consumer input if the
                // current evaluated compiler graph explicitly selects its bytes.
                var saved = File.ReadAllBytes(resultPath);
                File.WriteAllText(projectPath, failureProject.Replace("</Project>",
                    "<ItemGroup><AdditionalFiles Include=\"../result.json\" /></ItemGroup></Project>", StringComparison.Ordinal));
                // This selected consumer-input alias is refused before private
                // execution storage is allocated, so no locator may be invented.
                var aliasText = await Retry(allocatedDiagnostics: false);
                Assert.Equal(saved, File.ReadAllBytes(resultPath));
                using var aliasResult = JsonDocument.Parse(aliasText);
                Assert.Contains(aliasResult.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray(),
                    c => c.GetProperty("reason").GetString() == "output.notWritten");
                File.WriteAllText(projectPath, projectText);
                return;
            }
            if (completedReuse)
            {
                var testCounter = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Path, "Tests", "bin"), "test-count.txt", SearchOption.AllDirectories));
                Assert.Single(File.ReadAllLines(testCounter));
                var locator = Assert.Single(terminal.RootElement.GetProperty("run").GetProperty("artifacts").EnumerateArray(),
                    a => a.GetProperty("kind").GetString() == "manifest").GetProperty("path").GetString()!;
                var reusedOutput = new StringWriter(); var reusedErrors = new StringWriter();
                var reusedExit = await global::App.RunAsync(["check", "--reuse-artifacts", locator, "--policy", "policy.json",
                    "--base", "HEAD", "--format", "json"], fixture.Path, reusedOutput, reusedErrors, token);
                // A reused check must never silently fall back to fresh consumer execution,
                // including when the currently supported recipe refuses this real capture.
                Assert.Single(File.ReadAllLines(testCounter));
                Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Library", "build-count.txt")));
                Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Tests", "build-count.txt")));
                Assert.True(reusedExit == 0, reusedErrors + Environment.NewLine + reusedOutput);
                using var reused = JsonDocument.Parse(reusedOutput.ToString());
                var freshEvaluation = terminal.RootElement.GetProperty("evaluation");
                var reusedEvaluation = reused.RootElement.GetProperty("evaluation");
                Assert.Equal(freshEvaluation.GetProperty("metrics").GetRawText(), reusedEvaluation.GetProperty("metrics").GetRawText());
                Assert.Equal(freshEvaluation.GetProperty("findings").GetRawText(), reusedEvaluation.GetProperty("findings").GetRawText());
                Assert.Equal(freshEvaluation.GetProperty("decision").GetProperty("policyDecision").GetString(),
                    reusedEvaluation.GetProperty("decision").GetProperty("policyDecision").GetString());
                Assert.True(reusedEvaluation.GetProperty("decision").GetProperty("completed").GetBoolean());
                Assert.Equal("verified", reusedEvaluation.GetProperty("provenance").GetProperty("status").GetString());
            }
            if (priorBuild)
            {
                var testCounter = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Path, "Tests", "bin"), "test-count.txt", SearchOption.AllDirectories));
                Assert.Single(File.ReadAllLines(testCounter));
                var secondOutput = new StringWriter(); var secondErrors = new StringWriter();
                var secondArgs = publicArgs.Select(a => a == "bundle" ? "bundle-next" : a)
                    .Concat(["--no-build", "--build-artifacts", "bundle/manifest.json"]).ToArray();
                var secondExit = await global::App.RunAsync(secondArgs, fixture.Path, secondOutput, secondErrors, token);
                Assert.True(secondExit == 0, secondErrors + Environment.NewLine + secondOutput);
                Assert.Equal(2, File.ReadAllLines(testCounter).Length);
                Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Library", "build-count.txt")));
                Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Tests", "build-count.txt")));
                var secondBundle = ArtifactBundle.Load(Path.Combine(fixture.Path, "bundle-next", "manifest.json"), fixture.Path);
                Assert.Equal(ProvenanceStatus.Captured, ProvenanceVerifier.VerifyCapture(secondBundle.Manifest, secondBundle.Bytes).Status);
                using var secondTerminal = JsonDocument.Parse(secondOutput.ToString());
                Assert.Contains(secondTerminal.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray(),
                    c => c.GetProperty("name").GetString() == "testExecution" && c.GetProperty("status").GetString() == "pass");
                Assert.Equal("priorCompilerFreshTestPostflightBinding",
                    secondTerminal.RootElement.GetProperty("evaluation").GetProperty("provenance").GetProperty("basis").GetString());
                var recipeArtifact = Assert.Single(bundle.Manifest.Artifacts, a => a.Kind == FreshPriorBuild.Kind);
                using var recipe = JsonDocument.Parse(bundle.Bytes[recipeArtifact.Locator].ToArray());
                var savedNode = recipe.RootElement.GetProperty("nodes").EnumerateArray().Single(n =>
                    n.GetProperty("id").GetString() == bundle.Manifest.Contexts.Single().Id);
                string SavedOutput(string name) => Path.GetFullPath(savedNode.GetProperty(name).GetProperty("path")
                    .GetProperty("relative").GetString()!, fixture.Path);
                async Task RejectMutation(string name, string path, bool add = false)
                {
                    var originalBytes = add ? null : File.ReadAllBytes(path);
                    try
                    {
                        if (add) File.WriteAllText(path, "public class NewCompilerInput {}");
                        else if (path.EndsWith(".cs", StringComparison.Ordinal)) File.AppendAllText(path, "\n// authored drift\n");
                        else { var changed = originalBytes!.ToArray(); changed[^1] ^= 1; File.WriteAllBytes(path, changed); }
                        var rejectedOutput = new StringWriter(); var rejectedErrors = new StringWriter();
                        var rejectedArgs = secondArgs.Select(a => a == "bundle-next" ? "bundle-rejected-" + name : a).ToArray();
                        var rejectedExit = await global::App.RunAsync(rejectedArgs, fixture.Path, rejectedOutput, rejectedErrors, token);
                        Assert.True(rejectedExit == 1, name + Environment.NewLine + rejectedOutput + rejectedErrors);
                        using var rejected = JsonDocument.Parse(rejectedOutput.ToString());
                        Assert.False(rejected.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("completed").GetBoolean());
                        Assert.Equal(2, File.ReadAllLines(testCounter).Length);
                        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Library", "build-count.txt")));
                        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Tests", "build-count.txt")));
                        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle-rejected-" + name)));
                    }
                    finally
                    {
                        if (add) File.Delete(path); // Only this newly created owned fixture input.
                        else File.WriteAllBytes(path, originalBytes!);
                    }
                }
                await RejectMutation("source", Path.Combine(fixture.Path, "Library", "Code.cs"));
                await RejectMutation("membership", Path.Combine(fixture.Path, "Library", "Added.cs"), add: true);
                await RejectMutation("test-source", Path.Combine(fixture.Path, "Tests", "Tests.cs"));
                await RejectMutation("pe", SavedOutput("assembly"));
                await RejectMutation("pdb", SavedOutput("pdb"));
                var copiedLibrary = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Path, "Tests", "bin"), "Library.dll", SearchOption.AllDirectories));
                await RejectMutation("runtime-copy", copiedLibrary);
                await RejectMutation("recipe", Path.GetFullPath(recipeArtifact.Locator, Path.Combine(fixture.Path, "bundle")));
            }
            if (hasGenerated)
            {
                var context = bundle.Manifest.Contexts.Single();
                Assert.Contains(context.Inputs, i => i.Generated && i.LogicalPath.EndsWith("Extra.cs", StringComparison.Ordinal));
                Assert.Equal(includeGenerated, context.IncludeGenerated);
                var replay = AnalyzeCommand.Replay(bundle, fixture.Path, DateTimeOffset.UtcNow, TimeSpan.Zero, token, allSources: true);
                if (includeGenerated)
                    Assert.Contains(replay.Evaluation.Callables!, c => c.SemanticSignature?.Contains("Extra", StringComparison.Ordinal) == true && c.Applicability == "applicable" && c.Coverage is not null);
                else Assert.DoesNotContain(replay.Evaluation.Callables!, c => c.SemanticSignature?.Contains("Extra", StringComparison.Ordinal) == true);
                var toggledContext = context with { IncludeGenerated = !includeGenerated };
                Assert.NotEqual(context.ContextHash, ManifestIdentity.ContextHash(toggledContext, context.SourceSetHash, context.InputClosureHash));
                var tampered = bundle.Manifest with { Contexts = [toggledContext] };
                var verification = ProvenanceVerifier.VerifyCapture(tampered, bundle.Bytes);
                Assert.Equal(ProvenanceStatus.Invalid, verification.Status);
                Assert.Contains(ProvenanceReasonCodes.ContextHashChanged, verification.Reasons);
                Assert.Contains(ProvenanceReasonCodes.ManifestHashChanged, verification.Reasons);
                if (policyScope == "all")
                    Assert.Equal(includeGenerated, replay.Evaluation.Scope.Sources.Any(s => s.EndsWith("Extra.cs", StringComparison.Ordinal)));
            }
            if (emptyEligible)
            {
                Assert.NotEmpty(bundle.Manifest.Executions);
                Assert.DoesNotContain(bundle.Manifest.Artifacts, a => a.Kind == "coverage");
                Assert.Equal(ProvenanceStatus.Captured, ProvenanceVerifier.VerifyCapture(bundle.Manifest, bundle.Bytes).Status);
                var hidden = System.Collections.Immutable.ImmutableArray.Create(System.Text.Encoding.UTF8.GetBytes(
                    "#line hidden\npublic static class Hidden { public static int M() => 42; }"));
                var forgedBytes = bundle.Bytes.ToDictionary(p => p.Key, p => p.Value);
                forgedBytes.Add("inputs/hidden.cs", hidden);
                var oldContext = bundle.Manifest.Contexts.Single();
                var forged = ManifestIdentity.Seal(bundle.Manifest with { Contexts = [oldContext with { Inputs = [.. oldContext.Inputs,
                    new ManifestInput("compiler-source", "Hidden.cs", "inputs/hidden.cs", hidden.Length,
                        CanonicalIdentity.Sha256(hidden.AsSpan()), "utf-8", false) { RepositoryPath = "Library/Hidden.cs" }] }] });
                Assert.Equal(ProvenanceStatus.Invalid, ProvenanceVerifier.VerifyCapture(forged, forgedBytes).Status);
                var checks = terminal.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray().ToArray();
                Assert.Contains(checks, c => c.GetProperty("name").GetString() == "testExecution" && c.GetProperty("status").GetString() == "pass");
                Assert.Contains(checks, c => c.GetProperty("name").GetString() == "coverage" && c.GetProperty("status").GetString() == "notApplicable");
                Assert.Contains(checks, c => c.GetProperty("name").GetString() == "crap" && c.GetProperty("status").GetString() == "notApplicable");
            }
        }
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Library", "build-count.txt")));
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Tests", "build-count.txt")));
        if (partialDrift)
        {
            var path = Path.Combine(fixture.Path, "Library", "Code.cs");
            Assert.Contains("43", File.ReadAllText(path), StringComparison.Ordinal);
            // Only the intentionally adversarial test body mutates this owned fixture.
            File.WriteAllBytes(path, original[path]);
        }
        foreach (var (path, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private static void AssertRetainedPrivateDiagnostics(JsonElement terminal, bool requireBuildLog)
    {
        var run = terminal.GetProperty("run");
        var artifact = Assert.Single(run.GetProperty("artifacts").EnumerateArray(),
            a => a.GetProperty("kind").GetString() == "private-diagnostics");
        var path = artifact.GetProperty("path").GetString()!;
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.True(Directory.Exists(path));
        Assert.StartsWith("crap4csharp-fresh-private-", Path.GetFileName(path), StringComparison.Ordinal);
        Assert.Equal("retained", artifact.GetProperty("retention").GetString());
        Assert.False(artifact.GetProperty("complete").GetBoolean());
        Assert.False(artifact.GetProperty("reusable").GetBoolean());
        Assert.Contains(run.GetProperty("diagnosticLocations").EnumerateArray(), p => p.GetString() == path);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
        if (requireBuildLog)
            Assert.NotEmpty(Directory.GetFiles(path, "compiler.binlog", SearchOption.AllDirectories));
    }
    private static void ValidateEligibilityContexts(ArtifactBundle bundle, CompilerGraphPhaseResult compiler,
        string root, bool empty, bool generated, CancellationToken token)
    {
        var gate = typeof(App).Assembly.GetType("Crap4CSharp.Core.SavedCompilerContexts")?.GetMethod("Validate",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(gate);
        void Agree(ArtifactBundle candidate)
        {
            try { gate.Invoke(null, [candidate, compiler, root, token]); }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
        }
        void Refuse(ArtifactBundle candidate)
            => Assert.Equal("provenance.savedCompilerContextsUnproven", Assert.Throws<PolicyException>(() => Agree(candidate)).Code);
        void Reject(ManifestContext changed)
            => Refuse(bundle with { Manifest = ManifestIdentity.Seal(bundle.Manifest with { Contexts = [changed] }) });
        Agree(bundle);
        var context = Assert.Single(bundle.Manifest.Contexts);
        Assert.Equal(empty ? "semantic-empty" : null, context.CoverageApplicability);
        Assert.Equal(generated, context.IncludeGenerated);
        Assert.False(context.ReuseRecipeComplete);
        var replay = AnalyzeCommand.Replay(bundle, root, DateTimeOffset.UtcNow, TimeSpan.Zero, token, allSources: true);
        Assert.Equal(0, replay.Run.ExitCode);
        if (empty) Assert.DoesNotContain(replay.Evaluation.Callables!, c => c.Applicability == "applicable");
        else Assert.Contains(replay.Evaluation.Callables!, c => c.SemanticSignature?.Contains("Extra", StringComparison.Ordinal) == true && c.Coverage is not null);
        Reject(context with { CoverageApplicability = empty ? null : "semantic-empty" });
        Reject(context with { IncludeGenerated = !generated });
        var source = context.Inputs.First(i => i.Role == "source" && !i.Generated);
        foreach (var changed in new[] { source with { Encoding = "utf-16" }, source with { LogicalPath = "foreign.cs" },
            source with { Generated = true }, source with { Role = "compiler-source" } })
            Reject(context with { Inputs = context.Inputs.Select(i => i == source ? changed : i).ToArray() });
        var reference = context.Inputs.First(i => i.Role == "reference");
        var otherReference = context.Inputs.First(i => i.Role == "reference" && i.Sha256 != reference.Sha256);
        Reject(context with { Inputs = context.Inputs.Where(i => i != reference).ToArray() });
        Reject(context with { Inputs = context.Inputs.Select(i => i == reference ? otherReference with { LogicalPath = reference.LogicalPath } : i).ToArray() });
        foreach (var artifact in bundle.Manifest.Artifacts.Where(a => a.Kind is "compiler-input-closure" or "test-compiler-input-closure" or "test-compiler-source" or "test-compiler-reference").GroupBy(a => a.Kind).Select(g => g.First()))
        {
            var bytes = bundle.Bytes[artifact.Locator].ToArray();
            if (artifact.Kind.EndsWith("closure", StringComparison.Ordinal))
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(bytes)!;
                var entries = (System.Text.Json.Nodes.JsonArray)(node[artifact.Kind == "compiler-input-closure" ? "metadata" : "inputs"]!);
                Assert.NotEmpty(entries); entries.RemoveAt(entries.Count - 1);
                bytes = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
            }
            else bytes[^1] ^= 1;
            var altered = System.Collections.Immutable.ImmutableArray.Create(bytes);
            var hash = CanonicalIdentity.Sha256(altered.AsSpan()); var locator = "inputs/" + hash + ".bin";
            Refuse(bundle with { Manifest = ManifestIdentity.Seal(bundle.Manifest with { Artifacts = bundle.Manifest.Artifacts.Select(a =>
                a.Id == artifact.Id ? a with { Locator = locator, Length = altered.Length, Sha256 = hash } : a).ToArray() }),
                Bytes = new Dictionary<string, System.Collections.Immutable.ImmutableArray<byte>>(bundle.Bytes) { [locator] = altered } });
        }
        var path = compiler.FinalGraph.GraphContexts.First(b => b.IsTestProject).Context.CompilerDocuments.First(d => !d.IsGenerated).ResolvedPath!;
        var original = File.ReadAllBytes(path); var timestamp = File.GetLastWriteTimeUtc(path);
        try
        {
            File.AppendAllText(path, "\n// preserved timestamp drift"); File.SetLastWriteTimeUtc(path, timestamp);
            Refuse(bundle);
        }
        finally { File.WriteAllBytes(path, original); File.SetLastWriteTimeUtc(path, timestamp); }
        Agree(bundle);
    }

}
