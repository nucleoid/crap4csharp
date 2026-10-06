using System.Text;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class TrustedPolicyTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void TrustedResolutionLoadsEveryOverlayFromOneImmutableTree(string newline)
    {
        var input = PolicyTests.ValidPolicy.ReplaceLineEndings(newline);
        var strict = input.Replace("\"incremental\"", "\"strict\"", StringComparison.Ordinal)
            .Replace(",\n  \"baseline\": \"baseline.json\"", "", StringComparison.Ordinal)
            .Replace("\"exemptionFiles\": []", "\"exemptionFiles\": [\"exceptions.json\"]", StringComparison.Ordinal);
        var commands = new List<string>();
        byte[] Git(IReadOnlyList<string> arguments)
        {
            commands.Add(string.Join(" ", arguments));
            return string.Join(" ", arguments) switch
            {
                "merge-base --all HEAD origin/main" => Encoding.UTF8.GetBytes("0123456789012345678901234567890123456789\n"),
                "show 0123456789012345678901234567890123456789:quality/policy.json" => Encoding.UTF8.GetBytes(strict),
                "show 0123456789012345678901234567890123456789:quality/exceptions.json" => Encoding.UTF8.GetBytes("{\"version\":\"callable-exemptions-v1\",\"entries\":[]}"),
                _ => throw new InvalidOperationException(string.Join(" ", arguments))
            };
        }

        var resolved = TrustedPolicyLoader.LoadFromBase("origin/main", "quality/policy.json", Git,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("base-trusted", resolved.Trust);
        Assert.Single(resolved.ExemptionBytes);
        Assert.All(commands.Skip(1), command => Assert.Contains("0123456789012345678901234567890123456789:", command));
    }

    [Fact]
    public void CancellationStopsTrustedGitBeforeLaunchAndBetweenOverlayReads()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var calls = 0;
        byte[] Git(IReadOnlyList<string> arguments)
        {
            calls++;
            cancellation.Cancel();
            return Encoding.UTF8.GetBytes(new string('a', 40) + "\n");
        }
        Assert.Throws<OperationCanceledException>(() => TrustedPolicyLoader.LoadFromBase("HEAD",
            "policy.json", Git, timeout: TimeSpan.FromSeconds(1), cancellationToken: cancellation.Token));
        Assert.Equal(0, calls);
        using var between = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        byte[] Between(IReadOnlyList<string> arguments)
        {
            calls++;
            between.Cancel();
            return Encoding.UTF8.GetBytes(new string('a', 40) + "\n");
        }
        Assert.Throws<OperationCanceledException>(() => TrustedPolicyLoader.LoadFromBase("HEAD",
            "policy.json", Between, timeout: TimeSpan.FromSeconds(1), cancellationToken: between.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void OverrideValidatorRejectsNarrowingAndWeakening()
    {
        var policy = RepositoryPolicyParser.Parse(Encoding.UTF8.GetBytes(PolicyTests.ValidPolicy), "quality/policy.json").Policy;
        var proposed = new PolicyOverrides(Threshold: 99, Frameworks: ["net10.0"], ProductionProjects: [],
            TestProjects: [], Exclusions: ["src/**"], ExemptionFile: "branch.json");

        var result = PolicyOverrideValidator.Validate(policy, proposed);

        Assert.False(result.Allowed);
        Assert.Contains("policy.thresholdOverrideForbidden", result.Reasons);
        Assert.Contains("policy.productionProjectNarrowingForbidden", result.Reasons);
        Assert.Contains("policy.testProjectNarrowingForbidden", result.Reasons);
        Assert.Contains("policy.exclusionOverrideForbidden", result.Reasons);
        Assert.Contains("policy.exemptionOverrideForbidden", result.Reasons);
    }

    [Fact]
    public void IncrementalTrustedPolicyAcceptsCandidateCreatedUnderEquivalentStrictAdoptionPolicy()
    {
        var incremental = RepositoryPolicyParser.Parse(Encoding.UTF8.GetBytes(PolicyTests.ValidPolicy),
            "quality/policy.json");
        var strictText = PolicyTests.ValidPolicy.Replace("\"incremental\"", "\"strict\"", StringComparison.Ordinal)
            .Replace("\"scope\": \"base\"", "\"scope\": \"all\"", StringComparison.Ordinal)
            .Replace(",\n  \"baseline\": \"baseline.json\"", "", StringComparison.Ordinal);
        var strict = RepositoryPolicyParser.Parse(Encoding.UTF8.GetBytes(strictText), "quality/policy.json");
        var boundHash = RepositoryPolicyParser.BindExemptions(strict.CompatibilityHash,
            Array.Empty<KeyValuePair<string, ReadOnlyMemory<byte>>>());
        var candidate = BaselineDocument.Serialize(BaselineDocument.Generate(boundHash,
            ComplexityRules.CallablesV1, "source", "revision", 5, []));
        byte[] Git(IReadOnlyList<string> arguments) => string.Join(" ", arguments) switch
        {
            "merge-base --all HEAD origin/main" => Encoding.UTF8.GetBytes("0123456789012345678901234567890123456789\n"),
            "show 0123456789012345678901234567890123456789:quality/policy.json" =>
                Encoding.UTF8.GetBytes(PolicyTests.ValidPolicy),
            "show 0123456789012345678901234567890123456789:quality/baseline.json" => candidate,
            var command => throw new InvalidOperationException(command)
        };

        var resolved = TrustedPolicyLoader.LoadFromBase("origin/main", "quality/policy.json", Git,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(resolved.Baseline);
        Assert.Equal(boundHash, resolved.Baseline.PolicyHash);
    }
}
