using System.Text;
using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class AnalyzeCommandTests
{
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
        var coverage = manifest.Artifacts.Single(item => item.Kind == "coverage") with
        { Id = "coverage-2", ContextId = "ctx-2", BuildId = "build-2", ExecutionId = "test-2" };
        manifest = ManifestIdentity.Seal(manifest with
        {
            Contexts = [manifest.Contexts[0], second],
            Builds = [manifest.Builds[0], build],
            Executions = [manifest.Executions[0], execution],
            Artifacts = [.. manifest.Artifacts, coverage],
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
        var source = Encoding.UTF8.GetBytes("class C { C() { } }");
        var coverage = Encoding.UTF8.GetBytes("""
            <coverage><packages><package name="App"><classes><class name="C" filename="src/C.cs">
            <methods><method name=".ctor" signature="()"><lines><line number="1" hits="1" /></lines></method></methods>
            </class></classes></package></packages></coverage>
            """);
        File.WriteAllBytes(Path.Combine(directory.Path, "artifacts", "source.bin"), source);
        File.WriteAllBytes(Path.Combine(directory.Path, "artifacts", "coverage.xml"), coverage);
        var manifest = ReadManifest(path);
        var input = manifest.Contexts[0].Inputs[0] with { Length = source.Length, Sha256 = CanonicalIdentity.Sha256(source) };
        var artifacts = manifest.Artifacts.Select(item => item.Kind switch
        {
            "source" => item with { Length = source.Length, Sha256 = CanonicalIdentity.Sha256(source) },
            "coverage" => item with { Length = coverage.Length, Sha256 = CanonicalIdentity.Sha256(coverage) },
            _ => item
        }).ToArray();
        manifest = ManifestIdentity.Seal(manifest with
        {
            Producer = manifest.Producer with { ComplexityRuleset = ComplexityRules.CallablesV1 },
            Contexts = [manifest.Contexts[0] with { Inputs = [input] }],
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
        var source = Encoding.UTF8.GetBytes("class C { int M() => 1; }");
        var coverage = Encoding.UTF8.GetBytes("""
            <coverage><packages><package name="App"><classes><class name="C" filename="src/C.cs">
            <methods><method name="M" signature="()"><lines><line number="1" hits="1" /></lines></method></methods>
            </class></classes></package></packages></coverage>
            """);
        File.WriteAllBytes(Path.Combine(artifacts, "source.bin"), source);
        File.WriteAllBytes(Path.Combine(artifacts, "coverage.xml"), coverage);
        var context = new ManifestContext("ctx", "missing/App.csproj", "net10.0", "Debug", "AnyCPU",
            "sources", "context", "closure", true, true,
            [new ManifestInput("source", "src/C.cs", "artifacts/source.bin", source.Length,
                CanonicalIdentity.Sha256(source), "utf-8", false)])
        {
            ParseOptions = new ManifestParseOptions("preview", "Regular", [],
                new Dictionary<string, string>(StringComparer.Ordinal)),
            PathPolicy = new ManifestPathPolicy("sensitive", [])
        };
        var manifest = new RunManifest("1.0", CanonicalIdentity.Algorithm,
            new ManifestProducer("crap4csharp", "test", ComplexityRules.OrdinaryMethodsV1,
                ProjectAnalysisContext.ProtocolVersion, "coverage-v1", "paths-v1"),
            new ManifestCapture("completed", true, true, []),
            new ManifestRevision("none", "local", "deleted-original", null, null, null),
            [new ManifestRoot("workspace", "workspace", "sensitive")], [context],
            [new ManifestBuild("build", "ctx", "App", "dll", "mvid", "pdb", "portable-pdb")],
            [new ManifestExecution("test", "ctx", "build", true, 0, 1, 1, 0, 0)],
            [new ManifestArtifact("source", "source", "artifacts/source.bin", source.Length,
                 CanonicalIdentity.Sha256(source), "ctx", null, null, null, null),
             new ManifestArtifact("coverage", "coverage", "artifacts/coverage.xml", coverage.Length,
                 CanonicalIdentity.Sha256(coverage), "ctx", "build", "test", "cobertura", "line")],
            new ManifestEvaluationInputs("scope", "policy", null, null), null);
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
}
