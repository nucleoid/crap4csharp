namespace Crap4CSharp.Core;

// Internal scheduling seam; final source/output binding is a separate mandatory gate.
internal static class CompilerGraphExecution
{
    internal static async Task<IReadOnlyList<CompilerObservation>> RunAsync(ProjectContextLoadResult loaded,
        Func<ProjectBuildGraphNode, CancellationToken, Task<CompilerObservation>> build, CancellationToken token)
    {
        var results = new List<CompilerObservation>();
        foreach (var node in loaded.BuildGraph)
        {
            token.ThrowIfCancellationRequested();
            results.Add(await build(node, token));
        }
        return results;
    }
}
