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

    private static async Task RunCase(bool failing, bool coordinatorOnly, bool emptyEligible = false, bool includeGenerated = false,
        bool generatedFixture = false, string policyScope = "base", string granularity = "method", bool local = false, bool priorBuild = false, bool partial = false, bool partialDrift = false, bool scopedEmpty = false, bool staleOutput = false, bool staleRawOutput = false, bool replaceRawOutput = false, string? outputLayout = null, string? outputMutation = null)
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
            (priorBuild ? "System.IO.File.AppendAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, \"test-count.txt\"), \"once\\n\"); " : "") +
            "Assert.Equal(" + (failing ? "99" : "42") + ", " + (emptyEligible ? "42" : hasGenerated ? "Extra.Value" : "Library.Value") + "); } }");
        fixture.Write("policy.json", """
            {"schemaVersion":"repository-policy-v1","mode":"strict","productionProjects":["Library/Library.csproj"],
            "testProjects":["Tests/Tests.csproj"],"configuration":"Debug","targetFrameworks":["net10.0"],"scope":"base",
            "threshold":8,"missingCoverage":"fail","requiredChecks":["tests","coverage","crap"],"exclusions":[],"ruleset":"callables-v1"}
            """.Replace("\"ruleset\":\"callables-v1\"", includeGenerated ?
                "\"ruleset\":\"callables-v1\",\"allowedOverrides\":{\"includeGenerated\":true}" : "\"ruleset\":\"callables-v1\"", StringComparison.Ordinal)
                .Replace("\"scope\":\"base\"", "\"scope\":\"" + policyScope + "\"", StringComparison.Ordinal));
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
            var request = new FreshCheckRequest("Tests/Tests.csproj", "policy.json", "HEAD", null, null, "worktree", "method",
                null, "bundle", false, false, null, new PolicyOverrides(), TimeSpan.FromMinutes(3));
            var preflight = await FreshCheckPreflightLoader.LoadAsync(request, fixture.Path, token);
            var evidence = await FreshCheckExecution.RunAsync(preflight, token);
            Assert.Equal(2, evidence.Compiler.Observations.Count);
            Assert.Single(evidence.Tests); Assert.Single(evidence.Tests.Outcomes);
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
                Assert.NotNull(evidence.Coverage); Assert.Single(evidence.SafeChildren);
                var captured = await FreshManifestCapture.CaptureAsync(evidence, token);
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
                var manifest = await FreshManifestCapture.PublishAsync(evidence, captured,
                    Path.Combine(evidence.PrivateDirectory, "safe-export"), token);
                if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.GetDirectoryName(manifest)!));
                var bundle = ArtifactBundle.Load(manifest, evidence.PrivateDirectory);
                Assert.Equal(ProvenanceStatus.Captured, ProvenanceVerifier.VerifyCapture(bundle.Manifest, bundle.Bytes).Status);
                var replay = AnalyzeCommand.Replay(bundle, fixture.Path, DateTimeOffset.UtcNow, TimeSpan.Zero, token, allSources: true);
                Assert.Equal(0, replay.Run.ExitCode);
                Assert.Equal("captured", replay.Evaluation.Provenance!.Status);
                var policy = FreshPolicyEvaluation.Evaluate(preflight, bundle.Manifest, replay);
                Assert.Equal(0, policy.Evaluation.ExitCode);
                Assert.Equal(2, FreshPolicyEvaluation.Evaluate(preflight with
                    { Policy = preflight.Policy with { Threshold = .5 } }, bundle.Manifest, replay).Evaluation.ExitCode);
                Assert.Equal("captured", replay.Evaluation.Provenance.Status); // pure policy is not execution attestation
            }
            Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
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
            Assert.True(exit == 0, errors + Environment.NewLine + output);
            Assert.True(File.Exists(Path.Combine(fixture.Path, "bundle", "manifest.json")));
            var bundle = ArtifactBundle.Load(Path.Combine(fixture.Path, "bundle", "manifest.json"), fixture.Path);
            Assert.Contains(bundle.Manifest.Artifacts, a => a.Kind == "test-child-build");
            Assert.DoesNotContain(bundle.Manifest.Artifacts, a => a.Kind == "binlog");
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
}
