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

    private static async Task RunCase(bool failing, bool coordinatorOnly)
    {
        using var fixture = TestDirectory.Create("crap4csharp-public-cold-check");
        const string counter = """
            <Target Name="CountActualCompile" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-count.txt" Lines="once" Overwrite="false" /></Target>
            """;
        const string privateCanary = "fresh-private-property-never-export-7264c8e";
        fixture.Write("Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            (coordinatorOnly ? "<PrivateCanary>" + privateCanary + "</PrivateCanary>" : "") + "</PropertyGroup>" + counter + "</Project>");
        fixture.Write("Library/Code.cs", "public static class Library { public static int Value => 42; }");
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
        fixture.Write("Tests/Tests.cs", "using Xunit; public class Once { [Fact] public void Test() { Assert.Equal(" + (failing ? "99" : "42") + ", Library.Value); } }");
        fixture.Write("policy.json", """
            {"schemaVersion":"repository-policy-v1","mode":"strict","productionProjects":["Library/Library.csproj"],
            "testProjects":["Tests/Tests.csproj"],"configuration":"Debug","targetFrameworks":["net10.0"],"scope":"base",
            "threshold":8,"missingCoverage":"fail","requiredChecks":["tests","coverage","crap"],"exclusions":[],"ruleset":"callables-v1"}
            """);
        fixture.Write(".gitignore", coordinatorOnly ? "build-count.txt\nbundle/\n" : "bin/\nobj/\nbuild-count.txt\nbundle/\n");
        var token = TestContext.Current.CancellationToken;
        foreach (var args in new[] { new[] { "init", "-q" }, new[] { "config", "user.name", "Fixture" },
            new[] { "config", "user.email", "fixture@example.invalid" }, new[] { "add", "." }, new[] { "commit", "-qm", "cold fixture" } })
            Assert.Equal(0, (await ProcessRunner.RunAsync("git", args, fixture.Path, TimeSpan.FromSeconds(30), token)).ExitCode);
        var original = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).Where(p =>
            !p.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(p => p, File.ReadAllBytes);
        Assert.Empty(Directory.GetDirectories(fixture.Path, "obj", SearchOption.AllDirectories));
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
        var exit = await global::App.RunAsync(["check", "--project", "Tests/Tests.csproj", "--policy", "policy.json", "--base", "HEAD",
            "--artifact-output", "bundle", "--format", "json", "--timeout-seconds", "180"], fixture.Path, output, errors, token);
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
        }
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Library", "build-count.txt")));
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Tests", "build-count.txt")));
        foreach (var (path, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
