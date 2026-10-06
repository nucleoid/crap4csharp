namespace Crap4CSharp.Core;

// Internal scheduling seam; final source/output binding is a separate mandatory gate.
internal static class CompilerGraphExecution
{
    internal static async Task<IReadOnlyList<CompilerObservation>> RunAsync(ProjectContextLoadResult loaded,
        Func<ProjectBuildGraphNode, CancellationToken, Task<CompilerObservation>> build, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var plan = ValidateAndFreeze(loaded); // Validate the ENTIRE plan before executing its first node.
        var results = new List<CompilerObservation>();
        foreach (var node in plan)
        {
            token.ThrowIfCancellationRequested();
            results.Add(await build(node, token));
        }
        return results;
    }

    private static IReadOnlyList<ProjectBuildGraphNode> ValidateAndFreeze(ProjectContextLoadResult loaded)
    {
        if (!loaded.Success || loaded.BuildGraph.Count == 0 || loaded.BuildGraph.Count > 4096)
            throw Refuse("Invalid or incomplete loaded build graph.");
        var nodes = loaded.BuildGraph.Select(n => n with
        {
            Dependencies = n.Dependencies.ToArray(),
            GlobalProperties = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(n.GlobalProperties, StringComparer.OrdinalIgnoreCase))
        }).ToArray();
        if (nodes.Any(n => string.IsNullOrWhiteSpace(n.Id)) ||
            nodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != nodes.Length)
            throw Refuse("Build graph node identities are missing or duplicated.");
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var projects = new HashSet<string>(pathComparer);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var bindings = loaded.GraphContexts.ToArray();
        if (bindings.Length != nodes.Length || bindings.Select(b => b.GraphId).Distinct(StringComparer.Ordinal).Count() != nodes.Length)
            throw Refuse("Every graph node requires exactly one Workspace binding.");
        var contexts = loaded.Contexts.Select(c => c.ContextId).ToHashSet(StringComparer.Ordinal);
        if (contexts.Count != loaded.Contexts.Count || !contexts.SetEquals(bindings.Select(b => b.Context.ContextId)))
            throw Refuse("The loaded context inventory differs from the bound graph inventory.");
        foreach (var node in nodes)
        {
            if (!Path.IsPathFullyQualified(node.ProjectPath) || node.ProjectPath != Path.GetFullPath(node.ProjectPath))
                throw Refuse("Graph project paths must be canonical absolute paths.");
            // Conservative until evaluated output destinations are independently bound: never let
            // two mapped instances of one project silently overwrite an earlier instance's PE/PDB.
            if (!projects.Add(node.ProjectPath))
                throw new ProjectContextException("compiler.graphOutputUnproven", "Multiple contexts of one physical project require independently proven noncolliding output destinations.");
            var binding = bindings.SingleOrDefault(b => b.GraphId == node.Id);
            if (binding is null || !pathComparer.Equals(binding.ProjectPath, node.ProjectPath) ||
                !pathComparer.Equals(binding.Context.ResolvedProjectPath, node.ProjectPath) ||
                binding.Context.Configuration != node.Configuration || binding.Context.Platform != node.Platform ||
                binding.Context.TargetFramework != node.TargetFramework || !binding.Context.Capabilities.DesignTimeComplete)
                throw Refuse("A graph context is missing, incomplete or mismatched.");
            foreach (var key in new[] { "Configuration", "Platform", "TargetFramework" })
            {
                var expected = key == "Configuration" ? node.Configuration : key == "Platform" ? node.Platform : node.TargetFramework;
                if (node.GlobalProperties.TryGetValue(key, out var mapped) && mapped != expected)
                    throw Refuse("Mapped global properties disagree with the selected context.");
            }
            if (node.Dependencies.Distinct(StringComparer.Ordinal).Count() != node.Dependencies.Count ||
                node.Dependencies.Any(d => !completed.Contains(d)))
                throw Refuse("Dependencies must exist and precede dependents; cycles, self edges and duplicate edges are unsupported.");
            completed.Add(node.Id);
        }
        return nodes;
    }

    private static ProjectContextException Refuse(string message) => new("compiler.graphInvalid", message);
}
