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
    public async Task RealGitRevisionIndexAndScopeChangesRefusePublication(string mutation)
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
        }
        var outputs = new[] { Path.Combine(fixture.Path, "App", "obj") };
        if (mutation is "none" or "expected-output") await FreshGitProof.ValidateAsync(preflight, cancellation, outputs);
        else Assert.Equal("provenance.gitStateChanged", (await Assert.ThrowsAsync<PolicyException>(() => FreshGitProof.ValidateAsync(preflight, cancellation, outputs))).Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
    }
}
