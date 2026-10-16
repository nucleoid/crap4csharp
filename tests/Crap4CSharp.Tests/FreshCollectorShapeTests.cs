using System.Collections.Immutable;
using System.Text.Json;
using System.Xml.Linq;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class FreshCollectorShapeTests
{
    [Theory]
    [InlineData("async")]
    [InlineData("iterator")]
    [InlineData("lambda")]
    [InlineData("localfunction")]
    [InlineData("record")]
    [InlineData("positionalrecord")]
    [InlineData("recordinitializer")]
    public Task ActualPassingTestShapesKeepUnsupportedCoverageExplicit(string variant)
        => RunCase(variant);

    private static async Task RunCase(string variant)
    {
        using var fixture = TestDirectory.Create("crap4csharp-collector-shapes");
        const string counter = "<Target Name=\"CountCompile\" BeforeTargets=\"CoreCompile\" Condition=\"'$(DesignTimeBuild)' != 'true'\"><WriteLinesToFile File=\"$(MSBuildProjectDirectory)/build-count.txt\" Lines=\"once\" Overwrite=\"false\" /></Target>";
        fixture.Write("Directory.Build.props", "<Project><PropertyGroup><ContinuousIntegrationBuild>false</ContinuousIntegrationBuild><Deterministic>true</Deterministic></PropertyGroup></Project>");
        fixture.Write("Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" + counter + "</Project>");
        var body = variant switch
        {
            "async" => " public static async System.Threading.Tasks.Task<int> Other()\n {\n  await System.Threading.Tasks.Task.Yield();\n  return 43;\n }\n",
            "iterator" => " public static System.Collections.Generic.IEnumerable<int> Other()\n {\n  yield return 43;\n }\n",
            "lambda" => " public static int Other()\n {\n  System.Func<int> value = () =>\n  {\n   return 43;\n  };\n  return value();\n }\n",
            "localfunction" => " public static int Other()\n {\n  int Value()\n  {\n   return 43;\n  }\n  return Value();\n }\n",
            "record" or "recordinitializer" => " public static int Other() => new Value().Read();\n",
            "positionalrecord" => " public static int Other() => new Value(43).Read();\n",
            _ => throw new System.ArgumentOutOfRangeException(nameof(variant))
        };
        fixture.Write("Library/Code.cs", "public static class Library\n{\n public static int Covered() => 42;\n" + body + "}\n" +
            (variant switch
            {
                "record" => "public record Value\n{\n public int Read() => 43;\n}\n",
                "positionalrecord" => "public record Value(int Number)\n{\n public int Read() => Number;\n}\n",
                "recordinitializer" => "public record Value\n{\n public int Number { get; init; } = 43;\n public int Read() => Number;\n}\n",
                _ => ""
            }));
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
                    SHAPE_ASSERT
                }
            }
            """.Replace("SHAPE_ASSERT", variant switch
            {
                "async" => "Assert.Equal(43, Library.Other().GetAwaiter().GetResult());",
                "iterator" => "Assert.Equal(43, System.Linq.Enumerable.Single(Library.Other()));",
                _ => "Assert.Equal(43, Library.Other());"
            }, StringComparison.Ordinal));
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
        var diagnosticRoot = TestValidationOutput.CreateDirectory("collector-shape", fixture.Path);
        File.WriteAllText(Path.Combine(diagnosticRoot, "collector-shape-" + variant + ".json"), output.ToString());
        File.WriteAllText(Path.Combine(diagnosticRoot, "collector-shape-" + variant + "-stderr.log"), errors.ToString());
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
        var sourcePath = Path.Combine(fixture.Path, "Library", "Code.cs");
        var analysisTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath),
            new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp14), "Code.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        var analysisCompilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Library", [analysisTree],
            [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var classified = CompilerMethodEligibility.Classify(analysisCompilation, inspected,
            new Dictionary<string, string>(StringComparer.Ordinal) { [sourcePath.Replace('\\', '/')] = "Code.cs" },
            TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(diagnosticRoot, "collector-shape-" + variant + "-methods.json"),
            JsonSerializer.Serialize(inspected.Methods.Select(method => new
            {
                method.MetadataToken, method.TypeName, method.MethodName, method.CompilerGenerated,
                method.ParameterTypes, method.SequencePoints, classified = classified.Contains(method.MetadataToken)
            })));
        Assert.Contains(inspected.Methods, m => m.MethodName == "Covered" && m.SequencePoints.Count > 0);
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Tests/build-count.txt")));
        var testCounter = Assert.Single(Directory.GetFiles(fixture.Path, "test-count.txt", SearchOption.AllDirectories));
        Assert.Single(File.ReadAllLines(testCounter));
        var trxPath = Assert.Single(Directory.GetFiles(privatePath, "tests.trx", SearchOption.AllDirectories));
        var trx = XDocument.Load(trxPath);
        var counters = trx.Descendants().Single(e => e.Name.LocalName == "Counters");
        Assert.Equal("1", counters.Attribute("passed")!.Value);
        Assert.Equal("0", counters.Attribute("failed")!.Value);
        var outcome = Assert.Single(run.GetProperty("testOutcomes").EnumerateArray());
        Assert.Equal("completed", outcome.GetProperty("status").GetString());
        Assert.Equal(0, outcome.GetProperty("exitCode").GetInt32());
        var deployment = trx.Descendants().Single(e => e.Name.LocalName == "Deployment").Attribute("runDeploymentRoot")!.Value;
        var link = trx.Descendants().Single(e => e.Name.LocalName == "A").Attribute("href")!.Value;
        var attachmentPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(trxPath)!, deployment, "In", link));
        Assert.StartsWith(Path.GetFullPath(privatePath) + Path.DirectorySeparatorChar, attachmentPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var report = XDocument.Load(attachmentPath);
        Assert.Single(report.Descendants("Module"), m => m.Element("ModuleName")!.Value == "Library");
        if (variant == "recordinitializer")
        {
            Assert.Equal(1, exit);
            Assert.False(evaluation.GetProperty("decision").GetProperty("completed").GetBoolean());
            Assert.Equal("unknown", evaluation.GetProperty("decision").GetProperty("policyDecision").GetString());
            Assert.Equal("scope.unsupportedCallable", evaluation.GetProperty("decision").GetProperty("reason").GetString());
            var observations = evaluation.GetProperty("metrics").EnumerateArray().ToArray();
            Assert.Equal(4, observations.Length);
            foreach (var signature in new[] { "::Covered()", "::Other()", "::Read()" })
            {
                var known = Assert.Single(observations, item => item.GetProperty("signature").GetString()?.Contains(signature, StringComparison.Ordinal) == true);
                Assert.Equal(1, known.GetProperty("coverage").GetDouble());
                Assert.Equal(1, known.GetProperty("crap").GetDouble());
                Assert.Equal(JsonValueKind.Null, known.GetProperty("coverageReason").ValueKind);
            }
            var unknown = Assert.Single(observations, item => item.GetProperty("signature").ValueKind == JsonValueKind.Null);
            Assert.Equal("scope.unsupportedCallable", unknown.GetProperty("coverageReason").GetString());
            Assert.Equal(JsonValueKind.Null, unknown.GetProperty("coverage").ValueKind);
            Assert.Equal(JsonValueKind.Null, unknown.GetProperty("crap").ValueKind);
            var initializer = Assert.Single(evaluation.GetProperty("callables").EnumerateArray(), item => item.GetProperty("kind").GetString() == "PropertyInitializer");
            Assert.Equal("applicable", initializer.GetProperty("applicability").GetString());
            Assert.Equal("scope.unsupportedCallable", initializer.GetProperty("coverageReason").GetString());
            Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
            Assert.DoesNotContain(run.GetProperty("artifacts").EnumerateArray(), artifact => artifact.GetProperty("complete").GetBoolean() || artifact.GetProperty("reusable").GetBoolean());
            return;
        }
        if (variant == "record")
        {
            Assert.Equal(0, exit);
            Assert.True(evaluation.GetProperty("decision").GetProperty("completed").GetBoolean());
            Assert.Equal("pass", evaluation.GetProperty("decision").GetProperty("policyDecision").GetString());
            var authored = evaluation.GetProperty("metrics").EnumerateArray().ToArray();
            Assert.Equal(3, authored.Length);
            foreach (var signature in new[] { "::Covered()", "::Other()", "::Read()" })
            {
                var metric = Assert.Single(authored, item => item.GetProperty("signature").GetString()!.Contains(signature, StringComparison.Ordinal));
                Assert.Equal(1, metric.GetProperty("coverage").GetDouble());
                Assert.Equal(1, metric.GetProperty("crap").GetDouble());
                Assert.Equal(JsonValueKind.Null, metric.GetProperty("coverageReason").ValueKind);
            }
            var provenance = evaluation.GetProperty("provenance");
            Assert.Equal("verified", provenance.GetProperty("status").GetString());
            Assert.True(provenance.GetProperty("postflightVerified").GetBoolean());
            Assert.True(provenance.GetProperty("reusable").GetBoolean());
            var manifest = Assert.Single(run.GetProperty("artifacts").EnumerateArray(), artifact => artifact.GetProperty("kind").GetString() == "manifest");
            Assert.True(manifest.GetProperty("complete").GetBoolean());
            Assert.True(manifest.GetProperty("reusable").GetBoolean());
            Assert.True(File.Exists(manifest.GetProperty("path").GetString()));
            return;
        }
        if (variant == "lambda")
        {
            Assert.Equal(1, exit);
            Assert.False(evaluation.GetProperty("decision").GetProperty("completed").GetBoolean());
            Assert.Equal("unknown", evaluation.GetProperty("decision").GetProperty("policyDecision").GetString());
            Assert.Equal("coverage.ambiguousCallableOwnership", evaluation.GetProperty("decision").GetProperty("reason").GetString());
            var observations = evaluation.GetProperty("metrics").EnumerateArray().ToArray();
            Assert.Equal(3, observations.Length);
            var ordinary = Assert.Single(observations, metric => metric.GetProperty("signature").GetString()?.Contains("::Covered()", StringComparison.Ordinal) == true);
            Assert.Equal(1, ordinary.GetProperty("coverage").GetDouble());
            Assert.Equal(1, ordinary.GetProperty("crap").GetDouble());
            var parent = Assert.Single(observations, metric => metric.GetProperty("signature").GetString()?.Contains("::Other()", StringComparison.Ordinal) == true);
            Assert.Equal("coverage.ambiguousCallableOwnership", parent.GetProperty("coverageReason").GetString());
            var anonymous = Assert.Single(observations, metric => metric.GetProperty("signature").ValueKind == JsonValueKind.Null);
            Assert.Equal("coverage.unsupportedGeneratedMapping", anonymous.GetProperty("coverageReason").GetString());
            foreach (var unknown in new[] { parent, anonymous })
            {
                Assert.Equal(JsonValueKind.Null, unknown.GetProperty("coverage").ValueKind);
                Assert.Equal(JsonValueKind.Null, unknown.GetProperty("crap").ValueKind);
            }
            Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
            Assert.DoesNotContain(run.GetProperty("artifacts").EnumerateArray(), artifact => artifact.GetProperty("complete").GetBoolean() || artifact.GetProperty("reusable").GetBoolean());
            return;
        }
        if (variant == "positionalrecord")
        {
            Assert.Equal(1, exit);
            var incomplete = evaluation.GetProperty("decision");
            Assert.False(incomplete.GetProperty("completed").GetBoolean());
            Assert.Equal("unknown", incomplete.GetProperty("policyDecision").GetString());
            Assert.Equal("coverage.noMatchingMethod", incomplete.GetProperty("reason").GetString());
            var retainedMetrics = evaluation.GetProperty("metrics").EnumerateArray().ToArray();
            Assert.Equal(4, retainedMetrics.Length);
            var constructor = Assert.Single(retainedMetrics, metric => metric.GetProperty("signature").GetString()!.Contains("::.ctor(System.Int32)", StringComparison.Ordinal));
            Assert.Equal(JsonValueKind.Null, constructor.GetProperty("coverage").ValueKind);
            Assert.Equal(JsonValueKind.Null, constructor.GetProperty("crap").ValueKind);
            Assert.Equal("coverage.noMatchingMethod", constructor.GetProperty("coverageReason").GetString());
            foreach (var metric in retainedMetrics.Where(metric => metric.GetProperty("signature").GetString() != constructor.GetProperty("signature").GetString()))
            {
                Assert.Equal(1, metric.GetProperty("coverage").GetDouble());
                Assert.Equal(1, metric.GetProperty("crap").GetDouble());
            }
            Assert.Equal("pass", Assert.Single(evaluation.GetProperty("checks").EnumerateArray(), check => check.GetProperty("name").GetString() == "testExecution").GetProperty("status").GetString());
            Assert.True(evaluation.GetProperty("provenance").GetProperty("postflightVerified").GetBoolean());
            Assert.False(evaluation.GetProperty("provenance").GetProperty("reusable").GetBoolean());
            Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
            Assert.DoesNotContain(run.GetProperty("artifacts").EnumerateArray(), artifact => artifact.GetProperty("complete").GetBoolean() || artifact.GetProperty("reusable").GetBoolean());
            foreach (var (path, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(path));
            return;
        }
        // Actual passing VSTest never promotes unsupported coverage to green.
        Assert.True(exit == 1, errors + Environment.NewLine + output);
        var decision = evaluation.GetProperty("decision");
        Assert.False(decision.GetProperty("completed").GetBoolean());
        Assert.Equal("unknown", decision.GetProperty("policyDecision").GetString());
        var aggregateRefusal = variant is "lambda" or "record" or "positionalrecord" or "recordinitializer";
        Assert.Equal(aggregateRefusal ? "coverage.aggregateUnproven" : "coverage.unsupportedGeneratedMapping",
            decision.GetProperty("reason").GetString());
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
        Assert.DoesNotContain(run.GetProperty("artifacts").EnumerateArray(), a => a.GetProperty("complete").GetBoolean() || a.GetProperty("reusable").GetBoolean());
        var metrics = evaluation.GetProperty("metrics");
        if (aggregateRefusal)
        {
            Assert.Empty(metrics.EnumerateArray());
            Assert.Equal("invalid", evaluation.GetProperty("provenance").GetProperty("status").GetString());
            if (variant is "record" or "positionalrecord" or "recordinitializer")
            {
                Assert.Contains(inspected.Methods, m => m.MethodName == "Read" && m.SequencePoints.Count > 0);
                Assert.True(inspected.Methods.Count(m => m.SequencePoints.Count > 0) > 3);
                if (variant is "positionalrecord" or "recordinitializer")
                {
                    Assert.Contains(inspected.Methods, m => m.MethodName == ".ctor" && m.SequencePoints.Count > 0);
                    Assert.Contains(inspected.Methods, m => m.MethodName == "get_Number" && m.SequencePoints.Count > 0);
                }
            }
        }
        else
        {
            Assert.Equal(variant == "localfunction" ? 3 : 2, metrics.GetArrayLength());
            var covered = Assert.Single(metrics.EnumerateArray(), m => m.GetProperty("signature").GetString()!.Contains("::Covered()", StringComparison.Ordinal));
            Assert.Equal(1, covered.GetProperty("coverage").GetDouble());
            Assert.Equal(1, covered.GetProperty("crap").GetDouble());
            var unknown = Assert.Single(metrics.EnumerateArray(), m => m.GetProperty("coverageReason").ValueKind == JsonValueKind.String);
            Assert.Equal("coverage.unsupportedGeneratedMapping", unknown.GetProperty("coverageReason").GetString());
            Assert.Equal(JsonValueKind.Null, unknown.GetProperty("coverage").ValueKind);
            Assert.Equal(JsonValueKind.Null, unknown.GetProperty("crap").ValueKind);
            Assert.Contains(variant == "localfunction" ? "::Value()" : "::Other()", unknown.GetProperty("signature").GetString(), StringComparison.Ordinal);
            Assert.Equal("pass", Assert.Single(evaluation.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "testExecution").GetProperty("status").GetString());
            if (variant != "localfunction")
                Assert.Contains(report.Descendants("Module").Single(m => m.Element("ModuleName")!.Value == "Library").Descendants("Method").Select(m => m.Element("Name")!.Value), m => m.Contains("::MoveNext()", StringComparison.Ordinal));
        }
        foreach (var (path, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
