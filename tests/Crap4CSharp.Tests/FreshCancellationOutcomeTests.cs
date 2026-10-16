using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class FreshCancellationOutcomeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicCancellationRetainsActualCompletedInterruptedAndNotRunContexts(bool failFirst)
    {
        using var fixture = TestDirectory.Create("crap4csharp-public-cancel-outcomes");
        fixture.Write("Dep/Dep.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var source = fixture.Write("Dep/Code.cs", "public static class Dependency { public static int Value => 42; }");
        var order = Path.Combine(fixture.Path, "order.txt");
        var ready = Path.Combine(fixture.Path, "ready.txt");
        foreach (var name in new[] { "First", "Second", "Third" })
        {
            fixture.Write(name + "/" + name + ".csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
                <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" PrivateAssets="all" />
                <PackageReference Include="xunit.v3" Version="3.2.2" PrivateAssets="all" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
                <PackageReference Include="coverlet.collector" Version="6.0.4" PrivateAssets="all" />
                <ProjectReference Include="../Dep/Dep.csproj" /></ItemGroup></Project>
                """);
            fixture.Write(name + "/Tests.cs", "using Xunit; public class Once { [Fact] public void Test() { Assert.Equal(42, Dependency.Value); " +
                "System.IO.File.AppendAllText(" + JsonSerializer.Serialize(order) + ", " + JsonSerializer.Serialize(name + "\n") + "); " +
                "if (System.IO.File.ReadAllLines(" + JsonSerializer.Serialize(order) + ").Length == 2) { System.IO.File.WriteAllText(" + JsonSerializer.Serialize(ready) + ", \"ready\"); System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite); } " +
                (failFirst ? "if (System.IO.File.ReadAllLines(" + JsonSerializer.Serialize(order) + ").Length == 1) Assert.Fail(\"Real first-context failure\"); " : "") + "} }");
        }
        fixture.Write("Root/Root.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><ProjectReference Include="../First/First.csproj" /><ProjectReference Include="../Second/Second.csproj" /><ProjectReference Include="../Third/Third.csproj" /></ItemGroup></Project>
            """);
        fixture.Write("Root/Code.cs", "public class Root {}");
        fixture.Write("policy.json", """
            {"schemaVersion":"repository-policy-v1","mode":"strict","productionProjects":["Dep/Dep.csproj","Root/Root.csproj"],
            "testProjects":["First/First.csproj","Second/Second.csproj","Third/Third.csproj"],"configuration":"Debug","targetFrameworks":["net10.0"],"scope":"all",
            "threshold":8,"missingCoverage":"fail","requiredChecks":["tests","coverage","crap"],"exclusions":[],"ruleset":"callables-v1"}
            """);
        var before = File.ReadAllBytes(source);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var observerStop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var observer = CancelWhenSecondStarts();
        async Task CancelWhenSecondStarts()
        {
            while (!File.Exists(ready)) await Task.Delay(100, observerStop.Token);
            cancellation.Cancel();
        }
        var output = new StringWriter();
        int exit;
        try
        {
            exit = await global::App.RunAsync(["check", "--project", "Root/Root.csproj", "--policy", "policy.json", "--scope", "all",
                "--timeout-seconds", "600", "--format", "json", "--artifact-output", "bundle"],
                fixture.Path, output, new StringWriter(), cancellation.Token);
        }
        finally
        {
            observerStop.Cancel();
            try { await observer; } catch (OperationCanceledException) when (observerStop.IsCancellationRequested) { }
        }
        Assert.True(File.Exists(ready), output.ToString());
        var launched = File.ReadAllLines(order);
        Assert.Equal(2, launched.Length);
        Assert.Equal(2, launched.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(1, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(failFirst ? "FAIL" : "INCOMPLETE", InstalledCheckTests.NormalizeDeliveryForgeGate(
            json.RootElement, exit, global::App.ToolVersion, "fixture-base"));
        Assert.Equal(global::App.ToolVersion, json.RootElement.GetProperty("toolVersion").GetString());
        Assert.Equal(".", json.RootElement.GetProperty("evaluation").GetProperty("scope").GetProperty("logicalWorkspaceRoot").GetString());
        var run = json.RootElement.GetProperty("run");
        Assert.True(run.GetProperty("cancellation").GetProperty("cancelled").GetBoolean());
        Assert.False(run.GetProperty("cancellation").GetProperty("timedOut").GetBoolean());
        Assert.Equal("run.cancelled", run.GetProperty("cancellation").GetProperty("reason").GetString());
        Assert.Equal(failFirst ? "tests.failed" : "run.cancelled", json.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("reason").GetString());
        Assert.Contains(json.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == "testExecution" &&
                check.GetProperty("status").GetString() == (failFirst ? "fail" : "cancelled"));
        var outcomes = run.GetProperty("testOutcomes").EnumerateArray().ToArray();
        Assert.Equal(3, outcomes.Length);
        Assert.Equal(launched, outcomes.Take(2).Select(o => Path.GetFileNameWithoutExtension(o.GetProperty("project").GetString())));
        Assert.DoesNotContain(Path.GetFileNameWithoutExtension(outcomes[2].GetProperty("project").GetString()), launched);
        Assert.Equal(new[] { failFirst ? "failed" : "completed", "failed", "not-run" }, outcomes.Select(o => o.GetProperty("status").GetString()));
        Assert.Equal(failFirst ? 1 : 0, outcomes[0].GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, outcomes[1].GetProperty("exitCode").ValueKind);
        var firstTrx = Assert.Single(Directory.GetFiles(outcomes[0].GetProperty("resultsDirectory").GetString()!, "*.trx"));
        var counters = System.Xml.Linq.XDocument.Load(firstTrx).Descendants().Single(element => element.Name.LocalName == "Counters");
        Assert.Equal(failFirst ? "1" : "0", counters.Attribute("failed")!.Value);
        Assert.True(Directory.Exists(outcomes[1].GetProperty("logDirectory").GetString()));
        Assert.All(run.GetProperty("artifacts").EnumerateArray(), a =>
        {
            Assert.False(a.GetProperty("complete").GetBoolean()); Assert.False(a.GetProperty("reusable").GetBoolean());
            Assert.NotEqual("manifest", a.GetProperty("kind").GetString());
        });
        var diagnostics = Assert.Single(run.GetProperty("artifacts").EnumerateArray(),
            a => a.GetProperty("kind").GetString() == "private-diagnostics");
        var privateRoot = diagnostics.GetProperty("path").GetString()!;
        Assert.True(Directory.Exists(privateRoot));
        PrivateDiagnosticsCleanupTests.AssertSealed(privateRoot);
        Assert.Contains(run.GetProperty("diagnosticLocations").EnumerateArray(), p => p.GetString() == privateRoot);
        Assert.NotEmpty(Directory.GetFiles(privateRoot, "compiler.binlog", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
        Assert.Equal(before, File.ReadAllBytes(source));
    }
}
