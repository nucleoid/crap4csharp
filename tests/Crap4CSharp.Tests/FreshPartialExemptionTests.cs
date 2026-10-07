using System.Text.Json;
using System.Text.Json.Nodes;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class FreshPartialExemptionTests
{
    [Fact]
    public async Task RealFailedAsyncTestsHonorOnlyExactReviewedUnsupportedExemptions()
        => await RunCase(false);

    [Fact]
    public async Task RealFailedLambdaTestsRequireExactReviewedAnonymousMemberCount()
        => await RunCase(true);

    private static async Task RunCase(bool duplicateLambdas)
    {
        using var fixture = TestDirectory.Create(duplicateLambdas ? "crap4csharp-failed-lambda-exemption" : "crap4csharp-failed-async-exemption");
        var token = TestContext.Current.CancellationToken;
        var source = duplicateLambdas
            ? "public static class Library { public static int Value() { System.Func<int,int> a = x => x + 1; System.Func<int,int> b = x => x + 1; return a(41) + b(0); } }"
            : "public static class Library { public static async System.Threading.Tasks.Task<int> Value() { var value = await System.Threading.Tasks.Task.FromResult(42); return value; } }";
        const string counter = "<Target Name=\"CountCompile\" BeforeTargets=\"CoreCompile\" Condition=\"'$(DesignTimeBuild)' != 'true'\"><WriteLinesToFile File=\"$(MSBuildProjectDirectory)/build-count.txt\" Lines=\"once\" Overwrite=\"false\" /></Target>";
        fixture.Write("Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" + counter + "</Project>");
        fixture.Write("Library/Code.cs", source);
        fixture.Write("Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
            <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
            <PackageReference Include="xunit.v3" Version="3.2.2" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
            <PackageReference Include="coverlet.collector" Version="6.0.4" />
            <ProjectReference Include="../Library/Library.csproj" /></ItemGroup>COUNTER</Project>
            """.Replace("COUNTER", counter, StringComparison.Ordinal));
        fixture.Write("Tests/Tests.cs", ("public class Once { [Xunit.Fact] public async System.Threading.Tasks.Task Test() { System.IO.File.AppendAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, \"test-count.txt\"), \"once\\n\"); Xunit.Assert.Equal(99, await Library.Value()); } }").Replace("await Library.Value()", duplicateLambdas ? "await System.Threading.Tasks.Task.FromResult(Library.Value())" : "await Library.Value()", StringComparison.Ordinal));
        const string policy = """
            {"schemaVersion":"repository-policy-v1","mode":"strict","productionProjects":["Library/Library.csproj"],
            "testProjects":["Tests/Tests.csproj"],"configuration":"Debug","targetFrameworks":["net10.0"],"scope":"all",
            "threshold":100,"missingCoverage":"fail","requiredChecks":["tests","coverage","crap"],"exclusions":[],"ruleset":"callables-v1","exemptionFiles":[]}
            """;
        fixture.Write("policy.json", policy);
        fixture.Write(".gitignore", "bin/\nobj/\nbuild-count.txt\nbundle*/\n");
        async Task Git(params string[] args)
        {
            var result = await ProcessRunner.RunAsync("git", args, fixture.Path, TimeSpan.FromSeconds(30), token);
            Assert.True(result.ExitCode == 0, result.StandardError);
        }
        await Git("init", "-q"); await Git("config", "user.name", "Fixture");
        await Git("config", "user.email", "fixture@example.invalid");
        await Git("add", "."); await Git("commit", "-qm", "failed async fixture");
        var runs = 0;
        async Task<JsonDocument> Check()
        {
            var output = new StringWriter(); var error = new StringWriter();
            var exit = await global::App.RunAsync(["check", "--project", "Tests/Tests.csproj", "--policy", "policy.json",
                "--base", "HEAD", "--scope", "all", "--artifact-output", "bundle" + runs, "--format", "json", "--timeout-seconds", "300"],
                fixture.Path, output, error, token);
            Assert.True(exit == 1, error + Environment.NewLine + output);
            var document = JsonDocument.Parse(output.ToString());
            var evaluation = document.RootElement.GetProperty("evaluation");
            Assert.False(evaluation.GetProperty("decision").GetProperty("completed").GetBoolean());
            Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle" + runs)));
            Assert.DoesNotContain(document.RootElement.GetProperty("run").GetProperty("artifacts").EnumerateArray(),
                a => a.GetProperty("kind").GetString() == "manifest" || a.GetProperty("complete").GetBoolean() || a.GetProperty("reusable").GetBoolean());
            runs++;
            var outcome = Assert.Single(document.RootElement.GetProperty("run").GetProperty("testOutcomes").EnumerateArray());
            Assert.Equal(1, outcome.GetProperty("exitCode").GetInt32());
            Assert.Equal("invalid", evaluation.GetProperty("provenance").GetProperty("status").GetString());
            foreach (var project in new[] { "Library", "Tests" })
                Assert.Equal(runs, File.ReadAllLines(Path.Combine(fixture.Path, project, "build-count.txt")).Length);
            Assert.Equal(runs, File.ReadAllLines(Path.Combine(fixture.Path, "Tests/bin/Debug/net10.0/test-count.txt")).Length);
            return document;
        }
        using var probe = await Check();
        var reason = duplicateLambdas ? CoverageReasonCodes.AmbiguousCallableOwnership : CoverageReasonCodes.UnsupportedGeneratedMapping;
        var candidates = probe.RootElement.GetProperty("evaluation").GetProperty("callables").EnumerateArray()
            .Where(c => c.GetProperty("coverageReason").GetString() == reason &&
                (!duplicateLambdas || c.GetProperty("kind").GetString() == "Lambda")).ToArray();
        Assert.Equal(duplicateLambdas ? 2 : 1, candidates.Length);
        if (duplicateLambdas)
        {
            Assert.All(candidates, c => Assert.Equal("Lambda", c.GetProperty("kind").GetString()));
            Assert.Single(candidates.Select(c => c.GetProperty("callableId").GetString()).Distinct());
            Assert.Single(candidates.Select(c => c.GetProperty("bodyChecksum").GetString()).Distinct());
        }
        var callable = candidates[0];
        var callableId = callable.GetProperty("callableId").GetString();
        var entry = new JsonObject
        {
            ["ruleset"] = callable.GetProperty("ruleset").GetString(), ["contextId"] = callable.GetProperty("contextId").GetString(),
            ["targetFramework"] = "net10.0", ["callableId"] = callableId,
            ["bodyChecksum"] = callable.GetProperty("bodyChecksum").GetString(), ["reasonCode"] = reason,
            ["justification"] = duplicateLambdas ? "Reviewed exact duplicate anonymous bodies" : "Reviewed actual async collector mapping limitation", ["reviewReference"] = "fixture-review",
            ["familyIds"] = JsonNode.Parse(callable.GetProperty("familyIds").GetRawText())
        };
        if (duplicateLambdas) entry["memberCount"] = 2;
        var waiver = new JsonObject { ["version"] = CallableExemptions.Version, ["entries"] = new JsonArray(entry) };
        var approvedBytes = waiver.ToJsonString();
        fixture.Write("policy.json", policy.Replace("\"exemptionFiles\":[]", "\"exemptionFiles\":[\"exemptions.json\"]", StringComparison.Ordinal));
        fixture.Write("exemptions.json", approvedBytes);
        await Git("add", "policy.json", "exemptions.json"); await Git("commit", "-qm", "approve exact async limitation");
        using (var approved = await Check())
        {
            var evaluation = approved.RootElement.GetProperty("evaluation");
            Assert.Contains(evaluation.GetProperty("callableExemptions").EnumerateArray(),
                e => e.GetProperty("callableId").GetString() == callableId && e.GetProperty("status").GetString() == "exempted-unsupported");
            if (duplicateLambdas)
            {
                // Exempting the two anonymous members does not waive the
                // same-line parent's independently ambiguous coverage.
                Assert.Equal([reason], evaluation.GetProperty("exemptionErrors").EnumerateArray()
                    .Select(e => e.GetString()!).ToArray());
                Assert.Contains(evaluation.GetProperty("callables").EnumerateArray(),
                    c => c.GetProperty("kind").GetString() != "Lambda" &&
                        c.GetProperty("coverageReason").GetString() == reason);
            }
            else Assert.Empty(evaluation.GetProperty("exemptionErrors").EnumerateArray());
            Assert.NotEmpty(evaluation.GetProperty("metrics").EnumerateArray());
        }
        if (duplicateLambdas)
        {
            // Reviewed count is evidence, not permission to widen anonymous debt.
            entry["memberCount"] = 1;
            fixture.Write("exemptions.json", waiver.ToJsonString());
            await Git("add", "exemptions.json"); await Git("commit", "-qm", "adverse anonymous count");
            using (var count = await Check())
            {
                Assert.Empty(count.RootElement.GetProperty("evaluation").GetProperty("metrics").EnumerateArray());
                Assert.Contains("exemption.ambiguityWidened", count.RootElement.GetRawText(), StringComparison.Ordinal);
            }
            entry["memberCount"] = 2;
            fixture.Write("exemptions.json", approvedBytes);
            await Git("add", "exemptions.json"); await Git("commit", "-qm", "restore reviewed anonymous count");
            fixture.Write("Library/Code.cs", source.Replace("return a(41)", "System.Func<int,int> c = x => x + 1; return c(0) + a(41)", StringComparison.Ordinal));
            using (var widened = await Check())
            {
                Assert.Empty(widened.RootElement.GetProperty("evaluation").GetProperty("metrics").EnumerateArray());
                Assert.Contains("exemption.ambiguityWidened", widened.RootElement.GetRawText(), StringComparison.Ordinal);
            }
            fixture.Write("Library/Code.cs", source);
        }
        // Immutable base trust ignores a pre-existing worktree waiver edit: only
        // reviewed bytes may affect findings, and failed tests still run once.
        fixture.Write("exemptions.json", approvedBytes.Replace("fixture-review", "unapproved-review", StringComparison.Ordinal));
        using (var edited = await Check())
        {
            var match = Assert.Single(edited.RootElement.GetProperty("evaluation").GetProperty("callableExemptions").EnumerateArray());
            Assert.Equal("exempted-unsupported", match.GetProperty("status").GetString());
            Assert.Equal("fixture-review", match.GetProperty("reviewReference").GetString());
            Assert.Contains("unapproved-review", File.ReadAllText(Path.Combine(fixture.Path, "exemptions.json")), StringComparison.Ordinal);
        }
        fixture.Write("exemptions.json", approvedBytes);
        // A body edit invalidates the old identity but does not erase actual failed-test outcomes.
        fixture.Write("Library/Code.cs", duplicateLambdas ? source.Replace("x + 1", "x + 2", StringComparison.Ordinal) : source.Replace("42", "43", StringComparison.Ordinal));
        using (var changed = await Check())
            Assert.Contains(changed.RootElement.GetProperty("evaluation").GetProperty("callableExemptions").EnumerateArray(),
                e => e.GetProperty("status").GetString() == "stale-ignored");
        fixture.Write("Library/Code.cs", source);
        // Even committed bytes cannot waive an unacknowledged family.
        entry["familyIds"] = new JsonArray("unrelated-family");
        fixture.Write("exemptions.json", waiver.ToJsonString());
        await Git("add", "exemptions.json"); await Git("commit", "-qm", "adverse family acknowledgement");
        using (var family = await Check())
        {
            var evaluation = family.RootElement.GetProperty("evaluation");
            if (evaluation.TryGetProperty("callableExemptions", out var matches)) Assert.Empty(matches.EnumerateArray());
            Assert.Empty(evaluation.GetProperty("metrics").EnumerateArray());
            Assert.Contains("exemption.familyAcknowledgementMismatch", family.RootElement.GetRawText(), StringComparison.Ordinal);
        }
        Assert.Equal(source, File.ReadAllText(Path.Combine(fixture.Path, "Library/Code.cs")));
    }
}
