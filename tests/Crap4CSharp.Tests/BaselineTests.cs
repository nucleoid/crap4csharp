using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class BaselineTests
{
    [Fact]
    public void IncrementalUsesIndependentComponentCeilings()
    {
        var policy = Policy(RepositoryPolicyMode.Incremental);
        var baseline = Baseline(new BaselineEntry("method", "key", "crap.thresholdExceeded", "callables-v1",
            "samples/Fixture/Fixture/Scorer.cs", "old-body", 4, .5, 6));

        Assert.Equal(0, Evaluate(policy, baseline, Observation(4, .5, 6)).ExitCode);
        Assert.Equal(1, Evaluate(policy, baseline, Observation(5, .9, 5.1)).ExitCode);
        Assert.Contains("baseline.complexityWorsened", Evaluate(policy, baseline, Observation(5, .9, 5.1)).OperationalReasons);
        Assert.Equal(1, Evaluate(policy, baseline, Observation(3, .4, 5.2)).ExitCode);
        Assert.Contains("baseline.coverageWorsened", Evaluate(policy, baseline, Observation(3, .4, 5.2)).OperationalReasons);
        Assert.Equal(1, Evaluate(policy, baseline, Observation(4, .5, 6.01)).ExitCode);
    }

    [Fact]
    public void StrictAndNewIncrementalViolationsUseNumericExitTwo()
    {
        Assert.Equal(2, Evaluate(Policy(RepositoryPolicyMode.Strict), null, Observation(4, .5, 6)).ExitCode);
        Assert.Equal(2, Evaluate(Policy(RepositoryPolicyMode.Incremental), Baseline(), Observation(4, .5, 6)).ExitCode);
        Assert.Equal(0, Evaluate(Policy(RepositoryPolicyMode.Strict), null, Observation(2, .5, 5)).ExitCode);
    }

    [Fact]
    public void UnknownCoverageNeedsNarrowApprovedExemptionAndCannotExcuseNumericViolation()
    {
        var unknown = Observation(4, null, null) with { CoverageReason = "coverage.unsupportedGeneratedMapping" };
        var exemption = new PolicyExemption("key", "crap.thresholdExceeded", "ctx", "coverage.unsupportedGeneratedMapping", "reviewed");
        Assert.Equal(1, Evaluate(Policy(RepositoryPolicyMode.Strict), null, unknown).ExitCode);
        Assert.Equal(0, PolicyEvaluator.Evaluate(Policy(RepositoryPolicyMode.Strict), null, [unknown], [exemption]).ExitCode);
        Assert.Equal(2, PolicyEvaluator.Evaluate(Policy(RepositoryPolicyMode.Strict), null,
            [Observation(4, .5, 6)], [exemption]).ExitCode);
    }

    [Fact]
    public void BodyChangeRetainsNamedCeilingAndReportsDifferenceWhileDeletedEntriesAreStale()
    {
        var baseline = Baseline(
            new BaselineEntry("method", "key", "crap.thresholdExceeded", "callables-v1", "old.cs", "old", 4, .5, 6),
            new BaselineEntry("method", "deleted", "crap.thresholdExceeded", "callables-v1", "gone.cs", "old", 4, .5, 6));
        var result = Evaluate(Policy(RepositoryPolicyMode.Incremental), baseline, Observation(4, .5, 6) with
        { BodyChecksum = "new", Path = "moved.cs" });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Findings, item => item.Code == "baseline.bodyFingerprintChanged");
        Assert.Contains(result.Findings, item => item.Code == "baseline.pathChanged");
        Assert.Contains(result.Findings, item => item.Code == "baseline.staleEntry" && item.EntityKey == "deleted");
    }

    [Fact]
    public void BaselineRejectsDuplicatesNonFiniteAndCompatibilityMismatch()
    {
        var entry = new BaselineEntry("method", "key", "crap.thresholdExceeded", "callables-v1", "a.cs", "body", 4, .5, 6);
        Assert.Throws<BaselineException>(() => BaselineDocument.Validate(Baseline(entry, entry), "hash", "callables-v1"));
        Assert.Throws<BaselineException>(() => BaselineDocument.Validate(Baseline(entry with { Coverage = double.NaN }), "hash", "callables-v1"));
        Assert.Throws<BaselineException>(() => BaselineDocument.Validate(Baseline(entry), "other", "callables-v1"));
    }

    private static PolicyEvaluationResult Evaluate(RepositoryPolicy policy, BaselineDocument? baseline,
        PolicyObservation observation) => PolicyEvaluator.Evaluate(policy, baseline, [observation], []);

    private static RepositoryPolicy Policy(RepositoryPolicyMode mode) => new("repository-policy-v1", mode,
        ["samples/Fixture/Fixture/Fixture.csproj"], ["samples/Fixture/Fixture.Tests/Fixture.Tests.csproj"], "Release",
        ["net10.0"], "base", 5, MissingCoveragePolicy.Fail, ["tests", "coverage", "crap"], [],
        "callables-v1", mode == RepositoryPolicyMode.Incremental ? "baseline.json" : null, [], new PolicyAllowedOverrides());

    private static BaselineDocument Baseline(params BaselineEntry[] entries) => new("baseline-v1", "callables-v1",
        "hash", "source", "revision", entries);

    private static PolicyObservation Observation(int complexity, double? coverage, double? crap) =>
        new("method", "key", "crap.thresholdExceeded", "callables-v1", "ctx", "a.cs", "body", complexity,
            coverage, crap, null, true, false);
}
