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

        Assert.NotEqual(oldHash, newHash);
        Assert.Null(context.CurrentRevalidation);
    }

    private static RunManifest EmptyManifest(ManifestContext context) => new(ManifestIdentity.SchemaVersion,
        CanonicalIdentity.Algorithm, new ManifestProducer("crap4csharp", "0.1.0", ComplexityRules.CallablesV1,
            ProjectAnalysisContext.ProtocolVersion, ManifestIdentity.CoverageProtocol, ManifestIdentity.PathProtocol),
        new ManifestCapture("completed", true, true, []), new ManifestRevision("none", "none", "workspace", null, null, null),
        [new ManifestRoot("workspace", "workspace", "sensitive")], [context], [], [], [],
        new ManifestEvaluationInputs("scope", "policy", null, null), null);
}
