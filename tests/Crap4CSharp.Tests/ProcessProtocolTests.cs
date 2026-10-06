using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ProcessProtocolTests
{
    private static string Host => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        ?? Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")!, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "process-helper", "Helper.dll");

    [Fact]
    public async Task CompleteBinaryProtocolPreservesNulAndInvalidUtf8()
    {
        const int count = 1048577;
        var result = await ProcessRunner.RunProtocolAsync(Host, [Helper, "binary-protocol", count.ToString()],
            AppContext.BaseDirectory, TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken, count);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Enumerable.Range(0, count).Select(i => (byte)(i % 256)), result.StandardOutput);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("binary-protocol-complete" + Environment.NewLine), result.StandardError);
    }

    [Theory]
    [InlineData(4096, 4096)]
    [InlineData(4097, 4096)]
    public async Task CompleteProtocolReturnsBoundaryOrTypedLimitRefusalNeverTail(int count, int limit)
    {
        Task<ProcessProtocolResult> operation = ProcessRunner.RunProtocolAsync(Host, [Helper, "flood", count.ToString()],
            AppContext.BaseDirectory, TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken, limit);
        if (count <= limit)
        {
            var result = await operation;
            Assert.Equal(Enumerable.Repeat((byte)'O', count), result.StandardOutput);
            Assert.Equal(Enumerable.Repeat((byte)'E', count), result.StandardError);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => operation);
            Assert.Equal(limit, failure.Data["limitBytes"]);
            Assert.Equal((long)count, failure.Data["observedBytes"]);
            Assert.Contains((string)failure.Data["stream"]!, new[] { "stdout", "stderr" });
        }
    }
}
