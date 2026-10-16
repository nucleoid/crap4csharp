using System.Text;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class FreshPolicyEvaluationTests
{
    [Theory]
    [InlineData("strict", "method", 2)]
    [InlineData("incremental", "method", 0)]
    [InlineData("incremental", "file", 2)]
    public void TrustedModeAndExactMethodOrFileScopeAreNotReplacedByReplayPass(string mode, string granularity, int expected)
    {
        var (preflight, manifest, replay) = Fixture(mode, granularity);
        var decision = FreshPolicyEvaluation.Evaluate(preflight, manifest, replay);
        Assert.Equal(expected, decision.Evaluation.ExitCode);
        Assert.False(decision.Observations.Single(o => o.EntityKey == "cold").Selected && granularity == "method");
        Assert.True(decision.Observations.Single(o => o.EntityKey == "changed").Selected);
        Assert.Equal("pass", replay.Evaluation.Decision.PolicyDecision);
        Assert.Equal(0, replay.Run.ExitCode);
    }

    [Theory]
    [InlineData("all", "method", false, true, 2)]
    [InlineData("all", "file", false, true, 2)]
    [InlineData("base", "method", false, false, 0)]
    [InlineData("base", "file", false, true, 2)]
    [InlineData("base", "method", true, false, 0)]
    [InlineData("base", "file", true, false, 0)]
    public void GeneratedObservationsFollowExactSelectionRatherThanBeingRelabeledOrSilentlyDropped(
        string scope, string granularity, bool noChanges, bool coldSelected, int expectedExit)
    {
        var (preflight, manifest, replay) = Fixture("incremental", granularity);
        var context = manifest.Contexts.Single();
        manifest = manifest with { Contexts = [context with { IncludeGenerated = true,
            Inputs = context.Inputs.Select(i => i with { Generated = true }).ToArray() }] };
        preflight = preflight with { Policy = preflight.Policy with { Scope = scope },
            Scope = scope == "all" ? null : noChanges ? preflight.Scope! with { Files = [] } : preflight.Scope };
        var result = FreshPolicyEvaluation.Evaluate(preflight, manifest, replay);
        Assert.Equal(expectedExit, result.Evaluation.ExitCode);
        Assert.Equal(coldSelected, result.Observations.Single(o => o.EntityKey == "cold").Selected);
        Assert.Equal(scope == "all" || !noChanges, result.Observations.Single(o => o.EntityKey == "changed").Selected);
        Assert.Equal(2, result.Observations.Count);
        Assert.All(manifest.Contexts.Single().Inputs, i => Assert.True(i.Generated));
    }

    [Theory]
    [InlineData("strict", false, true, 2)]
    [InlineData("incremental", false, false, 0)]
    [InlineData("incremental", true, true, 2)]
    public void EmptyGitSelectionDoesNotBypassStrictOrWidenedEffectivePolicyInventory(
        string mode, bool widened, bool applicable, int exit)
    {
        var (preflight, manifest, replay) = Fixture(mode, "method");
        preflight = preflight with { Scope = preflight.Scope! with { Files = [],
            Completeness = widened ? ScopeCompleteness.ContextIncomplete : ScopeCompleteness.Complete } };
        var result = FreshPolicyEvaluation.Evaluate(preflight, manifest, replay);
        Assert.Equal(applicable, result.HasApplicableCrap);
        Assert.Equal(exit, result.Evaluation.ExitCode);
        Assert.Equal(2, result.Observations.Count);
    }

    [Fact]
    public void EmptyApplicablePolicyInventoryIsExplicitWithoutErasingCapturedCallables()
    {
        var (preflight, manifest, replay) = Fixture("strict", "method");
        preflight = preflight with { Policy = preflight.Policy with { Exclusions = ["App/Code.cs"] } };
        var result = FreshPolicyEvaluation.Evaluate(preflight, manifest, replay);
        Assert.False(result.HasApplicableCrap);
        Assert.Empty(result.Observations);
        Assert.Equal(2, replay.Evaluation.Callables!.Count);
    }

    [Fact]
    public void PermittedTightenedThresholdIsAppliedWithoutChangingBaselineCompatibility()
    {
        var (preflight, manifest, replay) = Fixture("strict", "method");
        var original = FreshPolicyEvaluation.Evaluate(preflight, manifest, replay);
        var tightened = FreshPolicyEvaluation.Evaluate(preflight with { Policy = preflight.Policy with { Threshold = 1 } }, manifest, replay);
        Assert.Equal(original.CompatibilityHash, tightened.CompatibilityHash);
        Assert.Contains(tightened.Findings, f => f.EntityKey == "changed" && f.Threshold == 1);
    }

    [Fact]
    public void IncompleteScopeWidensInsteadOfSilentlySelectingNoCallables()
    {
        var (preflight, manifest, replay) = Fixture("incremental", "method");
        var decision = FreshPolicyEvaluation.Evaluate(preflight with { Scope = preflight.Scope! with
            { Files = [], Completeness = ScopeCompleteness.ContextIncomplete } }, manifest, replay);
        Assert.All(decision.Observations, o => Assert.True(o.Selected));
        Assert.Equal(2, decision.Evaluation.ExitCode);
    }

    [Fact]
    public void SelectedChildSelectsItsFamilyEvenWhenRootIsOutsideChangedRange()
    {
        var (preflight, manifest, replay) = Fixture("incremental", "method");
        var callables = replay.Evaluation.Callables!.Select(c => c with { FamilyIds = ["family"] }).ToArray();
        var family = new CallableFamilyMetric("family", "cold", CallableFamilyEvaluator.Rule, ComplexityRules.CallablesV1,
            12, 1, 2, 2, 12, true, ["cold", "changed"], [], [])
            { MemberObservationIds = callables.Select(c => c.ObservationId).ToArray() };
        var result = FreshPolicyEvaluation.Evaluate(preflight, manifest, replay with
            { Evaluation = replay.Evaluation with { Callables = callables, Families = [family] } });
        Assert.True(result.HasApplicableCrap);
        Assert.True(result.Observations.Single(o => o.Kind == "family").Selected);
        Assert.Contains(result.Findings, f => f.EntityKey == "family" && f.Decision == "fail");
        Assert.Equal(2, result.Evaluation.ExitCode);
    }

    [Fact]
    public void ReplayPassCannotHideSelectedUnknownCoverage()
    {
        var (preflight, manifest, replay) = Fixture("incremental", "method");
        var callables = replay.Evaluation.Callables!.Select(c => c.CallableId == "changed" ? c with
            { Coverage = null, Crap = null, CoverageStatus = "unknown", CoverageReason = CoverageReasonCodes.NoMatchingMethod } : c).ToArray();
        var result = FreshPolicyEvaluation.Evaluate(preflight, manifest, replay with
            { Evaluation = replay.Evaluation with { Callables = callables } });
        Assert.Equal(1, result.Evaluation.ExitCode);
        Assert.True(result.HasApplicableCrap);
        Assert.Contains(CoverageReasonCodes.NoMatchingMethod, result.Evaluation.OperationalReasons);
    }

    [Fact]
    public void ScopeRevisionMismatchFailsRatherThanEvaluatingStaleSelection()
    {
        var (preflight, manifest, replay) = Fixture("strict", "method");
        var error = Assert.Throws<PolicyException>(() => FreshPolicyEvaluation.Evaluate(preflight,
            manifest with { Revision = manifest.Revision with { ScopeHead = "other" } }, replay));
        Assert.Equal("provenance.scopeRevisionMismatch", error.Code);
    }

    private static (FreshCheckPreflight, RunManifest, ResultDocument) Fixture(string mode, string granularity)
    {
        var text = "{\"schemaVersion\":\"repository-policy-v1\",\"mode\":\"" + mode + "\",\"productionProjects\":[\"App/App.csproj\"],\"testProjects\":[\"Tests/Tests.csproj\"],\"configuration\":\"Debug\",\"targetFrameworks\":[\"net10.0\"],\"scope\":\"base\",\"threshold\":8,\"missingCoverage\":\"fail\",\"requiredChecks\":[\"tests\",\"coverage\",\"crap\"],\"exclusions\":[],\"ruleset\":\"callables-v1\"" +
            (mode == "incremental" ? ",\"baseline\":\"baseline.json\"" : "") + "}";
        var parsed = RepositoryPolicyParser.Parse(Encoding.UTF8.GetBytes(text), "policy.json");
        var exemptions = new Dictionary<string, byte[]>();
        var compatibility = TrustedPolicyLoader.BoundCompatibilityHash(parsed, exemptions);
        var baseline = mode == "incremental" ? new BaselineDocument(BaselineDocument.Version, ComplexityRules.CallablesV1, compatibility,
            new string('a', 64), "base", []) : null;
        var trusted = new TrustedPolicyResolution("base-trusted", "base", parsed, baseline, exemptions, new Dictionary<string,string>());
        var scope = new CapturedChangeScope(ChangeScopeMode.Base, ScopeSourceState.Worktree, ".",
            new ScopeRevision("base", null, "base", "head", "head", "base", "index"),
            [ChangedFile.Modified("App/Code.cs", "old", "new", [new LineRange(2, 2)], [])], []);
        var request = new FreshCheckRequest(null, "policy.json", "base", "base", null, "worktree", granularity,
            null, null, false, false, null, new PolicyOverrides(), TimeSpan.FromMinutes(1));
        var preflight = new FreshCheckPreflight(".", request, trusted, parsed.Policy, scope);
        var input = new ManifestInput("source", "Code.cs", "inputs/source", 1, "hash", "utf-8", false) { RepositoryPath = "App/Code.cs" };
        var context = new ManifestContext("ctx", "App/App.csproj", "net10.0", "Debug", "AnyCPU", "", "", "", true, true, [input]);
        var manifest = new RunManifest(ManifestIdentity.SchemaVersion, CanonicalIdentity.Algorithm,
            new ManifestProducer("crap4csharp", "0.1.0", ComplexityRules.CallablesV1, ProjectAnalysisContext.ProtocolVersion,
                ManifestIdentity.CoverageProtocol, ManifestIdentity.PathProtocol), new ManifestCapture("completed", true, true, []),
            new ManifestRevision("git", "repo", "workspace", "head", "base", "state") { ScopeHead = "head" },
            [new ManifestRoot("workspace", "workspace", "sensitive")], [context], [], [], [], new ManifestEvaluationInputs("scope", "policy", null, null), null);
        CallableResult Callable(string id, int line, double crap) => new(id, id + "-observation", "Method", null, "ctx", "Code.cs",
            new CallableSourceSpan(line, 1, line, 10, line, 10), 2, "applicable", "known", 1, crap, null, "semantic-ordinary", "body");
        var evaluation = new EvaluationSection("analyze", new PolicyOptions(8, false), new EvaluationScope(".", []),
            [new EvaluationContext("ctx", "captured", "App/App.csproj", "net10.0", "Debug", "source", null)], [], [], [],
            new CoverageSummary(0, 0, 0, new Dictionary<string,int>()), [], new EvaluationDecision(true, "pass", "crap.withinThreshold"))
            { Callables = [Callable("changed", 2, 2), Callable("cold", 20, 20)], Families = [] };
        var replay = new ResultDocument(ResultContract.SchemaVersion, "test", ComplexityRules.CallablesV1, evaluation,
            new RunSection("run", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, [], [], [], new CancellationDetails(false,false,null), "completed", 0));
        return (preflight, manifest, replay);
    }
}
