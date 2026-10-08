using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class SavedBundleFootprintTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("extra-file")]
    [InlineData("extra-empty-directory")]
    [InlineData("missing-file")]
    [InlineData("missing-manifest")]
    [InlineData("bytes")]
    [InlineData("manifest-whitespace")]
    [InlineData("missing-raw-identity")]
    [InlineData("protected-file")]
    [InlineData("workspace-root")]
    [InlineData("workspace-beneath-bundle")]
    [InlineData("sibling-protected")]
    public void ExactLoadedFilesAndBytesAreRequiredWithoutDirectoryBlanketExemption(string mutation)
    {
        using var fixture = TestDirectory.Create("crap4csharp-bundle-footprint");
        var manifestPath = WriteBundle(fixture);
        var bundle = ArtifactBundle.Load(manifestPath, fixture.Path);
        var artifactPath = Path.Combine(bundle.BundleRoot, "nested", "input.json");
        var time = File.GetLastWriteTimeUtc(artifactPath);
        var workspace = fixture.Path;
        string[] protectedPaths = [];
        switch (mutation)
        {
            case "extra-file": fixture.Write("bundle/extra.json", "{}"); break;
            case "extra-empty-directory": Directory.CreateDirectory(Path.Combine(bundle.BundleRoot, "extra")); break;
            case "missing-file": File.Delete(artifactPath); break;
            case "missing-manifest": File.Delete(manifestPath); break;
            case "bytes": File.WriteAllText(artifactPath, "[]"); File.SetLastWriteTimeUtc(artifactPath, time); break;
            case "manifest-whitespace":
                var manifestTime = File.GetLastWriteTimeUtc(manifestPath);
                File.AppendAllText(manifestPath, " "); File.SetLastWriteTimeUtc(manifestPath, manifestTime); break;
            case "missing-raw-identity": bundle = bundle with { LoadedManifestSha256 = null }; break;
            case "protected-file": protectedPaths = [artifactPath]; break;
            case "workspace-root": workspace = bundle.BundleRoot; break;
            case "workspace-beneath-bundle": workspace = Path.Combine(bundle.BundleRoot, "nested"); break;
            case "sibling-protected": protectedPaths = [Path.Combine(fixture.Path, "bundle-sibling", "App.csproj")]; break;
        }
        var token = TestContext.Current.CancellationToken;
        if (mutation is "none" or "sibling-protected")
        {
            var exact = SavedBundleFootprint.Validate(bundle, workspace, protectedPaths, token);
            Assert.Equal(bundle.ResolvedInputs.Order(StringComparer.Ordinal), exact.Order(StringComparer.Ordinal));
            Assert.Equal(2, exact.Count);
        }
        else Assert.Equal("provenance.savedBundleFootprintUnproven", Assert.Throws<PolicyException>(() =>
            SavedBundleFootprint.Validate(bundle, workspace, protectedPaths, token)).Code);
    }

    [Fact]
    public void CancellationPrecedesFileSystemAccess()
    {
        using var fixture = TestDirectory.Create("crap4csharp-bundle-footprint-cancel");
        var bundle = ArtifactBundle.Load(WriteBundle(fixture), fixture.Path);
        File.Delete(bundle.ManifestPath);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SavedBundleFootprint.Validate(bundle,
            fixture.Path, [], cancelled.Token));
    }

    [Fact]
    public void LinkedFileOrAncestorCannotBecomeAProvenBundleFootprint()
    {
        // Linux link controls are additional to portable membership/byte controls;
        // this does not claim Windows link-creation permission or runtime coverage.
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = TestDirectory.Create("crap4csharp-bundle-footprint-link");
        var bundle = ArtifactBundle.Load(WriteBundle(fixture), fixture.Path);
        var artifact = Path.Combine(bundle.BundleRoot, "nested", "input.json");
        var outside = fixture.Write("outside.json", "{}");
        File.Delete(artifact);
        File.CreateSymbolicLink(artifact, outside);
        try
        {
            Assert.Equal("provenance.savedBundleFootprintUnproven", Assert.Throws<PolicyException>(() =>
                SavedBundleFootprint.Validate(bundle, fixture.Path, [], TestContext.Current.CancellationToken)).Code);
        }
        finally { File.Delete(artifact); File.WriteAllText(artifact, "{}"); }
        var alias = Path.Combine(fixture.Path, "alias");
        Directory.CreateSymbolicLink(alias, bundle.BundleRoot);
        try
        {
            // Exercise this boundary with the already-loaded immutable bytes;
            // the separate loader may itself reject linked ancestors earlier.
            var linked = bundle with
            {
                BundleRoot = alias,
                ManifestPath = Path.Combine(alias, "manifest.json"),
                ResolvedInputs = bundle.ResolvedInputs.Select(path => Path.Combine(alias,
                    Path.GetRelativePath(bundle.BundleRoot, path))).ToArray()
            };
            Assert.Equal("provenance.savedBundleFootprintUnproven", Assert.Throws<PolicyException>(() =>
                SavedBundleFootprint.Validate(linked, fixture.Path, [], TestContext.Current.CancellationToken)).Code);
        }
        finally { Directory.Delete(alias); }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("changed-original")]
    [InlineData("deleted-original")]
    public async Task ProvenFileSetNeverErasesAnOriginalLocalEntry(string mutation)
    {
        using var fixture = TestDirectory.Create("crap4csharp-bundle-original-entry");
        fixture.Write("App/App.csproj", "<Project />"); fixture.Write("Tests/Tests.csproj", "<Project />");
        fixture.Write("bundle/original.json", "{}");
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
        var manifest = ManifestIdentity.Seal(new RunManifest(ManifestIdentity.SchemaVersion, CanonicalIdentity.Algorithm,
            new("crap4csharp", "0.1.0", "callables-v1", "context", "coverage", "path"),
            new("incomplete", false, false, []), new("none", "repository", "workspace", null, null, null) { StateHash = state },
            [], [], [], [], [artifact], new("scope", "policy", null, null), null));
        var contents = new Dictionary<string, System.Collections.Immutable.ImmutableArray<byte>>(StringComparer.Ordinal)
            { [artifact.Locator] = System.Collections.Immutable.ImmutableArray.Create(bytes) };
        var bundle = new ArtifactBundle(manifest, contents, Path.Combine(fixture.Path, "bundle/manifest.json"), fixture.Path, []);
        var current = new Dictionary<string, string>(original, StringComparer.Ordinal) { ["bundle/new.json"] = new string('a', 64) };
        if (mutation == "changed-original") current["bundle/original.json"] = new string('b', 64);
        if (mutation == "deleted-original") current.Remove("bundle/original.json");
        var provenFiles = new HashSet<string>(PathIdentityPolicy.Current.Comparer)
            { Path.Combine(fixture.Path, "bundle/original.json"), Path.Combine(fixture.Path, "bundle/new.json") };
        // Direct normalization-only contract; this fixture does not attest saved compiler/tests.
        var observed = SavedLocalMembership.Observe(bundle, preflight with { LocalSnapshot = current }, [], token, provenFiles);
        Assert.Equal(mutation == "none", observed == state);
        Assert.Equal(state, FreshLocalProof.Identity(original));
    }

    private static string WriteBundle(TestDirectory fixture)
    {
        // Minimal loader/footprint fixture: not successful compiler/test evidence.
        var bytes = System.Text.Encoding.UTF8.GetBytes("{}");
        var context = new ManifestContext("ctx", "App.csproj", "net10.0", "Debug", "AnyCPU",
            "", "", "", false, false, [])
        {
            ParseOptions = new("latest", "Regular", [], new Dictionary<string, string>()),
            PathPolicy = new("sensitive", [])
        };
        var manifest = ManifestIdentity.Seal(new RunManifest(ManifestIdentity.SchemaVersion,
            CanonicalIdentity.Algorithm, new("crap4csharp", "0.1.0", "callables-v1", "context", "coverage", "path"),
            new("incomplete", false, false, []), new("none", "repository", "workspace", null, null, null),
            [], [context], [], [], [new("input", "input", "nested/input.json", bytes.Length,
                CanonicalIdentity.Sha256(bytes), null, null, null, "json", null)], new("scope", "policy", null, null), null));
        fixture.Write("bundle/nested/input.json", "{}");
        return fixture.Write("bundle/manifest.json", JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}
