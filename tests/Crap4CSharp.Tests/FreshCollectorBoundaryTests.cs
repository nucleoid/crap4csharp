using System.Collections.Immutable;
using System.Text.Json;
using System.Xml.Linq;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class FreshCollectorBoundaryTests
{
    [Theory]
    [InlineData("deterministic")]
    [InlineData("excluded")]
    [InlineData("ci")]
    [InlineData("pathmap")]
    [InlineData("generator")]
    public Task ActualCollectorAndCompilerBoundariesNeverInventEligibleCoverage(string variant)
        => RunCase(variant);

    [Fact]
    public Task ExcludedCollectorOmissionRetainsActualCompletedTestOutcome()
        => RunCase("excluded", requireOutcome: true);

    private static async Task RunCase(string variant, bool requireOutcome = false)
    {
        // Retain actual compiler, PDB and collector evidence for boundary failures.
        var fixture = TestDirectory.Create("crap4csharp-collector-boundary");
        const string counter = "<Target Name=\"CountCompile\" BeforeTargets=\"CoreCompile\" Condition=\"'$(DesignTimeBuild)' != 'true'\"><WriteLinesToFile File=\"$(MSBuildProjectDirectory)/build-count.txt\" Lines=\"once\" Overwrite=\"false\" /></Target>";
        fixture.Write("Directory.Build.props", "<Project><PropertyGroup><ContinuousIntegrationBuild>" +
            (variant is "ci" or "pathmap" ? "true" : "false") + "</ContinuousIntegrationBuild><Deterministic>true</Deterministic>" +
            (variant == "pathmap" ? "<PathMap>$(MSBuildThisFileDirectory)=/crap4csharp-mapped/</PathMap>" : "") + "</PropertyGroup></Project>");
        var analyzerItem = "";
        if (variant == "generator")
        {
            const string generator = "using Microsoft.CodeAnalysis; [Generator] public sealed class FixtureGenerator : ISourceGenerator { public void Initialize(GeneratorInitializationContext context) {} public void Execute(GeneratorExecutionContext context) => context.AddSource(\"Hidden.cs\", Microsoft.CodeAnalysis.Text.SourceText.From(\"public class Hidden { public int Risk(int value) => value > 0 ? 1 : 2; }\", System.Text.Encoding.UTF8, Microsoft.CodeAnalysis.Text.SourceHashAlgorithm.Sha256)); }";
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Append(typeof(Microsoft.CodeAnalysis.ISourceGenerator).Assembly.Location).Distinct()
                .Select(p => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p));
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("FixtureGenerator",
                [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(generator, cancellationToken: TestContext.Current.CancellationToken)], references,
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
            using var stream = File.Create(Path.Combine(fixture.Path, "FixtureGenerator.dll"));
            var emitted = compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(emitted.Success, string.Join(" | ", emitted.Diagnostics));
            analyzerItem = "<ItemGroup><Analyzer Include=\"../FixtureGenerator.dll\" /></ItemGroup>";
        }
        fixture.Write("Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" + counter + analyzerItem + "</Project>");
        fixture.Write("Library/Code.cs", "public static class Library\n{\n public static int Covered() => 42;\n" +
            (variant == "excluded" ? " [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]\n" : "") +
            (variant == "generator" ? " public static int Other() => new Hidden().Risk(1) + 42;\n}\n" : " public static int Other() => 43;\n}\n"));
        fixture.Write("Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
            <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" PrivateAssets="all" />
            <PackageReference Include="xunit.v3" Version="3.2.2" PrivateAssets="all" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
            <PackageReference Include="coverlet.collector" Version="6.0.4" PrivateAssets="all" />
            <ProjectReference Include="../Library/Library.csproj" /></ItemGroup>COUNTER</Project>
            """.Replace("COUNTER", counter, StringComparison.Ordinal));
        fixture.Write("Tests/Tests.cs", """
            using Xunit;
            public class Once
            {
                [Fact] public void Test()
                {
                    System.IO.File.AppendAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "test-count.txt"), "once\n");
                    Assert.Equal(42, Library.Covered());
                    Assert.Equal(43, Library.Other());
                }
            }
            """);
        fixture.Write("policy.json", """
            {"schemaVersion":"repository-policy-v1","mode":"strict","productionProjects":["Library/Library.csproj"],
            "testProjects":["Tests/Tests.csproj"],"configuration":"Debug","targetFrameworks":["net10.0"],"scope":"all",
            "threshold":8,"missingCoverage":"fail","requiredChecks":["tests","coverage","crap"],"exclusions":[],"ruleset":"callables-v1"}
            """.Replace("\"ruleset\":\"callables-v1\"", variant == "generator" ?
                "\"ruleset\":\"callables-v1\",\"allowedOverrides\":{\"includeGenerated\":true}" :
                "\"ruleset\":\"callables-v1\"", StringComparison.Ordinal));
        var original = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var output = new StringWriter(); var errors = new StringWriter();
        var exit = await global::App.RunAsync(["check", "--project", "Tests/Tests.csproj", "--policy", "policy.json",
            "--scope", "all", "--artifact-output", "bundle", "--format", "json", "--timeout-seconds", "300",
            .. (variant == "generator" ? new[] { "--include-generated" } : Array.Empty<string>())],
            fixture.Path, output, errors, TestContext.Current.CancellationToken);
        using var terminal = JsonDocument.Parse(output.ToString());
        var run = terminal.RootElement.GetProperty("run");
        var evaluation = terminal.RootElement.GetProperty("evaluation");
        var privateArtifacts = run.GetProperty("artifacts").EnumerateArray()
            .Where(a => a.GetProperty("kind").GetString() == "private-diagnostics").ToArray();
        Assert.True(privateArtifacts.Length == 1, $"variant={variant}; exit={exit}\n{errors}\n{output}");
        var privateArtifact = Assert.Single(privateArtifacts);
        var privatePath = privateArtifact.GetProperty("path").GetString()!;
        Assert.True(Directory.Exists(privatePath));
        Assert.False(privateArtifact.GetProperty("complete").GetBoolean());
        Assert.False(privateArtifact.GetProperty("reusable").GetBoolean());
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Library", "build-count.txt")));
        var pe = File.ReadAllBytes(Path.Combine(fixture.Path, "Library/bin/Debug/net10.0/Library.dll"));
        var pdb = File.ReadAllBytes(Path.Combine(fixture.Path, "Library/bin/Debug/net10.0/Library.pdb"));
        var inspected = ArtifactEvidenceInspector.InspectBuild(ImmutableArray.CreateRange(pe), ImmutableArray.CreateRange(pdb));
        if (variant == "pathmap")
            Assert.Contains(inspected.Documents.Keys, p => p.StartsWith("/crap4csharp-mapped/", StringComparison.Ordinal));
        Assert.Contains(inspected.Methods, m => m.MethodName == "Covered" && m.SequencePoints.Count > 0);
        Assert.Contains(inspected.Methods, m => m.MethodName == "Other" && m.SequencePoints.Count > 0);
        if (variant == "ci")
        {
            var testBuild = ArtifactEvidenceInspector.InspectBuild(
                ImmutableArray.CreateRange(File.ReadAllBytes(Path.Combine(fixture.Path, "Tests/bin/Debug/net10.0/Tests.dll"))),
                ImmutableArray.CreateRange(File.ReadAllBytes(Path.Combine(fixture.Path, "Tests/bin/Debug/net10.0/Tests.pdb"))));
            Assert.Contains(testBuild.Documents.Keys, path => !File.Exists(path));
        }
        {
            Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Tests/build-count.txt")));
            var testCounters = Directory.GetFiles(fixture.Path, "test-count.txt", SearchOption.AllDirectories);
            Assert.True(testCounters.Length == 1, output.ToString());
            Assert.Single(File.ReadAllLines(testCounters[0]));
            var trxPath = Assert.Single(Directory.GetFiles(privatePath, "tests.trx", SearchOption.AllDirectories));
            var trx = XDocument.Load(trxPath);
            var counters = trx.Descendants().Single(e => e.Name.LocalName == "Counters");
            Assert.Equal("1", counters.Attribute("passed")!.Value);
            Assert.Equal("0", counters.Attribute("failed")!.Value);
            // VSTest retains both the GUID producer copy and the deployment
            // copy. Inspect the exact TRX-declared deployment, never glob-select.
            var deployment = trx.Descendants().Single(e => e.Name.LocalName == "Deployment").Attribute("runDeploymentRoot")!.Value;
            var link = trx.Descendants().Single(e => e.Name.LocalName == "A").Attribute("href")!.Value;
            var attachmentPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(trxPath)!, deployment, "In", link));
            Assert.StartsWith(Path.GetFullPath(privatePath) + Path.DirectorySeparatorChar, attachmentPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            var report = XDocument.Load(attachmentPath);
            if (variant is "ci" or "pathmap")
            {
                var moduleNames = report.Descendants("Module").Select(module => module.Element("ModuleName")!.Value).ToArray();
                Assert.Equal(new[] { "Library" }, moduleNames);
                Assert.True(new FileInfo(attachmentPath).Length <= 16 * 1024 * 1024);
            }
            var module = report.Descendants("Module").Single(m => m.Element("ModuleName")!.Value == "Library");
            Assert.All(module.Descendants("Method"), m => Assert.Empty(m.Element("MetadataToken")!.Value));
            var sourceMappings = inspected.Documents.Keys.SelectMany(p =>
            {
                var logical = variant == "pathmap" ? p["/crap4csharp-mapped/".Length..] :
                    Path.GetRelativePath(fixture.Path, p).Replace('\\', '/');
                return variant == "pathmap"
                    ? new[] { new ManifestReportRootMapping(p, logical),
                        new ManifestReportRootMapping(Path.Combine(fixture.Path, logical), logical) }
                    : new[] { new ManifestReportRootMapping(p, logical) };
            }).ToArray();
            var represented = ArtifactEvidenceInspector.MatchedMethodTokens(
                ImmutableArray.CreateRange(File.ReadAllBytes(attachmentPath)), "coverage.opencover.xml", inspected,
                sourceMappings.Select(m => m.LogicalRoot).ToArray(), new CapturedPathPolicy(!OperatingSystem.IsWindows(), sourceMappings),
                TestContext.Current.CancellationToken);
            Assert.NotNull(represented);
            var covered = inspected.Methods.Single(m => m.MethodName == "Covered");
            var other = inspected.Methods.Single(m => m.MethodName == "Other");
            Assert.Contains(covered.MetadataToken, represented);
            if (variant == "excluded")
            {
                Assert.DoesNotContain(other.MetadataToken, represented);
                Assert.Equal(1, exit);
                Assert.Contains("coverage.aggregateUnproven", output.ToString(), StringComparison.Ordinal);
                Assert.Equal("INCOMPLETE", InstalledCheckTests.NormalizeDeliveryForgeGate(
                    terminal.RootElement, exit, terminal.RootElement.GetProperty("toolVersion").GetString()!, "fixture-base"));
                if (requireOutcome)
                {
                    var outcome = Assert.Single(run.GetProperty("testOutcomes").EnumerateArray());
                    Assert.Equal("completed", outcome.GetProperty("status").GetString());
                    Assert.Equal(0, outcome.GetProperty("exitCode").GetInt32());
                    Assert.True(Directory.Exists(outcome.GetProperty("resultsDirectory").GetString()));
                }
            }
            else
            {
                Assert.Contains(other.MetadataToken, represented);
                Assert.True(exit == 0, errors + Environment.NewLine + output);
                Assert.True(evaluation.GetProperty("decision").GetProperty("completed").GetBoolean());
                Assert.Equal(variant == "generator" ? 3 : 2, evaluation.GetProperty("metrics").GetArrayLength());
                var outcome = Assert.Single(run.GetProperty("testOutcomes").EnumerateArray());
                Assert.Equal("completed", outcome.GetProperty("status").GetString());
                Assert.Equal(0, outcome.GetProperty("exitCode").GetInt32());
            }
        }
        if (variant == "excluded")
        {
            Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
            Assert.Empty(evaluation.GetProperty("metrics").EnumerateArray());
            Assert.False(evaluation.GetProperty("decision").GetProperty("completed").GetBoolean());
            Assert.DoesNotContain(run.GetProperty("artifacts").EnumerateArray(), a => a.GetProperty("kind").GetString() == "manifest" || a.GetProperty("complete").GetBoolean() || a.GetProperty("reusable").GetBoolean());
        }
        if (variant == "generator")
        {
            var manifestPath = Path.Combine(fixture.Path, "bundle/manifest.json");
            var bundle = ArtifactBundle.Load(manifestPath, fixture.Path);
            var generated = Assert.Single(Assert.Single(bundle.Manifest.Contexts).Inputs,
                i => i.Generated && i.Role == "source" && i.LogicalPath.StartsWith("<generated>/", StringComparison.Ordinal));
            Assert.Null(generated.RepositoryPath); // Virtual output is never committed-path authority.
            Assert.StartsWith("<generated>/", generated.LogicalPath, StringComparison.Ordinal);
            Assert.True(Assert.Single(bundle.Manifest.Contexts).IncludeGenerated);
            Assert.Contains(generated.LogicalPath, CapturedEvaluationInputs.Read(bundle).Scope.Sources);
            Assert.Equal(generated.Sha256, CanonicalIdentity.Sha256(bundle.Bytes[generated.Locator].AsSpan()));
            var replayOutput = new StringWriter();
            Assert.Equal(0, await global::App.RunAsync(["analyze", "--reuse-artifacts", "bundle/manifest.json", "--format", "json"],
                fixture.Path, replayOutput, new StringWriter(), TestContext.Current.CancellationToken));
            using var replay = JsonDocument.Parse(replayOutput.ToString());
            Assert.Equal(3, replay.RootElement.GetProperty("evaluation").GetProperty("metrics").GetArrayLength());
            var priorOutput = new StringWriter(); var priorError = new StringWriter();
            var priorExit = await global::App.RunAsync(["check", "--project", "Tests/Tests.csproj", "--policy", "policy.json", "--scope", "all",
                "--no-build", "--build-artifacts", "bundle/manifest.json", "--artifact-output", "bundle-prior", "--format", "json", "--timeout-seconds", "300", "--include-generated"],
                fixture.Path, priorOutput, priorError, TestContext.Current.CancellationToken);
            Assert.True(priorExit == 0, priorError + Environment.NewLine + priorOutput);
            var prior = ArtifactBundle.Load(Path.Combine(fixture.Path, "bundle-prior/manifest.json"), fixture.Path);
            var priorGenerated = Assert.Single(Assert.Single(prior.Manifest.Contexts).Inputs,
                i => i.Generated && i.Role == "source" && i.LogicalPath.StartsWith("<generated>/", StringComparison.Ordinal));
            Assert.Equal(generated, priorGenerated);
            Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Library/build-count.txt")));
            Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Tests/build-count.txt")));
        }
        foreach (var (path, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
