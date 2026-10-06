using System.Diagnostics;
using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ProcessRunnerTests
{
    private static string Host => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        ?? Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")
            ?? throw new InvalidOperationException("Exact .NET host not supplied."),
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "process-helper", "Helper.dll");

    [Fact]
    public async Task PreCancelledInvocationNeverAttemptsConsumerStart()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        // Nonexistent executable is an opposing control: an attempted OS start throws Win32Exception,
        // independently of whether a fast child could write a marker before termination.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent-consumer"),
            [], Path.GetTempPath(), TimeSpan.FromSeconds(5), cancellation.Token));
    }

    [Fact]
    public async Task NormalExitCapturesIndependentConcurrentOutputStreams()
    {
        const int count = 1048577;
        var result = await ProcessRunner.RunAsync(Host, [Helper, "flood", count.ToString()],
            Path.GetTempPath(), TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new string('O', count), result.StandardOutput);
        Assert.Equal(new string('E', count), result.StandardError);
    }

    [Theory]
    [InlineData("parent-exit")]
    [InlineData("parent-live")]
    public async Task DeadlineCoversInheritedPipesAndTerminatesOwnedDescendant(string mode)
    {
        string directory = Path.Combine(Path.GetTempPath(), "crap4csharp-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string ready = Path.Combine(directory, "ready.json");
        string release = Path.Combine(directory, "release");
        Task<ProcessResult>? operation = null;
        Exception? outcome = null;
        bool guardExpired = false;
        bool readyObserved = false;
        bool aliveBeforeFixtureCleanup = false;
        try
        {
            operation = ProcessRunner.RunAsync(Host,
                [Helper, "descendant", Host, Helper, ready, release, mode], directory,
                TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            try { await operation.WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken); }
            catch (TimeoutException) when (!operation.IsCompleted) { guardExpired = true; }
            catch (Exception exception) { outcome = exception; }
            readyObserved = File.Exists(ready);
            if (readyObserved)
            {
                using var child = OwnedChild(ready);
                aliveBeforeFixtureCleanup = child is not null && !child.HasExited;
            }
        }
        finally
        {
            #pragma warning disable xUnit1051 // Owned cleanup must finish even after test cancellation; every wait is independently bounded.
            // A failing regression must not leak fixtures. These actions are not product containment.
            await File.WriteAllTextAsync(release, "release", CancellationToken.None);
            if (File.Exists(ready))
            {
                using var child = OwnedChild(ready);
                if (child is not null && !child.HasExited)
                {
                    try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException)
                    {
                        if (!child.HasExited) child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    Assert.True(child.HasExited, "Invocation-owned fixture cleanup did not finish.");
                }
            }
            if (operation is not null)
            {
                try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) when (operation.IsCompleted) { /* Observe original failure after cleanup. */ }
            }
            Directory.Delete(directory, recursive: true);
            #pragma warning restore xUnit1051
        }
        Assert.True(readyObserved, "The descendant must positively reach the inherited-pipe scenario.");
        Assert.False(guardExpired, "The product deadline was bypassed by inherited child pipes.");
        Assert.IsType<ProcessTimeoutException>(outcome);
        Assert.False(aliveBeforeFixtureCleanup, "The product returned while its owned descendant survived.");
    }

    private static Process? OwnedChild(string receipt)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(receipt));
        int pid = json.RootElement.GetProperty("pid").GetInt32();
        Process child;
        try { child = Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
        try
        {
            if (child.HasExited) return child;
            bool identityMatches;
            if (OperatingSystem.IsLinux())
            {
                string stat = File.ReadAllText($"/proc/{pid}/stat");
                string startTicks = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19];
                identityMatches = startTicks == json.RootElement.GetProperty("linuxStartTicks").GetString();
            }
            else identityMatches = child.StartTime.ToUniversalTime().Ticks == json.RootElement.GetProperty("startTimeUtcTicks").GetInt64();
            if (!identityMatches) throw new InvalidOperationException("PID identity changed; refusing unrelated cleanup.");
            return child;
        }
        catch { child.Dispose(); throw; }
    }
}
