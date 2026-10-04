using System.Text;
using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class PolicyCheckCommandTests
{
    [Fact]
    public void BranchClassifiedGeneratedSourceOutsideObjFailsClosed()
    {
        var source = new ManifestInput("source", "src/Gate.g.cs", "inputs/gate.bin", 1, new string('a', 64),
            "utf-8", true);
        var context = Context([source]);
        var manifest = Manifest(context);

        var error = Assert.Throws<PolicyException>(() => PolicyCheckCommand.ValidateGeneratedInventory(manifest));

        Assert.Equal("policy.branchGeneratedSource", error.Code);
        PolicyCheckCommand.ValidateGeneratedInventory(Manifest(Context(
            [source with { LogicalPath = "obj/Release/Gate.g.cs" }])));
    }

    [Fact]
    public void DeletionOnlyHunkSelectsCurrentCallable()
    {
        var callable = Callable("method", "observation", "src/Gate.cs", 10, 20);
        var change = ChangedFile.Modified("src/Gate.cs", "old", "new", [], [new LineRange(12, 12)]);

        Assert.True(PolicyCheckCommand.IsSelected("base", false, [change], callable));
        Assert.True(PolicyCheckCommand.IsSelected("base", true, [], callable));
        Assert.True(PolicyCheckCommand.IsSelected("base", false, [], callable, ["src/Gate.cs"]));
    }

    [Fact]
    public void UnselectedDuplicateAnonymousBodiesDoNotBlockIncrementalPolicy()
    {
        var policy = Policy(RepositoryPolicyMode.Incremental);
        var first = Observation("same", false);
        var second = Observation("same", false);
        var baseline = new BaselineDocument(BaselineDocument.Version, ComplexityRules.CallablesV1, "hash",
            "source", "revision", []);

        var result = PolicyEvaluator.Evaluate(policy, baseline, [first, second], [], "hash");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void NarrowLeafExemptionAlsoCompletesItsAcknowledgedFamily()
    {
        var policy = Policy(RepositoryPolicyMode.Strict);
        var leaf = Observation("leaf", true) with
        { CoverageReason = CoverageReasonCodes.UnsupportedCallable };
        var family = Observation("family", true) with
        {
            Kind = "family",
            Rule = CallableFamilyEvaluator.Rule,
            CoverageReason = CoverageReasonCodes.UnsupportedCallable,
            RelatedEntityKeys = ["leaf"]
        };
        var exemption = new PolicyExemption("leaf", "crap.thresholdExceeded", "ctx",
            CoverageReasonCodes.UnsupportedCallable, "reviewed");

        var result = PolicyEvaluator.Evaluate(policy, null, [leaf, family], [exemption]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("family", result.ExemptedEntityKeys);
    }

    [Fact]
    public void NullBaselineEntryIsStructuredBaselineError()
    {
        var bytes = Encoding.UTF8.GetBytes("""
            {"schemaVersion":"baseline-v1","ruleset":"callables-v1","policyHash":"hash",
             "sourceIdentity":"source","revision":"revision","entries":[null]}
            """);

        var error = Assert.Throws<BaselineException>(() => BaselineDocument.Parse(bytes));

        Assert.Equal("baseline.entryInvalid", error.Code);
        Assert.True(global::App.IsHandled(new JsonException("malformed exemption")));
    }

    [Fact]
    public void DeletedOrEditedTrustedExemptionIsReportedStaleAndIgnored()
    {
        var result = EmptyResult([]);
        var bytes = Encoding.UTF8.GetBytes("""
            {"version":"callable-exemptions-v1","entries":[{
              "ruleset":"callables-v1","contextId":"ctx","targetFramework":"net10.0",
              "callableId":"deleted","bodyChecksum":"old","reasonCode":"scope.unsupportedCallable",
              "justification":"reviewed exception","reviewReference":"review-1","familyIds":[]}]}
            """);

        var parsed = PolicyCheckCommand.ParseExemptions(
            new Dictionary<string, byte[]> { ["quality/exemptions.json"] = bytes }, result);

        Assert.Empty(parsed.Active);
        Assert.Equal("stale-ignored", Assert.Single(parsed.Matches).Status);
    }

    [Theory]
    [InlineData("src/Gate.cs")]
    [InlineData("quality/baseline.json")]
    public void CheckOutputCannotAliasCurrentSourceOrTrustedOverlay(string output)
    {
        using var directory = TestDirectory.Create("crap4csharp-policy-output-alias");
        var parsed = RepositoryPolicyParser.Parse(Encoding.UTF8.GetBytes(PolicyTests.ValidPolicy),
            "quality/policy.json");
        var trusted = new TrustedPolicyResolution("base-trusted", new string('a', 40), parsed, null,
            new Dictionary<string, byte[]>(), new Dictionary<string, string>
            { ["quality/baseline.json"] = new string('b', 64) });
        var current = new CurrentEvidence("repo", "workspace", null, [],
            new Dictionary<string, string>())
        { ProtectedPaths = [Path.Combine(directory.Path, "src", "Gate.cs")] };

        var error = Assert.Throws<PolicyException>(() => PolicyCheckCommand.RejectOutputAlias(output,
            directory.Path, trusted, current));
        Assert.Equal("output.aliasesPolicyInput", error.Code);
    }

    private static CallableResult Callable(string id, string observation, string path, int start, int end) =>
        new(id, observation, "Method", null, "ctx", path,
            new CallableSourceSpan(start, 1, end, 1, 0, 1), 2, "applicable", "known", 1, 2, null,
            "semantic-ordinary", "body");

    private static PolicyObservation Observation(string key, bool selected) => new("anonymous", key,
        "crap.thresholdExceeded", ComplexityRules.CallablesV1, "ctx", "src/Gate.cs", "body", 2,
        null, null, CoverageReasonCodes.AmbiguousCallableOwnership, selected, true);

    private static RepositoryPolicy Policy(RepositoryPolicyMode mode) => new(RepositoryPolicy.Version, mode,
        ["App/App.csproj"], ["App.Tests/App.Tests.csproj"], "Release", ["net10.0"], "base", 5,
        MissingCoveragePolicy.Fail, ["tests", "coverage", "crap"], [], ComplexityRules.CallablesV1,
        mode == RepositoryPolicyMode.Incremental ? "baseline.json" : null, [], new PolicyAllowedOverrides());

    private static ManifestContext Context(IReadOnlyList<ManifestInput> inputs) => new("ctx", "App/App.csproj",
        "net10.0", "Release", "AnyCPU", "", "", "", true, true, inputs);

    private static RunManifest Manifest(ManifestContext context) => new(ManifestIdentity.SchemaVersion,
        CanonicalIdentity.Algorithm, new ManifestProducer("crap4csharp", "0.1.0", ComplexityRules.CallablesV1,
            ProjectAnalysisContext.ProtocolVersion, ManifestIdentity.CoverageProtocol, ManifestIdentity.PathProtocol),
        new ManifestCapture("completed", true, true, []), new ManifestRevision("none", "none", "workspace", null, null, null),
        [new ManifestRoot("workspace", "workspace", "sensitive")], [context], [], [], [],
        new ManifestEvaluationInputs("scope", "policy", null, null), null);

    private static ResultDocument EmptyResult(IReadOnlyList<CallableResult> callables)
    {
        var evaluation = new EvaluationSection("check", new PolicyOptions(5, false), new EvaluationScope(".", []),
            [new EvaluationContext("ctx", "captured", "App/App.csproj", "net10.0", "Release", "source", null)],
            [], [], [], new CoverageSummary(0, 0, 0, new Dictionary<string, int>()), [],
            new EvaluationDecision(true, "pass", "crap.withinThreshold"))
        { Callables = callables, Families = [], CallableExemptions = [], ExemptionErrors = [] };
        return new ResultDocument(ResultContract.SchemaVersion, "test", ComplexityRules.CallablesV1, evaluation,
            new RunSection("run", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, [], [], [],
                new CancellationDetails(false, false, null), "completed", 0));
    }
}
