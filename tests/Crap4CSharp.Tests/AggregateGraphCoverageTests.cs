using System.Reflection;
using System.Xml.Linq;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class AggregateGraphCoverageTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task ActualRequiredTestClosuresRequireAggregateEligibleMembership(bool includeTests, bool uncoveredRoot,
        bool compactSources, bool sharedDependency)
    {
        using var fixture = TestDirectory.Create("crap4csharp-aggregate-contexts");
        foreach (var name in new[] { "Left", "Right" })
        {
            fixture.Write(name + "/" + name + ".csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            fixture.Write(name + "/Code.cs", compactSources
                ? "public class " + name + " { public static int Value => 42; public static int Unused => 43; }"
                : "public class " + name + "\n{\n    public static int Value => 42;\n    public static int Unused => 43;\n}\n");
            fixture.Write(name + "Tests/" + name + "Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
                <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" PrivateAssets="all" />
                <PackageReference Include="xunit.v3" Version="3.2.2" PrivateAssets="all" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
                <PackageReference Include="coverlet.collector" Version="6.0.4" PrivateAssets="all" />
                <ProjectReference Include="../DEPENDENCY/DEPENDENCY.csproj" /></ItemGroup></Project>
                """.Replace("DEPENDENCY", sharedDependency ? "Left" : name, StringComparison.Ordinal));
            var dependency = sharedDependency ? "Left" : name;
            var complementary = sharedDependency && name == "Right";
            fixture.Write(name + "Tests/Tests.cs", "using Xunit; public class " + name + "Assertions { [Fact] public void Once() { Assert.Equal(" +
                (complementary ? "43" : "42") + ", " + dependency + (complementary ? ".Unused" : ".Value") + "); } }");
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
        var phase = "initial";
        object Bind()
        {
            TestContext.Current.TestOutputHelper!.WriteLine("AGGREGATE_PHASE " + phase);
            return binder.Invoke(null, [run, bound, token])!;
        }
        foreach (var invocation in run)
        {
            var association = CoverageGraphBinding.ValidateInvocation(invocation, bound, token);
            TestContext.Current.TestOutputHelper!.WriteLine("ACTUAL_ASSOCIATIONS " + string.Join(" | ", association.Modules.Select(m =>
                m.GraphId + ":" + string.Join(",", m.ActualMethodTokens))));
            TestContext.Current.TestOutputHelper!.WriteLine("ACTUAL_REPORTED_METHODS " + string.Join(" | ",
                XDocument.Parse(System.Text.Encoding.UTF8.GetString(association.Attachment.Bytes.AsSpan()))
                    .Descendants("Method").Select(m => m.Element("Name")!.Value)));
        }
        if (uncoveredRoot || compactSources)
        {
            var failure = Assert.Throws<TargetInvocationException>(() => Bind());
            Assert.Equal("coverage.aggregateUnproven", Assert.IsType<ProjectContextException>(failure.InnerException).Reason);
            return;
        }
        var aggregate = Assert.IsType<BoundGraphCoverage>(Bind());
        Assert.Equal(2, aggregate.Invocations.Count);
        Assert.Equal(sharedDependency ? 1 : includeTests ? 4 : 2, aggregate.EligibleGraphIds.Count);
        if (sharedDependency)
        {
            var modules = aggregate.Invocations.SelectMany(i => i.Modules).Where(m =>
                !bound.FinalGraph.GraphContexts.Single(b => b.GraphId == m.GraphId).IsTestProject).ToArray();
            Assert.Equal(2, modules.Length);
            Assert.Equal(4, modules.Sum(m => m.ActualMethodTokens.Count));
            Assert.Equal(2, modules.SelectMany(m => m.ActualMethodTokens).Distinct().Count());
            var methods = modules.SelectMany(m => CoverageReader.Read(m.LineProjection.AsSpan(), "coverage.opencover.xml")).ToArray();
            Assert.Equal(1, methods.Count(m => m.MethodName == "get_Value" && m.SequencePoints.Any(p => p.Visits > 0)));
            Assert.Equal(1, methods.Count(m => m.MethodName == "get_Unused" && m.SequencePoints.Any(p => p.Visits > 0)));
        }
        Assert.All(aggregate.Invocations.SelectMany(i => i.Modules), module =>
            Assert.Equal(module.ActualMethodTokens.Count, module.ActualMethodTokens.Distinct().Count()));
        var originals = new List<(string Path, byte[] Bytes)>();
        foreach (var invocation in run)
        {
            var selected = Assert.Single(bound.FinalGraph.GraphContexts, b => b.GraphId == invocation.GraphId);
            var compiler = Assert.Single(bound.Observations, c => c.ProjectPath == selected.ProjectPath);
            var report = OwnedCoverageAttachment.ReadOpenCover(invocation, selected.Context, compiler, token);
            var original = File.ReadAllBytes(report.Path);
            originals.Add((report.Path, original));
            var document = XDocument.Load(report.Path);
            var names = document.Descendants("ModuleName").Select(e => e.Value).ToArray();
            var own = sharedDependency ? "Left" : selected.Context.AssemblyName.Replace("Tests", "", StringComparison.Ordinal);
            Assert.Contains(own, names);
            Assert.DoesNotContain(own == "Left" ? "Right" : "Left", names);
            Assert.Equal(includeTests, names.Contains(selected.Context.AssemblyName, StringComparer.Ordinal));
            // Remove a genuine compiler/PDB method from an otherwise valid owned report.
            // Module presence alone cannot turn that missing membership into zero coverage.
            document.Descendants("Module").Single(m => m.Element("ModuleName")!.Value == own)
                .Descendants("Method").Single(m => m.Element("Name")!.Value.Contains("get_Unused", StringComparison.Ordinal)).Remove();
            document.Save(report.Path);
            phase = "reject-missing-" + own;
            try
            {
                if (sharedDependency) Assert.IsType<BoundGraphCoverage>(Bind()); // Other actual report still represents the method.
                else
                {
                    var failure = Assert.Throws<TargetInvocationException>(() => Bind());
                    Assert.Equal("coverage.aggregateUnproven", Assert.IsType<ProjectContextException>(failure.InnerException).Reason);
                }
            }
            finally { File.WriteAllBytes(report.Path, original); }
        }
        if (sharedDependency)
        {
            phase = "reject-missing-in-all-shared-reports";
            try
            {
                foreach (var saved in originals)
                {
                    var document = XDocument.Load(saved.Path);
                    document.Descendants("Module").Single(m => m.Element("ModuleName")!.Value == "Left")
                        .Descendants("Method").Single(m => m.Element("Name")!.Value.Contains("get_Unused", StringComparison.Ordinal)).Remove();
                    document.Save(saved.Path);
                }
                var failure = Assert.Throws<TargetInvocationException>(() => Bind());
                Assert.Equal("coverage.aggregateUnproven", Assert.IsType<ProjectContextException>(failure.InnerException).Reason);
            }
            finally { foreach (var saved in originals) File.WriteAllBytes(saved.Path, saved.Bytes); }
        }
        phase = "restored-control";
        Bind();
        var missing = new TestExecutionRun(Array.AsReadOnly(run.Take(1).ToArray()), run.Outcomes, null);
        var duplicate = new TestExecutionRun(Array.AsReadOnly(new[] { run[0], run[0] }), run.Outcomes, null);
        var failedState = new TestExecutionRun(run, Array.AsReadOnly(run.Outcomes.Select((o, index) =>
            index == 0 ? o with { State = "failed" } : o).ToArray()), null);
        foreach (var invalid in new[] { missing, duplicate, failedState,
            new TestExecutionRun(run, run.Outcomes, new ProjectContextException("compiler.inputDrift", "refused")) })
        {
            var failure = Assert.Throws<TargetInvocationException>(() => binder.Invoke(null, [invalid, bound, token]));
            Assert.Equal("coverage.aggregateUnproven", Assert.IsType<ProjectContextException>(failure.InnerException).Reason);
        }
    }
}
