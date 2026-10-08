namespace Crap4CSharp.Core;

// Internal scheduling seams; RunBoundAsync composes mandatory source/output/reference gates.
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
            if (string.IsNullOrWhiteSpace(observed.ProjectPath) || !Path.IsPathFullyQualified(observed.ProjectPath) ||
                observed.ProjectPath != Path.GetFullPath(observed.ProjectPath) || !comparer.Equals(observed.ProjectPath, node.ProjectPath))
                throw new ProjectContextException("compiler.contextMismatch", "Actual compiler observation belongs to a different or unresolved project.");
            snapshot.Validate(frozen, token); // Stop dependents immediately on authored drift.
            results.Add(observed);
        }
        return results;
    }

    internal static async Task<CompilerGraphPhaseResult> RunPhasedAsync(ProjectContextLoadRequest request,
        ProjectContextLoadResult loaded,
        Func<ProjectBuildGraphNode, CancellationToken, Task<CompilerObservation>> build,
        CancellationToken token,
        Func<ProjectContextLoadRequest, CancellationToken, Task<ProjectContextLoadResult>>? reload = null)
    {
        token.ThrowIfCancellationRequested();
        var frozenRequest = request with { Frameworks = request.Frameworks.ToArray() };
        var plan = ValidateAndFreeze(loaded);
        var bindings = loaded.GraphContexts.Select(b => b with
        {
            Context = b.Context with
            {
                PhaseInputs = b.Context.PhaseInputs.ToArray(),
                PreprocessorSymbols = b.Context.PreprocessorSymbols.ToArray()
            }
        }).ToArray();
        var frozen = loaded with { BuildGraph = plan, GraphContexts = bindings, Contexts = bindings.Select(b => b.Context).ToArray() };
        var snapshot = CompilerPhaseSnapshot.Capture(frozen, token);
        var observations = await RunAsync(frozen, build, token);
        token.ThrowIfCancellationRequested();
        var final = await (reload ?? ProjectContextLoader.LoadGraphAsync)(frozenRequest, token);
        token.ThrowIfCancellationRequested();
        ValidateFinalGraph(frozen, final);
        snapshot.Validate(final, token); // Rediscovered membership, not the old file list.
        return new(final, observations);
    }

    // The fresh execution entrypoint. Structural RunPhasedAsync deliberately remains
    // available to structural tests; it must never be used to assert compiler binding.
    internal static async Task<CompilerGraphPhaseResult> RunBoundAsync(ProjectContextLoadRequest request,
        ProjectContextLoadResult loaded,
        Func<ProjectBuildGraphNode, CancellationToken, Task<CompilerObservation>> build,
        CancellationToken token,
        Func<ProjectContextLoadRequest, CancellationToken, Task<ProjectContextLoadResult>>? reload = null)
    {
        token.ThrowIfCancellationRequested();
        var plan = ValidateAndFreeze(loaded);
        var bindings = loaded.GraphContexts.Select(b => b with
        {
            Context = b.Context with
            {
                PhaseInputs = b.Context.PhaseInputs.ToArray(),
                PreprocessorSymbols = b.Context.PreprocessorSymbols.ToArray(),
                CompilerDocuments = b.Context.CompilerDocuments.ToArray(),
                ReferencePaths = b.Context.ReferencePaths.ToArray(),
                Sources = b.Context.Sources.ToArray(),
                Imports = b.Context.Imports.ToArray(),
                Exclusions = b.Context.Exclusions.ToArray(),
                ProtectedPaths = b.Context.ProtectedPaths.ToArray(),
                GeneratedTransitionPaths = b.Context.GeneratedTransitionPaths.ToArray()
            }
        }).ToArray();
        var frozen = loaded with { BuildGraph = plan, GraphContexts = bindings, Contexts = bindings.Select(b => b.Context).ToArray() };
        var frozenRequest = request with { Frameworks = request.Frameworks.ToArray() };
        var authoredSnapshot = CompilerPhaseSnapshot.Capture(frozen, token);
        var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var byId = bindings.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
        var closures = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var snapshots = new Dictionary<string, CompilerReferenceSnapshot>(StringComparer.Ordinal);
        // Classify graph-produced references independently, BEFORE any consumer callback.
        // Stale reference assemblies are NEVER captured as prepared package/SDK inputs.
        foreach (var node in plan)
        {
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            void AddDependencies(string id)
            {
                foreach (var dependency in plan.Single(n => n.Id == id).Dependencies)
                    if (dependencies.Add(dependency)) AddDependencies(dependency);
            }
            AddDependencies(node.Id);
            var authorized = dependencies.Select(id => byId[id].Context.EvaluatedReferenceAssemblyPath!).ToArray();
            closures.Add(node.Id, authorized);
            var context = byId[node.Id].Context;
            var references = new HashSet<string>(paths);
            var prepared = new List<string>();
            foreach (var reference in context.ReferencePaths)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(reference) || !Path.IsPathFullyQualified(reference) ||
                    reference != Path.GetFullPath(reference) || !references.Add(reference)) throw ReferenceRefuse();
                if (bindings.Any(b => paths.Equals(reference, b.Context.EvaluatedReferenceAssemblyPath)))
                {
                    if (!authorized.Contains(reference, paths)) throw ReferenceRefuse();
                    continue;
                }
                // Do not accept implementation copies, refint outputs, or other graph
                // output aliases as independently prepared references.
                if (bindings.Any(b => Contains(b.Context.EvaluatedIntermediateDirectory!, reference) ||
                    Contains(Path.GetDirectoryName(b.Context.EvaluatedTargetPath!)!, reference))) throw ReferenceRefuse();
                prepared.Add(reference);
            }
            snapshots.Add(node.Id, CompilerReferenceSnapshot.Capture(prepared, token));
        }
        var completed = new Dictionary<string, CompilerObservation>(StringComparer.Ordinal);
        var result = await RunPhasedAsync(request, frozen, async (node, cancellation) =>
        {
            foreach (var snapshot in snapshots.Values) snapshot.VerifyUnchanged(cancellation);
            foreach (var (id, earlier) in completed)
                CompilerReferenceOutputBinding.Validate(byId[id].Context, earlier, cancellation);
            var closure = snapshots[node.Id].Extend(closures[node.Id], cancellation);
            // All dependency copies above came from earlier once builds that passed the
            // independent actual Csc/reference PE/copy gate; no implementation fallback.
            var returned = await build(node, cancellation);
            var observation = returned with { Parameters = returned.Parameters.Select(p =>
                p with { Items = p.Items.ToArray() }).ToArray(),
                RuntimeCopies = Array.AsReadOnly(returned.RuntimeCopies.Select(c => c with { }).ToArray()) };
            CompilerReferenceOutputBinding.Validate(byId[node.Id].Context, observation, cancellation);
            closure.Validate(observation, cancellation);
            snapshots[node.Id] = closure;
            completed.Add(node.Id, observation);
            return observation;
        }, token, reload);
        foreach (var node in plan)
        {
            var context = result.FinalGraph.GraphContexts.Single(b => b.GraphId == node.Id).Context;
            var observation = completed[node.Id];
            CompilerOutputBinding.Validate(context, observation, token);
            CompilerReferenceOutputBinding.Validate(context, observation, token);
            snapshots[node.Id].Validate(observation, token);
            // Final Workspace reference rediscovery plus authorized transitive graph
            // references must independently agree with the staged compiler closure.
            var rediscovered = new HashSet<string>(paths);
            foreach (var reference in context.ReferencePaths)
                if (string.IsNullOrWhiteSpace(reference) || !Path.IsPathFullyQualified(reference) ||
                    reference != Path.GetFullPath(reference) || !rediscovered.Add(reference)) throw ReferenceRefuse();
            var finalReferences = rediscovered.Concat(closures[node.Id]).Distinct(paths).ToArray();
            snapshots[node.Id].Validate(observation with
            {
                Parameters = [new CompilerParameter("References", finalReferences)]
            }, token);
        }
        RuntimeCopyBinding.Validate(result.FinalGraph, completed, token);
        var retainedGraph = FreezeGraph(result.FinalGraph);
        authoredSnapshot.Validate(retainedGraph, token);
        var documentSnapshot = CompilerDocumentSnapshot.Capture(retainedGraph, token);
        var proof = new CompilerRetainedProof(frozenRequest, retainedGraph, authoredSnapshot,
            new Dictionary<string, CompilerReferenceSnapshot>(snapshots, StringComparer.Ordinal),
            new Dictionary<string, string[]>(closures, StringComparer.Ordinal),
            new Dictionary<string, CompilerObservation>(completed, StringComparer.Ordinal), documentSnapshot,
            reload ?? ProjectContextLoader.LoadGraphAsync);
        proof.ValidateCurrent(token);
        return result with { FinalGraph = retainedGraph, RetainedProof = proof }; // Still private; no reusable-recipe assertion.
    }

    private static ProjectContextException ReferenceRefuse() => new("compiler.referencesUnproven",
        "Graph-produced references are not an authorized independently staged dependency closure.");

    private static ProjectContextLoadResult FreezeGraph(ProjectContextLoadResult graph)
    {
        var nodes = ValidateAndFreeze(graph);
        var source = graph.GraphContexts.ToDictionary(binding => binding.GraphId, StringComparer.Ordinal);
        var bindings = nodes.Select(node => source[node.Id] with
        {
            Context = source[node.Id].Context with
            {
                PreprocessorSymbols = Array.AsReadOnly(source[node.Id].Context.PreprocessorSymbols.ToArray()),
                Imports = Array.AsReadOnly(source[node.Id].Context.Imports.ToArray()),
                Sources = Array.AsReadOnly(source[node.Id].Context.Sources.ToArray()),
                Exclusions = Array.AsReadOnly(source[node.Id].Context.Exclusions.ToArray()),
                CompilerDocuments = Array.AsReadOnly(source[node.Id].Context.CompilerDocuments.ToArray()),
                PhaseInputs = Array.AsReadOnly(source[node.Id].Context.PhaseInputs.ToArray()),
                ReferencePaths = Array.AsReadOnly(source[node.Id].Context.ReferencePaths.ToArray()),
                ProtectedPaths = Array.AsReadOnly(source[node.Id].Context.ProtectedPaths.ToArray()),
                GeneratedTransitionPaths = Array.AsReadOnly(source[node.Id].Context.GeneratedTransitionPaths.ToArray())
            }
        }).ToArray();
        return graph with
        {
            BuildGraph = nodes,
            GraphContexts = Array.AsReadOnly(bindings),
            Contexts = Array.AsReadOnly(bindings.Select(binding => binding.Context).ToArray()),
            TestRunners = Array.AsReadOnly(graph.TestRunners.Select(runner => runner with { }).ToArray()),
            Diagnostics = Array.AsReadOnly(graph.Diagnostics.ToArray()),
            Metadata = Array.AsReadOnly(graph.Metadata.Select(metadata => metadata with
            { Frameworks = Array.AsReadOnly(metadata.Frameworks.ToArray()) }).ToArray()),
            ExcludedProjects = graph.ExcludedProjects is null ? null : Array.AsReadOnly(graph.ExcludedProjects.ToArray())
        };
    }

    internal static void ValidateFinalGraph(ProjectContextLoadResult before, ProjectContextLoadResult after)
    {
        try { _ = ValidateAndFreeze(after); }
        catch (ProjectContextException)
        { throw GraphChanged(); }
        if (before.BuildGraph.Count != after.BuildGraph.Count) throw GraphChanged();
        var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var node in before.BuildGraph)
        {
            var current = after.BuildGraph.SingleOrDefault(n => n.Id == node.Id);
            if (current is null || !paths.Equals(node.ProjectPath, current.ProjectPath) ||
                node.Configuration != current.Configuration || node.Platform != current.Platform ||
                node.TargetFramework != current.TargetFramework ||
                !node.Dependencies.Order(StringComparer.Ordinal).SequenceEqual(current.Dependencies.Order(StringComparer.Ordinal)) ||
                node.GlobalProperties.Count != current.GlobalProperties.Count ||
                node.GlobalProperties.Any(p => !current.GlobalProperties.Any(q =>
                    StringComparer.OrdinalIgnoreCase.Equals(p.Key, q.Key) && p.Value == q.Value)))
                throw GraphChanged();
            var oldBinding = before.GraphContexts.Single(b => b.GraphId == node.Id);
            var newBinding = after.GraphContexts.Single(b => b.GraphId == node.Id);
            var oldContext = oldBinding.Context;
            var newContext = newBinding.Context;
            if (oldBinding.IsTestProject != newBinding.IsTestProject ||
                oldContext.ProjectPath != newContext.ProjectPath || oldContext.AssemblyName != newContext.AssemblyName ||
                oldContext.AnalysisMode != newContext.AnalysisMode || oldContext.LanguageVersion != newContext.LanguageVersion ||
                oldContext.SourceKind != newContext.SourceKind || oldContext.ExclusionPolicy != newContext.ExclusionPolicy ||
                oldContext.Adapter != newContext.Adapter ||
                !oldContext.PreprocessorSymbols.Order(StringComparer.Ordinal).SequenceEqual(newContext.PreprocessorSymbols.Order(StringComparer.Ordinal)) ||
                !paths.Equals(oldContext.EvaluatedTargetPath, newContext.EvaluatedTargetPath) ||
                !paths.Equals(oldContext.EvaluatedIntermediateDirectory, newContext.EvaluatedIntermediateDirectory) ||
                !paths.Equals(oldContext.EvaluatedReferenceAssemblyPath, newContext.EvaluatedReferenceAssemblyPath))
                throw GraphChanged();
            // Content ContextId/SourceSetIdentity may legitimately change with generated transitions.
            // This is NOT actual compiler binding; the caller still owes source/options/output evidence.
        }
    }

    private static ProjectContextException GraphChanged() => new("compiler.graphChanged", "Final rediscovery changed the selected graph, mapped globals or execution context selectors.");

    internal static IReadOnlyList<ProjectBuildGraphNode> ValidateAndFreeze(ProjectContextLoadResult loaded)
    {
        if (!loaded.Success || loaded.BuildGraph.Count == 0 || loaded.BuildGraph.Count > 4096)
            throw Refuse("Invalid or incomplete loaded build graph.");
        var nodes = loaded.BuildGraph.Select(n => n with
        {
            Dependencies = Array.AsReadOnly(n.Dependencies.ToArray()),
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
            var reference = RequireOutputPath(binding.Context.EvaluatedReferenceAssemblyPath);
            // Supported SDK reference copies live strictly beneath this node's intermediate
            // directory. Refuse redirects before ANY node executes, not after it has written.
            if (!Contains(intermediate, reference) || pathComparer.Equals(intermediate, reference) ||
                pathComparer.Equals(target, reference) || Directory.Exists(reference) ||
                bindings.Any(b => pathComparer.Equals(b.ProjectPath, reference) ||
                    b.Context.PhaseInputs.Any(input => pathComparer.Equals(input.Path, reference)) ||
                    b.Context.CompilerDocuments.Any(document => pathComparer.Equals(document.ResolvedPath, reference))))
                throw OutputRefuse("Reference destinations must be nonalias files strictly beneath their evaluated intermediate directory.");
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
        return Array.AsReadOnly(nodes);
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

internal sealed record CompilerGraphPhaseResult(ProjectContextLoadResult FinalGraph, IReadOnlyList<CompilerObservation> Observations)
{
    internal CompilerRetainedProof? RetainedProof { get; init; }
}

internal sealed class CompilerRetainedProof
{
    private readonly ProjectContextLoadRequest request;
    private readonly ProjectContextLoadResult graph;
    private readonly CompilerPhaseSnapshot authored;
    private readonly IReadOnlyDictionary<string, CompilerReferenceSnapshot> references;
    private readonly IReadOnlyDictionary<string, string[]> closures;
    private readonly IReadOnlyDictionary<string, CompilerObservation> observations;
    private readonly CompilerDocumentSnapshot documents;
    private readonly Func<ProjectContextLoadRequest, CancellationToken, Task<ProjectContextLoadResult>> reload;

    internal CompilerRetainedProof(ProjectContextLoadRequest request, ProjectContextLoadResult graph,
        CompilerPhaseSnapshot authored, IReadOnlyDictionary<string, CompilerReferenceSnapshot> references,
        IReadOnlyDictionary<string, string[]> closures, IReadOnlyDictionary<string, CompilerObservation> observations,
        CompilerDocumentSnapshot documents,
        Func<ProjectContextLoadRequest, CancellationToken, Task<ProjectContextLoadResult>> reload)
    {
        this.request = request; this.graph = graph; this.authored = authored; this.references = references;
        this.closures = closures; this.observations = observations; this.documents = documents; this.reload = reload;
    }

    internal void ValidateCurrent(CancellationToken token) => Validate(graph, token);

    internal async Task ValidateFinalAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current = await reload(request, token);
        token.ThrowIfCancellationRequested();
        ValidateAgainstCurrentGraph(current, token);
    }

    internal void ValidateAgainstCurrentGraph(ProjectContextLoadResult current, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CompilerGraphExecution.ValidateFinalGraph(graph, current);
        Validate(current, token);
    }

    private void Validate(ProjectContextLoadResult current, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        authored.Validate(current, token);
        documents.Validate(current, token);
        var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var binding in current.GraphContexts)
        {
            token.ThrowIfCancellationRequested();
            if (!observations.TryGetValue(binding.GraphId, out var observation) ||
                !references.TryGetValue(binding.GraphId, out var referenceSnapshot) ||
                !closures.TryGetValue(binding.GraphId, out var closure)) throw Refuse();
            CompilerOutputBinding.Validate(binding.Context, observation, token);
            CompilerReferenceOutputBinding.Validate(binding.Context, observation, token);
            var rediscovered = new HashSet<string>(paths);
            foreach (var reference in binding.Context.ReferencePaths)
                if (string.IsNullOrWhiteSpace(reference) || !Path.IsPathFullyQualified(reference) ||
                    reference != Path.GetFullPath(reference) || !rediscovered.Add(reference)) throw Refuse();
            var actual = rediscovered.Concat(closure).Distinct(paths).ToArray();
            referenceSnapshot.Validate(observation with
            { Parameters = [new CompilerParameter("References", actual)] }, token);
        }
        RuntimeCopyBinding.Validate(current, observations, token);
    }

    private static ProjectContextException Refuse() => new("compiler.retainedProofInvalid",
        "Retained compiler graph, source, document, output or reference evidence is incomplete.");
}
