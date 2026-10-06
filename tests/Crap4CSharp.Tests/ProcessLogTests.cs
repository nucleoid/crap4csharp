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
                : System.Text.Encoding.UTF8.GetBytes("binary-protocol-complete" + Environment.NewLine);
            Assert.Equal(error, await File.ReadAllBytesAsync(result.StandardError.Path, TestContext.Current.CancellationToken));
            Assert.Equal(error.LongLength, result.StandardError.TotalBytes);
            Assert.Equal(error.TakeLast(257), result.StandardError.Tail);
            Assert.Equal(error.Length > 257, result.StandardError.TailTruncated);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
