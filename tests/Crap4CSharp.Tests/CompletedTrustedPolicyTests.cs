using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompletedTrustedPolicyTests
{
    private static readonly byte[] Policy = "{\"version\":1,\"threshold\":30}"u8.ToArray();
    private static readonly byte[] Exemption = "{\"version\":\"callable-exemptions-v1\",\"entries\":[]}"u8.ToArray();
    private const string Path = "reviewed/exemptions.json";
    private static string ExemptionId => "exemption-" + CanonicalIdentity.Tuple("exemption", Path);

    private static ManifestArtifact Artifact(string id, string kind, string locator, byte[] bytes) =>
        new(id, kind, locator, bytes.Length, CanonicalIdentity.Sha256(bytes), null, null, null, "json", null);

    private static void Validate(IReadOnlyList<ManifestArtifact> artifacts,
        IReadOnlyDictionary<string, ImmutableArray<byte>> bytes, byte[] policy,
        IReadOnlyDictionary<string, byte[]> exemptions, CancellationToken token)
    {
        var type = typeof(App).Assembly.GetType("CompletedTrustedPolicy");
        Assert.NotNull(type);
        var method = type.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        try { method.Invoke(null, [artifacts, bytes, policy, exemptions, token]); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("no-exemptions")]
    [InlineData("missing-policy")]
    [InlineData("duplicate-policy")]
    [InlineData("policy-id")]
    [InlineData("policy-kind")]
    [InlineData("policy-format")]
    [InlineData("policy-context")]
    [InlineData("policy-build")]
    [InlineData("policy-execution")]
    [InlineData("policy-coordinate")]
    [InlineData("policy-length")]
    [InlineData("policy-hash")]
    [InlineData("policy-bytes")]
    [InlineData("policy-coherent-substitution")]
    [InlineData("policy-whitespace")]
    [InlineData("missing-bytes")]
    [InlineData("missing-exemption")]
    [InlineData("extra-exemption")]
    [InlineData("duplicate-exemption")]
    [InlineData("exemption-path")]
    [InlineData("exemption-kind")]
    [InlineData("exemption-format")]
    [InlineData("exemption-context")]
    [InlineData("exemption-coherent-substitution")]
    [InlineData("locator-alias")]
    [InlineData("empty-policy")]
    public void IndependentlyLoadedPolicyAndExactExemptionMembershipAreRequired(string mutation)
    {
        var policy = Policy.ToArray();
        var artifacts = new List<ManifestArtifact> { Artifact("trusted-policy", "trusted-policy", "policy.json", Policy),
            Artifact(ExemptionId, "trusted-exemption", "exemption.json", Exemption) };
        var bytes = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal)
        { ["policy.json"] = ImmutableArray.Create(Policy), ["exemption.json"] = ImmutableArray.Create(Exemption) };
        var exemptions = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [Path] = Exemption };
        switch (mutation)
        {
            case "no-exemptions": artifacts.RemoveAt(1); exemptions.Clear(); break;
            case "missing-policy": artifacts.RemoveAt(0); break;
            case "duplicate-policy": artifacts.Add(artifacts[0] with { Locator = "duplicate.json" }); break;
            case "policy-id": artifacts[0] = artifacts[0] with { Id = "other-policy" }; break;
            case "policy-kind": artifacts[0] = artifacts[0] with { Kind = "policy" }; break;
            case "policy-format": artifacts[0] = artifacts[0] with { Format = "binary" }; break;
            case "policy-context": artifacts[0] = artifacts[0] with { ContextId = "context" }; break;
            case "policy-build": artifacts[0] = artifacts[0] with { BuildId = "build" }; break;
            case "policy-execution": artifacts[0] = artifacts[0] with { ExecutionId = "test" }; break;
            case "policy-coordinate": artifacts[0] = artifacts[0] with { CoordinateKind = "line" }; break;
            case "policy-length": artifacts[0] = artifacts[0] with { Length = Policy.Length + 1 }; break;
            case "policy-hash": artifacts[0] = artifacts[0] with { Sha256 = new string('0', 64) }; break;
            case "policy-bytes": bytes["policy.json"] = ImmutableArray.Create("{}"u8.ToArray()); break;
            case "policy-coherent-substitution":
                var substituted = "{\"version\":1,\"threshold\":300}"u8.ToArray();
                artifacts[0] = Artifact("trusted-policy", "trusted-policy", "policy.json", substituted);
                bytes["policy.json"] = ImmutableArray.Create(substituted); break;
            case "policy-whitespace":
                var whitespace = Policy.Concat(" "u8.ToArray()).ToArray();
                artifacts[0] = Artifact("trusted-policy", "trusted-policy", "policy.json", whitespace);
                bytes["policy.json"] = ImmutableArray.Create(whitespace); break;
            case "missing-bytes": bytes.Remove("policy.json"); break;
            case "missing-exemption": artifacts.RemoveAt(1); break;
            case "extra-exemption": artifacts.Add(Artifact("extra", "trusted-exemption", "extra.json", Exemption)); break;
            case "duplicate-exemption": artifacts.Add(artifacts[1] with { Locator = "duplicate.json" }); break;
            case "exemption-path": exemptions.Clear(); exemptions["other/exemptions.json"] = Exemption; break;
            case "exemption-kind": artifacts[1] = artifacts[1] with { Kind = "other" }; break;
            case "exemption-format": artifacts[1] = artifacts[1] with { Format = "binary" }; break;
            case "exemption-context": artifacts[1] = artifacts[1] with { ContextId = "context" }; break;
            case "exemption-coherent-substitution":
                var other = "{\"version\":\"callable-exemptions-v1\",\"entries\":[{}]}"u8.ToArray();
                artifacts[1] = Artifact(ExemptionId, "trusted-exemption", "exemption.json", other);
                bytes["exemption.json"] = ImmutableArray.Create(other); break;
            case "locator-alias": artifacts[1] = artifacts[1] with { Locator = "policy.json" }; break;
            case "empty-policy": policy = []; break;
        }
        var token = TestContext.Current.CancellationToken;
        if (mutation is "none" or "no-exemptions") Validate(artifacts, bytes, policy, exemptions, token);
        else Assert.Equal("provenance.trustedPolicyMismatch", Assert.Throws<PolicyException>(() =>
            Validate(artifacts, bytes, policy, exemptions, token)).Code);
    }

    [Fact]
    public void CancellationPrecedesMalformedEvidence()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Validate([], new Dictionary<string, ImmutableArray<byte>>(),
            [], new Dictionary<string, byte[]>(), cancellation.Token));
    }
}
