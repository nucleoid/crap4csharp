using System.Reflection;
using System.Runtime.ExceptionServices;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class FreshProjectPlatformTests
{
    private static Task<string> Resolve(string target, CancellationToken token)
    {
        var type = typeof(App).Assembly.GetType("FreshProjectPlatform");
        Assert.NotNull(type);
        var method = type.GetMethod("ResolveAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        try { return (Task<string>)method.Invoke(null, [target, "Debug", token])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    [Theory]
    [InlineData("<Solution />", "Any CPU")]
    [InlineData("<Solution><Configurations><Platform Name=\"AnyCPU\" /></Configurations></Solution>", "AnyCPU")]
    [InlineData("<Solution><Configurations><Platform Name=\"x64\" /></Configurations></Solution>", "x64")]
    public async Task RootSolutionPlatformSpellingIsPreserved(string xml, string expected)
    {
        using var fixture = TestDirectory.Create("crap4csharp-root-platform");
        fixture.Write("Root.slnx", xml);
        Assert.Equal(expected, await Resolve(Path.Combine(fixture.Path, "Root.slnx"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DirectProjectKeepsExistingAnyCpuSelection()
    {
        Assert.Equal("AnyCPU", await Resolve("Project.csproj", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationPrecedesSolutionRead()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Resolve("missing.slnx", source.Token));
    }
}
