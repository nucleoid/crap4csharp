using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class PolicyCheckIntegrationTests
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
    private const string PolicyLogical = "tests/Fixtures/policy-check/policy.json";
#else
    private const string BuildConfiguration = "Release";
    private const string PolicyLogical = "tests/Fixtures/policy-check/policy-release.json";
#endif

    [Fact]
    public async Task TrustedCheckRunsEndToEndAgainstFreshCurrentProjectEvidence()
    {
        var repository = RepositoryRoot();
        const string projectLogical = "tests/Crap4CSharp.ProvenanceFixture/Crap4CSharp.ProvenanceFixture.csproj";
        var project = Path.Combine(repository, projectLogical.Replace('/', Path.DirectorySeparatorChar));
        var projectDirectory = Path.GetDirectoryName(project)!;
        var request = new ProjectContextLoadRequest(project, BuildConfiguration, "AnyCPU", ["net10.0"], true, true,
            TimeSpan.FromMinutes(2));
        var loaded = await ProjectContextLoader.LoadAsync(request, TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, loaded.FailureReason ?? string.Join(Environment.NewLine, loaded.Diagnostics));
        var context = Assert.Single(loaded.Contexts);
        var assemblyPath = typeof(Crap4CSharp.ProvenanceFixture.CompiledEvidence).Assembly.Location;
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        var assembly = ImmutableArray.Create(await File.ReadAllBytesAsync(assemblyPath, TestContext.Current.CancellationToken));
        var pdb = ImmutableArray.Create(await File.ReadAllBytesAsync(pdbPath, TestContext.Current.CancellationToken));
        var inspected = ArtifactEvidenceInspector.InspectBuild(assembly, pdb);
        var bytes = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        var inputs = new List<ManifestInput>();
        var index = 0;
        foreach (var source in context.Sources.OrderBy(item => item.LogicalPath, StringComparer.Ordinal))
        {
            var physical = Path.GetFullPath(source.PhysicalPath, projectDirectory);
            var value = ImmutableArray.Create(await File.ReadAllBytesAsync(physical, TestContext.Current.CancellationToken));
            var sourceLocator = $"inputs/source-{index++}.bin";
            bytes.Add(sourceLocator, value);
            inputs.Add(new ManifestInput("source", source.LogicalPath, sourceLocator, value.Length,
                CanonicalIdentity.Sha256(value.AsSpan()), "utf-8", source.IsGenerated));
        }

        var coverage = ImmutableArray.Create(Encoding.UTF8.GetBytes(
            "<coverage><packages><package name=\"Crap4CSharp.ProvenanceFixture\"><classes><class name=\"Crap4CSharp.ProvenanceFixture.CompiledEvidence\" filename=\"CompiledEvidence.cs\"><methods><method name=\".ctor\" signature=\"()\"><lines><line number=\"5\" hits=\"1\" /></lines></method><method name=\"M\" signature=\"()\"><lines><line number=\"9\" hits=\"1\" /><line number=\"10\" hits=\"1\" /></lines></method></methods></class></classes></package></packages></coverage>"));
        var trx = ImmutableArray.Create(Encoding.UTF8.GetBytes($$"""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <TestDefinitions><UnitTest storage="{{inspected.ModuleIdentity}}.dll" /></TestDefinitions>
              <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0"
                error="0" timeout="0" aborted="0" inconclusive="0" notExecuted="0" /></ResultSummary>
            </TestRun>
            """));
        var scope = ImmutableArray.Create(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { version = 1, sources = inputs.Where(item => !item.Generated).Select(item => item.LogicalPath).ToArray() })));
        var policyBytes = await File.ReadAllBytesAsync(Path.Combine(repository,
            PolicyLogical.Replace('/', Path.DirectorySeparatorChar)), TestContext.Current.CancellationToken);
        var policy = RepositoryPolicyParser.Parse(policyBytes, PolicyLogical);
        var canonicalPolicy = ImmutableArray.Create(policy.CanonicalBytes);
        foreach (var item in new[]
        {
            ("evidence/app.dll", assembly), ("evidence/app.pdb", pdb), ("evidence/test.dll", assembly),
            ("evidence/test.pdb", pdb), ("evidence/results.trx", trx), ("evidence/coverage.xml", coverage),
            ("evaluation/scope.json", scope), ("evaluation/policy.json", canonicalPolicy)
        }) bytes.Add(item.Item1, item.Item2);

        var manifestContext = new ManifestContext(context.ContextId, projectLogical, context.TargetFramework,
            context.Configuration, context.Platform, "", "", "", true, true, inputs)
        {
            ParseOptions = new ManifestParseOptions(inspected.LanguageVersion, context.SourceKind.ToString(),
                inspected.PreprocessorSymbols, new Dictionary<string, string>()),
            PathPolicy = new ManifestPathPolicy("sensitive",
                [new ManifestReportRootMapping("/_/tests/Crap4CSharp.ProvenanceFixture", "")]),
            CurrentRevalidation = new ManifestCurrentRevalidation(CurrentEvidenceAdapter.SupportedRecipeProvider,
                projectLogical, Path.GetRelativePath(repository, assemblyPath).Replace('\\', '/'),
                Path.GetRelativePath(repository, pdbPath).Replace('\\', '/'))
        };
        var build = new ManifestBuild("build", context.ContextId, inspected.ModuleIdentity,
            CanonicalIdentity.Sha256(assembly.AsSpan()), inspected.Mvid, CanonicalIdentity.Sha256(pdb.AsSpan()),
            inspected.DebugIdentity);
        var execution = new ManifestExecution("test", context.ContextId, build.Id, true, 0, 1, 1, 0, 0)
        {
            TestProject = projectLogical, TestModuleIdentity = inspected.ModuleIdentity,
            TestAssemblySha256 = CanonicalIdentity.Sha256(assembly.AsSpan()), TestMvid = inspected.Mvid,
            TestPdbSha256 = CanonicalIdentity.Sha256(pdb.AsSpan()), TestDebugIdentity = inspected.DebugIdentity
        };
        ManifestArtifact Artifact(string id, string kind, string locator, ImmutableArray<byte> value,
            string? contextId = null, string? buildId = null, string? executionId = null, string? format = null,
            string? coordinate = null) => new(id, kind, locator, value.Length, CanonicalIdentity.Sha256(value.AsSpan()),
                contextId, buildId, executionId, format, coordinate);
        var artifacts = new[]
        {
            Artifact("scope", "scope", "evaluation/scope.json", scope),
            Artifact("policy", "policy", "evaluation/policy.json", canonicalPolicy),
            Artifact("assembly", "assembly", "evidence/app.dll", assembly, context.ContextId, build.Id),
            Artifact("pdb", "pdb", "evidence/app.pdb", pdb, context.ContextId, build.Id),
            Artifact("test-assembly", "test-assembly", "evidence/test.dll", assembly, context.ContextId, build.Id, execution.Id),
            Artifact("test-pdb", "test-pdb", "evidence/test.pdb", pdb, context.ContextId, build.Id, execution.Id),
            Artifact("test-result", "test-result", "evidence/results.trx", trx, context.ContextId, build.Id, execution.Id, "trx"),
            Artifact("coverage", "coverage", "evidence/coverage.xml", coverage, context.ContextId, build.Id, execution.Id, "cobertura", "line")
        };
        var manifest = new RunManifest(ManifestIdentity.SchemaVersion, CanonicalIdentity.Algorithm,
            new ManifestProducer("crap4csharp", "0.1.0", ComplexityRules.CallablesV1,
                ProjectAnalysisContext.ProtocolVersion, ManifestIdentity.CoverageProtocol, ManifestIdentity.PathProtocol),
            new ManifestCapture("completed", true, true, []),
            new ManifestRevision("none", "none", CanonicalIdentity.Set("local-workspace-v1", [repository]), null, null, null),
            [new ManifestRoot("workspace", "workspace", "sensitive")], [manifestContext], [build], [execution], artifacts,
            new ManifestEvaluationInputs(CanonicalIdentity.Sha256(scope.AsSpan()), policy.Hash, null, null), null);
        using var directory = TestDirectory.Create("crap4csharp-policy-check-e2e");
        var locator = ArtifactCaptureAdapter.PublishNew(manifest, bytes, Path.Combine(directory.Path, "bundle"));
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await global::App.RunAsync(["check", "--reuse-artifacts", locator, "--policy", PolicyLogical,
            "--base", "HEAD", "--format", "json"], repository, output, error,
            TestContext.Current.CancellationToken);

        Assert.True(exit == 0, error + Environment.NewLine + output);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("base-trusted", result.RootElement.GetProperty("evaluation").GetProperty("policyTrust")
            .GetProperty("trust").GetString());
        Assert.Equal("verified", result.RootElement.GetProperty("evaluation").GetProperty("provenance")
            .GetProperty("status").GetString());
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Crap4CSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
