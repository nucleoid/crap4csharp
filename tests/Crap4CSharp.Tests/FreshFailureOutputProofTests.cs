using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class FreshFailureOutputProofTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedInvocationCannotStartFailureOutputDiscovery(bool expired)
    {
        using var fixture = TestDirectory.Create("crap4csharp-stopped-output-proof");
        var marker = Path.Combine(fixture.Path, "consumer-started.txt");
        var project = fixture.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk" InitialTargets="RecordUnexpectedStart">
            <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <Target Name="RecordUnexpectedStart"><WriteLinesToFile File="consumer-started.txt" Lines="started" /></Target>
            </Project>
            """);
        var request = new FreshCheckRequest("App.csproj", "policy.json", null, "all", null, "worktree", "method",
            "result.json", null, false, false, null, new PolicyOverrides(), TimeSpan.FromMinutes(3));
        var preflight = new FreshCheckPreflight(fixture.Path, request, null!, null!, null);
        var load = new ProjectContextLoadRequest(project, "Debug", "AnyCPU", [], false, false, request.Timeout);
        var proof = new FreshFailureOutputProof(preflight, load, new(true, [], []), []);
        using var cancellation = new CancellationTokenSource();
        if (expired)
            await Assert.ThrowsAsync<ProcessTimeoutException>(() => proof.ValidateAsync(cancellation.Token, TimeSpan.Zero));
        else
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => proof.ValidateAsync(cancellation.Token, request.Timeout));
        }
        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "result.json")));
        Assert.Empty(Directory.GetDirectories(fixture.Path));
    }
}
