using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ProcessLogTests
{
    private static string Host => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        ?? Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")!, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "process-helper", "Helper.dll");

    [Theory]
    [InlineData("flood", 1048577)]
    [InlineData("flood", 4194305)]
    [InlineData("binary-protocol", 1048577)]
    public async Task LogsPreserveCompleteBytesAndOnlyRetainBoundedIndependentTails(string mode, int count)
    {
        string directory = Directory.CreateTempSubdirectory("crap4csharp-log-tests-").FullName;
        try
        {
            var result = await ProcessRunner.RunLoggedAsync(Host, [Helper, mode, count.ToString()],
                directory, TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken, directory, 257);
            Assert.Equal(0, result.ExitCode);
            byte[] expected = mode == "flood" ? Enumerable.Repeat((byte)'O', count).ToArray()
                : Enumerable.Range(0, count).Select(i => (byte)(i % 256)).ToArray();
            Assert.Equal(expected, await File.ReadAllBytesAsync(result.StandardOutput.Path, TestContext.Current.CancellationToken));
            Assert.Equal(count, result.StandardOutput.TotalBytes);
            Assert.Equal(expected[^257..], result.StandardOutput.Tail);
            Assert.True(result.StandardOutput.TailTruncated);
            byte[] error = mode == "flood" ? Enumerable.Repeat((byte)'E', count).ToArray()
                : System.Text.Encoding.UTF8.GetBytes("binary-protocol-complete\n");
            Assert.Equal(error, await File.ReadAllBytesAsync(result.StandardError.Path, TestContext.Current.CancellationToken));
            Assert.Equal(error.LongLength, result.StandardError.TotalBytes);
            Assert.Equal(error.TakeLast(257), result.StandardError.Tail);
            Assert.Equal(error.Length > 257, result.StandardError.TailTruncated);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(257)]
    public async Task ByteTailBoundaryRetainsExactLastBytesWithoutTextConversion(int limit)
    {
        byte[] bytes = Enumerable.Range(0, 20003).Select(i => (byte)(i % 256)).ToArray();
        using var source = new MemoryStream(bytes);
        using var sink = new MemoryStream();
        var capture = await ProcessByteLog.ReadAsync(source, sink, limit, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, sink.ToArray());
        Assert.Equal(bytes.LongLength, capture.TotalBytes);
        Assert.Equal(bytes.TakeLast(limit), capture.Tail);
    }

    [Theory]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public async Task SinkFailureTerminatesPositivelyReadyDescendantBeforeReturning(string failedStream)
    {
        string directory = Directory.CreateTempSubdirectory("crap4csharp-sink-tests-").FullName;
        string ready = Path.Combine(directory, "ready.json");
        string release = Path.Combine(directory, "release");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<ProcessResult>? operation = null;
        try
        {
            async Task<string> Pump(Stream source, string name, CancellationToken token)
            {
                if (name == failedStream)
                {
                    // Failure is injected only AFTER independent descendant readiness.
                    while (!File.Exists(ready)) await Task.Delay(10, token);
                    throw new IOException("controlled-log-sink-failure");
                }
                await source.CopyToAsync(Stream.Null, token);
                return string.Empty;
            }
            operation = ProcessRunner.RunWithBytePumpAsync(Host,
                [Helper, "descendant", Host, Helper, ready, release, "parent-live"], directory,
                TimeSpan.FromSeconds(20), cancellation.Token, Pump);
            var failure = await Assert.ThrowsAsync<IOException>(() => operation.WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken));
            Assert.Equal("controlled-log-sink-failure", failure.Message);
            Assert.True(File.Exists(ready));
            using var child = ProcessRunnerTests.OwnedChild(ready);
            Assert.True(child is null || child.HasExited, "Product returned before owned descendant termination.");
        }
        finally
        {
            cancellation.Cancel();
            #pragma warning disable xUnit1051 // Finite owned cleanup observes original operation independent of test cancellation.
            await File.WriteAllTextAsync(release, "release", CancellationToken.None);
            if (File.Exists(ready))
            {
                using var child = ProcessRunnerTests.OwnedChild(ready);
                if (child is not null && !child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            if (operation is not null)
                try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) when (operation.IsCompleted) { _ = operation.Exception; }
            #pragma warning restore xUnit1051
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoggedFloodPreservesPrimaryDeadlineOrCancellationAndRetainsPartialLogs(bool cancelAfterReady)
    {
        string directory = Directory.CreateTempSubdirectory("crap4csharp-log-terminal-").FullName;
        string ready = Path.Combine(directory, "ready");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<LoggedProcessResult>? operation = null;
        try
        {
            operation = ProcessRunner.RunLoggedAsync(Host, [Helper, "flood-hold", "4194305", ready], directory,
                cancelAfterReady ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(3), cancellation.Token, directory, 257);
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            readiness.CancelAfter(TimeSpan.FromSeconds(10));
            while (!File.Exists(ready))
            {
                Assert.False(operation.IsCompleted, "Consumer must positively emit both oversized streams first.");
                await Task.Delay(10, readiness.Token);
            }
            if (cancelAfterReady) cancellation.Cancel();
            if (cancelAfterReady)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken));
            else
                await Assert.ThrowsAsync<ProcessTimeoutException>(() => operation.WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken));
            foreach (string name in new[] { "stdout.log", "stderr.log" })
                Assert.InRange(new FileInfo(Path.Combine(directory, name)).Length, 1, 4194305);
        }
        finally
        {
            cancellation.Cancel();
            #pragma warning disable xUnit1051 // Original task is observed under an independent finite cleanup deadline.
            if (operation is not null)
                try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) when (operation.IsCompleted) { _ = operation.Exception; }
            #pragma warning restore xUnit1051
            Directory.Delete(directory, recursive: true);
        }
    }

}
