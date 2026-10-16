using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class SdkExecutionIdentityTests
{
    [Theory]
    [InlineData("version")]
    [InlineData("engine")]
    [InlineData("sdk-path")]
    [InlineData("root")]
    [InlineData("host")]
    [InlineData("route")]
    public void ChangedSelectorCannotReplaceFrozenExecutionIdentity(string change)
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var frozen = new ProjectContextSdkResolution("10.0.103", Path.Combine(root, "sdk"), "18.0.11", root,
            Path.Combine(root, "dotnet"), "native");
        var changed = change switch
        {
            "version" => frozen with { Version = "10.0.104" },
            "engine" => frozen with { MsBuildVersion = "18.0.12" },
            "sdk-path" => frozen with { Path = Path.Combine(root, "other-sdk") },
            "root" => frozen with { DotNetRoot = Path.Combine(root, "other-root") },
            "host" => frozen with { HostPath = Path.Combine(root, "other-host") },
            _ => frozen with { ResolutionRoute = "cli" }
        };
        Assert.Same(frozen, SdkExecutionIdentity.Validate(frozen, frozen with { }));
        Assert.Equal("context.sdkMismatch", Assert.Throws<ProjectContextException>(() =>
            SdkExecutionIdentity.Validate(frozen, changed)).Reason);
    }

    [Fact]
    public async Task SDKMismatchRefusesRestoreBeforeProjectTargetOrAssetsCreation()
    {
        using var fixture = TestDirectory.Create("crap4csharp-frozen-sdk-effect");
        var project = fixture.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <Target Name="ObserveRestore" BeforeTargets="Restore"><WriteLinesToFile File="$(MSBuildProjectDirectory)/restore-count.txt" Lines="effect" /></Target></Project>
            """);
        var token = TestContext.Current.CancellationToken;
        var selected = await ProjectContextLoader.ResolveSdkAsync(fixture.Path, TimeSpan.FromMinutes(1), token);
        var request = new ProjectContextLoadRequest(project, "Release", null, [], false, false, TimeSpan.FromMinutes(1))
            { FrozenSdk = selected with { Version = "mismatched-sdk-version" } };
        var refusal = await Assert.ThrowsAsync<ProjectContextException>(() => ProjectBuildPreparation.RestoreAsync(request, token));
        Assert.Equal("context.sdkMismatch", refusal.Reason);
        Assert.False(File.Exists(Path.Combine(fixture.Path, "restore-count.txt")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "obj")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bin")));
        var owned = Directory.CreateDirectory(Path.Combine(fixture.Path, "private-build")).FullName;
        var node = new ProjectBuildGraphNode("probe", project, "Release", "AnyCPU", "net10.0",
            new Dictionary<string, string> { ["Configuration"] = "Release" }, []);
        var buildRefusal = await Assert.ThrowsAsync<ProjectContextException>(() => CompilerEvidence.BuildAsync(
            node, owned, TimeSpan.FromMinutes(1), token, nativeProbe: false, frozenSdk: request.FrozenSdk));
        Assert.Equal("context.sdkMismatch", buildRefusal.Reason);
        Assert.Empty(Directory.GetFiles(owned));
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "obj")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bin")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSDKOrResolutionRouteRefusesBeforeDesignTimeWorker(bool routeChange)
    {
        using var fixture = TestDirectory.Create("crap4csharp-frozen-sdk-loader");
        var project = fixture.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <Target Name="ObserveDesign" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/design-count.txt" Lines="effect" /></Target></Project>
            """);
        var token = TestContext.Current.CancellationToken;
        var selected = await ProjectContextLoader.ResolveSdkAsync(fixture.Path, TimeSpan.FromMinutes(1), token);
        var request = new ProjectContextLoadRequest(project, "Release", null, [], false, false, TimeSpan.FromMinutes(1))
            { FrozenSdk = routeChange ? selected with { ResolutionRoute = "native" } : selected with { Version = "changed-sdk" } };
        var result = await ProjectContextLoader.LoadGraphAsync(request, token);
        Assert.False(result.Success);
        Assert.Equal("context.sdkMismatch", result.FailureReason);
        Assert.False(File.Exists(Path.Combine(fixture.Path, "design-count.txt")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "obj")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bin")));
    }
}
