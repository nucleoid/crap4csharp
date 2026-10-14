using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class FreshGitProofTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("head")]
    [InlineData("index")]
    [InlineData("source")]
    [InlineData("untracked")]
    [InlineData("context")]
    [InlineData("expected-output")]
    [InlineData("index-output")]
    [InlineData("owned-bundle")]
    [InlineData("bundle-extra")]
    [InlineData("index-bundle")]
    public Task RealGitRevisionIndexAndScopeChangesRefusePublication(string mutation) => RunCase(mutation, false);

    [Theory]
    [InlineData("none")]
    [InlineData("head")]
    [InlineData("index")]
    [InlineData("source")]
    [InlineData("untracked")]
    [InlineData("context")]
    [InlineData("expected-output")]
    [InlineData("index-output")]
    [InlineData("owned-bundle")]
    [InlineData("bundle-extra")]
    [InlineData("index-bundle")]
    public Task PairedOriginalAndCurrentReadsNeverRebaselineOriginalGitState(string mutation) => RunCase(mutation, true);

    private static async Task RunCase(string mutation, bool paired)
    {
        using var fixture = TestDirectory.Create("crap4csharp-fresh-git-proof");
        fixture.Write("App/App.csproj", "project metadata never loaded");
        fixture.Write("Tests/Tests.csproj", "test metadata never loaded");
        fixture.Write("App/Code.cs", "class A { int Value() => 42; }");
        fixture.Write("README.md", "original");
        fixture.Write("policy.json", """
            {"schemaVersion":"repository-policy-v1","mode":"strict","productionProjects":["App/App.csproj"],
            "testProjects":["Tests/Tests.csproj"],"configuration":"Debug","targetFrameworks":["net10.0"],"scope":"base",
            "threshold":8,"missingCoverage":"fail","requiredChecks":["tests","coverage","crap"],"exclusions":[],"ruleset":"callables-v1"}
            """);
        var cancellation = Xunit.TestContext.Current.CancellationToken;
        async Task Git(params string[] args) => Assert.Equal(0, (await ProcessRunner.RunAsync("git", args, fixture.Path, TimeSpan.FromSeconds(30), cancellation)).ExitCode);
        await Git("init", "-q"); await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@example.invalid");
        await Git("add", "."); await Git("commit", "-qm", "original");
        var request = new FreshCheckRequest(null, "policy.json", "HEAD", null, null, "worktree", "method", null, "bundle",
            false, false, null, new PolicyOverrides(), TimeSpan.FromSeconds(30));
        var preflight = await FreshCheckPreflightLoader.LoadAsync(request, fixture.Path, cancellation);
        switch (mutation)
        {
            case "head": await Git("commit", "--allow-empty", "-qm", "moved HEAD"); break;
            case "index": fixture.Write("README.md", "staged change"); await Git("add", "README.md"); break;
            case "source":
                var source = Path.Combine(fixture.Path, "App/Code.cs"); var clock = File.GetLastWriteTimeUtc(source);
                fixture.Write("App/Code.cs", "class A { int Value() => 99; }"); File.SetLastWriteTimeUtc(source, clock); break;
            case "untracked": fixture.Write("App/New.cs", "class New { int Value() => 1; }"); break;
            case "context": fixture.Write("Directory.Build.props", "<Project />"); break;
            case "expected-output": fixture.Write("App/obj/Generated.g.cs", "class Generated {}"); fixture.Write("App/obj/generated.props", "<Project />"); break;
            case "index-output": fixture.Write("App/obj/Generated.g.cs", "class Generated {}"); await Git("add", "App/obj/Generated.g.cs"); break;
            case "owned-bundle": fixture.Write(".bundle-owned.incomplete/manifest.json", "owned"); break;
            case "bundle-extra": fixture.Write(".bundle-owned.incomplete/manifest.json", "owned"); fixture.Write(".bundle-owned.incomplete/extra.cs", "class Foreign {}"); break;
            case "index-bundle": fixture.Write(".bundle-owned.incomplete/manifest.json", "owned"); await Git("add", ".bundle-owned.incomplete/manifest.json"); break;
        }
        var outputs = new[] { Path.Combine(fixture.Path, "App", "obj") };
        var consumerIndex = Path.Combine(fixture.Path, ".git", "index");
        var indexBeforeProof = File.ReadAllBytes(consumerIndex);
        var ownedFiles = mutation is "owned-bundle" or "bundle-extra" or "index-bundle"
            ? new HashSet<string>(PathIdentityPolicy.Current.Comparer) { Path.Combine(fixture.Path, ".bundle-owned.incomplete", "manifest.json") } : null;
        var current = paired ? await FreshCheckPreflightLoader.LoadAsync(request, fixture.Path, cancellation) : preflight;
        Task Validate() => paired
            ? FreshGitProof.ValidateOriginalAndCurrentAsync(preflight, current, cancellation, outputs,
                ownedFiles ?? new HashSet<string>(PathIdentityPolicy.Current.Comparer))
            : FreshGitProof.ValidateAsync(preflight, cancellation, outputs, ownedFiles);
        if (mutation is "none" or "expected-output" or "owned-bundle") await Validate();
        else Assert.Equal("provenance.gitStateChanged", (await Assert.ThrowsAsync<PolicyException>(Validate)).Code);
        Assert.Equal(indexBeforeProof, File.ReadAllBytes(consumerIndex));
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
    }
}
