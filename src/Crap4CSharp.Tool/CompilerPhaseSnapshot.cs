namespace Crap4CSharp.Core;

// Private pre-build authored-input gate; actual compiler and output binding remain separate.
internal sealed class CompilerPhaseSnapshot
{
    internal static CompilerPhaseSnapshot Capture(ProjectContextLoadResult loaded, CancellationToken token) => new();
    internal void Validate(ProjectContextLoadResult current, CancellationToken token) { }
}
