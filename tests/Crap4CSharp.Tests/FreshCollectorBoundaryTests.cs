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
    public Task ActualCollectorAndCompilerBoundariesNeverInventEligibleCoverage(string variant)
        => RunCase(variant);

    [Fact]
    public Task ExcludedCollectorOmissionRetainsActualCompletedTestOutcome()
        => RunCase("excluded", requireOutcome: true);

    private static async Task RunCase(string variant, bool requireOutcome = false)
    {
        using var fixture = TestDirectory.Create("crap4csharp-collector-boundary");
        const string counter = "<Target Name=\"CountCompile\" BeforeTargets=\"CoreCompile\" Condition=\"'$(DesignTimeBuild)' != 'true'\"><WriteLinesToFile File=\"$(MSBuildProjectDirectory)/build-count.txt\" Lines=\"once\" Overwrite=\"false\" /></Target>";
        fixture.Write("Directory.Build.props", "<Project><PropertyGroup><ContinuousIntegrationBuild>" +
            (variant is "ci" or "pathmap" ? "true" : "false") + "</ContinuousIntegrationBuild><Deterministic>true</Deterministic>" +
            (variant == "pathmap" ? "<PathMap>$(MSBuildThisFileDirectory)=/crap4csharp-mapped/</PathMap>" : "") + "</PropertyGroup></Project>");
        fixture.Write("Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" + counter + "</Project>");
        fixture.Write("Library/Code.cs", "public static class Library\n{\n public static int Covered() => 42;\n" +
            (variant == "excluded" ? " [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]\n" : "") +
            " public static int Other() => 43;\n}\n");
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
            """);
        var original = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var output = new StringWriter(); var errors = new StringWriter();
        var exit = await global::App.RunAsync(["check", "--project", "Tests/Tests.csproj", "--policy", "policy.json",
            "--scope", "all", "--artifact-output", "bundle", "--format", "json", "--timeout-seconds", "300"],
            fixture.Path, output, errors, TestContext.Current.CancellationToken);
        using var terminal = JsonDocument.Parse(output.ToString());
        var run = terminal.RootElement.GetProperty("run");
        var evaluation = terminal.RootElement.GetProperty("evaluation");
        var privateArtifact = Assert.Single(run.GetProperty("artifacts").EnumerateArray(), a => a.GetProperty("kind").GetString() == "private-diagnostics");
        var privatePath = privateArtifact.GetProperty("path").GetString()!;
        Assert.True(Directory.Exists(privatePath));
        Assert.False(privateArtifact.GetProperty("complete").GetBoolean());
        Assert.False(privateArtifact.GetProperty("reusable").GetBoolean());
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Library", "build-count.txt")));
        var pe = File.ReadAllBytes(Path.Combine(fixture.Path, "Library/bin/Debug/net10.0/Library.dll"));
        var pdb = File.ReadAllBytes(Path.Combine(fixture.Path, "Library/bin/Debug/net10.0/Library.pdb"));
        var inspected = ArtifactEvidenceInspector.InspectBuild(ImmutableArray.CreateRange(pe), ImmutableArray.CreateRange(pdb));
        Assert.Contains(inspected.Methods, m => m.MethodName == "Covered" && m.SequencePoints.Count > 0);
        Assert.Contains(inspected.Methods, m => m.MethodName == "Other" && m.SequencePoints.Count > 0);
        if (variant == "ci")
        {
            Assert.Equal(1, exit);
            Assert.Contains("compiler.outputsUnproven", output.ToString(), StringComparison.Ordinal);
            if (variant == "pathmap")
                Assert.Contains(inspected.Documents.Keys, p => p.StartsWith("/crap4csharp-mapped/", StringComparison.Ordinal));
            else
            {
                var testBuild = ArtifactEvidenceInspector.InspectBuild(
                    ImmutableArray.CreateRange(File.ReadAllBytes(Path.Combine(fixture.Path, "Tests/bin/Debug/net10.0/Tests.dll"))),
                    ImmutableArray.CreateRange(File.ReadAllBytes(Path.Combine(fixture.Path, "Tests/bin/Debug/net10.0/Tests.pdb"))));
                Assert.Contains(testBuild.Documents.Keys, p => !File.Exists(p));
            }
            Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Tests/build-count.txt")));
            Assert.Empty(Directory.GetFiles(fixture.Path, "test-count.txt", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(privatePath, "tests.trx", SearchOption.AllDirectories));
        }
        else
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
                Assert.Equal(2, evaluation.GetProperty("metrics").GetArrayLength());
                var outcome = Assert.Single(run.GetProperty("testOutcomes").EnumerateArray());
                Assert.Equal("completed", outcome.GetProperty("status").GetString());
                Assert.Equal(0, outcome.GetProperty("exitCode").GetInt32());
            }
        }
        if (variant is "ci" or "excluded")
        {
            Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
            Assert.Empty(evaluation.GetProperty("metrics").EnumerateArray());
            Assert.False(evaluation.GetProperty("decision").GetProperty("completed").GetBoolean());
            Assert.DoesNotContain(run.GetProperty("artifacts").EnumerateArray(), a => a.GetProperty("kind").GetString() == "manifest" || a.GetProperty("complete").GetBoolean() || a.GetProperty("reusable").GetBoolean());
        }
        foreach (var (path, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
