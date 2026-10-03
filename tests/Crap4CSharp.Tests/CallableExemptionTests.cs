using System.Text;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CallableExemptionTests
{
    [Fact]
    public void ExactTrustedExemptionAcknowledgesUnsupportedLeafAndNamedFamilyOnly()
    {
        var inventory = Inventory("class C { int M() { int L() => 1; return L(); } }");
        var child = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.LocalFunction);
        var family = Assert.Single(CallableFamilyEvaluator.Evaluate(inventory, [], 8));
        var bytes = Document(child, [family.FamilyId]);

        var result = CallableExemptions.Validate(bytes, ExemptionTrust.BaseTrusted, inventory,
            [new CallableCoverageObservation(child.CallableId, "unknown", [], CoverageReasonCodes.UnsupportedGeneratedMapping)],
            [family]);

        var match = Assert.Single(result.Matches);
        Assert.Equal("exempted-unsupported", match.Status);
        Assert.Equal("review-42", match.ReviewReference);
        Assert.True(match.ApprovedForEnforcement);
        Assert.True(result.IncompleteScope);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void LocalBytesCanOnlyCreateLocalUnreviewedIncompleteClaims()
    {
        var inventory = Inventory("class C { void M() { System.Action a = () => { }; } }");
        var lambda = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.Lambda);
        var result = CallableExemptions.Validate(Document(lambda, []), ExemptionTrust.LocalUnreviewed, inventory,
            [new(lambda.CallableId, "unknown", [], CoverageReasonCodes.UnsupportedGeneratedMapping)], []);

        var match = Assert.Single(result.Matches);
        Assert.Equal("local-unreviewed", match.Status);
        Assert.False(match.ApprovedForEnforcement);
        Assert.True(result.IncompleteScope);
    }

    [Theory]
    [InlineData("callableId", "*")]
    [InlineData("ruleset", "wrong")]
    [InlineData("contextId", "wrong")]
    [InlineData("targetFramework", "wrong")]
    [InlineData("bodyChecksum", "stale")]
    [InlineData("reasonCode", "crap.thresholdExceeded")]
    public void WildcardStaleWrongOrThresholdEntriesFailClosed(string field, string replacement)
    {
        var inventory = Inventory("class C { void M() { System.Action a = () => { }; } }");
        var lambda = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.Lambda);
        var json = Encoding.UTF8.GetString(Document(lambda, [])).Replace(
            $"\"{field}\":\"{Value(field, lambda)}\"", $"\"{field}\":\"{replacement}\"", StringComparison.Ordinal);

        var result = CallableExemptions.Validate(Encoding.UTF8.GetBytes(json), ExemptionTrust.BaseTrusted, inventory,
            [new(lambda.CallableId, "unknown", [], CoverageReasonCodes.UnsupportedGeneratedMapping)], []);

        Assert.Empty(result.Matches);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void DuplicateUnmatchedMalformedAndMissingFamilyAcknowledgementFailClosed()
    {
        var inventory = Inventory("class C { int M() { int L() => 1; return L(); } }");
        var child = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.LocalFunction);
        var family = Assert.Single(CallableFamilyEvaluator.Evaluate(inventory, [], 8));
        var single = Encoding.UTF8.GetString(Document(child, []));
        var entry = single[(single.IndexOf('{', 1) + 1)..single.LastIndexOf('}')];
        var duplicate = single.Replace("]}", $",{entry}]}}", StringComparison.Ordinal);

        Assert.NotEmpty(CallableExemptions.Validate(Encoding.UTF8.GetBytes(duplicate), ExemptionTrust.BaseTrusted,
            inventory, [new(child.CallableId, "unknown", [], CoverageReasonCodes.UnsupportedGeneratedMapping)], [family]).Errors);
        Assert.NotEmpty(CallableExemptions.Validate(Encoding.UTF8.GetBytes("{}"), ExemptionTrust.BaseTrusted,
            inventory, [], [family]).Errors);
        Assert.Contains(CallableExemptions.Validate(Document(child, []), ExemptionTrust.BaseTrusted,
            inventory, [new(child.CallableId, "unknown", [], CoverageReasonCodes.UnsupportedGeneratedMapping)], [family]).Errors,
            error => error == "exemption.familyAcknowledgementMissing");

        var unmatched = Encoding.UTF8.GetString(Document(child, [family.FamilyId]))
            .Replace(child.CallableId, new string('0', child.CallableId.Length), StringComparison.Ordinal);
        Assert.Contains(CallableExemptions.Validate(Encoding.UTF8.GetBytes(unmatched), ExemptionTrust.BaseTrusted,
            inventory, [new(child.CallableId, "unknown", [], CoverageReasonCodes.UnsupportedGeneratedMapping)], [family]).Errors,
            error => error == "exemption.unmatched");
    }

    private static string Value(string field, CallableEntry item) => field switch
    {
        "callableId" => item.CallableId,
        "ruleset" => ComplexityRules.CallablesV1,
        "contextId" => "ctx",
        "targetFramework" => "net10.0",
        "bodyChecksum" => item.BodyChecksum,
        "reasonCode" => CoverageReasonCodes.UnsupportedGeneratedMapping,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static byte[] Document(CallableEntry item, IReadOnlyList<string> families) => Encoding.UTF8.GetBytes(
        $$"""{"version":"callable-exemptions-v1","entries":[{"ruleset":"callables-v1","contextId":"ctx","targetFramework":"net10.0","callableId":"{{item.CallableId}}","bodyChecksum":"{{item.BodyChecksum}}","reasonCode":"coverage.unsupportedGeneratedMapping","justification":"compiler mapping unavailable","reviewReference":"review-42","familyIds":[{{string.Join(',', families.Select(id => $"\"{id}\""))}}]}]}""");

    private static CallableInventoryResult Inventory(string source) => CallableInventory.Analyze(source, "C.cs",
        new CallableAnalysisContext("App.csproj", "net10.0", "Debug", "AnyCPU", "ctx", CSharpParseOptions.Default));
}
