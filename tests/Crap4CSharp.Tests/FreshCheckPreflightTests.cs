using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class FreshCheckPreflightTests
{
    [Theory]
    [InlineData("staged", "scope.executionSnapshotUnsupported")]
    [InlineData("head", "scope.executionSnapshotUnsupported")]
    [InlineData("threshold", "policy.thresholdOverrideForbidden")]
    [InlineData("configuration", "policy.configurationOverrideForbidden")]
    [InlineData("exemption", "policy.exemptionOverrideForbidden")]
    [InlineData("output", "output.aliasesPolicyInput")]
    [InlineData("artifact", "output.aliasesPolicyInput")]
    public async Task PublicFreshCheckRejectsUnsafePreflightBeforeAnyConsumerEvaluation(string attack, string expected)
    {
        using var fixture = TestDirectory.Create("crap4csharp-fresh-preflight");
        var policy = fixture.Write("policy.json", """
            {"schemaVersion":"repository-policy-v1","mode":"strict","productionProjects":["App/App.csproj"],
             "testProjects":["Tests/Tests.csproj"],"configuration":"Debug","targetFrameworks":["net10.0"],
             "scope":"base","threshold":8,"missingCoverage":"fail","requiredChecks":["tests","coverage","crap"],
             "exclusions":[],"ruleset":"callables-v1","exemptionFiles":[]}
            """);
        // A consumer evaluation would fail here, never yield the requested preflight reason.
        var project = fixture.Write("App/App.csproj", "<Project><Error Condition=\"'$(DesignTimeBuild)' == 'true'\" Text=\"CONSUMER_MUST_NOT_LOAD\" /></Project>");
        fixture.Write("Tests/Tests.csproj", "deliberately invalid project");
        fixture.Write(".gitignore", "bin/\nobj/\n");
        var token = TestContext.Current.CancellationToken;
        async Task Git(params string[] args) => Assert.Equal(0, (await ProcessRunner.RunAsync("git", args, fixture.Path, TimeSpan.FromSeconds(30), token)).ExitCode);
        await Git("init", "-q"); await Git("config", "user.email", "fixture@example.invalid");
        await Git("config", "user.name", "Fixture"); await Git("add", "."); await Git("commit", "-qm", "trusted policy");
        // Branch policy cannot approve a weaker override.
        File.WriteAllText(policy, File.ReadAllText(policy).Replace("\"threshold\":8", "\"threshold\":99", StringComparison.Ordinal));
        var beforePolicy = File.ReadAllBytes(policy); var beforeProject = File.ReadAllBytes(project);
        var arguments = new List<string> { "check", "--project", "App/App.csproj", "--policy", "policy.json", "--base", "HEAD", "--format", "json" };
        arguments.AddRange(attack switch
        {
            "staged" => ["--scope", "staged"], "head" => ["--source-state", "head"],
            "threshold" => ["--threshold", "9"], "configuration" => ["--configuration", "Release"],
            "exemption" => ["--callable-exemptions", "branch-waiver.json"],
            "output" => ["--output", "App/App.csproj"], "artifact" => ["--artifact-output", "App"],
            _ => throw new InvalidOperationException()
        });
        var output = new StringWriter(); var errors = new StringWriter();
        Assert.Equal(1, await global::App.RunAsync(arguments.ToArray(), fixture.Path, output, errors, token));
        using var terminal = JsonDocument.Parse(output.ToString());
        Assert.Equal(expected, terminal.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("reason").GetString());
        Assert.Equal(beforePolicy, File.ReadAllBytes(policy)); Assert.Equal(beforeProject, File.ReadAllBytes(project));
        Assert.Empty(Directory.GetDirectories(fixture.Path, "obj", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(fixture.Path, "bin", SearchOption.AllDirectories));
        Assert.DoesNotContain("CONSUMER_MUST_NOT_LOAD", errors.ToString(), StringComparison.Ordinal);
    }
}
