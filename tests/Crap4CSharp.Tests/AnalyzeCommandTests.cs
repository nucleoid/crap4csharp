using System.Text;
using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class AnalyzeCommandTests
{
    private const string CompiledSource = "tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs";
    [Theory]
    [InlineData("--threshold", "999")]
    [InlineData("--callable-exemptions", "branch.json")]
    public async Task TrustedCheckRejectsBranchSuppliedPolicyWeakeningInputs(string option, string value)
    {
        using var directory = TestDirectory.Create("crap4csharp-check-override");
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await global::App.RunAsync(["check", "--reuse-artifacts", "manifest.json", "--policy",
            "policy.json", "--base", "origin/main", option, value, "--format", "json"], directory.Path,
            output, error, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Contains("cannot be combined", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrustedCheckRequiresExplicitCiAuthorizedBase()
    {
        using var directory = TestDirectory.Create("crap4csharp-check-base");
        var error = new StringWriter();

        var exit = await global::App.RunAsync(["check", "--reuse-artifacts", "manifest.json", "--policy",
            "policy.json", "--format", "json"], directory.Path, TextWriter.Null, error,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Contains("requires --policy and --base", error.ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public async Task TruncatedManifestReturnsOneStructuredJsonDocument()
    {
        using var directory = TestDirectory.Create("crap4csharp-truncated-manifest");
        var manifest = Path.Combine(directory.Path, "manifest.json");
        File.WriteAllText(manifest, "{\"manifestSchemaVersion\":");
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await global::App.RunAsync(
            ["analyze", "--reuse-artifacts", manifest, "--format", "json"],
            directory.Path, output, error, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, document.RootElement.GetProperty("run").GetProperty("exitCode").GetInt32());
        Assert.Equal("artifact.invalid", document.RootElement.GetProperty("evaluation")
            .GetProperty("decision").GetProperty("reason").GetString());
        Assert.DoesNotContain("JsonException", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatedManifestStillWritesRequestedResultOutsideBundle()
    {
        using var directory = TestDirectory.Create("crap4csharp-truncated-manifest-output");
        var bundle = Path.Combine(directory.Path, "bundle");
        Directory.CreateDirectory(bundle);
        var manifest = Path.Combine(bundle, "manifest.json");
        var destination = Path.Combine(directory.Path, "result.json");
        File.WriteAllText(manifest, "{\"manifestSchemaVersion\":");
        var output = new StringWriter();

        var exit = await global::App.RunAsync(
            ["analyze", "--reuse-artifacts", manifest, "--output", destination, "--format", "json"],
            directory.Path, output, TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.True(File.Exists(destination));
        Assert.Equal(output.ToString(), File.ReadAllText(destination));
        using var document = JsonDocument.Parse(File.ReadAllText(destination));
        Assert.Equal("artifact.invalid", document.RootElement.GetProperty("evaluation")
            .GetProperty("decision").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task SyntaxOnlyRejectsProjectInsteadOfIgnoringIt()
    {
        using var directory = TestDirectory.Create("crap4csharp-syntax-project");
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await global::App.RunAsync(
            ["analyze", "--syntax-only", "--project", "App.csproj", "--format", "json"],
            directory.Path, output, error, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Contains("--project", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapturedReplayUsesOnlyBundleBytesAndReportsCapturedProvenance()
    {
        using var directory = TestDirectory.Create("crap4csharp-captured-analyze");
        var manifest = CreateBundle(directory.Path);
        var original = Directory.GetCurrentDirectory();
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await global::App.RunAsync(["analyze", "--reuse-artifacts", manifest, "--format", "json"],
            directory.Path, output, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Empty(error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        var provenance = json.RootElement.GetProperty("evaluation").GetProperty("provenance");
        Assert.Equal("captured", provenance.GetProperty("status").GetString());
        Assert.False(provenance.GetProperty("postflightVerified").GetBoolean());
        var provenanceCheck = json.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "provenance");
        Assert.Equal("pass", provenanceCheck.GetProperty("status").GetString());
        Assert.Equal("provenance.captureConsistent", provenanceCheck.GetProperty("reason").GetString());
        Assert.Equal(original, Directory.GetCurrentDirectory());
    }

    [Theory]
    [InlineData("--coverage")]
    [InlineData("--project")]
    [InlineData("--coverage-path-case")]
    public async Task CapturedReplayRejectsLiveOverrides(string option)
    {
        using var directory = TestDirectory.Create("crap4csharp-captured-overrides");
        var manifest = CreateBundle(directory.Path);
        var value = option == "--coverage-path-case" ? "sensitive" : "anything";
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await global::App.RunAsync(["analyze", "--reuse-artifacts", manifest, option, value,
            "--format", "json"], directory.Path, output, error, TestContext.Current.CancellationToken);
        Assert.Equal(1, exit);
        Assert.Contains("cannot be combined", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapturedReplayRejectsALiveThresholdOverride()
    {
        using var directory = TestDirectory.Create("crap4csharp-captured-threshold-override");
        var manifest = CreateBundle(directory.Path);
        var error = new StringWriter();

        var exit = await global::App.RunAsync(
            ["analyze", "--reuse-artifacts", manifest, "--threshold", "99", "--format", "json"],
            directory.Path, TextWriter.Null, error, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Contains("cannot be combined", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapturedPolicyThresholdControlsTheReplayDecision()
    {
        using var directory = TestDirectory.Create("crap4csharp-captured-policy");
        var manifestPath = CreateBundle(directory.Path);
        ReplaceEvaluationArtifact(manifestPath, "policy",
            Encoding.UTF8.GetBytes("""{"version":1,"threshold":0,"allowMissingCoverage":false}"""));
        var output = new StringWriter();

        var exit = await global::App.RunAsync(
            ["analyze", "--reuse-artifacts", manifestPath, "--format", "json"],
            directory.Path, output, TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(2, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, document.RootElement.GetProperty("evaluation").GetProperty("policy")
            .GetProperty("threshold").GetDouble());
        Assert.NotEmpty(document.RootElement.GetProperty("evaluation").GetProperty("findings").EnumerateArray());
    }

    [Fact]
    public void TrustedReplayThresholdControlsReportsWithoutChangingCapturedAnalyze()
    {
        using var directory = TestDirectory.Create("crap4csharp-trusted-report-threshold");
        var path = CreateBundle(directory.Path);
        var bundle = ArtifactBundle.Load(path, directory.Path);
        var trusted = AnalyzeCommand.Replay(bundle, directory.Path, DateTimeOffset.UnixEpoch,
            TimeSpan.Zero, TestContext.Current.CancellationToken, allSources: true, thresholdOverride: 0);
        var captured = AnalyzeCommand.Replay(bundle, directory.Path, DateTimeOffset.UnixEpoch,
            TimeSpan.Zero, TestContext.Current.CancellationToken);
        Assert.Equal(0, trusted.Evaluation.Policy.Threshold);
        Assert.NotEmpty(trusted.Evaluation.Findings);
        Assert.All(trusted.Evaluation.Findings, finding => Assert.Equal(0, finding.Threshold));
        Assert.Equal(8, captured.Evaluation.Policy.Threshold);
        Assert.Empty(captured.Evaluation.Findings);
        Assert.Equal(captured.Evaluation.Metrics, trusted.Evaluation.Metrics);
    }

    [Fact]
    public async Task CapturedScopeFiltersSourcesRatherThanUsingTheLiveDefault()
    {
        using var directory = TestDirectory.Create("crap4csharp-captured-scope");
        var manifestPath = CreateBundle(directory.Path);
        ReplaceEvaluationArtifact(manifestPath, "scope",
            Encoding.UTF8.GetBytes("""{"version":1,"sources":[]}"""));
        var output = new StringWriter();

        var exit = await global::App.RunAsync(
            ["analyze", "--reuse-artifacts", manifestPath, "--format", "json"],
            directory.Path, output, TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Empty(document.RootElement.GetProperty("evaluation").GetProperty("metrics").EnumerateArray());
        Assert.Equal("notApplicable", document.RootElement.GetProperty("evaluation")
            .GetProperty("decision").GetProperty("policyDecision").GetString());
    }

    [Fact]
    public async Task NonIntegralCapturedVersionReturnsStructuredArtifactFailure()
    {
        using var directory = TestDirectory.Create("crap4csharp-captured-version-shape");
        var manifestPath = CreateBundle(Path.Combine(directory.Path, "bundle"));
        ReplaceEvaluationArtifact(manifestPath, "policy",
            Encoding.UTF8.GetBytes("""{"version":1.5,"threshold":8,"allowMissingCoverage":false}"""));
        var destination = Path.Combine(directory.Path, "failure.json");
        var output = new StringWriter();

        var exit = await global::App.RunAsync(
            ["analyze", "--reuse-artifacts", manifestPath, "--output", destination, "--format", "json"],
            directory.Path, output, TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.True(File.Exists(destination));
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("artifact.invalid", document.RootElement.GetProperty("evaluation")
            .GetProperty("decision").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InvalidEvaluationArtifactPreservesVerifierReasonsWithoutParsingIt()
    {
        using var directory = TestDirectory.Create("crap4csharp-invalid-policy-reasons");
        var manifestPath = CreateBundle(directory.Path);
        var policyPath = Path.Combine(directory.Path, "artifacts", "policy.json");
        var malformed = new byte[new FileInfo(policyPath).Length];
        Array.Fill(malformed, (byte)0xff);
        File.WriteAllBytes(policyPath, malformed);
        var output = new StringWriter();

        var exit = await global::App.RunAsync(
            ["analyze", "--reuse-artifacts", manifestPath, "--format", "json"],
            directory.Path, output, TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(output.ToString());
        var provenance = document.RootElement.GetProperty("evaluation").GetProperty("provenance");
        Assert.Equal("invalid", provenance.GetProperty("status").GetString());
        Assert.Contains(provenance.GetProperty("reasons").EnumerateArray(),
            reason => reason.GetString() == ProvenanceReasonCodes.ArtifactChanged);
    }

    [Fact]
    public async Task ReplayOutputCannotAliasAnyBundleEntry()
    {
        using var directory = TestDirectory.Create("crap4csharp-output-alias");
        var manifest = CreateBundle(directory.Path);
        var source = Path.Combine(directory.Path, "artifacts", "source.bin");
        var before = File.ReadAllBytes(source);
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await global::App.RunAsync(
            ["analyze", "--reuse-artifacts", manifest, "--output", source, "--format", "json"],
            directory.Path, output, error, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Equal(before, File.ReadAllBytes(source));
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("artifact.invalid", result.RootElement.GetProperty("evaluation")
            .GetProperty("decision").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ReplayEvaluatesEveryCapturedContextIndependently()
    {
        using var directory = TestDirectory.Create("crap4csharp-multi-context");
        var path = CreateBundle(directory.Path);
        var manifest = ReadManifest(path);
        var second = manifest.Contexts[0] with { Id = "ctx-2", TargetFramework = "net9.0" };
        var build = manifest.Builds[0] with { Id = "build-2", ContextId = "ctx-2" };
        var execution = manifest.Executions[0] with
        { Id = "test-2", ContextId = "ctx-2", BuildId = "build-2" };
        var secondArtifacts = manifest.Artifacts.Where(item => item.ContextId == "ctx" && item.Kind != "source").Select(item => item with
        {
            Id = item.Id + "-2",
            ContextId = "ctx-2",
            BuildId = "build-2",
            ExecutionId = item.ExecutionId is null ? null : "test-2"
        }).ToArray();
        manifest = ManifestIdentity.Seal(manifest with
        {
            Contexts = [manifest.Contexts[0], second],
            Builds = [manifest.Builds[0], build],
            Executions = [manifest.Executions[0], execution],
            Artifacts = [.. manifest.Artifacts, .. secondArtifacts],
            ManifestHash = null
        });
        WriteManifest(path, manifest);
        var output = new StringWriter();

        var exit = await global::App.RunAsync(["analyze", "--reuse-artifacts", path, "--format", "json"],
            directory.Path, output, TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        using var result = JsonDocument.Parse(output.ToString());
        var contexts = result.RootElement.GetProperty("evaluation").GetProperty("contexts")
            .EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(["ctx", "ctx-2"], contexts);
        Assert.Equal(2, result.RootElement.GetProperty("evaluation").GetProperty("metrics").GetArrayLength());
    }

    [Fact]
    public async Task CallablesManifestUsesModernInventoryIncludingConstructors()
    {
        using var directory = TestDirectory.Create("crap4csharp-callables-replay");
        var path = CreateBundle(directory.Path);
        var coverage = Encoding.UTF8.GetBytes("""
            <coverage><packages><package name="Crap4CSharp.ProvenanceFixture"><classes>
            <class name="Crap4CSharp.ProvenanceFixture.CompiledEvidence" filename="tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs">
            <methods>
              <method name=".ctor" signature="()"><lines><line number="5" hits="1" /></lines></method>
              <method name="M" signature="()"><lines><line number="9" hits="1" /><line number="10" hits="1" /></lines></method>
            </methods>
            </class></classes></package></packages></coverage>
            """);
        File.WriteAllBytes(Path.Combine(directory.Path, "artifacts", "coverage.xml"), coverage);
        var manifest = ReadManifest(path);
        var artifacts = manifest.Artifacts.Select(item => item.Kind switch
        {
            "coverage" => item with { Length = coverage.Length, Sha256 = CanonicalIdentity.Sha256(coverage) },
            _ => item
        }).ToArray();
        manifest = ManifestIdentity.Seal(manifest with
        {
            Producer = manifest.Producer with { ComplexityRuleset = ComplexityRules.CallablesV1 },
            Artifacts = artifacts,
            ManifestHash = null
        });
        WriteManifest(path, manifest);
        var output = new StringWriter();

        var exit = await global::App.RunAsync(["analyze", "--reuse-artifacts", path, "--format", "json"],
            directory.Path, output, TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        using var result = JsonDocument.Parse(output.ToString());
        var callables = result.RootElement.GetProperty("evaluation").GetProperty("callables").EnumerateArray().ToArray();
        Assert.Contains(callables, callable => callable.GetProperty("kind").GetString() == "Constructor");
        Assert.All(callables, callable => Assert.Equal("callables-v1", callable.GetProperty("ruleset").GetString()));
    }

    private static string CreateBundle(string root)
    {
        var artifacts = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(artifacts);
        var source = CompiledSourceBytes();
        var coverage = Encoding.UTF8.GetBytes("""
            <coverage><packages><package name="Crap4CSharp.ProvenanceFixture"><classes>
            <class name="Crap4CSharp.ProvenanceFixture.CompiledEvidence" filename="tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs">
            <methods><method name="M" signature="()"><lines><line number="9" hits="1" /><line number="10" hits="1" /></lines></method></methods>
            </class></classes></package></packages></coverage>
            """);
        var assemblyPath = typeof(Crap4CSharp.ProvenanceFixture.CompiledEvidence).Assembly.Location;
        var assembly = File.ReadAllBytes(assemblyPath);
        var pdb = File.ReadAllBytes(Path.ChangeExtension(assemblyPath, ".pdb"));
        var inspected = ArtifactEvidenceInspector.InspectBuild(
            System.Collections.Immutable.ImmutableArray.Create(assembly),
            System.Collections.Immutable.ImmutableArray.Create(pdb));
        var trx = Encoding.UTF8.GetBytes("""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <TestDefinitions><UnitTest storage="Crap4CSharp.ProvenanceFixture.dll" /></TestDefinitions>
              <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0"
                error="0" timeout="0" aborted="0" inconclusive="0" notExecuted="0" /></ResultSummary>
            </TestRun>
            """);
        var scope = Encoding.UTF8.GetBytes($$"""{"version":1,"sources":["{{CompiledSource}}"]}""");
        var policy = Encoding.UTF8.GetBytes("""{"version":1,"threshold":8,"allowMissingCoverage":false}""");
        File.WriteAllBytes(Path.Combine(artifacts, "source.bin"), source);
        File.WriteAllBytes(Path.Combine(artifacts, "coverage.xml"), coverage);
        File.WriteAllBytes(Path.Combine(artifacts, "app.dll"), assembly);
        File.WriteAllBytes(Path.Combine(artifacts, "app.pdb"), pdb);
        File.WriteAllBytes(Path.Combine(artifacts, "results.trx"), trx);
        File.WriteAllBytes(Path.Combine(artifacts, "scope.json"), scope);
        File.WriteAllBytes(Path.Combine(artifacts, "policy.json"), policy);
        var context = new ManifestContext("ctx", "missing/App.csproj", "net10.0", "Debug", "AnyCPU",
            "sources", "context", "closure", true, true,
            [new ManifestInput("source", CompiledSource, "artifacts/source.bin", source.Length,
                CanonicalIdentity.Sha256(source), "utf-8", false)])
        {
            ParseOptions = new ManifestParseOptions(inspected.LanguageVersion, "Regular",
                inspected.PreprocessorSymbols,
                new Dictionary<string, string>(StringComparer.Ordinal)),
            PathPolicy = new ManifestPathPolicy("sensitive", [new ManifestReportRootMapping("/_/", "")])
        };
        var manifest = new RunManifest("1.0", CanonicalIdentity.Algorithm,
            new ManifestProducer("crap4csharp", "0.1.0", ComplexityRules.OrdinaryMethodsV1,
                ProjectAnalysisContext.ProtocolVersion, "coverage-v1", "paths-v1"),
            new ManifestCapture("completed", true, true, []),
            new ManifestRevision("none", "local", "deleted-original", null, null, null),
            [new ManifestRoot("workspace", "workspace", "sensitive")], [context],
            [new ManifestBuild("build", "ctx", inspected.ModuleIdentity, CanonicalIdentity.Sha256(assembly),
                inspected.Mvid, CanonicalIdentity.Sha256(pdb), inspected.DebugIdentity)],
            [new ManifestExecution("test", "ctx", "build", true, 0, 1, 1, 0, 0)
            {
                TestModuleIdentity = inspected.ModuleIdentity,
                TestAssemblySha256 = CanonicalIdentity.Sha256(assembly),
                TestMvid = inspected.Mvid,
                TestPdbSha256 = CanonicalIdentity.Sha256(pdb),
                TestDebugIdentity = inspected.DebugIdentity
            }],
            [new ManifestArtifact("source", "source", "artifacts/source.bin", source.Length,
                 CanonicalIdentity.Sha256(source), "ctx", null, null, null, null),
             new ManifestArtifact("coverage", "coverage", "artifacts/coverage.xml", coverage.Length,
                 CanonicalIdentity.Sha256(coverage), "ctx", "build", "test", "cobertura", "line"),
             new ManifestArtifact("assembly", "assembly", "artifacts/app.dll", assembly.Length,
                 CanonicalIdentity.Sha256(assembly), "ctx", "build", null, null, null),
             new ManifestArtifact("pdb", "pdb", "artifacts/app.pdb", pdb.Length,
                 CanonicalIdentity.Sha256(pdb), "ctx", "build", null, null, null),
             new ManifestArtifact("test-result", "test-result", "artifacts/results.trx", trx.Length,
                 CanonicalIdentity.Sha256(trx), "ctx", "build", "test", "trx", null),
             new ManifestArtifact("test-assembly", "test-assembly", "artifacts/app.dll", assembly.Length,
                 CanonicalIdentity.Sha256(assembly), "ctx", "build", "test", null, null),
             new ManifestArtifact("test-pdb", "test-pdb", "artifacts/app.pdb", pdb.Length,
                 CanonicalIdentity.Sha256(pdb), "ctx", "build", "test", null, null),
             new ManifestArtifact("scope", "scope", "artifacts/scope.json", scope.Length,
                 CanonicalIdentity.Sha256(scope), null, null, null, "json", null),
             new ManifestArtifact("policy", "policy", "artifacts/policy.json", policy.Length,
                 CanonicalIdentity.Sha256(policy), null, null, null, "json", null)],
            new ManifestEvaluationInputs(CanonicalIdentity.Sha256(scope), CanonicalIdentity.Sha256(policy), null, null), null);
        manifest = ManifestIdentity.Seal(manifest);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        var path = Path.Combine(root, "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, options));
        return path;
    }

    private static RunManifest ReadManifest(string path) => JsonSerializer.Deserialize<RunManifest>(
        File.ReadAllBytes(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static void WriteManifest(string path, RunManifest manifest) => File.WriteAllText(path,
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

    private static void ReplaceEvaluationArtifact(string manifestPath, string kind, byte[] bytes)
    {
        var manifest = ReadManifest(manifestPath);
        var artifact = manifest.Artifacts.Single(item => item.Kind == kind);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(manifestPath)!,
            artifact.Locator.Replace('/', Path.DirectorySeparatorChar)), bytes);
        var hash = CanonicalIdentity.Sha256(bytes);
        var artifacts = manifest.Artifacts.Select(item => item.Id == artifact.Id
            ? item with { Length = bytes.Length, Sha256 = hash } : item).ToArray();
        var evaluation = kind switch
        {
            "scope" => manifest.EvaluationInputs with { ScopeHash = hash },
            "policy" => manifest.EvaluationInputs with { PolicyHash = hash },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        WriteManifest(manifestPath, ManifestIdentity.Seal(manifest with
        { Artifacts = artifacts, EvaluationInputs = evaluation, ManifestHash = null }));
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
}
