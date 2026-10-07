using System.Security;
using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class FreshDeadlineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicWholeDeadlineIsNotReportedAsUserCancellation(bool userCancellation)
    {
        using var fixture = TestDirectory.Create("crap4csharp-deadline");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.Combine(
            Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? throw new InvalidOperationException("Missing host"),
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var helper = Path.Combine(AppContext.BaseDirectory, "process-helper", "Helper.dll");
        var command = string.Join(" ", new[] { host, helper, "hold", Path.Combine(fixture.Path, "ready.json"),
            Path.Combine(fixture.Path, "release") }.Select(v => "\"" + v + "\""));
        // Metadata remains normal. If the SDK reaches restore before the deadline,
        // an owned portable child holds restore rather than allowing a fast failure.
        fixture.Write("Tests/Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><ProjectReference Include=\"../App.csproj\" /></ItemGroup>" +
            "<Target Name=\"HoldRestore\" BeforeTargets=\"Restore\"><Exec Command=\"" + SecurityElement.Escape(command) + "\" /></Target></Project>");
        fixture.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Remove=\"Tests/**/*.cs\" /></ItemGroup></Project>");
        fixture.Write("Code.cs", "public class Code { public int Value => 42; }");
        fixture.Write("policy.json", """
            {"schemaVersion":"repository-policy-v1","mode":"strict","productionProjects":["App.csproj"],
            "testProjects":["Tests/Tests.csproj"],"configuration":"Debug","targetFrameworks":["net10.0"],"scope":"all",
            "threshold":8,"missingCoverage":"fail","requiredChecks":["tests","coverage","crap"],"exclusions":[],"ruleset":"callables-v1"}
            """);
        var source = File.ReadAllBytes(Path.Combine(fixture.Path, "Code.cs"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (userCancellation) cancellation.CancelAfter(TimeSpan.FromSeconds(1));
        var output = new StringWriter();
        var exit = await global::App.RunAsync(["check", "--project", "Tests/Tests.csproj", "--policy", "policy.json", "--scope", "all",
            "--timeout-seconds", userCancellation ? "30" : "1", "--format", "json"], fixture.Path, output, new StringWriter(), cancellation.Token);
        Assert.Equal(1, exit);
        using var result = JsonDocument.Parse(output.ToString());
        var run = result.RootElement.GetProperty("run");
        Assert.Equal(userCancellation ? "run.cancelled" : "run.timedOut",
            result.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("reason").GetString());
        Assert.Equal(userCancellation, run.GetProperty("cancellation").GetProperty("cancelled").GetBoolean());
        Assert.Equal(!userCancellation, run.GetProperty("cancellation").GetProperty("timedOut").GetBoolean());
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(fixture.Path, "Code.cs")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
    }
}
