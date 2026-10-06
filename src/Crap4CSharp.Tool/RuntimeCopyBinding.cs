namespace Crap4CSharp.Core;

// Binds selected-SDK observed graph implementation-copy pairs, not runtime loading.
internal static class RuntimeCopyBinding
{
    internal static void Validate(ProjectContextLoadResult graph,
        IReadOnlyDictionary<string, CompilerObservation> observations, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var nodes = CompilerGraphExecution.ValidateAndFreeze(graph);
        var bindings = graph.GraphContexts.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
        long total = 0;
        try
        {
            foreach (var node in nodes)
            {
                token.ThrowIfCancellationRequested();
                if (!observations.TryGetValue(node.Id, out var observed)) throw Refuse();
                var dependencies = new HashSet<string>(StringComparer.Ordinal);
                void Add(string id)
                {
                    foreach (var dependency in nodes.Single(n => n.Id == id).Dependencies)
                        if (dependencies.Add(dependency)) Add(dependency);
                }
                Add(node.Id);
                var directory = Path.GetDirectoryName(bindings[node.Id].Context.EvaluatedTargetPath!)! + Path.DirectorySeparatorChar;
                var destinations = new HashSet<string>(paths);
                foreach (var id in dependencies)
                {
                    token.ThrowIfCancellationRequested();
                    var dependency = bindings[id];
                    var source = dependency.Context.EvaluatedTargetPath!;
                    var pairs = observed.RuntimeCopies.Where(c => paths.Equals(c.Source, source)).ToArray();
                    if (pairs.Length != 1 || string.IsNullOrWhiteSpace(pairs[0].Destination) ||
                        !Path.IsPathFullyQualified(pairs[0].Destination) || Path.GetFullPath(pairs[0].Destination) != pairs[0].Destination ||
                        !pairs[0].Destination.StartsWith(directory, comparison) || !destinations.Add(pairs[0].Destination) ||
                        observed.RuntimeCopies.Count(c => paths.Equals(c.Destination, pairs[0].Destination)) != 1 ||
                        !observations.TryGetValue(id, out var compiler) || compiler.Assembly is null) throw Refuse();
                    // Match both observed source and actual SDK destination against the
                    // independently built dependency Csc output; never bind to ref PE.
                    CompilerOutputBinding.Match(compiler.Assembly, CompilerOutputBinding.Read(source, ref total, token));
                    CompilerOutputBinding.Match(compiler.Assembly, CompilerOutputBinding.Read(pairs[0].Destination, ref total, token));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is ProjectContextException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw Refuse(); }
    }

    private static ProjectContextException Refuse() => new("compiler.runtimeCopiesUnproven",
        "Actual selected-SDK graph dependency implementation-copy source/destination evidence is missing, redirected or differs from independent compiler PE bytes.");
}
