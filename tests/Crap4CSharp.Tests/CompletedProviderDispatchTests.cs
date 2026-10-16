using System.Reflection;
using System.Runtime.ExceptionServices;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompletedProviderDispatchTests
{
    private static ManifestContext Context(string provider) => new("context", "Library/Library.csproj", "net10.0",
        "Debug", "AnyCPU", "source", "context", "closure", true, true, [])
    { CurrentRevalidation = new(provider, "Library/Library.csproj", "Library/bin/Library.dll", "Library/bin/Library.pdb") };

    private static void Validate(IReadOnlyList<ManifestContext> contexts)
    {
        var manifest = new RunManifest(ManifestIdentity.SchemaVersion, CanonicalIdentity.Algorithm,
            new("crap4csharp", "test", ComplexityRules.CallablesV1, ProjectAnalysisContext.ProtocolVersion,
                ManifestIdentity.CoverageProtocol, ManifestIdentity.PathProtocol), new("completed", true, true, []),
            new("none", "repository", "workspace", null, null, null), [], contexts, [], [], [],
            new("scope", "policy", null, null), null);
        var method = typeof(App).Assembly.GetType("CurrentEvidenceAdapter")!.GetMethod("ValidateSupportedRecipes",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        try { method.Invoke(null, [manifest]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }

    [Theory]
    [InlineData("sdk-project-context-output-v1")]
    [InlineData("finite-sdk-completed-tests-v1")]
    public void HomogeneousCompleteDeclarationsReachTheirOwnValidator(string provider)
        => Validate([Context(provider), Context(provider) with { Id = "second" }]);

    [Theory]
    [InlineData("empty")]
    [InlineData("incomplete")]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("mixed")]
    public void UnsupportedOrMixedProvidersRefuseBeforeProjectWork(string mutation)
    {
        var context = Context("finite-sdk-completed-tests-v1");
        IReadOnlyList<ManifestContext> contexts = mutation switch
        {
            "empty" => [],
            "incomplete" => [context with { ReuseRecipeComplete = false }],
            "missing" => [context with { CurrentRevalidation = null }],
            "foreign" => [Context("foreign")],
            "mixed" => [context, Context("sdk-project-context-output-v1") with { Id = "second" }],
            _ => throw new InvalidOperationException()
        };
        Assert.Equal("provenance.revalidationRecipeUnsupported", Assert.Throws<PolicyException>(() => Validate(contexts)).Code);
    }
}
