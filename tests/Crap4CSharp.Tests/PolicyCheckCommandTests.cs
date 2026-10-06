using System.Text;
using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class PolicyCheckCommandTests
{
    [Theory]
    [InlineData("src/Gate.g.cs")]
    [InlineData("obj/Release/Gate.g.cs")]
    [InlineData("artifacts/intermediate/Release/Gate.g.cs")]
    public void LogicalGeneratedPrefixNeverSubstitutesForIndependentCurrentRecipe(string logicalPath)
    {
        var source = new ManifestInput("source", logicalPath, "inputs/gate.bin", 1, new string('a', 64), "utf-8", true);
        var context = Context([source]);
        var error = Assert.Throws<PolicyException>(() => PolicyCheckCommand.ValidateGeneratedInventory(Manifest(context)));
        Assert.Equal("policy.branchGeneratedSource", error.Code);
        PolicyCheckCommand.ValidateGeneratedInventory(Manifest(context with
        { CurrentRevalidation = new ManifestCurrentRevalidation(CurrentEvidenceAdapter.SupportedRecipeProvider,
            "App/App.csproj", "App/bin/App.dll", "App/bin/App.pdb") }));
    }

    [Fact]
    public void DeletionOnlyHunkSelectsCurrentCallable()
    {
        var callable = Callable("method", "observation", "Gate.cs", 10, 20);
        var change = ChangedFile.Modified("App/Gate.cs", "old", "new", [], [new LineRange(12, 12)]);

        Assert.True(PolicyCheckCommand.IsSelected("base", false, [change], callable, "App/Gate.cs"));
        Assert.True(PolicyCheckCommand.IsSelected("base", true, [], callable, "App/Gate.cs"));
        Assert.True(PolicyCheckCommand.IsSelected("base", false, [], callable, "App/Gate.cs", ["App/Gate.cs"]));
        Assert.Equal("App/Gate.cs", CapturedEvaluationInputs.RepositorySourcePath("App/App.csproj", "Gate.cs"));
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
    public void DuplicateAnonymousBodiesCanUseOneNarrowTrustedExemption()
    {
        var first = Callable("same", "first", "Gate.cs", 10, 10) with
        { CoverageReason = CoverageReasonCodes.AmbiguousCallableOwnership, FamilyIds = ["family"] };
        first = first with { Kind = "Lambda" };
        var second = first with { ObservationId = "second", Span = new CallableSourceSpan(20, 1, 20, 2, 20, 2) };
        var result = EmptyResult([first, second]);
        var bytes = Encoding.UTF8.GetBytes("""
            {"version":"callable-exemptions-v1","entries":[{
              "ruleset":"callables-v1","contextId":"ctx","targetFramework":"net10.0",
              "callableId":"same","bodyChecksum":"body","reasonCode":"coverage.ambiguousCallableOwnership",
              "justification":"reviewed duplicate body","reviewReference":"review-1","familyIds":["family"],"memberCount":2}]}
            """);

        var parsed = PolicyCheckCommand.ParseExemptions(
            new Dictionary<string, byte[]> { ["quality/exemptions.json"] = bytes }, result);

        Assert.Equal("same", Assert.Single(parsed.Active).EntityKey);
        var evaluated = PolicyEvaluator.Evaluate(Policy(RepositoryPolicyMode.Strict), null,
            [Observation("same", true)], parsed.Active);
        Assert.Equal(0, evaluated.ExitCode);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("{}")]
    public void NonStringExemptionVersionReturnsStableMalformedReason(string version)
    {
        var exception = Assert.Throws<PolicyException>(() => PolicyCheckCommand.ParseExemptions(
            new Dictionary<string, byte[]> { ["exemptions.json"] = Encoding.UTF8.GetBytes(
                "{\"version\":" + version + ",\"entries\":[]}") }, EmptyResult([])));
        Assert.Equal("exemption.malformed", exception.Code);
    }

    [Fact]
    public void TrustedExemptionSurvivesCurrentContextContentChange()
    {
        var callable = Callable("same", "current", "Gate.cs", 10, 10) with
        {
            ContextId = "current-context",
            CoverageReason = CoverageReasonCodes.UnsupportedCallable
        };
        var result = EmptyResult([callable], "current-context");
        var bytes = Encoding.UTF8.GetBytes("""
            {"version":"callable-exemptions-v1","entries":[{
              "ruleset":"callables-v1","contextId":"reviewed-base-context","targetFramework":"net10.0",
              "callableId":"same","bodyChecksum":"body","reasonCode":"scope.unsupportedCallable",
              "justification":"reviewed exact callable","reviewReference":"review-1","familyIds":[]}]}
            """);

        var parsed = PolicyCheckCommand.ParseExemptions(
            new Dictionary<string, byte[]> { ["quality/exemptions.json"] = bytes }, result);
        var evaluated = PolicyEvaluator.Evaluate(Policy(RepositoryPolicyMode.Strict), null,
            [Observation("same", true) with { ContextId = "current-context", CoverageReason = CoverageReasonCodes.UnsupportedCallable }],
            parsed.Active);

        Assert.Single(parsed.Active);
        Assert.Equal(0, evaluated.ExitCode);
    }

    [Fact]
    public void UnsupportedExemptionCannotHideKnownComplexityViolation()
    {
        var observation = Observation("leaf", true) with
        { CoverageReason = CoverageReasonCodes.UnsupportedCallable, Complexity = 6 };
        var exemption = new PolicyExemption("leaf", "crap.thresholdExceeded", "old-context",
            CoverageReasonCodes.UnsupportedCallable, "reviewed");

        var result = PolicyEvaluator.Evaluate(Policy(RepositoryPolicyMode.Strict), null, [observation], [exemption]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.Findings, finding => finding.Code == "crap.thresholdExceeded");
    }

    [Fact]
    public void AmbiguityExemptionCannotCoverDifferentBodies()
    {
        var first = Callable("same", "first", "Gate.cs", 10, 10) with
        { CoverageReason = CoverageReasonCodes.AmbiguousCallableOwnership, FamilyIds = ["family"] };
        var second = first with { ObservationId = "second", BodyChecksum = "other-body" };
        var bytes = Encoding.UTF8.GetBytes("""
            {"version":"callable-exemptions-v1","entries":[{
              "ruleset":"callables-v1","contextId":"ctx","targetFramework":"net10.0",
              "callableId":"same","bodyChecksum":"body","reasonCode":"coverage.ambiguousCallableOwnership",
              "justification":"reviewed one body","reviewReference":"review-1","familyIds":["family"]}]}
            """);

        var error = Assert.Throws<PolicyException>(() => PolicyCheckCommand.ParseExemptions(
            new Dictionary<string, byte[]> { ["quality/exemptions.json"] = bytes }, EmptyResult([first, second])));

        Assert.Equal("exemption.ambiguityWidened", error.Code);
    }

    [Fact]
    public void AmbiguityExemptionCannotCoverAdditionalIdenticalMembers()
    {
        var first = Callable("same", "first", "Gate.cs", 10, 10) with
        { Kind = "Lambda", CoverageReason = CoverageReasonCodes.AmbiguousCallableOwnership, FamilyIds = ["family"] };
        var second = first with { ObservationId = "second" };
        var third = first with { ObservationId = "third" };
        var bytes = Encoding.UTF8.GetBytes("""
            {"version":"callable-exemptions-v1","entries":[{
              "ruleset":"callables-v1","contextId":"ctx","targetFramework":"net10.0",
              "callableId":"same","bodyChecksum":"body","reasonCode":"coverage.ambiguousCallableOwnership",
              "justification":"reviewed two copies","reviewReference":"review-1","familyIds":["family"],"memberCount":2}]}
            """);

        var error = Assert.Throws<PolicyException>(() => PolicyCheckCommand.ParseExemptions(
            new Dictionary<string, byte[]> { ["quality/exemptions.json"] = bytes }, EmptyResult([first, second, third])));

        Assert.Equal("exemption.ambiguityWidened", error.Code);
    }

    [Fact]
    public async Task FailureOutputNeverOverwritesAnExplicitInputFile()
    {
        using var directory = TestDirectory.Create("crap4csharp-policy-failure-output");
        var source = Path.Combine(directory.Path, "Gate.cs");
        await File.WriteAllTextAsync(source, "class Gate {}", TestContext.Current.CancellationToken);
        var output = new StringWriter();

        var exit = await global::App.RunAsync(["check", "Gate.cs", "--reuse-artifacts", "missing.json", "--policy",
            "missing-policy.json", "--base", "HEAD", "--output", "Gate.cs", "--format", "json"],
            directory.Path, output, TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Equal("class Gate {}", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void BaseTrustedCheckRejectsRevisionNone()
    {
        var error = Assert.Throws<PolicyException>(() =>
            PolicyCheckCommand.ValidateTrustedRevision(Manifest(Context([]))));
        Assert.Equal("provenance.gitRevisionRequired", error.Code);
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

    private static ResultDocument EmptyResult(IReadOnlyList<CallableResult> callables, string contextId = "ctx")
    {
        var evaluation = new EvaluationSection("check", new PolicyOptions(5, false), new EvaluationScope(".", []),
            [new EvaluationContext(contextId, "captured", "App/App.csproj", "net10.0", "Release", "source", null)],
            [], [], [], new CoverageSummary(0, 0, 0, new Dictionary<string, int>()), [],
            new EvaluationDecision(true, "pass", "crap.withinThreshold"))
        { Callables = callables, Families = [], CallableExemptions = [], ExemptionErrors = [] };
        return new ResultDocument(ResultContract.SchemaVersion, "test", ComplexityRules.CallablesV1, evaluation,
            new RunSection("run", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, [], [], [],
                new CancellationDetails(false, false, null), "completed", 0));
    }
}
