using System.Runtime.CompilerServices;

namespace Crap4CSharp.Core;

internal static class WarmAcquisitionDiagnostic
{
    // Keep Microsoft.Build-dependent acquisition behind the registered bootstrap.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WarmAcquisitionDiagnosticBootstrap.Result> RunAsync(WarmAcquisitionDiagnosticBootstrap.Request request)
    {
        var gate = new object();
        var trace = new List<string>();
        var diagnostics = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var liveSeen = 0;
        void Record(string value) { lock (gate) trace.Add(value); }
        void Message(string value)
        {
            if (value != "CRAP_WARM_DIAGNOSTIC_LIVE" || Interlocked.Exchange(ref liveSeen, 1) != 0) return;
            Record("task.live");
            if (request.CancelOnLiveMessage) cancellation.Cancel();
        }
        var status = "failed";
        try
        {
            var loader = request.Loader;
            using (await WarmDesignTimeProject.AcquireAsync(loader.Target,
                new Dictionary<string, string>
                {
                    ["Configuration"] = loader.Configuration,
                    ["Platform"] = loader.Platform ?? "AnyCPU",
                    ["TargetFramework"] = loader.Frameworks.Single()
                }, diagnostics, cancellation.Token, Record, Message))
            {
                Record("project.acquired");
                status = "success";
            }
            Record("workspace.disposed");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            status = "canceled";
            Record("acquisition.canceled.returned");
        }
        catch (Exception exception)
        {
            diagnostics.Add(exception.ToString());
            Record("acquisition.failed.returned");
        }
        Record("diagnostic.terminal");
        lock (gate) return new(status, trace.ToArray(), diagnostics.ToArray());
    }
}
