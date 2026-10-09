using System.Diagnostics;

// Invocation-owned numeric diagnostics, no callbacks or physical paths.
// Consumers inspect only after validation terminates. Never an acceptance gate.
internal sealed class CompletedProofTiming
{
    internal sealed record Span(string Phase, double Milliseconds);
    private readonly List<Span> spans = [];
    internal IReadOnlyList<Span> Spans => spans;
    internal IDisposable Start(string phase) => new Lease(this, phase);
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
