namespace Crap4CSharp.Core;

internal sealed record CompilerParameter(string Name, IReadOnlyList<string> Items);
internal sealed record CompilerObservation(string ProjectPath, IReadOnlyList<CompilerParameter> Parameters);
internal static class CompilerEvidence
{
    internal static Task<CompilerObservation> BuildAsync(ProjectBuildGraphNode node, string ownedDirectory,
        TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(new CompilerObservation(node.ProjectPath, []));
    internal static Task<CompilerObservation> ReadAsync(string binlog, string project, TimeSpan timeout,
        CancellationToken cancellationToken) => Task.FromException<CompilerObservation>(new ProjectContextException("compiler.notImplemented", "Not implemented."));
}
