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
    [InlineData("framework", "policy.frameworkOverrideForbidden")]
    [InlineData("prior-missing", "arguments.invalid")]
    [InlineData("prior-orphan", "arguments.invalid")]
    [InlineData("unsafe-output-new", "scope.executionSnapshotUnsupported")]
    [InlineData("noncurrent", "scope.nonCurrentHeadWorktree")]
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
        await Git("commit", "--allow-empty", "-qm", "current head");
        // Branch policy cannot approve a weaker override.
        File.WriteAllText(policy, File.ReadAllText(policy).Replace("\"threshold\":8", "\"threshold\":99", StringComparison.Ordinal));
        var beforePolicy = File.ReadAllBytes(policy); var beforeProject = File.ReadAllBytes(project);
        var valid = new FreshCheckRequest("App/App.csproj", "policy.json", "HEAD", null, null, "worktree", "method",
            "result.json", "new-bundle", false, false, null, new PolicyOverrides(Threshold: 7), TimeSpan.FromSeconds(30));
        var preflight = await FreshCheckPreflightLoader.LoadAsync(valid, fixture.Path, token);
        Assert.Equal(8, preflight.Trusted.Policy.Policy.Threshold); Assert.Equal(7, preflight.Policy.Threshold);
        Assert.Equal(preflight.Trusted.Revision, preflight.Scope!.Revision.MergeBase);
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "new-bundle")));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "result.json")));
        var arguments = new List<string> { "check", "--project", "App/App.csproj", "--policy", "policy.json", "--base", "HEAD", "--format", "json" };
        arguments.AddRange(attack switch
        {
            "staged" => ["--scope", "staged"], "head" => ["--source-state", "head"],
            "threshold" => ["--threshold", "9"], "configuration" => ["--configuration", "Release"],
            "exemption" => ["--callable-exemptions", "branch-waiver.json"],
            "output" => ["--output", "App/App.csproj"], "artifact" => ["--artifact-output", "App"],
            "framework" => ["--framework", "net9.0"], "prior-missing" => ["--no-build"],
            "prior-orphan" => ["--build-artifacts", "warm-manifest.json"],
            "unsafe-output-new" => ["--scope", "staged", "--output", "App/Generated.cs"],
            "noncurrent" => ["--head", "HEAD~1"],
            _ => throw new InvalidOperationException()
        });
        var output = new StringWriter(); var errors = new StringWriter();
        Assert.Equal(1, await global::App.RunAsync(arguments.ToArray(), fixture.Path, output, errors, token));
        using var terminal = JsonDocument.Parse(output.ToString());
        Assert.Equal(expected, terminal.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("reason").GetString());
        Assert.DoesNotContain(terminal.RootElement.GetProperty("run").GetProperty("artifacts").EnumerateArray(),
            a => a.GetProperty("kind").GetString() == "private-diagnostics");
        Assert.Equal(beforePolicy, File.ReadAllBytes(policy)); Assert.Equal(beforeProject, File.ReadAllBytes(project));
        Assert.Empty(Directory.GetDirectories(fixture.Path, "obj", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(fixture.Path, "bin", SearchOption.AllDirectories));
        Assert.DoesNotContain("CONSUMER_MUST_NOT_LOAD", errors.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.Path, "App/Generated.cs")));
    }
}
