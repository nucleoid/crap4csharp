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
