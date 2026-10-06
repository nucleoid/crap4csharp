using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class RuntimeDependencyCopyTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("before")]
    [InlineData("after")]
    public async Task ActualCopiedDependencyMustMatchIndependentCompilerBeforeAndAfterTests(string drift)
    {
        using var fixture = TestDirectory.Create("crap4csharp-runtime-copy");
        fixture.Write("Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        fixture.Write("Library/Code.cs", "public static class Library { public static int Value => 42; }");
        var project = fixture.Write("Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
            <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" PrivateAssets="all" />
            <PackageReference Include="xunit.v3" Version="3.2.2" PrivateAssets="all" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
            <PackageReference Include="coverlet.collector" Version="6.0.4" PrivateAssets="all" />
            <ProjectReference Include="../Library/Library.csproj" /></ItemGroup>
            <Target Name="DriftAfterTests" AfterTargets="VSTest" Condition="'$(DesignTimeBuild)' != 'true' and 'DRIFT' == 'after'">
            <WriteLinesToFile File="$(TargetDir)Library.dll" Lines="copied dependency drift" Overwrite="true" />
            <Touch Files="$(TargetDir)Library.dll" Time="2000-01-01T00:00:00Z" /></Target></Project>
            """.Replace("DRIFT", drift, StringComparison.Ordinal));
        fixture.Write("Tests/Tests.cs", "using Xunit; public class Once { [Fact] public void Pass() { Assert.Equal(42, Library.Value); } }");
        var token = TestContext.Current.CancellationToken;
        var request = new ProjectContextLoadRequest(project, "Debug", "AnyCPU", [], false, false, TimeSpan.FromMinutes(3));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, token)).ExitCode);
        var discovery = await ProjectContextLoader.DiscoverTestRunnersAsync(request, token);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, token);
        Assert.True(loaded.Success, loaded.FailureReason + string.Join(" | ", loaded.Diagnostics));
        var plan = CheckExecutionPlan.Create(discovery, loaded, token);
        var bound = await CompilerGraphExecution.RunBoundAsync(request, plan.PreparedGraph, async (node, cancellation) =>
        {
            var directory = Path.Combine(fixture.Path, "build", node.Id); Directory.CreateDirectory(directory);
            return await CompilerEvidence.BuildAsync(node, directory, TimeSpan.FromMinutes(3), cancellation);
        }, token);
        var selected = Assert.Single(bound.FinalGraph.GraphContexts, b => b.IsTestProject);
        var library = Assert.Single(bound.FinalGraph.GraphContexts, b => !b.IsTestProject);
        var destination = Path.Combine(Path.GetDirectoryName(selected.Context.EvaluatedTargetPath!)!, "Library.dll");
        var copyObservation = Assert.Single(bound.Observations, o => o.ProjectPath == selected.ProjectPath);
        var actualPair = Assert.Single(copyObservation.RuntimeCopies, c => c.Source == library.Context.EvaluatedTargetPath);
        Assert.Equal(destination, actualPair.Destination); // Actual SDK event destination, not an inferred validation path.
        var original = File.ReadAllBytes(destination);
        Assert.Equal(File.ReadAllBytes(library.Context.EvaluatedTargetPath!), original);
        var clock = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(destination, clock);
        var results = Path.Combine(fixture.Path, "test-run"); Directory.CreateDirectory(results);
        if (drift == "before")
        {
            File.WriteAllBytes(destination, [..original, 0]); File.SetLastWriteTimeUtc(destination, clock);
            Assert.Equal(clock, File.GetLastWriteTimeUtc(destination));
            var error = await Assert.ThrowsAsync<ProjectContextException>(() =>
                TestExecutionAdapter.RunVerifiedAsync(plan, bound, results, TimeSpan.FromMinutes(3), token));
            Assert.Equal("compiler.runtimeCopiesUnproven", error.Reason);
            Assert.Empty(Directory.EnumerateFileSystemEntries(results));
            return;
        }
        var run = await TestExecutionAdapter.RunVerifiedAsync(plan, bound, results, TimeSpan.FromMinutes(3), token);
        Assert.Single(run); Assert.Equal(0, run[0].Process.ExitCode);
        Assert.Equal("completed", Assert.Single(run.Outcomes).State);
        Assert.Equal(clock, File.GetLastWriteTimeUtc(destination));
        Assert.Equal(original, File.ReadAllBytes(library.Context.EvaluatedTargetPath!));
        if (drift == "after")
        {
            Assert.NotEqual(original, File.ReadAllBytes(destination));
            Assert.Equal("compiler.runtimeCopiesUnproven", Assert.IsType<ProjectContextException>(run.Failure).Reason);
            Assert.True(File.Exists(Path.Combine(run.Outcomes[0].LogDirectory!, "stdout.log")));
        }
        else
        {
            Assert.Null(run.Failure); Assert.Equal(original, File.ReadAllBytes(destination));
            var safe = TestChildEvidenceExport.Capture(run[0], run.Outcomes[0], token);
            using (var document = System.Text.Json.JsonDocument.Parse(safe.ToArray()))
            {
                Assert.Equal(0, document.RootElement.GetProperty("compilerTasks").GetInt32());
                Assert.False(document.RootElement.GetProperty("runtimeLoadingAttested").GetBoolean());
                Assert.False(document.RootElement.GetProperty("reusableRecipe").GetBoolean());
            }
            Assert.DoesNotContain(fixture.Path, System.Text.Encoding.UTF8.GetString(safe.AsSpan()), StringComparison.Ordinal);
            var log = Path.Combine(run.Outcomes[0].LogDirectory!, "test-child.binlog");
            var originalLog = File.ReadAllBytes(log); var logClock = File.GetLastWriteTimeUtc(log);
            try
            {
                File.WriteAllBytes(log, [..originalLog, 0]); File.SetLastWriteTimeUtc(log, logClock);
                Assert.Equal("test.childExportUnproven", Assert.Throws<ProjectContextException>(() =>
                    TestChildEvidenceExport.Capture(run[0], run.Outcomes[0], token)).Reason);
            }
            finally { File.WriteAllBytes(log, originalLog); File.SetLastWriteTimeUtc(log, logClock); }
            Assert.Equal(safe, TestChildEvidenceExport.Capture(run[0], run.Outcomes[0], token));
            var observations = bound.FinalGraph.GraphContexts.ToDictionary(b => b.GraphId,
                b => Assert.Single(bound.Observations, o => o.ProjectPath == b.ProjectPath), StringComparer.Ordinal);
            foreach (var bad in new[]
            {
                copyObservation with { RuntimeCopies = [] },
                copyObservation with { RuntimeCopies = [actualPair, actualPair] },
                copyObservation with { RuntimeCopies = [actualPair, actualPair with { Source = library.Context.EvaluatedReferenceAssemblyPath! }] },
                copyObservation with { RuntimeCopies = [actualPair with { Destination = library.Context.EvaluatedTargetPath! }] },
                copyObservation with { RuntimeCopies = [actualPair with { Source = library.Context.EvaluatedReferenceAssemblyPath! }] }
            })
            {
                observations[selected.GraphId] = bad;
                Assert.Equal("compiler.runtimeCopiesUnproven", Assert.Throws<ProjectContextException>(() =>
                    RuntimeCopyBinding.Validate(bound.FinalGraph, observations, token)).Reason);
            }
            observations[selected.GraphId] = copyObservation;
            RuntimeCopyBinding.Validate(bound.FinalGraph, observations, token);
            Assert.IsType<BoundGraphCoverage>(CoverageGraphBinding.ValidateAggregate(run, bound, token));
            var missing = new TestExecutionRun([run[0] with { ChildBuild = null }], run.Outcomes, null);
            Assert.Equal("coverage.aggregateUnproven", Assert.Throws<ProjectContextException>(() =>
                CoverageGraphBinding.ValidateAggregate(missing, bound, token)).Reason);
            var wrongProject = new TestExecutionRun([run[0] with { ChildBuild = run[0].ChildBuild! with { ProjectPath = library.ProjectPath } }], run.Outcomes, null);
            Assert.Equal("coverage.aggregateUnproven", Assert.Throws<ProjectContextException>(() =>
                CoverageGraphBinding.ValidateAggregate(wrongProject, bound, token)).Reason);
            var repeated = new TestExecutionRun([run[0] with { ChildBuild = run[0].ChildBuild! with { CompilerTasks = 1 } }], run.Outcomes, null);
            Assert.Equal("coverage.aggregateUnproven", Assert.Throws<ProjectContextException>(() =>
                CoverageGraphBinding.ValidateAggregate(repeated, bound, token)).Reason);
        }
    }
}
