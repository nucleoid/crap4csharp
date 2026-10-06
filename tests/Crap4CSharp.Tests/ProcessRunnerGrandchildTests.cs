using System.Diagnostics;
using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ProcessRunnerGrandchildTests
{
    private static string Host => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        ?? Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")
            ?? throw new InvalidOperationException("Exact .NET host not supplied."),
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "process-helper", "Helper.dll");

    [Theory]
    [InlineData("grandparent-exit", false)]
    [InlineData("grandparent-live", false)]
    [InlineData("grandparent-exit", true)]
    [InlineData("grandparent-live", true)]
    public async Task DeadlineOrCancellationCoversInheritedPipesAndTerminatesOwnedDescendant(string mode, bool cancelAfterReady)
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
        using var invocationCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            operation = ProcessRunner.RunAsync(Host,
                [Helper, "grandparent", Host, Helper, ready, release, mode], directory,
                cancelAfterReady ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(3), invocationCancellation.Token);
            if (cancelAfterReady)
            {
                // Cancel only after BOTH descendants positively own inherited pipes. A prelaunch
                // cancellation or missing marker cannot masquerade as descendant containment.
                using var readiness = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                readiness.CancelAfter(TimeSpan.FromSeconds(10));
                while (!File.Exists(ready) || !File.Exists(ready + ".intermediate"))
                {
                    if (operation.IsCompleted)
                        throw new InvalidOperationException("Invocation completed before both descendants became ready.");
                    await Task.Delay(25, readiness.Token);
                }
                invocationCancellation.Cancel();
            }
            try { await operation.WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken); }
            catch (TimeoutException) when (!operation.IsCompleted) { guardExpired = true; }
            catch (Exception exception) { outcome = exception; }
            readyObserved = File.Exists(ready) && File.Exists(ready + ".intermediate");
            if (readyObserved)
            {
                using var leaf = OwnedChild(ready);
                using var intermediate = OwnedChild(ready + ".intermediate");
                aliveBeforeFixtureCleanup = (leaf is not null && !leaf.HasExited) ||
                    (intermediate is not null && !intermediate.HasExited);
            }
        }
        finally
        {
            #pragma warning disable xUnit1051 // Owned cleanup must finish even after test cancellation; every wait is independently bounded.
            // A failing regression must not leak fixtures. These actions are not product containment.
            await File.WriteAllTextAsync(release, "release", CancellationToken.None);
            foreach (string receipt in new[] { ready, ready + ".intermediate" })
            {
                if (!File.Exists(receipt)) continue;
                using var child = OwnedChild(receipt);
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
        if (cancelAfterReady) Assert.IsAssignableFrom<OperationCanceledException>(outcome);
        else Assert.IsType<ProcessTimeoutException>(outcome);
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
