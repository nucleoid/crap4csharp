using System.Text;
using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class SavedLocalMembershipTests
{
    private static readonly string Hash = new('a', 64);
    private static Dictionary<string, string> Original() => new(StringComparer.Ordinal)
        { ["App/App.csproj"] = Hash, ["compiler-cache/Authored.cs"] = Hash, ["compiler-cache/Rules.json"] = Hash };

    [Fact]
    public void OriginalRecipeIsCanonicalHashOnlyAndBoundToOriginalState()
    {
        var original = Original(); var token = TestContext.Current.CancellationToken;
        var bytes = SavedLocalMembership.Capture(original, token);
        var reversed = original.Reverse().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        Assert.Equal(bytes, SavedLocalMembership.Capture(reversed, token));
        Assert.Equal(FreshLocalProof.Identity(original), FreshLocalProof.Identity(
            SavedLocalMembership.Parse(bytes, FreshLocalProof.Identity(original), token)));
        Assert.DoesNotContain("public class", Encoding.UTF8.GetString(bytes));
        Assert.Equal("provenance.savedLocalMembershipUnproven", Assert.Throws<PolicyException>(() =>
            SavedLocalMembership.Parse(bytes, new string('b', 64), token)).Code);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("duplicate-field")]
    [InlineData("duplicate-path")]
    [InlineData("order")]
    [InlineData("absolute")]
    [InlineData("escape")]
    [InlineData("backslash")]
    [InlineData("dot")]
    [InlineData("drive")]
    [InlineData("empty")]
    [InlineData("hash")]
    [InlineData("uppercase-hash")]
    [InlineData("type")]
    [InlineData("entry-field")]
    [InlineData("oversize")]
    [InlineData("depth")]
    [InlineData("count")]
    public void ParserRejectsMalformedOrUnboundOriginalRecipes(string mutation)
    {
        var original = Original(); var token = TestContext.Current.CancellationToken;
        var json = Encoding.UTF8.GetString(SavedLocalMembership.Capture(original, token));
        json = mutation switch
        {
            "provider" => json.Replace("finite-local-membership-v1", "other", StringComparison.Ordinal),
            "missing" => "{\"provider\":\"finite-local-membership-v1\"}",
            "unknown" => json.Replace("{\"provider\"", "{\"extra\":true,\"provider\"", StringComparison.Ordinal),
            "duplicate-field" => json.Replace("{\"provider\"", "{\"provider\":\"finite-local-membership-v1\",\"provider\"", StringComparison.Ordinal),
            "duplicate-path" => json.Replace("compiler-cache/Authored.cs", "App/App.csproj", StringComparison.Ordinal),
            "order" => json.Replace("App/App.csproj", "z.cs", StringComparison.Ordinal),
            "absolute" => json.Replace("App/App.csproj", "/App/App.csproj", StringComparison.Ordinal),
            "escape" => json.Replace("App/App.csproj", "../App/App.csproj", StringComparison.Ordinal),
            "backslash" => json.Replace("App/App.csproj", "App\\\\App.csproj", StringComparison.Ordinal),
            "dot" => json.Replace("App/App.csproj", "App/./App.csproj", StringComparison.Ordinal),
            "drive" => json.Replace("App/App.csproj", "C:/App.csproj", StringComparison.Ordinal),
            "empty" => json.Replace("App/App.csproj", "", StringComparison.Ordinal),
            "hash" => json.Replace(Hash, "wrong", StringComparison.Ordinal),
            "uppercase-hash" => json.Replace(Hash, Hash.ToUpperInvariant(), StringComparison.Ordinal),
            "type" => json.Replace("\"entries\":[", "\"entries\":{\"x\":[", StringComparison.Ordinal),
            "entry-field" => json.Replace("\"sha256\":", "\"extra\":1,\"sha256\":", StringComparison.Ordinal),
            "oversize" => new string(' ', SavedLocalMembership.MaximumBytes + 1),
            "depth" => "{\"provider\":\"finite-local-membership-v1\",\"entries\":[[[[[[]]]]]]}",
            "count" => JsonSerializer.Serialize(new { provider = "finite-local-membership-v1", entries = Enumerable.Range(0, 20001).Select(i => new { path = i + ".cs", sha256 = Hash }) }),
            _ => throw new InvalidOperationException()
        };
        Assert.Equal("provenance.savedLocalMembershipUnproven", Assert.Throws<PolicyException>(() =>
            SavedLocalMembership.Parse(Encoding.UTF8.GetBytes(json), FreshLocalProof.Identity(original), token)).Code);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("changed-original")]
    [InlineData("deleted-original")]
    [InlineData("changed-original-json")]
    [InlineData("sibling")]
    [InlineData("outside")]
    public async Task OnlyNewProvenOutputEntriesAreOmittedFromCurrentObservation(string mutation)
    {
        using var fixture = TestDirectory.Create("crap4csharp-saved-local-membership");
        fixture.Write("App/App.csproj", "<Project />"); fixture.Write("Tests/Tests.csproj", "<Project />");
        fixture.Write("policy.json", """
            {"schemaVersion":"repository-policy-v1","mode":"strict","productionProjects":["App/App.csproj"],
             "testProjects":["Tests/Tests.csproj"],"configuration":"Debug","targetFrameworks":["net10.0"],
             "scope":"all","threshold":999,"missingCoverage":"fail","requiredChecks":["tests","coverage","crap"],
             "exclusions":[],"ruleset":"callables-v1","exemptionFiles":[]}
            """);
        var token = TestContext.Current.CancellationToken;
        var preflight = await FreshCheckPreflightLoader.LoadAsync(new("App/App.csproj", "policy.json", null, "all", null,
            "worktree", "method", null, null, false, false, null, new PolicyOverrides(), TimeSpan.FromSeconds(30)), fixture.Path, token);
        var original = Original();
        var saved = SavedLocalMembership.Capture(original, token);
        var parsed = SavedLocalMembership.Parse(saved, FreshLocalProof.Identity(original), token);
        var current = new Dictionary<string, string>(original, StringComparer.Ordinal)
            { ["compiler-cache/project.assets.json"] = new string('b', 64), ["compiler-cache/Debug/Generated.cs"] = Hash };
        switch (mutation)
        {
            case "changed-original": current["compiler-cache/Authored.cs"] = new string('c', 64); break;
            case "deleted-original": current.Remove("compiler-cache/Authored.cs"); break;
            case "changed-original-json": current["compiler-cache/Rules.json"] = new string('c', 64); break;
            case "sibling": current.Add("compiler-cache-sibling/New.cs", Hash); break;
            case "outside": current.Add("New.resx", Hash); break;
        }
        var normalized = FreshLocalProof.NormalizeCurrent(preflight, parsed, current, [Path.Combine(fixture.Path, "compiler-cache")], token);
        Assert.Equal(mutation == "none", FreshLocalProof.Identity(original) == FreshLocalProof.Identity(normalized));
        Assert.Equal(FreshLocalProof.Identity(original), FreshLocalProof.Identity(parsed));
        Assert.DoesNotContain("compiler-cache/project.assets.json", normalized.Keys);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("id")]
    [InlineData("kind")]
    [InlineData("format")]
    [InlineData("context")]
    [InlineData("build")]
    [InlineData("execution")]
    [InlineData("coordinate")]
    [InlineData("missing-bytes")]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("bytes")]
    [InlineData("coherent-other-original")]
    [InlineData("revision-kind")]
    public async Task OriginalRecipeArtifactDeclarationIsRequiredAndBoundToOriginalRevision(string mutation)
    {
        using var fixture = TestDirectory.Create("crap4csharp-saved-local-declaration");
        fixture.Write("App/App.csproj", "<Project />"); fixture.Write("Tests/Tests.csproj", "<Project />");
        fixture.Write("policy.json", """
            {"schemaVersion":"repository-policy-v1","mode":"strict","productionProjects":["App/App.csproj"],
             "testProjects":["Tests/Tests.csproj"],"configuration":"Debug","targetFrameworks":["net10.0"],
             "scope":"all","threshold":999,"missingCoverage":"fail","requiredChecks":["tests","coverage","crap"],
             "exclusions":[],"ruleset":"callables-v1","exemptionFiles":[]}
            """);
        var token = TestContext.Current.CancellationToken;
        var preflight = await FreshCheckPreflightLoader.LoadAsync(new("App/App.csproj", "policy.json", null, "all", null,
            "worktree", "method", null, null, false, false, null, new PolicyOverrides(), TimeSpan.FromSeconds(30)), fixture.Path, token);
        var original = preflight.LocalSnapshot!;
        var state = FreshLocalProof.Identity(original);
        var bytes = SavedLocalMembership.Capture(original, token);
        var artifact = new ManifestArtifact(SavedLocalMembership.Kind, SavedLocalMembership.Kind, "membership.json",
            bytes.Length, CanonicalIdentity.Sha256(bytes), null, null, null, "json", null);
        var contents = new Dictionary<string, System.Collections.Immutable.ImmutableArray<byte>>(StringComparer.Ordinal)
            { [artifact.Locator] = System.Collections.Immutable.ImmutableArray.Create(bytes) };
        var revision = new ManifestRevision("none", "repository", "workspace", null, null, null) { StateHash = state };
        var artifacts = new[] { artifact };
        switch (mutation)
        {
            case "missing": artifacts = []; break;
            case "duplicate": artifacts = [artifact, artifact with { Id = "duplicate" }]; break;
            case "id": artifacts = [artifact with { Id = "other" }]; break;
            case "kind": artifacts = [artifact with { Kind = "other" }]; break;
            case "format": artifacts = [artifact with { Format = "xml" }]; break;
            case "context": artifacts = [artifact with { ContextId = "context" }]; break;
            case "build": artifacts = [artifact with { BuildId = "build" }]; break;
            case "execution": artifacts = [artifact with { ExecutionId = "execution" }]; break;
            case "coordinate": artifacts = [artifact with { CoordinateKind = "line" }]; break;
            case "missing-bytes": contents.Clear(); break;
            case "length": artifacts = [artifact with { Length = bytes.Length + 1 }]; break;
            case "hash": artifacts = [artifact with { Sha256 = new string('b', 64) }]; break;
            case "bytes": contents[artifact.Locator] = System.Collections.Immutable.ImmutableArray.Create(System.Text.Encoding.UTF8.GetBytes("{}")); break;
            case "coherent-other-original":
                var other = new Dictionary<string, string>(original, StringComparer.Ordinal) { ["other.json"] = Hash };
                var replacement = SavedLocalMembership.Capture(other, token);
                contents[artifact.Locator] = System.Collections.Immutable.ImmutableArray.Create(replacement);
                artifacts = [artifact with { Length = replacement.Length, Sha256 = CanonicalIdentity.Sha256(replacement) }];
                break;
            case "revision-kind": revision = revision with { Kind = "git" }; break;
        }
        // Minimal declaration fixture intentionally does not attest compiler/test evidence.
        // Observe is called directly ONLY to exercise its distinct artifact-boundary contract.
        var manifest = ManifestIdentity.Seal(new RunManifest("run-manifest-v1", "canonical-sha256-v1",
            new("Crap4CSharp", "1", "callables-v1", "context", "coverage", "path"),
            new("complete", true, true, []), revision, [], [], [], [], artifacts,
            new("scope", "policy", null, null), null));
        var bundle = new ArtifactBundle(manifest, contents, Path.Combine(fixture.Path, "manifest.json"), fixture.Path, []);
        if (mutation == "none")
        {
            Assert.Equal(state, SavedLocalMembership.Observe(bundle, preflight, [], token));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => SavedLocalMembership.Observe(bundle, preflight, [], cancelled.Token));
        }
        else Assert.Equal("provenance.savedLocalMembershipUnproven", Assert.Throws<PolicyException>(() =>
            SavedLocalMembership.Observe(bundle, preflight, [], token)).Code);
        Assert.Equal(state, FreshLocalProof.Identity(original));
    }

    [Fact]
    public void OriginalRecipeHonorsCancellationBeforeCaptureOrParse()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SavedLocalMembership.Capture(Original(), cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SavedLocalMembership.Parse([], null, cancelled.Token));
    }
}
