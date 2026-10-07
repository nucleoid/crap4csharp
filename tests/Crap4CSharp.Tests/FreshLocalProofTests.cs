using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class FreshLocalProofTests
{
    [Theory]
    [InlineData("source-content")]
    [InlineData("source-add")]
    [InlineData("source-delete")]
    [InlineData("project-content")]
    [InlineData("props-add")]
    [InlineData("targets-add")]
    [InlineData("context-json-add")]
    [InlineData("nested-source-add")]
    [InlineData("editorconfig-add")]
    [InlineData("globalconfig-add")]
    [InlineData("resource-add")]
    [InlineData("policy-content")]
    public async Task LocalRegateRejectsAuthoredMembershipOrContentDrift(string change)
    {
        using var fixture = Create();
        var token = TestContext.Current.CancellationToken;
        var preflight = await Load(fixture, token);
        FreshLocalProof.Validate(preflight, token);
        switch (change)
        {
            case "source-content": fixture.Write("App/Value.cs", "public class Changed { }"); break;
            case "source-add": fixture.Write("App/New.cs", "public class NewValue { }"); break;
            case "source-delete": File.Delete(Path.Combine(fixture.Path, "App/Value.cs")); break;
            case "project-content": fixture.Write("App/App.csproj", "<Project><PropertyGroup><Changed>true</Changed></PropertyGroup></Project>"); break;
            case "props-add": fixture.Write("Directory.Build.props", "<Project />"); break;
            case "targets-add": fixture.Write("Directory.Build.targets", "<Project />"); break;
            case "context-json-add": fixture.Write("global.json", "{}"); break;
            case "nested-source-add": fixture.Write("New/Nested/Extra.cs", "public class Extra { }"); break;
            case "editorconfig-add": fixture.Write(".editorconfig", "root=true"); break;
            case "globalconfig-add": fixture.Write(".globalconfig", "is_global=true"); break;
            case "resource-add": fixture.Write("App/Extra.resx", "<root />"); break;
            case "policy-content": fixture.Write("policy.json", "{}"); break;
        }
        Assert.Equal("provenance.localWorkspaceChanged", Assert.Throws<PolicyException>(() => FreshLocalProof.Validate(preflight, token)).Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, ".git")));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "result.json")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bundle")));
    }

    [Fact]
    public async Task DeclaredLocalOverlayOutsideAuthoredExtensionInventoryStillHasImmutableContentGuard()
    {
        using var fixture = Create();
        fixture.Write("policy.json", Policy.Replace("\"exemptionFiles\":[]", "\"exemptionFiles\":[\"waivers.txt\"]", StringComparison.Ordinal));
        fixture.Write("waivers.txt", "[]");
        var token = TestContext.Current.CancellationToken;
        var preflight = await Load(fixture, token);
        FreshLocalProof.Validate(preflight, token);
        fixture.Write("waivers.txt", "[{}]");
        Assert.Equal("provenance.localPolicyChanged", Assert.Throws<PolicyException>(() => FreshLocalProof.Validate(preflight, token)).Code);
    }

    [Fact]
    public async Task LocalFingerprintHasNoGitSelectionOrReviewedBaseTrustAndIsDeterministic()
    {
        using var fixture = Create();
        var token = TestContext.Current.CancellationToken;
        var preflight = await Load(fixture, token);
        Assert.Equal("local-unreviewed", preflight.Trusted.Trust);
        Assert.Equal("none", preflight.Trusted.Revision);
        Assert.Null(preflight.GitSnapshot); Assert.Null(preflight.Scope);
        Assert.NotNull(preflight.LocalSnapshot);
        Assert.Equal(FreshLocalProof.Identity(preflight.LocalSnapshot), FreshLocalProof.Identity(FreshLocalProof.Capture(fixture.Path, token)));
        FreshLocalProof.Validate(preflight, token);
    }

    [Fact]
    public void LocalFingerprintRejectsOversizedAuthoredFileWithoutReadingItsContents()
    {
        using var fixture = Create();
        using (var stream = File.Create(Path.Combine(fixture.Path, "large.cs"))) stream.SetLength(16 * 1024 * 1024 + 1);
        Assert.Equal("provenance.localSnapshotUnsupported", Assert.Throws<PolicyException>(() =>
            FreshLocalProof.Capture(fixture.Path, TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public void LocalFingerprintHonorsCancellationBeforeReadingWorkspace()
    {
        using var fixture = Create();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => FreshLocalProof.Capture(fixture.Path, cancelled.Token));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("directory")]
    public void LocalFingerprintRejectsReparseMembershipInsteadOfFollowingIt(string kind)
    {
        using var fixture = Create();
        using var outside = TestDirectory.Create("crap4csharp-local-outside");
        outside.Write("Foreign.cs", "public class Foreign { }");
        if (kind == "source") File.CreateSymbolicLink(Path.Combine(fixture.Path, "Linked.cs"), Path.Combine(outside.Path, "Foreign.cs"));
        else Directory.CreateSymbolicLink(Path.Combine(fixture.Path, "Linked"), outside.Path);
        Assert.Equal("provenance.localSnapshotUnsupported", Assert.Throws<PolicyException>(() =>
            FreshLocalProof.Capture(fixture.Path, TestContext.Current.CancellationToken)).Code);
    }

    private static Task<FreshCheckPreflight> Load(TestDirectory fixture, CancellationToken token) =>
        FreshCheckPreflightLoader.LoadAsync(new("App/App.csproj", "policy.json", null, "all", null, "worktree", "method",
            "result.json", "bundle", false, false, null, new PolicyOverrides(), TimeSpan.FromSeconds(30)), fixture.Path, token);

    private static TestDirectory Create()
    {
        var fixture = TestDirectory.Create("crap4csharp-local-proof");
        fixture.Write("policy.json", Policy);
        // Deliberately not evaluable SDK projects: these controls run before consumer evaluation.
        fixture.Write("App/App.csproj", "<Project />");
        fixture.Write("Tests/Tests.csproj", "<Project />");
        fixture.Write("App/Value.cs", "public class Value { }");
        return fixture;
    }

    private const string Policy = """
        {"schemaVersion":"repository-policy-v1","mode":"strict","productionProjects":["App/App.csproj"],
         "testProjects":["Tests/Tests.csproj"],"configuration":"Debug","targetFrameworks":["net10.0"],
         "scope":"all","threshold":999,"missingCoverage":"fail","requiredChecks":["tests","coverage","crap"],
         "exclusions":[],"ruleset":"callables-v1","exemptionFiles":[]}
        """;
}
