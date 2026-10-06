using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class TestChildCompilationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualVSTestChildRequiresIndependentObservedNoCompilation(bool compileInsideTest)
    {
        using var fixture = TestDirectory.Create("crap4csharp-test-child-compilation");
        var project = fixture.Write("Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable>
            <CompileInsideTest>COMPILE</CompileInsideTest></PropertyGroup>
            <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" PrivateAssets="all" />
            <PackageReference Include="xunit.v3" Version="3.2.2" PrivateAssets="all" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
            <PackageReference Include="coverlet.collector" Version="6.0.4" PrivateAssets="all" /></ItemGroup>
            <Target Name="CountActualCompileTarget" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/compile-target-count.txt" Lines="compile-target" Overwrite="false" /></Target>
            <Target Name="RepeatInsideVSTest" BeforeTargets="VSTest" Condition="'$(DesignTimeBuild)' != 'true' and '$(CompileInsideTest)' == 'true'">
            <PropertyGroup><OriginalSourceClock>ORIGINALCLOCK</OriginalSourceClock></PropertyGroup>
            <Touch Files="$(MSBuildProjectDirectory)/Tests.cs" />
            <MSBuild Projects="$(MSBuildProjectFullPath)" Targets="Build" BuildInParallel="false"
                Properties="BuildProjectReferences=false;UseSharedCompilation=false;VSTestNoBuild=true" />
            <Touch Files="$(MSBuildProjectDirectory)/Tests.cs" Time="$(OriginalSourceClock)" />
            </Target></Project>
            """.Replace("COMPILE", compileInsideTest ? "true" : "false", StringComparison.Ordinal));
        var source = fixture.Write("Tests.cs", "using Xunit; public class Once { [Fact] public void Pass() { Assert.True(true); } }");
        var originalSource = File.ReadAllBytes(source); var originalClock = File.GetLastWriteTimeUtc(source);
        File.WriteAllText(project, File.ReadAllText(project).Replace("ORIGINALCLOCK",
            originalClock.ToString("O", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
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
        var output = bound.FinalGraph.GraphContexts.Single().Context.EvaluatedTargetPath!;
        var before = File.ReadAllBytes(output);
        var destination = Path.Combine(fixture.Path, "test-run"); Directory.CreateDirectory(destination);
        var run = await TestExecutionAdapter.RunVerifiedAsync(plan, bound, destination, TimeSpan.FromMinutes(3), token);
        Assert.Single(run);
        Assert.True(run[0].Process.ExitCode == 0,
            System.Text.Encoding.UTF8.GetString(run[0].Process.StandardOutput.Tail) + "\n" +
            System.Text.Encoding.UTF8.GetString(run[0].Process.StandardError.Tail));
        Assert.Equal(originalSource, File.ReadAllBytes(source)); Assert.Equal(originalClock, File.GetLastWriteTimeUtc(source));
        Assert.Equal(before, File.ReadAllBytes(output));
        // This target counter is not itself an actual Csc observation. The new private
        // selected-SDK child binlog adapter must supply that independent proof.
        Assert.Equal(compileInsideTest ? 2 : 1, File.ReadAllLines(Path.Combine(fixture.Path, "compile-target-count.txt")).Length);
        if (compileInsideTest)
            Assert.Equal("test.compilationObserved", Assert.IsType<ProjectContextException>(run.Failure).Reason);
        else Assert.Null(run.Failure);
        // After genuine RED: inspect the new attached actual child observation and assert
        // one mapped VSTest task, actual Csc count 1/0, and bounded owned raw log identity.
    }
}
