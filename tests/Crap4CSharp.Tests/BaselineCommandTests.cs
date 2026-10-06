using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class BaselineCommandTests
{
    [Fact]
    public void ParserRequiresExplicitCandidateDestinationAndRejectsUnrelatedSelectors()
    {
        Assert.Throws<ArgumentException>(() => BaselineCommand.Parse(
            ["baseline", "create", "--policy", "policy.json", "--reuse-artifacts", "manifest.json"]));
        Assert.Throws<ArgumentException>(() => BaselineCommand.Parse(
            ["baseline", "create", "--policy", "policy.json", "--reuse-artifacts", "manifest.json", "--output", "b.json", "--framework", "net10.0"]));
        var parsed = BaselineCommand.Parse(
            ["baseline", "update", "--policy", "policy.json", "--reuse-artifacts", "manifest.json", "--output", "b.json", "--overwrite"]);
        Assert.True(parsed.Overwrite);
        Assert.Equal("update", parsed.Verb);
    }

    [Fact]
    public void CurrentRevalidationRecipeIsHashBoundWithoutUpgradingOldManifests()
    {
        var context = new ManifestContext("ctx", "App.csproj", "net10.0", "Release", "AnyCPU",
            "sources", "context", "closure", true, true, [])
        {
            ParseOptions = new ManifestParseOptions("Preview", "Regular", [], new Dictionary<string, string>()),
            PathPolicy = new ManifestPathPolicy("sensitive", [])
        };
        var manifest = EmptyManifest(context);
        var oldHash = ManifestIdentity.ManifestHash(manifest);
        var recipe = new ManifestCurrentRevalidation(CurrentEvidenceAdapter.SupportedRecipeProvider,
            "App.csproj", "bin/Release/net10.0/App.dll", "bin/Release/net10.0/App.pdb");
        var newHash = ManifestIdentity.ManifestHash(manifest with
        { Contexts = [context with { CurrentRevalidation = recipe }] });
        var referenceHash = ManifestIdentity.ManifestHash(manifest with
        { Contexts = [context with { CurrentRevalidation = recipe with
            { References = [new ManifestCurrentReference("refs/A.dll", "inputs/A.dll")] } }] });

        Assert.NotEqual(oldHash, newHash);
        Assert.NotEqual(newHash, referenceHash);
        Assert.Null(context.CurrentRevalidation);
    }

    [Fact]
    public async Task MissingManifestIsStructuredOperationalFailureAndDoesNotWriteCandidate()
    {
        using var directory = TestDirectory.Create("crap4csharp-baseline-missing-manifest");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "policy.json"), PolicyTests.ValidPolicy
            .Replace("\"incremental\"", "\"strict\"", StringComparison.Ordinal)
            .Replace("\"scope\": \"base\"", "\"scope\": \"all\"", StringComparison.Ordinal)
            .WithoutBaseline(),
            TestContext.Current.CancellationToken);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await BaselineCommand.RunAsync(["baseline", "create", "--policy", "policy.json",
            "--reuse-artifacts", "missing.json", "--output", "candidate.json"], directory.Path,
            output, error, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Contains("\"exitCode\": 1", output.ToString());
        Assert.False(File.Exists(Path.Combine(directory.Path, "candidate.json")));
    }

    [Fact]
    public void CandidateGenerationRequiresEveryPolicyProjectFrameworkConfigurationAndTestTarget()
    {
        var policy = RepositoryPolicyParser.Parse(System.Text.Encoding.UTF8.GetBytes(PolicyTests.ValidPolicy),
            "quality/policy.json").Policy;
        var context = new ManifestContext("ctx", policy.ProductionProjects.Single(), "net10.0", "Release", "AnyCPU",
            "sources", "context", "closure", true, true, []);
        var valid = EmptyManifest(context) with
        {
            Executions = [new ManifestExecution("tests", "ctx", "build", true, 0, 1, 1, 0, 0)
                { TestProject = policy.TestProjects.Single() }]
        };

        BaselineCommand.ValidatePolicyCoverage(valid, policy);
        Assert.Throws<PolicyException>(() => BaselineCommand.ValidatePolicyCoverage(valid with
        {
            Contexts = [context with { TargetFramework = "net9.0" }]
        }, policy));
        Assert.Throws<PolicyException>(() => BaselineCommand.ValidatePolicyCoverage(valid with
        {
            Executions = [valid.Executions.Single() with { TestProject = "other.csproj" }]
        }, policy));
    }

    [Fact]
    public void CandidateGenerationRejectsCapturedSubsetScope()
    {
        var context = new ManifestContext("ctx", "App/App.csproj", "net10.0", "Release", "AnyCPU",
            "", "", "", true, true,
            [new ManifestInput("source", "One.cs", "inputs/one", 1, new string('a', 64), "utf-8", false),
             new ManifestInput("source", "Two.cs", "inputs/two", 1, new string('b', 64), "utf-8", false)]);

        var error = Assert.Throws<PolicyException>(() => BaselineCommand.ValidateCompleteScope(
            EmptyManifest(context), new CapturedScope(["App/One.cs"])));

        Assert.Equal("baseline.scopeIncomplete", error.Code);
    }

    [Fact]
    public void CandidateSummaryReportsKnownUnknownCoverageDebtWithoutInventingAllowance()
    {
        var family = new PolicyObservation("family", "family", CallableFamilyEvaluator.Rule,
            ComplexityRules.CallablesV1, "ctx", "App/Gate.cs", "body", 9, null, null,
            CoverageReasonCodes.UnsupportedGeneratedMapping, true, false);
        var numeric = family with { EntityKey = "numeric", Coverage = .5, Crap = 19.125 };
        var below = family with { EntityKey = "below", Complexity = 1 };
        var omitted = Assert.Single(BaselineCommand.OmittedKnownViolations(8, [numeric, family, below]));
        Assert.Equal("family", omitted.EntityKey);
        Assert.Equal(9, omitted.Complexity);
        Assert.Equal("baseline.unknownCoverageCannotReceiveAllowance", omitted.Reason);
        Assert.Equal(CoverageReasonCodes.UnsupportedGeneratedMapping, omitted.CoverageReason);
        var candidate = BaselineDocument.Generate("hash", ComplexityRules.CallablesV1, "source", "revision",
            8, [numeric, family, below]);
        Assert.Equal("numeric", Assert.Single(candidate.Entries).EntityKey);
    }

    [Fact]
    public void CandidateSourceIdentityIsPortableAndBindsLogicalSourceContentAndContext()
    {
        var input = new ManifestInput("source", "Gate.cs", "inputs/gate", 1, new string('a', 64), "utf-8", false)
            { RepositoryPath = "App/Gate.cs" };
        var context = new ManifestContext("observed-a", "App/App.csproj", "net10.0", "Release", "AnyCPU",
            "sources", "context", "closure", true, true, [input]);
        var manifest = EmptyManifest(context);
        var relocated = manifest with { Revision = manifest.Revision with { WorkspaceIdentity = "different-machine-root" },
            Contexts = [context with { Id = "observed-b", PathPolicy = new ManifestPathPolicy("sensitive",
                [new ManifestReportRootMapping("/different/absolute/root", "")]) }] };
        Assert.Equal(BaselineCommand.SourceIdentity(manifest), BaselineCommand.SourceIdentity(relocated));
        Assert.NotEqual(BaselineCommand.SourceIdentity(manifest), BaselineCommand.SourceIdentity(manifest with
            { Contexts = [context with { Inputs = [input with { Sha256 = new string('b', 64) }] }] }));
        Assert.NotEqual(BaselineCommand.SourceIdentity(manifest), BaselineCommand.SourceIdentity(manifest with
            { Contexts = [context with { TargetFramework = "net11.0" }] }));
        Assert.NotEqual(BaselineCommand.SourceIdentity(manifest), BaselineCommand.SourceIdentity(manifest with
            { Contexts = [context with { Inputs = [input with { RepositoryPath = "App/Other.cs" }] }] }));
    }

    private static RunManifest EmptyManifest(ManifestContext context) => new(ManifestIdentity.SchemaVersion,
        CanonicalIdentity.Algorithm, new ManifestProducer("crap4csharp", "0.1.0", ComplexityRules.CallablesV1,
            ProjectAnalysisContext.ProtocolVersion, ManifestIdentity.CoverageProtocol, ManifestIdentity.PathProtocol),
        new ManifestCapture("completed", true, true, []), new ManifestRevision("none", "none", "workspace", null, null, null),
        [new ManifestRoot("workspace", "workspace", "sensitive")], [context], [], [], [],
        new ManifestEvaluationInputs("scope", "policy", null, null), null);
}
