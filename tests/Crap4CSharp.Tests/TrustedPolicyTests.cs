using System.Text;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class TrustedPolicyTests
{
    [Fact]
    public void TrustedResolutionLoadsEveryOverlayFromOneImmutableTree()
    {
        var strict = PolicyTests.ValidPolicy.Replace("\"incremental\"", "\"strict\"", StringComparison.Ordinal)
            .Replace(",\n  \"baseline\": \"baseline.json\"", "", StringComparison.Ordinal)
            .Replace("\"exemptionFiles\": []", "\"exemptionFiles\": [\"exceptions.json\"]", StringComparison.Ordinal);
        var commands = new List<string>();
        byte[] Git(IReadOnlyList<string> arguments)
        {
            commands.Add(string.Join(" ", arguments));
            return string.Join(" ", arguments) switch
            {
                "merge-base HEAD origin/main" => Encoding.UTF8.GetBytes("0123456789012345678901234567890123456789\n"),
                "show 0123456789012345678901234567890123456789:quality/policy.json" => Encoding.UTF8.GetBytes(strict),
                "show 0123456789012345678901234567890123456789:quality/exceptions.json" => Encoding.UTF8.GetBytes("{\"version\":\"callable-exemptions-v1\",\"entries\":[]}"),
                _ => throw new InvalidOperationException(string.Join(" ", arguments))
            };
        }

        var resolved = TrustedPolicyLoader.LoadFromBase("origin/main", "quality/policy.json", Git);

        Assert.Equal("base-trusted", resolved.Trust);
        Assert.Single(resolved.ExemptionBytes);
        Assert.All(commands.Skip(1), command => Assert.Contains("0123456789012345678901234567890123456789:", command));
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
}
