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
    [Theory]
    [InlineData("stdout", 0, 0)]
    [InlineData("stderr", 1, 0)]
    [InlineData("stderr", 4097, 4096)]
    public async Task EachBytePumpIndependentlyEnforcesItsLimit(string name, int count, int limit)
    {
        using var source = new MemoryStream(Enumerable.Range(0, count).Select(i => (byte)(i % 256)).ToArray());
        if (count <= limit)
            Assert.Empty(await ProcessByteProtocol.ReadAsync(source, name, limit, TestContext.Current.CancellationToken));
        else
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProcessByteProtocol.ReadAsync(source, name, limit, TestContext.Current.CancellationToken));
            Assert.Equal(name, failure.Data["stream"]);
            Assert.Equal(limit, failure.Data["limitBytes"]);
            Assert.Equal((long)count, failure.Data["observedBytes"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedProtocolPreservesPrimaryDeadlineAndCancellation(bool cancelAfterReady)
    {
        string directory = Directory.CreateTempSubdirectory("crap4csharp-protocol-terminal-").FullName;
        string ready = Path.Combine(directory, "ready");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<ProcessProtocolResult>? operation = null;
        try
        {
            operation = ProcessRunner.RunProtocolAsync(Host, [Helper, "flood-hold", "1048577", ready], directory,
                cancelAfterReady ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(3), cancellation.Token, 4096);
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            readiness.CancelAfter(TimeSpan.FromSeconds(10));
            while (!File.Exists(ready))
            {
                Assert.False(operation.IsCompleted, "Consumer must emit BOTH over-limit streams before the terminal action.");
                await Task.Delay(10, readiness.Token);
            }
            if (cancelAfterReady) cancellation.Cancel();
            if (cancelAfterReady)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken));
            else
                await Assert.ThrowsAsync<ProcessTimeoutException>(() => operation.WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken));
        }
        finally
        {
            cancellation.Cancel();
            #pragma warning disable xUnit1051 // Observe original operation under an independent finite cleanup deadline.
            if (operation is not null)
                try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) when (operation.IsCompleted) { _ = operation.Exception; }
            #pragma warning restore xUnit1051
            Directory.Delete(directory, recursive: true);
        }
    }

}
