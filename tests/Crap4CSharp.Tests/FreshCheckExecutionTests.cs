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

    [Fact]
    public Task SemanticEmptyProductionStillRunsRequiredTestsAndReportsExplicitNA()
        => RunCase(false, false, true);

    [Fact]
    public Task NonGitAllScopeFreshCheckRunsRequiredContextsWithLocalUnreviewedTrustAndNoGitRevision()
        => RunCase(false, false, policyScope: "all", local: true);

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
        bool generatedFixture = false, string policyScope = "base", string granularity = "method", bool local = false, bool priorBuild = false)
    {
        var hasGenerated = includeGenerated || generatedFixture;
        using var fixture = TestDirectory.Create("crap4csharp-public-cold-check");
        const string counter = """
            <Target Name="CountActualCompile" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-count.txt" Lines="once" Overwrite="false" /></Target>
            """;
        const string privateCanary = "fresh-private-property-never-export-7264c8e";
        fixture.Write("Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            (coordinatorOnly ? "<PrivateCanary>" + privateCanary + "</PrivateCanary>" : "") + "</PropertyGroup>" + counter +
            (hasGenerated ? "<Target Name=\"AddGenerated\" BeforeTargets=\"CoreCompile\"><ItemGroup><Compile Include=\"obj/Extra.cs\" /></ItemGroup></Target>" : "") + "</Project>");
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
            (priorBuild ? "System.IO.File.AppendAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, \"test-count.txt\"), \"once\\n\"); " : "") +
            "Assert.Equal(" + (failing ? "99" : "42") + ", " + (emptyEligible ? "42" : hasGenerated ? "Extra.Value" : "Library.Value") + "); } }");
        fixture.Write("policy.json", """
            {"schemaVersion":"repository-policy-v1","mode":"strict","productionProjects":["Library/Library.csproj"],
            "testProjects":["Tests/Tests.csproj"],"configuration":"Debug","targetFrameworks":["net10.0"],"scope":"base",
            "threshold":8,"missingCoverage":"fail","requiredChecks":["tests","coverage","crap"],"exclusions":[],"ruleset":"callables-v1"}
            """.Replace("\"ruleset\":\"callables-v1\"", includeGenerated ?
                "\"ruleset\":\"callables-v1\",\"allowedOverrides\":{\"includeGenerated\":true}" : "\"ruleset\":\"callables-v1\"", StringComparison.Ordinal)
                .Replace("\"scope\":\"base\"", "\"scope\":\"" + policyScope + "\"", StringComparison.Ordinal));
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
        if (includeGenerated) publicArgs = [.. publicArgs, "--include-generated"];
        var exit = await global::App.RunAsync(publicArgs, fixture.Path, output, errors, token);
        using var terminal = JsonDocument.Parse(output.ToString());
        if (failing)
        {
            Assert.Equal(1, exit);
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
        foreach (var (path, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
