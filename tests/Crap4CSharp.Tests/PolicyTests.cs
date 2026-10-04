using System.Text;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class PolicyTests
{
    [Fact]
    public void ParserRejectsUnknownDuplicateAndUnsupportedChecks()
    {
        Assert.Throws<PolicyException>(() => RepositoryPolicyParser.Parse(Encoding.UTF8.GetBytes(ValidPolicy.Replace(
            "\"threshold\": 5", "\"threshold\": 5, \"threshold\": 6", StringComparison.Ordinal)), "quality/policy.json"));
        Assert.Throws<PolicyException>(() => RepositoryPolicyParser.Parse(Encoding.UTF8.GetBytes(ValidPolicy.Replace(
            "\"threshold\": 5", "\"threshold\": 5, \"mystery\": true", StringComparison.Ordinal)), "quality/policy.json"));
        Assert.Throws<PolicyException>(() => RepositoryPolicyParser.Parse(Encoding.UTF8.GetBytes(ValidPolicy.Replace(
            "[\"tests\", \"coverage\", \"crap\"]", "[\"shell\"]", StringComparison.Ordinal)), "quality/policy.json"));
    }

    [Fact]
    public void ParserCanonicalizesPathsAndHashIndependentlyOfFormatting()
    {
        var first = RepositoryPolicyParser.Parse(Encoding.UTF8.GetBytes(ValidPolicy), "quality/policy.json");
        var second = RepositoryPolicyParser.Parse(Encoding.UTF8.GetBytes(ValidPolicy.Replace("  ", "    ", StringComparison.Ordinal)),
            "quality/policy.json");

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(first.CompatibilityHash, second.CompatibilityHash);
        Assert.Equal("quality/baseline.json", first.Policy.BaselinePath);
        Assert.Equal("samples/Fixture/Fixture/Fixture.csproj", first.Policy.ProductionProjects.Single());
    }

    [Fact]
    public void StrictAdoptionAndIncrementalEnforcementShareCompatibilityHash()
    {
        var incremental = RepositoryPolicyParser.Parse(Encoding.UTF8.GetBytes(ValidPolicy), "quality/policy.json");
        var strictText = ValidPolicy.Replace("\"incremental\"", "\"strict\"", StringComparison.Ordinal)
            .Replace("\"scope\": \"base\"", "\"scope\": \"all\"", StringComparison.Ordinal)
            .Replace(",\n  \"baseline\": \"baseline.json\"", "", StringComparison.Ordinal);
        var strict = RepositoryPolicyParser.Parse(Encoding.UTF8.GetBytes(strictText), "quality/policy.json");

        Assert.NotEqual(strict.Hash, incremental.Hash);
        Assert.Equal(strict.CompatibilityHash, incremental.CompatibilityHash);
    }

    [Fact]
    public void CompatibilityBindingIncludesExactExemptionBytes()
    {
        var parsed = RepositoryPolicyParser.Parse(Encoding.UTF8.GetBytes(ValidPolicy), "quality/policy.json");
        var first = RepositoryPolicyParser.BindExemptions(parsed.CompatibilityHash,
            [new("quality/exceptions.json", Encoding.UTF8.GetBytes("one"))]);
        var second = RepositoryPolicyParser.BindExemptions(parsed.CompatibilityHash,
            [new("quality/exceptions.json", Encoding.UTF8.GetBytes("two"))]);

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("../baseline.json")]
    [InlineData("/tmp/baseline.json")]
    [InlineData("C:/baseline.json")]
    public void BaselineMustRemainInsideRepository(string path)
    {
        var json = ValidPolicy.Replace("baseline.json", path, StringComparison.Ordinal);
        Assert.Throws<PolicyException>(() => RepositoryPolicyParser.Parse(Encoding.UTF8.GetBytes(json), "quality/policy.json"));
    }

    [Fact]
    public void CheckedInSamplesAreValidAndUseActualFixtureProjects()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var strict = RepositoryPolicyParser.Parse(File.ReadAllBytes(Path.Combine(root, "samples/policy/strict.json")),
            "samples/policy/strict.json");
        var incremental = RepositoryPolicyParser.Parse(File.ReadAllBytes(Path.Combine(root, "samples/policy/incremental.json")),
            "samples/policy/incremental.json");
        Assert.Equal("samples/Fixture/Fixture/Fixture.csproj", strict.Policy.ProductionProjects.Single());
        Assert.Equal("samples/Fixture/Fixture.Tests/Fixture.Tests.csproj", incremental.Policy.TestProjects.Single());
    }

    internal const string ValidPolicy = """
        {
          "schemaVersion": "repository-policy-v1",
          "mode": "incremental",
          "productionProjects": ["samples/Fixture/Fixture/Fixture.csproj"],
          "testProjects": ["samples/Fixture/Fixture.Tests/Fixture.Tests.csproj"],
          "configuration": "Release",
          "targetFrameworks": ["net10.0"],
          "scope": "base",
          "threshold": 5,
          "missingCoverage": "fail",
          "requiredChecks": ["tests", "coverage", "crap"],
          "exclusions": [],
          "ruleset": "callables-v1",
          "baseline": "baseline.json",
          "exemptionFiles": []
        }
        """;
}
