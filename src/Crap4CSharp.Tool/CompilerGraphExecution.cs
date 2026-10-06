namespace Crap4CSharp.Core;

// Internal scheduling seam; final source/output binding is a separate mandatory gate.
internal static class CompilerGraphExecution
{
    internal static async Task<IReadOnlyList<CompilerObservation>> RunAsync(ProjectContextLoadResult loaded,
        Func<ProjectBuildGraphNode, CancellationToken, Task<CompilerObservation>> build, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var plan = ValidateAndFreeze(loaded); // Validate the ENTIRE plan before executing its first node.
        // Copy membership before passing control to a consumer build callback. File identities
        // still come from disk, never from the callback's self-asserted observation.
        var bindings = loaded.GraphContexts.Select(b => b with
        {
            Context = b.Context with { PhaseInputs = b.Context.PhaseInputs.ToArray() }
        }).ToArray();
        var frozen = loaded with { GraphContexts = bindings, Contexts = bindings.Select(b => b.Context).ToArray() };
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (bindings.Any(b => !b.Context.PhaseInputs.Any(p => p.Role == "project" && !p.ExpectedGenerated &&
                comparer.Equals(p.Path, b.ProjectPath))))
            throw new ProjectContextException("compiler.inputsMutated", "Every graph binding requires its immutable prepared project input.");
        var snapshot = CompilerPhaseSnapshot.Capture(frozen, token); // Entire graph BEFORE first launch.
        var results = new List<CompilerObservation>();
        foreach (var node in plan)
        {
            token.ThrowIfCancellationRequested();
            snapshot.Validate(frozen, token);
            var observed = await build(node, token); // Preserve the original build failure unchanged.
            snapshot.Validate(frozen, token); // Stop dependents immediately on authored drift.
            results.Add(observed);
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
        var outputDirectories = new List<(string Node, string Path)>();
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
            var target = RequireOutputPath(binding.Context.EvaluatedTargetPath);
            var intermediate = RequireOutputPath(binding.Context.EvaluatedIntermediateDirectory);
            foreach (var directory in new[] { Path.GetDirectoryName(target)!, intermediate })
            {
                if (outputDirectories.Any(previous => previous.Node != node.Id &&
                    (Contains(previous.Path, directory) || Contains(directory, previous.Path))))
                    throw OutputRefuse("Different graph contexts have overlapping evaluated output directories.");
                outputDirectories.Add((node.Id, directory));
            }
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

    private static string RequireOutputPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path != Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            throw OutputRefuse("Every graph context requires canonical evaluated target and intermediate destinations.");
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw OutputRefuse("Reparse-point output destinations are unsupported.");
        return path;
    }

    private static bool Contains(string parent, string child)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return string.Equals(parent, child, comparison) || child.StartsWith(prefix, comparison);
    }

    private static ProjectContextException OutputRefuse(string message) => new("compiler.graphOutputUnproven", message);

    private static ProjectContextException Refuse(string message) => new("compiler.graphInvalid", message);
}
