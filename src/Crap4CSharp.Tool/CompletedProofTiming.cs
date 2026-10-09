using System.Diagnostics;

// Invocation-owned numeric diagnostics, no callbacks or physical paths.
// Consumers inspect only after validation terminates. Never an acceptance gate.
internal sealed class CompletedProofTiming
{
    internal sealed record Span(string Phase, double Milliseconds);
    private readonly List<Span> spans = [];
    internal IReadOnlyList<Span> Spans => spans;
    internal IDisposable Start(string phase) => new Lease(this, phase);
    internal void ImportWorker(string prefix, IReadOnlyList<Span> observations)
    {
        // Transport observations are diagnostics only: bound and filter, never alter a verdict.
        foreach (var span in observations.Take(4096))
            if (span.Phase is "worker.discovery" or "worker.graph" or "worker.nodeMetadata" or
                "worker.runnerMetadata" or "worker.contextMetadata" or "worker.workspace" or
                "worker.documents" or "worker.compilation" or "worker.context" &&
                double.IsFinite(span.Milliseconds) && span.Milliseconds >= 0)
                spans.Add(new Span(prefix + "." + span.Phase, span.Milliseconds));
    }
    private sealed class Lease(CompletedProofTiming owner, string phase) : IDisposable
    {
        private readonly long start = Stopwatch.GetTimestamp();
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            owner.spans.Add(new Span(phase, Stopwatch.GetElapsedTime(start).TotalMilliseconds));
        }
    }
}
