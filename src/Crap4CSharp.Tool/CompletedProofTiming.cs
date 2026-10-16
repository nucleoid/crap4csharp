using System.Diagnostics;

// Invocation-owned numeric diagnostics, no callbacks or physical paths.
// Consumers inspect only after validation terminates. Never an acceptance gate.
internal sealed class CompletedProofTiming
{
    internal sealed record Span(string Phase, double Milliseconds);
    private readonly List<Span> spans = [];
    private readonly object sync = new();
    internal IReadOnlyList<Span> Spans { get { lock (sync) return spans.ToArray(); } }
    internal IDisposable Start(string phase) => new Lease(this, phase);
    // CPU is process-wide diagnostic time, not attribution to a thread or proof.
    internal IDisposable StartSynchronous(string phase) => new Lease(this, phase, cpu: true);
    private void Add(Span span) { lock (sync) spans.Add(span); }
    internal void ImportWorker(string prefix, IReadOnlyList<Span> observations)
    {
        // Transport observations are diagnostics only: bound and filter, never alter a verdict.
        foreach (var span in observations.Take(4096))
        {
            if (span is null || span.Phase is null) continue;
            var phase = span.Phase.EndsWith(".cpu", StringComparison.Ordinal) || span.Phase.EndsWith(".jit", StringComparison.Ordinal)
                ? span.Phase[..^4] : span.Phase;
            if (phase is "worker.discovery" or "worker.graph" or "worker.projectSdk" or "worker.projectSdkNative" or "worker.nodeMetadata" or
                "worker.runnerMetadata" or "worker.graphEntryEvaluation" or "worker.graphConstruction" or "worker.graphProjection" or "worker.runnerEvaluation" or "worker.runnerBinding" or "worker.contextMetadata" or "worker.workspace" or
                "worker.documents" or "worker.compilation" or "worker.context" or
                "worker.authoredBefore" or "worker.authoredAfter" or "worker.trackedInputs" or
                "worker.generatorDiscovery" or "worker.generatorValidation" or
                "worker.warmEvaluation" or "worker.warmTargets" or "worker.warmCommandLine" or "worker.warmReconstruction" or
                "worker.warmWorkspaceCreation" or "worker.warmProjectInfo" or
                "worker.warmProjectInfoAdjustment" or "worker.warmAddProject" &&
                double.IsFinite(span.Milliseconds) && span.Milliseconds >= 0)
                Add(new Span(prefix + "." + span.Phase, span.Milliseconds));
        }
    }
    private sealed class Lease(CompletedProofTiming owner, string phase, bool cpu = false) : IDisposable
    {
        private readonly long start = Stopwatch.GetTimestamp();
        private readonly double? cpuStart = cpu ? ReadCpu() : null;
        private readonly double? jitStart = cpu ? ReadJit() : null;
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            owner.Add(new Span(phase, Stopwatch.GetElapsedTime(start).TotalMilliseconds));
            if (cpuStart is { } before && ReadCpu() is { } after && after >= before)
                owner.Add(new Span(phase + ".cpu", after - before));
            if (jitStart is { } jitBefore && ReadJit() is { } jitAfter && jitAfter >= jitBefore)
                owner.Add(new Span(phase + ".jit", jitAfter - jitBefore));
        }
        private static double? ReadCpu()
        {
            try { using var process = Process.GetCurrentProcess(); return process.TotalProcessorTime.TotalMilliseconds; }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            { return null; } // Unavailable diagnostics never alter validation.
        }
        private static double? ReadJit()
        {
            try { return System.Runtime.JitInfo.GetCompilationTime(currentThread: false).TotalMilliseconds; }
            catch (NotSupportedException) { return null; }
        }
    }
}
