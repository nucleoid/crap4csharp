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
                CanonicalIdentity.Sha256(source), "utf-8", false)]);
        var manifest = new RunManifest("1.0", CanonicalIdentity.Algorithm,
            new ManifestProducer("crap4csharp", "test", ComplexityRules.OrdinaryMethodsV1,
                ProjectAnalysisContext.ProtocolVersion, "coverage-v1", "paths-v1"),
            new ManifestCapture("completed", true, true, []),
            new ManifestRevision("none", "local", "deleted-original", null, null, null),
            [new ManifestRoot("workspace", "workspace", "sensitive")], [context], [], [],
            [new ManifestArtifact("source", "source", "artifacts/source.bin", source.Length,
                 CanonicalIdentity.Sha256(source), "ctx", null, null, null, null),
             new ManifestArtifact("coverage", "coverage", "artifacts/coverage.xml", coverage.Length,
                 CanonicalIdentity.Sha256(coverage), "ctx", null, null, "cobertura", "line")],
            new ManifestEvaluationInputs("scope", "policy", null, null), null);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        var path = Path.Combine(root, "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, options));
        return path;
    }
}
