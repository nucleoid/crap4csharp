using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class FreshCancellationOutcomeTests
{
    [Fact]
    public async Task PublicCancellationRetainsActualCompletedInterruptedAndNotRunContexts()
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
                (name == "Second" ? "System.IO.File.WriteAllText(" + JsonSerializer.Serialize(ready) + ", \"ready\"); System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite); " : "") + "} }");
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
        Assert.Equal(new[] { "First", "Second" }, File.ReadAllLines(order));
        Assert.Equal(1, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var run = json.RootElement.GetProperty("run");
        Assert.True(run.GetProperty("cancellation").GetProperty("cancelled").GetBoolean());
        Assert.False(run.GetProperty("cancellation").GetProperty("timedOut").GetBoolean());
        Assert.Equal("run.cancelled", json.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("reason").GetString());
        var outcomes = run.GetProperty("testOutcomes").EnumerateArray().ToArray();
        Assert.Equal(3, outcomes.Length);
        Assert.Equal(new[] { "completed", "failed", "not-run" }, outcomes.Select(o => o.GetProperty("status").GetString()));
        Assert.Equal(0, outcomes[0].GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, outcomes[1].GetProperty("exitCode").ValueKind);
        Assert.Single(Directory.GetFiles(outcomes[0].GetProperty("resultsDirectory").GetString()!, "*.trx"));
        Assert.True(Directory.Exists(outcomes[1].GetProperty("logDirectory").GetString()));
        Assert.All(run.GetProperty("artifacts").EnumerateArray(), a =>
        {
            Assert.False(a.GetProperty("complete").GetBoolean()); Assert.False(a.GetProperty("reusable").GetBoolean());
            Assert.NotEqual("manifest", a.GetProperty("kind").GetString());
        });
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
        Assert.Equal(before, File.ReadAllBytes(source));
    }
}
