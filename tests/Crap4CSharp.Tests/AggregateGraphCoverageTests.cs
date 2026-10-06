using System.Reflection;
using System.Xml.Linq;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class AggregateGraphCoverageTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ActualIndependentTestClosuresRequireAggregateEligibleMembership(bool includeTests, bool uncoveredRoot)
    {
        using var fixture = TestDirectory.Create("crap4csharp-aggregate-contexts");
        foreach (var name in new[] { "Left", "Right" })
        {
            fixture.Write(name + "/" + name + ".csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            fixture.Write(name + "/Code.cs", "public class " + name + " { public static int Value => 42; public static int Unused => 43; }");
            fixture.Write(name + "Tests/" + name + "Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
                <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" PrivateAssets="all" />
                <PackageReference Include="xunit.v3" Version="3.2.2" PrivateAssets="all" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
                <PackageReference Include="coverlet.collector" Version="6.0.4" PrivateAssets="all" />
                <ProjectReference Include="../NAME/NAME.csproj" /></ItemGroup></Project>
                """.Replace("NAME", name, StringComparison.Ordinal));
            fixture.Write(name + "Tests/Tests.cs", "using Xunit; public class " + name + "Assertions { [Fact] public void Once() { Assert.Equal(42, " + name + ".Value); } }");
        }
        var root = fixture.Write("Root/Root.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><ProjectReference Include="../LeftTests/LeftTests.csproj" /><ProjectReference Include="../RightTests/RightTests.csproj" /></ItemGroup></Project>
            """);
        fixture.Write("Root/Code.cs", uncoveredRoot ? "public class Root { public static int Uncovered => 7; }" : "public class Root {}");
        var token = TestContext.Current.CancellationToken;
        var request = new ProjectContextLoadRequest(root, "Debug", "AnyCPU", [], includeTests, false, TimeSpan.FromMinutes(3));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, token)).ExitCode);
        var discovery = await ProjectContextLoader.DiscoverTestRunnersAsync(request, token);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, token);
        Assert.True(loaded.Success, loaded.FailureReason + string.Join(" | ", loaded.Diagnostics));
        var plan = CheckExecutionPlan.Create(discovery, loaded, token);
        Assert.Equal(2, plan.Tests.Count);
        var bound = await CompilerGraphExecution.RunBoundAsync(request, plan.PreparedGraph, async (node, cancellation) =>
        {
            var directory = Path.Combine(fixture.Path, "build", node.Id); Directory.CreateDirectory(directory);
            return await CompilerEvidence.BuildAsync(node, directory, TimeSpan.FromMinutes(3), cancellation);
        }, token);
        var destination = Path.Combine(fixture.Path, "test-run"); Directory.CreateDirectory(destination);
        var run = await TestExecutionAdapter.RunVerifiedAsync(plan, bound, destination, TimeSpan.FromMinutes(3), token);
        Assert.Null(run.Failure);
        Assert.Equal(2, run.Count);
        Assert.All(run.Outcomes, result => Assert.Equal("completed", result.State));
        var binder = typeof(ProjectContextLoader).Assembly.GetType("Crap4CSharp.Core.CoverageGraphBinding")!
            .GetMethod("ValidateAggregate", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(binder);
        object Bind() => binder.Invoke(null, [run, bound, token])!;
        if (uncoveredRoot)
        {
            var failure = Assert.Throws<TargetInvocationException>(() => Bind());
            Assert.Equal("coverage.aggregateUnproven", Assert.IsType<ProjectContextException>(failure.InnerException).Reason);
            return;
        }
        Bind();
        foreach (var invocation in run)
        {
            var selected = Assert.Single(bound.FinalGraph.GraphContexts, b => b.GraphId == invocation.GraphId);
            var compiler = Assert.Single(bound.Observations, c => c.ProjectPath == selected.ProjectPath);
            var report = OwnedCoverageAttachment.ReadOpenCover(invocation, selected.Context, compiler, token);
            var original = File.ReadAllBytes(report.Path);
            var document = XDocument.Load(report.Path);
            var names = document.Descendants("ModuleName").Select(e => e.Value).ToArray();
            var own = selected.Context.AssemblyName.Replace("Tests", "", StringComparison.Ordinal);
            Assert.Contains(own, names);
            Assert.DoesNotContain(own == "Left" ? "Right" : "Left", names);
            Assert.Equal(includeTests, names.Contains(selected.Context.AssemblyName, StringComparer.Ordinal));
            // Remove a genuine compiler/PDB method from an otherwise valid owned report.
            // Module presence alone cannot turn that missing membership into zero coverage.
            document.Descendants("Module").Single(m => m.Element("ModuleName")!.Value == own)
                .Descendants("Method").Single(m => m.Element("Name")!.Value.Contains("get_Unused", StringComparison.Ordinal)).Remove();
            document.Save(report.Path);
            try
            {
                var failure = Assert.Throws<TargetInvocationException>(() => Bind());
                Assert.Equal("coverage.aggregateUnproven", Assert.IsType<ProjectContextException>(failure.InnerException).Reason);
            }
            finally { File.WriteAllBytes(report.Path, original); }
        }
        Bind();
        var missing = new TestExecutionRun(Array.AsReadOnly(run.Take(1).ToArray()), run.Outcomes, null);
        var duplicate = new TestExecutionRun(Array.AsReadOnly(new[] { run[0], run[0] }), run.Outcomes, null);
        foreach (var invalid in new[] { missing, duplicate, new TestExecutionRun(run, run.Outcomes, new ProjectContextException("compiler.inputDrift", "refused")) })
        {
            var failure = Assert.Throws<TargetInvocationException>(() => binder.Invoke(null, [invalid, bound, token]));
            Assert.Equal("coverage.aggregateUnproven", Assert.IsType<ProjectContextException>(failure.InnerException).Reason);
        }
    }
}
