namespace Crap4CSharp.Core;

// Proposed independent output gate; RED seam intentionally only delegates existing inputs.
internal static class CompilerOutputBinding
{
    internal static void Validate(ProjectAnalysisContext context, CompilerObservation observation, CancellationToken token) =>
        CompilerInputBinding.Validate(context, observation, token);
}
