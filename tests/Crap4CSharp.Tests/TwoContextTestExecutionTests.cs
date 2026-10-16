using System.Reflection;
using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class TwoContextTestExecutionTests
{
    [Theory]
    [InlineData("authored", true)]
    [InlineData("reference", true)]
    [InlineData("generated", true)]
    [InlineData("output", true)]
    [InlineData("authored", false)]
    [InlineData("none", true)]
    [InlineData("none", false)]
    public async Task ActualTwoContextsPreserveDriftAndRawPartialOutcomes(string defect, bool passes)
    {
        using var fixture = TestDirectory.Create("crap4csharp-two-contexts");
        var dependency = fixture.Write("Dep/Dep.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var authored = fixture.Write("Dep/Code.cs", "public class Dependency { public static int Value => 42; }");
        var control = Path.Combine(fixture.Path, "control.json");
        var order = Path.Combine(fixture.Path, "order.txt");
        foreach (var name in new[] { "First", "Second" })
        {
            fixture.Write(name + "/" + name + ".csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
                <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" PrivateAssets="all" />
                <PackageReference Include="xunit.v3" Version="3.2.2" PrivateAssets="all" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
                <PackageReference Include="coverlet.collector" Version="6.0.4" PrivateAssets="all" />
                <ProjectReference Include="../Dep/Dep.csproj" /></ItemGroup>
                </Project>
                """);
            fixture.Write(name + "/Tests.cs", """
                using Xunit;
                public class RuntimeAssertions {
                [Fact] public void Once() {
                    var settings = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(CONTROL)).RootElement;
                    var first = !System.IO.File.Exists(ORDER);
                    System.IO.File.AppendAllText(ORDER, NAME + "\n");
                    Assert.Equal(42, Dependency.Value);
                    if (first) {
                        var path = settings.GetProperty("path").GetString();
                        if (path != null) {
                            var timestamp = System.IO.File.GetLastWriteTimeUtc(path);
                            var bytes = System.IO.File.ReadAllBytes(path);
                            bytes[bytes.Length-1] ^= 1;
                            System.IO.File.WriteAllBytes(path, bytes);
                            System.IO.File.SetLastWriteTimeUtc(path, timestamp);
                        }
                        Assert.True(settings.GetProperty("passes").GetBoolean(), "intentional first-test failure");
                    }
                } }
                """.Replace("CONTROL", JsonSerializer.Serialize(control), StringComparison.Ordinal)
                    .Replace("ORDER", JsonSerializer.Serialize(order), StringComparison.Ordinal)
                    .Replace("NAME", JsonSerializer.Serialize(name), StringComparison.Ordinal));
        }
        var root = fixture.Write("Root/Root.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><ProjectReference Include="../First/First.csproj" /><ProjectReference Include="../Second/Second.csproj" /></ItemGroup></Project>
            """);
        fixture.Write("Root/Code.cs", "public class Root {}");
        var token = TestContext.Current.CancellationToken;
        var request = new ProjectContextLoadRequest(root, "Debug", "AnyCPU", [], false, false, TimeSpan.FromMinutes(3));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, token)).ExitCode);
        var discovery = await ProjectContextLoader.DiscoverTestRunnersAsync(request, token);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, token);
        Assert.True(loaded.Success, loaded.FailureReason + string.Join(" | ", loaded.Diagnostics));
        var plan = CheckExecutionPlan.Create(discovery, loaded, token);
        Assert.Equal(2, plan.Tests.Count);
        var bound = await CompilerGraphExecution.RunBoundAsync(request, plan.PreparedGraph, async (node, cancellation) =>
        {
            var directory = Path.Combine(fixture.Path, "build", node.Id);
            Directory.CreateDirectory(directory);
            return await CompilerEvidence.BuildAsync(node, directory, TimeSpan.FromMinutes(3), cancellation);
        }, token);
        var dep = Assert.Single(bound.FinalGraph.Contexts, c => c.AssemblyName == "Dep");
        var first = Assert.Single(bound.FinalGraph.GraphContexts, c => c.GraphId == plan.Tests[0].GraphId).Context;
        string? changedPath = defect switch
        {
            "authored" => authored,
            "reference" => dep.EvaluatedReferenceAssemblyPath!,
            "generated" => Directory.GetFiles(first.EvaluatedIntermediateDirectory!, "*.AssemblyInfo.cs").Single(),
            "output" => dep.EvaluatedTargetPath!,
            _ => null
        };
        var timestamp = changedPath is null ? default : File.GetLastWriteTimeUtc(changedPath);
        var before = changedPath is null ? null : File.ReadAllBytes(changedPath);
        File.WriteAllText(control, JsonSerializer.Serialize(new { path = changedPath, passes }));
        var destination = Path.Combine(fixture.Path, "test-run"); Directory.CreateDirectory(destination);
        var operation = typeof(ProjectContextLoader).Assembly.GetType("Crap4CSharp.Core.TestExecutionAdapter")!
            .GetMethod("RunVerifiedAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var task = (Task)operation.Invoke(null, [plan, bound, destination, TimeSpan.FromMinutes(3), token])!;
        await task;
        var run = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var observations = ((System.Collections.IEnumerable)run).Cast<TestInvocationObservation>().ToArray();
        var outcomesProperty = run.GetType().GetProperty("Outcomes");
        Assert.NotNull(outcomesProperty);
        var outcomes = ((System.Collections.IEnumerable)outcomesProperty.GetValue(run)!).Cast<object>().ToArray();
        Assert.Equal(2, outcomes.Length);
        string State(int i) => (string)outcomes[i].GetType().GetProperty("State")!.GetValue(outcomes[i])!;
        Assert.Equal(passes ? "completed" : "failed", State(0));
        Assert.Equal(changedPath is null ? "completed" : "not-run", State(1));
        Assert.Equal(changedPath is null ? 2 : 1, observations.Length);
        Assert.Equal(passes ? 0 : 1, observations[0].Process.ExitCode);
        Assert.Equal(changedPath is null ? 2 : 1, File.ReadAllLines(order).Length);
        foreach (var observation in observations)
        {
            Assert.NotEmpty(File.ReadAllBytes(observation.Process.StandardOutput.Path));
            Assert.True(File.Exists(observation.Process.StandardError.Path));
            Assert.Single(Directory.GetFiles(observation.ResultsDirectory, "*.trx"));
        }
        var error = run.GetType().GetProperty("Failure")!.GetValue(run);
        if (changedPath is null) Assert.Null(error);
        else
        {
            Assert.IsType<ProjectContextException>(error);
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(changedPath));
            Assert.NotEqual(before, File.ReadAllBytes(changedPath));
            Assert.False(Directory.Exists(Path.Combine(destination, "0001")));
        }
    }
}
