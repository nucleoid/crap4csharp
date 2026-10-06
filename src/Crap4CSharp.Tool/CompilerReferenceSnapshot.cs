namespace Crap4CSharp.Core;

// Proposed staged byte-binding seam; regression-first implementation follows.
internal sealed class CompilerReferenceSnapshot
{
    internal static CompilerReferenceSnapshot Capture(IEnumerable<string> paths, CancellationToken token) => new();
    internal CompilerReferenceSnapshot Extend(IEnumerable<string> paths, CancellationToken token) => this;
    internal void VerifyUnchanged(CancellationToken token) { }
    internal void Validate(CompilerObservation observation, CancellationToken token) { }
}
