namespace Crap4CSharp.Core;

// Scheduling may contain several framework contexts. Reference edges come only
// from independently acquired SDK/Workspace outputs, never nearest-TFM guesses.
internal static class CompilerReferenceGraph
{
    internal static IReadOnlyDictionary<string, string[]> Observe(ProjectContextLoadResult graph, CancellationToken token)
    {
        var nodes = CompilerGraphExecution.ValidateAndFreeze(graph).ToDictionary(n => n.Id, StringComparer.Ordinal);
        var bindings = graph.GraphContexts.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
        var paths = PathIdentityPolicy.Current.Comparer;
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var node in nodes.Values)
        {
            token.ThrowIfCancellationRequested();
            var permitted = new HashSet<string>(StringComparer.Ordinal);
            void Permit(string id)
            {
                foreach (var dependency in nodes[id].Dependencies)
                    if (permitted.Add(dependency)) Permit(dependency);
            }
            Permit(node.Id);
            var selected = new HashSet<string>(StringComparer.Ordinal);
            var context = bindings[node.Id].Context;
            foreach (var reference in context.ProjectReferenceOutputs)
            {
                token.ThrowIfCancellationRequested();
                if (!Canonical(reference.ProjectPath) || !Canonical(reference.OutputPath)) throw Refuse();
                var owners = bindings.Values.Where(b => paths.Equals(b.ProjectPath, reference.ProjectPath) &&
                    paths.Equals(b.Context.EvaluatedTargetPath, reference.OutputPath)).ToArray();
                if (owners.Length != 1 || !permitted.Contains(owners[0].GraphId)) throw Refuse();
                selected.Add(owners[0].GraphId);
            }
            foreach (var reference in context.ReferencePaths)
            {
                token.ThrowIfCancellationRequested();
                var owners = bindings.Values.Where(b => paths.Equals(b.Context.EvaluatedReferenceAssemblyPath, reference) ||
                    paths.Equals(b.Context.EvaluatedTargetPath, reference)).ToArray();
                if (owners.Length == 0) continue; // Independently snapshotted SDK/package reference.
                if (owners.Length != 1 || !permitted.Contains(owners[0].GraphId))
                    throw new ProjectContextException("compiler.referencesUnproven",
                        "Prepared metadata references do not have unique authorized graph owners.");
                selected.Add(owners[0].GraphId);
            }
            result.Add(node.Id, selected.Order(StringComparer.Ordinal).ToArray());
        }
        return result;
    }

    internal static IReadOnlySet<string> Closure(IReadOnlyDictionary<string, string[]> edges, string root)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal);
        void Add(string id)
        {
            if (!edges.TryGetValue(id, out var dependencies)) throw Refuse();
            if (!closure.Add(id)) return;
            foreach (var dependency in dependencies) Add(dependency);
        }
        Add(root);
        return closure;
    }

    internal static void ValidateSame(ProjectContextLoadResult before, ProjectContextLoadResult after, CancellationToken token)
    {
        var original = Observe(before, token); var current = Observe(after, token);
        if (original.Count != current.Count || original.Any(p => !current.TryGetValue(p.Key, out var edges) ||
            !p.Value.SequenceEqual(edges, StringComparer.Ordinal))) throw Refuse();
    }

    private static bool Canonical(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
        path == Path.GetFullPath(path);
    private static ProjectContextException Refuse() => new("compiler.referenceGraphUnproven",
        "SDK-selected project-reference outputs do not have unique authorized graph owners.");
}
