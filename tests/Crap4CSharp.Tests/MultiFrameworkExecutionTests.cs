using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class MultiFrameworkExecutionTests
{
    [Fact]
    public async Task ActualNetNineAndTenBuildAndTestOnceWithSeparateCompilerAndCoverageOwners()
    {
        using var fixture = TestDirectory.Create("crap4csharp-multi-framework");
        fixture.Write("global.json", "{\"sdk\":{\"version\":\"10.0.103\",\"rollForward\":\"disable\"}}");
        fixture.Write("Directory.Build.targets", """
            <Project><Target Name="CountActualCompile" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-count.txt" Lines="$(TargetFramework)" Overwrite="false" />
            </Target></Project>
            """);
        fixture.Write("Library/Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net9.0;net10.0</TargetFrameworks></PropertyGroup></Project>
            """);
        var source = fixture.Write("Library/Code.cs", """
            public static class Library
            {
                public static int Value()
                {
            #if NET9_0
                    return 9;
            #else
                    return 10;
            #endif
                }
            }
            """);
        var project = fixture.Write("Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFrameworks>net9.0;net10.0</TargetFrameworks><IsPackable>false</IsPackable></PropertyGroup>
            <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" PrivateAssets="all" />
            <PackageReference Include="xunit.v3" Version="3.2.2" PrivateAssets="all" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
            <PackageReference Include="coverlet.collector" Version="6.0.4" PrivateAssets="all" />
            <ProjectReference Include="../Library/Library.csproj" /></ItemGroup></Project>
            """);
        fixture.Write("Tests/Tests.cs", """
            public class ActualFrameworkTests
            {
                [Xunit.Fact] public void ExecutesItsOwnCompiledBranchOnce()
                {
            #if NET9_0
                    const int expected = 9;
            #else
                    const int expected = 10;
            #endif
                    Xunit.Assert.Equal(expected, Library.Value());
                    System.IO.File.AppendAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "test-count.txt"), expected + "\n");
                }
            }
            """);
        var before = File.ReadAllBytes(source);
        var token = TestContext.Current.CancellationToken;
        var timeout = TimeSpan.FromMinutes(3);
        var request = new ProjectContextLoadRequest(project, "Debug", "AnyCPU", ["net9.0", "net10.0"], false, false, timeout)
            { NativeSdkIdentityProbe = global::App.DefaultNativeSdkIdentity, WarmPostBuildProbe = true };
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, token)).ExitCode);
        var runners = await ProjectContextLoader.DiscoverTestRunnersAsync(request, token);
        var graph = await ProjectContextLoader.LoadGraphAsync(request, token);
        Assert.True(graph.Success, graph.FailureReason + " | " + string.Join(" | ", graph.Diagnostics));
        var plan = CheckExecutionPlan.Create(runners, graph, token);
        Assert.Equal(4, plan.PreparedGraph.BuildGraph.Count);
        Assert.Equal(2, plan.Tests.Count);
        Assert.Equal(new[] { "net10.0", "net9.0" }, plan.Tests.Select(t => t.TargetFramework).Order(StringComparer.Ordinal));
        var diagnostic = Path.Combine(TestValidationOutput.CreateDirectory("multi-framework", fixture.Path), "reference-observations.jsonl");
        File.WriteAllText(diagnostic, "");
        var bound = await CompilerGraphExecution.RunBoundAsync(request, plan.PreparedGraph, async (node, cancellation) =>
        {
            var directory = Path.Combine(fixture.Path, "compiler", node.Id);
            Directory.CreateDirectory(directory);
            CompilerObservation observation;
            try { observation = await CompilerEvidence.BuildAsync(node, directory, timeout, cancellation, request.NativeSdkIdentityProbe); }
            catch
            {
                foreach (var name in new[] { "stdout.log", "stderr.log" })
                    if (File.Exists(Path.Combine(directory, name)))
                        File.Copy(Path.Combine(directory, name), Path.Combine(Path.GetDirectoryName(diagnostic)!,
                            "multi-framework-failed-" + Path.GetFileNameWithoutExtension(node.ProjectPath) + "-" + node.TargetFramework + "-" + name), true);
                throw;
            }
            var context = plan.PreparedGraph.GraphContexts.Single(b => b.GraphId == node.Id).Context;
            var expected = context.ReferencePaths.Concat(node.Dependencies.Select(id =>
                plan.PreparedGraph.GraphContexts.Single(b => b.GraphId == id).Context.EvaluatedReferenceAssemblyPath!))
                .ToHashSet(PathIdentityPolicy.Current.Comparer);
            var actual = observation.Parameters.Single(p => p.Name == "References").Items.Select(path =>
                Path.GetFullPath(path, Path.GetDirectoryName(node.ProjectPath)!)).ToHashSet(PathIdentityPolicy.Current.Comparer);
            await File.AppendAllTextAsync(diagnostic, System.Text.Json.JsonSerializer.Serialize(new
            {
                node.Id, node.ProjectPath, node.TargetFramework, node.Dependencies, node.GlobalProperties,
                graphs = plan.PreparedGraph.GraphContexts.Select(b => new { b.GraphId, b.ProjectPath,
                    b.Context.TargetFramework, b.Context.EvaluatedReferenceAssemblyPath }),
                prepared = context.ReferencePaths, projectReferenceOutputs = context.ProjectReferenceOutputs, actual = actual.ToArray(),
                missing = expected.Except(actual, PathIdentityPolicy.Current.Comparer).ToArray(),
                unexpected = actual.Except(expected, PathIdentityPolicy.Current.Comparer).ToArray()
            }) + "\n", cancellation);
            if (!expected.SetEquals(actual))
                Console.WriteLine("MULTI_FRAMEWORK_REFERENCE_DELTA " + System.Text.Json.JsonSerializer.Serialize(new
                    { framework = node.TargetFramework, project = Path.GetFileName(node.ProjectPath),
                        missing = expected.Except(actual, PathIdentityPolicy.Current.Comparer).ToArray(),
                        unexpected = actual.Except(expected, PathIdentityPolicy.Current.Comparer).ToArray() }));
            return observation;
        }, token);
        var owned = bound.ByGraphId();
        Assert.Equal(4, owned.Count);
        var production = bound.FinalGraph.GraphContexts.Where(b => !b.IsTestProject).ToArray();
        Assert.Equal(2, production.Length);
        Assert.Equal(2, production.Select(b => owned[b.GraphId].Assembly!.Path).Distinct(PathIdentityPolicy.Current.Comparer).Count());
        Assert.NotEqual(owned[production[0].GraphId].Assembly!.Sha256, owned[production[1].GraphId].Assembly!.Sha256);
        var directory = Path.Combine(fixture.Path, "test-results"); Directory.CreateDirectory(directory);
        var tests = await TestExecutionAdapter.RunVerifiedAsync(plan, bound, directory, timeout, token);
        Assert.Null(tests.Failure);
        Assert.Equal(2, tests.Count);
        Assert.All(tests, t => Assert.Equal(0, t.Process.ExitCode));
        var coverage = CoverageGraphBinding.ValidateAggregate(tests, bound, token);
        Assert.Equal(2, coverage.Invocations.Count);
        foreach (var binding in production)
            Assert.Contains(coverage.Invocations.SelectMany(i => i.Modules), m => m.GraphId == binding.GraphId);
        foreach (var name in new[] { "Library", "Tests" })
            Assert.Equal(new[] { "net10.0", "net9.0" }, File.ReadAllLines(Path.Combine(fixture.Path, name, "build-count.txt")).Order(StringComparer.Ordinal));
        Assert.Equal("9", Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Tests/bin/Debug/net9.0/test-count.txt"))));
        Assert.Equal("10", Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Tests/bin/Debug/net10.0/test-count.txt"))));
        Assert.Equal(before, File.ReadAllBytes(source));
    }
}
