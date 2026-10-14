using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class SdkResolutionTests
{
    [Theory]
    [InlineData("10.0.103", "disable", false)]
    [InlineData("10.0.100", "latestPatch", false)]
    [InlineData("10.0.103", "disable", true)]
    [InlineData("10.0.100", "latestPatch", true)]
    public async Task ResolutionMatchesIndependentExactSdkAndMsBuildCommands(string version, string rollForward, bool nativeProbe)
    {
        using var fixture = TestDirectory.Create("crap4csharp-sdk-identity");
        var global = fixture.Write("global.json", $$"""
            { "sdk": { "version": "{{version}}", "rollForward": "{{rollForward}}" } }
            """);
        var original = File.ReadAllBytes(global);
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var timeout = TimeSpan.FromMinutes(2);
        var token = TestContext.Current.CancellationToken;
        // Independently observed serial CLI outputs are the reference contract,
        // including the complete four-part MSBuild version, not dotnet --info.
        var selected = await ProcessRunner.RunAsync(host, ["--version"], fixture.Path, timeout, token);
        var installed = await ProcessRunner.RunAsync(host, ["--list-sdks"], fixture.Path, timeout, token);
        var engine = await ProcessRunner.RunAsync(host, ["msbuild", "-version", "-nologo", "-nr:false"], fixture.Path, timeout, token);
        Assert.Equal(0, selected.ExitCode);
        Assert.Equal(0, installed.ExitCode);
        Assert.Equal(0, engine.ExitCode);
        var actual = await ProjectContextLoader.ResolveSdkAsync(fixture.Path, timeout, token, nativeProbe);
        if (nativeProbe)
        {
            var observation = await SdkIdentityProbe.QueryAsync(fixture.Path, host, timeout, token);
            Assert.Equal("resolved", observation.Status);
        }
        var expectedVersion = selected.StandardOutput.Trim();
        var entry = Assert.Single(installed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => line.Trim().StartsWith(expectedVersion + " [", StringComparison.Ordinal));
        var sdkRoot = entry[(entry.IndexOf('[') + 1)..entry.LastIndexOf(']')];
        Assert.Equal(expectedVersion, actual.Version);
        Assert.Equal(Path.Combine(sdkRoot, expectedVersion), actual.Path);
        Assert.Equal(engine.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last(), actual.MsBuildVersion);
        Assert.Equal(Directory.GetParent(sdkRoot)!.FullName, actual.DotNetRoot);
        Assert.Equal(Path.Combine(actual.DotNetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), actual.HostPath);
        Assert.Equal(original, File.ReadAllBytes(global));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableSelectedSdkCannotBecomeSuccessfulIdentity(bool nativeProbe)
    {
        using var fixture = TestDirectory.Create("crap4csharp-sdk-unavailable");
        fixture.Write("global.json", """
            { "sdk": { "version": "99.0.999", "rollForward": "disable" } }
            """);
        var error = await Assert.ThrowsAsync<ProjectContextException>(() =>
            ProjectContextLoader.ResolveSdkAsync(fixture.Path, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken, nativeProbe));
        Assert.Equal("context.sdkResolutionFailed", error.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationRemainsCancellation(bool nativeProbe)
    {
        using var fixture = TestDirectory.Create("crap4csharp-sdk-cancelled");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProjectContextLoader.ResolveSdkAsync(fixture.Path, TimeSpan.FromMinutes(2), cancellation.Token, nativeProbe));
    }
}
