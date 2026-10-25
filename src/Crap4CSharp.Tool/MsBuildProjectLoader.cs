using Microsoft.Build.Evaluation;
using Microsoft.Build.Graph;
using Microsoft.Build.Construction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using System.Text.Json;

namespace Crap4CSharp.Core;

internal static partial class MsBuildProjectLoader
{
    public static async Task<ProjectContextLoadResult> LoadAsync(ProjectContextLoadRequest request, CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        // The private warm probe requires complete graph preflight. Never acquire a
        // project through a non-graph route that could hide mapped dependencies.
        if (request.WarmAcquisitionProbe && request.WarmAcquisitionSelectionProbe)
            throw new ProjectContextException("context.warmModeConflict", "Select only one private warm acquisition mode.");
        if ((request.WarmAcquisitionProbe || request.WarmAcquisitionSelectionProbe) && !request.LoadGraphContexts)
            throw new ProjectContextException("context.warmGraphRequired", "Warm acquisition requires complete graph-context preflight.");
        var root = Path.GetDirectoryName(request.Target)!;
        var excludedProjects = new List<ProjectContextProjectExclusion>();
        ProjectSelection[] projects;
        using (request.Timing?.Start("worker.discovery"))
            projects = await DiscoverProjects(request.Target, request.Configuration, request.Platform, diagnostics, excludedProjects, cancellationToken);
        if (request.TestRunnersOnly)
        {
            ProjectContextLoadResult graph;
            using (request.Timing?.StartSynchronous("worker.graph"))
                graph = await DiscoverGraph(request, projects, diagnostics, excludedProjects, cancellationToken);
            var runners = new List<ProjectTestRunnerMetadata>();
            foreach (var node in graph.BuildGraph)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EvaluatedMetadata metadata;
                using (request.Timing?.Start("worker.nodeMetadata"))
                    metadata = Evaluate(node.ProjectPath, node.Configuration, node.Platform, node.TargetFramework, node.GlobalProperties);
                EnsurePreparedAssets(metadata, node.ProjectPath);
                if (metadata.IsTestProject)
                    using (request.Timing?.Start("worker.runnerMetadata"))
                        runners.Add(EvaluateTestRunner(node, request.SdkPath!, request.Timing));
            }
            return new(true, [], diagnostics, null, excludedProjects) { BuildGraph = graph.BuildGraph, TestRunners = runners.ToArray() };
        }
        if (request.LoadGraphContexts)
        {
            ProjectContextLoadResult graph;
            using (request.Timing?.StartSynchronous("worker.graph"))
                graph = await DiscoverGraph(request, projects, diagnostics, excludedProjects, cancellationToken);
            return await LoadGraphContextsAsync(request, root, graph, diagnostics, excludedProjects, cancellationToken);
        }
        if (request.GraphOnly)
            return await DiscoverGraph(request, projects, diagnostics, excludedProjects, cancellationToken);
        if (request.MetadataOnly)
        {
            // Evaluation only: no target invocation, assets check, Workspace, or runner inference.
            // Cold package imports may not yet expose test/runner properties.
            var discovered = new List<ProjectDiscoveryMetadata>();
            foreach (var selection in projects.OrderBy(value => value.Path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selectedRequest = request with { Configuration = selection.Configuration, Platform = selection.Platform };
                await EnsureProjectSdkAsync(selection.Path, selectedRequest, cancellationToken);
                var metadata = Evaluate(selection.Path, selection.Configuration, selection.Platform, null);
                discovered.Add(new(selection.Path, metadata.Configuration, metadata.Platform, metadata.Frameworks,
                    metadata.AssetsFile, metadata.IsTestProject));
            }
            return new(true, [], diagnostics, null, excludedProjects) { Metadata = discovered };
        }
        // Ordinary inventory still returns only its selected production entries.
        // Resolve incompatible dependency globals through the SDK graph BEFORE
        // any design-time target; an inherited parent TFM cannot stand in for a
        // negotiated ProjectReference, even when its binary is not built yet.
        var inventorySelections = new List<ProjectSelection>();
        var hasReferences = false;
        foreach (var selection in projects.OrderBy(value => value.Path, StringComparer.Ordinal))
        {
            var selectedRequest = request with { Configuration = selection.Configuration, Platform = selection.Platform };
            await EnsureProjectSdkAsync(selection.Path, selectedRequest, cancellationToken);
            var metadata = Evaluate(selection.Path, selection.Configuration, selection.Platform, null);
            if (metadata.IsTestProject && !request.IncludeTests) continue;
            inventorySelections.Add(selection);
            foreach (var framework in metadata.Frameworks.Where(framework => request.Frameworks.Count == 0 ||
                request.Frameworks.Contains(framework, StringComparer.OrdinalIgnoreCase)))
                hasReferences |= Evaluate(selection.Path, selection.Configuration, selection.Platform, framework).HasProjectReferences;
        }
        if (hasReferences)
        {
            var graph = await DiscoverGraph(request, inventorySelections.ToArray(), diagnostics, excludedProjects, cancellationToken);
            if (!graph.Success) return graph;
            if (!WarmGraphSupported(graph.BuildGraph))
            {
                foreach (var excluded in projects.Except(inventorySelections))
                {
                    diagnostics.Add($"Excluded test project: {Logical(root, excluded.Path)}");
                    excludedProjects.Add(new(Logical(root, excluded.Path), "testProject.defaultExcluded"));
                }
                var loaded = await LoadGraphContextsAsync(request, root, graph, diagnostics, excludedProjects, cancellationToken);
                var selectedPaths = inventorySelections.Select(selection => selection.Path).ToHashSet(PathComparer);
                return loaded with { Contexts = loaded.GraphContexts.Where(binding => selectedPaths.Contains(binding.ProjectPath) &&
                    (request.Frameworks.Count == 0 || request.Frameworks.Contains(binding.Context.TargetFramework, StringComparer.OrdinalIgnoreCase)))
                    .Select(binding => binding.Context).OrderBy(context => context.ProjectPath, StringComparer.Ordinal)
                    .ThenBy(context => context.TargetFramework, StringComparer.Ordinal).ToArray() };
            }
        }
        var contexts = new List<ProjectAnalysisContext>();
        var matchedFrameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in projects.OrderBy(value => value.Path, StringComparer.Ordinal))
        {
            var projectPath = selection.Path;
            var selectedRequest = request with { Configuration = selection.Configuration, Platform = selection.Platform };
            await EnsureProjectSdkAsync(projectPath, selectedRequest, cancellationToken);
            var metadata = Evaluate(projectPath, selectedRequest.Configuration, selectedRequest.Platform, null);
            EnsurePreparedAssets(metadata, projectPath);
            if (metadata.IsTestProject && !request.IncludeTests)
            {
                diagnostics.Add($"Excluded test project: {Logical(root, projectPath)}");
                excludedProjects.Add(new(Logical(root, projectPath), "testProject.defaultExcluded"));
                continue;
            }
            foreach (var framework in metadata.Frameworks)
            {
                if (request.Frameworks.Count > 0 && !request.Frameworks.Contains(framework, StringComparer.OrdinalIgnoreCase)) continue;
                matchedFrameworks.Add(framework);
                contexts.Add(await LoadContext(root, projectPath, framework, selectedRequest, diagnostics, cancellationToken));
            }
        }
        var unmatched = request.Frameworks.Where(framework => !matchedFrameworks.Contains(framework)).ToArray();
        if (unmatched.Length > 0)
            return new ProjectContextLoadResult(false, contexts, diagnostics.Concat(["Requested framework was not found: " + string.Join(", ", unmatched)]).ToArray(), "context.frameworkNotFound", excludedProjects);
        return new ProjectContextLoadResult(true, contexts.OrderBy(context => context.ProjectPath, StringComparer.Ordinal)
            .ThenBy(context => context.TargetFramework, StringComparer.Ordinal).ToArray(), diagnostics, null, excludedProjects);
    }

    private static async Task<ProjectContextLoadResult> LoadGraphContextsAsync(ProjectContextLoadRequest request,
        string root, ProjectContextLoadResult graph, List<string> diagnostics,
        List<ProjectContextProjectExclusion> excludedProjects, CancellationToken cancellationToken)
    {
        if (request.WarmAcquisitionSelectionProbe)
        {
            // Choose ONCE for the entire graph, before any design-time target.
            // No exception after LoadContext may switch acquisition paths.
            var useWarm = WarmGraphSupported(graph.BuildGraph);
            request = request with { WarmAcquisitionProbe = useWarm };
            diagnostics.Add(useWarm ? "Private warm selection: warm." : "Private warm selection: ordinary.");
        }
        else if (request.WarmAcquisitionProbe)
            EnsureWarmGraphSupported(graph.BuildGraph);
        var bindings = new List<ProjectGraphContextBinding>();
        var observations = request.CollectProjectObservation ? new List<WarmProjectObservationResult>() : null;
        var useMapped = !request.WarmAcquisitionProbe && !WarmGraphSupported(graph.BuildGraph);
        var mappedMetadata = new Dictionary<string, EvaluatedMetadata>(StringComparer.Ordinal);
        if (useMapped)
            foreach (var node in graph.BuildGraph)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = Evaluate(node.ProjectPath, node.Configuration, node.Platform, node.TargetFramework, node.GlobalProperties);
                EnsurePreparedAssets(metadata, node.ProjectPath);
                mappedMetadata.Add(node.Id, metadata);
            }
        static IEnumerable<string> MappedInputs(EvaluatedMetadata metadata) => metadata.AuthoredInputs
            .Concat(metadata.PhaseInputs.Where(input => input.Role != "analyzerConfigProbe").Select(input => input.Path));
        var mappedBefore = useMapped ? SnapshotInputs(mappedMetadata.Values.SelectMany(MappedInputs).Distinct(PathComparer)) : null;
        var mappedAbsent = useMapped ? mappedMetadata.Values.SelectMany(metadata => metadata.PhaseInputs)
            .Where(input => input.Role == "analyzerConfigProbe").Select(input => input.Path).Distinct(PathComparer).ToArray() : [];
        void ValidateMappedInputs()
        {
            if (mappedBefore is null) return;
            cancellationToken.ThrowIfCancellationRequested();
            var currentPaths = graph.BuildGraph.Select(node => Evaluate(node.ProjectPath, node.Configuration,
                node.Platform, node.TargetFramework, node.GlobalProperties)).SelectMany(MappedInputs).Distinct(PathComparer);
            if (mappedAbsent.Any(File.Exists))
                throw new ProjectContextException("context.inputsMutated", "A declared absent graph input appeared during mapped design-time acquisition.");
            if (!mappedBefore.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SequenceEqual(SnapshotInputs(currentPaths).OrderBy(pair => pair.Key, StringComparer.Ordinal)))
                throw new ProjectContextException("context.inputsMutated", "Authored graph inputs changed during mapped design-time acquisition.");
        }
        using var mappedWorkspace = useMapped ? new MappedDesignTimeWorkspace(graph.BuildGraph,
            mappedMetadata.ToDictionary(pair => pair.Key, pair => (pair.Value.TargetPath, pair.Value.ReferenceAssemblyPath), StringComparer.Ordinal)) : null;
        using var graphWorkspace = new GraphWorkspace(diagnostics);
        foreach (var node in graph.BuildGraph)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = request with { Configuration = node.Configuration, Platform = node.Platform };
            EvaluatedMetadata metadata;
            using (request.Timing?.Start("worker.nodeMetadata"))
                metadata = useMapped ? mappedMetadata[node.Id] : Evaluate(node.ProjectPath, node.Configuration, node.Platform, node.TargetFramework, node.GlobalProperties);
            EnsurePreparedAssets(metadata, node.ProjectPath);
            // Required execution contexts are independent of production inventory filtering.
            var context = await LoadContext(root, node.ProjectPath, node.TargetFramework, selected, diagnostics,
                cancellationToken, node.GlobalProperties, metadata, graphWorkspace, observations, mappedWorkspace, node.Id,
                useMapped ? ValidateMappedInputs : null);
            ValidateMappedInputs();
            bindings.Add(new(node.Id, node.ProjectPath, context, metadata.IsTestProject));
        }
        bindings = BindMappedProjectReferences(graph.BuildGraph, bindings, cancellationToken);
        return new(true, bindings.Select(binding => binding.Context).ToArray(), diagnostics, null, excludedProjects)
        { BuildGraph = graph.BuildGraph, GraphContexts = bindings, ProjectObservations = observations?.ToArray() ?? [] };
    }

    private static List<ProjectGraphContextBinding> BindMappedProjectReferences(
        IReadOnlyList<ProjectBuildGraphNode> nodes, IReadOnlyList<ProjectGraphContextBinding> bindings,
        CancellationToken token)
    {
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        return bindings.Select(binding =>
        {
            var node = byId[binding.GraphId];
            var permitted = new HashSet<string>(StringComparer.Ordinal);
            void Add(string id)
            {
                foreach (var dependency in byId[id].Dependencies)
                    if (permitted.Add(dependency)) Add(dependency);
            }
            Add(node.Id);
            var references = binding.Context.ProjectReferenceOutputs.Select(reference =>
            {
                token.ThrowIfCancellationRequested();
                var owners = bindings.Where(candidate => permitted.Contains(candidate.GraphId) &&
                    PathComparer.Equals(candidate.ProjectPath, reference.ProjectPath)).ToArray();
                if (owners.Any(owner => PathComparer.Equals(owner.Context.EvaluatedTargetPath, reference.OutputPath)))
                    return reference; // Framework selection was already independently observed.
                // Workspace can project the parent's globals onto a mapped reference.
                // MSBuild's evaluated graph owns the mapping. Reconcile only a unique
                // selected project, after proving the observed projection by evaluation;
                // never select a framework by proximity, filename or assembly name.
                if (owners.Length != 1)
                    throw new ProjectContextException("context.projectReferenceUnproven", "Mapped project-reference owner is ambiguous.");
                var projected = Evaluate(reference.ProjectPath, node.Configuration, node.Platform,
                    node.TargetFramework, node.GlobalProperties);
                if (!PathComparer.Equals(projected.TargetPath, reference.OutputPath))
                    throw new ProjectContextException("context.projectReferenceUnproven", "Workspace project-reference projection differs from independent evaluation.");
                return reference with { OutputPath = owners[0].Context.EvaluatedTargetPath! };
            }).ToArray();
            return binding with { Context = binding.Context with { ProjectReferenceOutputs = references } };
        }).ToList();
    }

    private static async Task<ProjectContextLoadResult> DiscoverGraph(ProjectContextLoadRequest request,
        ProjectSelection[] selections, List<string> diagnostics, List<ProjectContextProjectExclusion> exclusions,
        CancellationToken cancellationToken)
    {
        var entries = new List<ProjectGraphEntryPoint>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in selections)
        {
            await EnsureProjectSdkAsync(selection.Path, request, cancellationToken);
            EvaluatedMetadata metadata;
            using (request.Timing?.StartSynchronous("worker.graphEntryEvaluation"))
                metadata = Evaluate(selection.Path, selection.Configuration, selection.Platform, null);
            foreach (var framework in metadata.Frameworks)
            {
                if (request.Frameworks.Count > 0 && !request.Frameworks.Contains(framework, StringComparer.OrdinalIgnoreCase)) continue;
                matched.Add(framework);
                var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["Configuration"] = selection.Configuration };
                // A single-framework project already selects its framework by evaluation.
                // Adding a redundant global creates a second SDK graph node when the same
                // project is both a solution entry and a negotiated ProjectReference.
                // Multi-framework entries still require explicit inner-build selection.
                if (metadata.RequiresInnerFrameworkSelection) properties["TargetFramework"] = framework;
                if (selection.Platform is not null) properties["Platform"] = selection.Platform;
                entries.Add(new(selection.Path, properties));
            }
        }
        if (request.Frameworks.Any(framework => !matched.Contains(framework)))
            throw new ProjectContextException("context.frameworkNotFound", "A requested entry framework was not found.");
        using var collection = new ProjectCollection();
        // MSBuild owns ProjectReference mappings and framework negotiation; execute no targets here.
        // Serial construction plus the owning subprocess deadline also bounds evaluation cancellation.
        ProjectGraph graph;
        using (request.Timing?.StartSynchronous("worker.graphConstruction"))
            graph = new ProjectGraph(entries, collection, null, 1, cancellationToken);
        using var projectionTiming = request.Timing?.StartSynchronous("worker.graphProjection");
        var nodes = graph.ProjectNodesTopologicallySorted.ToArray();
        if (nodes.Length > 4096)
            throw new ProjectContextException("context.graphLimit", "The build graph exceeds 4096 evaluated nodes.");
        var identities = nodes.ToDictionary(node => node, node => GraphIdentity(node));
        bool IsOuter(ProjectGraphNode node) =>
            node.ProjectInstance.GetPropertyValue("IsCrossTargetingBuild").Equals("true", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(node.ProjectInstance.GetPropertyValue("TargetFramework"));
        IEnumerable<ProjectGraphNode> BuildDependencies(ProjectGraphNode node)
        {
            // Cross-target outer nodes orchestrate their declared inner nodes, not Csc invocations.
            // Preserve every graph-selected inner context; do not guess nearest TFM or flatten root globals.
            foreach (var dependency in node.ProjectReferences)
            {
                if (IsOuter(dependency))
                {
                    if (dependency.ProjectReferences.Count == 0)
                        throw new ProjectContextException("context.graphFrameworkUnresolved", "A cross-target dependency has no inner contexts.");
                    foreach (var inner in BuildDependencies(dependency)) yield return inner;
                }
                else yield return dependency;
            }
        }
        var result = new List<ProjectBuildGraphNode>();
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = node.ProjectInstance;
            if (!Path.GetExtension(instance.FullPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                throw new ProjectContextException("context.graphLanguageUnsupported", "A required dependency is not a supported C# project.");
            await EnsureProjectSdkAsync(instance.FullPath, request, cancellationToken);
            if (IsOuter(node)) continue;
            var framework = instance.GetPropertyValue("TargetFramework");
            if (string.IsNullOrWhiteSpace(framework))
                throw new ProjectContextException("context.graphFrameworkUnresolved", "A required graph node has no resolved target framework.");
            var globals = instance.GlobalProperties.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            result.Add(new(identities[node], Path.GetFullPath(instance.FullPath),
                instance.GetPropertyValue("Configuration"), instance.GetPropertyValue("Platform"), framework, globals,
                BuildDependencies(node).Select(dependency => identities[dependency]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
        }
        return new(true, [], diagnostics, null, exclusions) { BuildGraph = result };
    }

    private static void EnsureWarmGraphSupported(IReadOnlyList<ProjectBuildGraphNode> nodes)
    {
        if (!WarmGraphSupported(nodes))
            throw new ProjectContextException("context.warmMappedGraphUnsupported", "Warm acquisition does not support mapped dependency globals; no design-time targets were executed.");
    }

    private static bool WarmGraphSupported(IReadOnlyList<ProjectBuildGraphNode> nodes)
    {
        // Check the entire normalized graph before the first design-time target.
        // Mapped references can cause ordinary Workspace to retain additional
        // recursively loaded ProjectReferences, which this probe cannot reproduce.
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var node in nodes)
        foreach (var dependencyId in node.Dependencies)
        {
            if (!byId.TryGetValue(dependencyId, out var dependency))
                return false;
            // Compare the exact dictionaries both acquisition paths actually use.
            // Graph references may omit the TargetFramework global while their
            // evaluated framework is equal; WorkspaceProperties sets it explicitly.
            var parentGlobals = WorkspaceProperties(node.Configuration, node.Platform, node.TargetFramework, node.GlobalProperties);
            var childGlobals = WorkspaceProperties(dependency.Configuration, dependency.Platform, dependency.TargetFramework, dependency.GlobalProperties);
            if (node.Configuration != dependency.Configuration || node.Platform != dependency.Platform ||
                node.TargetFramework != dependency.TargetFramework ||
                parentGlobals.Count != childGlobals.Count ||
                parentGlobals.Any(pair => !childGlobals.TryGetValue(pair.Key, out var value) || value != pair.Value))
                return false;
        }
        return true;
    }

    private static string GraphIdentity(ProjectGraphNode node)
    {
        MappedGlobalPrivacy.Validate(node.ProjectInstance.GlobalProperties);
        // All mapped globals distinguish contexts, including custom AdditionalProperties.
        // JSON framing avoids delimiter collisions; physical identities remain private transport only.
        var globals = node.ProjectInstance.GlobalProperties.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new[] { pair.Key.ToUpperInvariant(), pair.Value }).ToArray();
        var path = Path.GetFullPath(node.ProjectInstance.FullPath);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return ProjectAnalysisContext.ContentHash(JsonSerializer.Serialize(new { path, globals }));
    }

    private static async Task<ProjectSelection[]> DiscoverProjects(string target, string configuration, string? platform,
        List<string> diagnostics, List<ProjectContextProjectExclusion> exclusions, CancellationToken cancellationToken)
    {
        if (Path.GetExtension(target).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            return [new ProjectSelection(target, configuration, platform)];
        if (Path.GetExtension(target).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(target);
            var model = await SolutionSerializers.SlnXml.OpenAsync(stream, cancellationToken);
            var solutionPlatform = platform ?? model.Platforms.Order(StringComparer.Ordinal).FirstOrDefault() ?? "Any CPU";
            var selections = new List<ProjectSelection>();
            foreach (var project in model.SolutionProjects)
            {
                var path = Path.GetFullPath(project.FilePath, Path.GetDirectoryName(target)!);
                if (!Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    exclusions.Add(new(Logical(Path.GetDirectoryName(target)!, path), "projectLanguage.unsupported"));
                    continue;
                }
                var mapping = project.GetProjectConfiguration(configuration, solutionPlatform);
                if (mapping.BuildType is null || mapping.Platform is null)
                    throw new ProjectContextException("context.solutionMappingUnsupported", $"No project configuration mapping exists for {path} under {configuration}|{solutionPlatform}.");
                if (!mapping.Build) { exclusions.Add(new(Logical(Path.GetDirectoryName(target)!, path), "solutionConfiguration.notBuilt")); continue; }
                selections.Add(new(path, mapping.BuildType, mapping.Platform.Replace(" ", string.Empty, StringComparison.Ordinal)));
            }
            if (selections.Count == 0) throw new ProjectContextException("context.noProjects", "The selected solution contains no buildable C# projects.");
            return selections.ToArray();
        }
        var solutionFile = SolutionFile.Parse(target);
        var solutionConfiguration = solutionFile.SolutionConfigurations
            .Where(value => value.ConfigurationName.Equals(configuration, StringComparison.OrdinalIgnoreCase))
            .Where(value => platform is null || value.PlatformName.Equals(platform, StringComparison.OrdinalIgnoreCase) ||
                value.PlatformName.Replace(" ", string.Empty, StringComparison.Ordinal).Equals(platform.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value.PlatformName, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new ProjectContextException("context.solutionMappingUnsupported", $"Solution configuration {configuration}|{platform ?? "<default>"} is unavailable in {target}.");
        var selectionsForSln = new List<ProjectSelection>();
        foreach (var project in solutionFile.ProjectsInOrder.Where(value => value.ProjectType != SolutionProjectType.SolutionFolder))
        {
            var path = Path.GetFullPath(project.AbsolutePath);
            if (!Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                exclusions.Add(new(Logical(Path.GetDirectoryName(target)!, path), "projectLanguage.unsupported"));
                continue;
            }
            if (!project.ProjectConfigurations.TryGetValue(solutionConfiguration.FullName, out var mapping))
                throw new ProjectContextException("context.solutionMappingUnsupported", $"No project configuration mapping exists for {path} under {solutionConfiguration.FullName}.");
            if (!mapping.IncludeInBuild) { exclusions.Add(new(Logical(Path.GetDirectoryName(target)!, path), "solutionConfiguration.notBuilt")); continue; }
            selectionsForSln.Add(new(path, mapping.ConfigurationName, mapping.PlatformName.Replace(" ", string.Empty, StringComparison.Ordinal)));
        }
        if (selectionsForSln.Count == 0) throw new ProjectContextException("context.noProjects", "The selected solution contains no buildable C# projects.");
        return selectionsForSln.ToArray();
    }

    private static async Task<ProjectAnalysisContext> LoadContext(string root, string projectPath, string framework,
        ProjectContextLoadRequest request, List<string> diagnostics, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? globalProperties = null, EvaluatedMetadata? evaluatedMetadata = null,
        GraphWorkspace? graphWorkspace = null, ICollection<WarmProjectObservationResult>? observations = null,
        MappedDesignTimeWorkspace? mappedWorkspace = null, string? graphId = null, Action? validateMappedInputs = null)
    {
        using var contextTiming = request.Timing?.StartSynchronous("worker.context");
        EvaluatedMetadata metadata;
        using (request.Timing?.StartSynchronous("worker.contextMetadata"))
            // Reuse only this exact graph node's evaluation in this worker invocation.
            // Each independent current load still evaluates every node afresh; differing
            // mapped globals and all authored snapshots remain isolated and unchanged.
            metadata = evaluatedMetadata ?? Evaluate(projectPath, request.Configuration, request.Platform, framework, globalProperties);
        EnsurePreparedAssets(metadata, projectPath);
        IReadOnlyDictionary<string, string> beforeInputs;
        using (request.Timing?.Start("worker.authoredBefore"))
            beforeInputs = SnapshotInputs(metadata.AuthoredInputs);
        List<WorkspaceDiagnostic> workspaceDiagnostics = [];
        using var ownedWorkspace = !request.WarmAcquisitionProbe && mappedWorkspace is null && graphWorkspace is null
            ? CreateWorkspace(request.Configuration, request.Platform, framework, diagnostics, out workspaceDiagnostics, globalProperties)
            : null;
        WarmDesignTimeProject? acquiredWarmProject = null;
        Microsoft.CodeAnalysis.Project project;
        try
        {
            using var workspaceTiming = request.Timing?.StartSynchronous("worker.workspace");
            if (request.WarmAcquisitionProbe || mappedWorkspace is not null)
            {
                acquiredWarmProject = await WarmDesignTimeProject.AcquireAsync(projectPath,
                    WorkspaceProperties(request.Configuration, request.Platform, framework, globalProperties), diagnostics, cancellationToken,
                    timing: request.Timing, mappedGraph: mappedWorkspace, graphId: graphId);
                validateMappedInputs?.Invoke();
                project = acquiredWarmProject.Project;
            }
            else
            {
                var workspace = ownedWorkspace ?? graphWorkspace!.Acquire(request.Configuration, request.Platform, framework,
                    globalProperties, out workspaceDiagnostics);
                project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
            }
        }
        catch
        {
            acquiredWarmProject?.Dispose();
            throw;
        }
        using var warmProject = acquiredWarmProject;
        ThrowOnWorkspaceFailure(workspaceDiagnostics, projectPath);
        if (project.ParseOptions is not CSharpParseOptions parseOptions)
            throw new ProjectContextException("context.parseOptionsUnavailable", $"C# parse options were unavailable for {projectPath}.");
        if (observations is not null)
            observations.Add(new(projectPath, await WarmProjectObservation.CaptureAsync(project, cancellationToken)));
        var sources = new List<ProjectSourceIdentity>();
        var generatorTreeSources = new HashSet<ProjectSourceIdentity>(ReferenceEqualityComparer.Instance);
        var compilerDocuments = new List<ProjectSourceIdentity>();
        var exclusions = new List<ProjectSourceExclusion>();
        var evaluatedCompileInputs = metadata.CompileInputs.ToHashSet(PathComparer);
        HashSet<string> trackedInputs;
        using (request.Timing?.Start("worker.trackedInputs"))
            trackedInputs = await TrackedInputs(projectPath, request.Timeout, cancellationToken);
        var documentPaths = project.Documents.Where(document => document.FilePath is not null)
            .Select(document => Path.GetFullPath(document.FilePath!)).ToHashSet(PathComparer);
        using (request.Timing?.Start("worker.documents"))
        foreach (var document in project.Documents.OrderBy(document => document.FilePath, StringComparer.Ordinal))
        {
            if (document.FilePath is null) throw new ProjectContextException("context.sourcePathUnavailable", $"A document in {projectPath} has no physical path.");
            var text = (await document.GetTextAsync(cancellationToken)).ToString();
            var logical = document.Folders.Count > 0 ? string.Join('/', document.Folders.Append(document.Name)) : document.Name;
            var physical = Path.GetFullPath(document.FilePath);
            var generated = !evaluatedCompileInputs.Contains(physical) && !trackedInputs.Contains(physical) &&
                metadata.IntermediatePaths.Any(path => IsWithin(path, physical)) && !TraversesReparsePoint(physical);
            var contentIdentity = File.Exists(document.FilePath)
                ? ProjectAnalysisContext.ContentHash(await File.ReadAllBytesAsync(document.FilePath, cancellationToken))
                : ProjectAnalysisContext.ContentHash(text);
            var identity = new ProjectSourceIdentity(Logical(root, document.FilePath), logical.Replace('\\', '/'),
                contentIdentity, generated, !IsWithin(root, document.FilePath))
            { ResolvedPath = Path.GetFullPath(document.FilePath) };
            compilerDocuments.Add(identity);
            if (generated && !request.IncludeGenerated) exclusions.Add(new(identity.PhysicalPath, identity.LogicalPath, "generated.defaultExcluded", identity.ContentIdentity));
            else sources.Add(identity);
        }
        var unresolvedReferences = warmProject?.UnresolvedProjectReferencePaths ?? [];
        var generatorOnlyTreeCount = 0;
        var generatedDocuments = new List<ProjectGeneratedSourceDocument>();
        if (unresolvedReferences.Count == 0)
        {
            if (project.AnalyzerReferences.Any(reference => reference is UnresolvedAnalyzerReference))
                throw new ProjectContextException("context.generatedSourceUnavailable", "An analyzer or generator reference could not be resolved.");
            var analyzerLoadFailures = new List<string>();
            var generators = new List<ISourceGenerator>();
            try
            {
                using (request.Timing?.Start("worker.generatorDiscovery"))
                foreach (var reference in project.AnalyzerReferences)
                {
                    if (reference is AnalyzerFileReference fileReference)
                        fileReference.AnalyzerLoadFailed += (_, args) => analyzerLoadFailures.Add(args.Message);
                    // GetGenerators performs generator-role discovery without instantiating
                    // ordinary DiagnosticAnalyzers. Analyzer constructor/load warnings must
                    // not become source-generator binding failures in shared loading.
                    generators.AddRange(reference.GetGenerators(LanguageNames.CSharp));
                }
            }
            catch (Exception exception) when (exception is not ProjectContextException)
            {
                throw new ProjectContextException("context.generatedSourceUnavailable", $"A generator could not be loaded: {exception.Message}");
            }
            if (analyzerLoadFailures.Count > 0)
                throw new ProjectContextException("context.generatedSourceUnavailable", string.Join(Environment.NewLine, analyzerLoadFailures));
            Compilation compilation;
            using (request.Timing?.StartSynchronous("worker.compilation"))
                compilation = await project.GetCompilationAsync(cancellationToken)
                    ?? throw new ProjectContextException("context.generatedSourceUnavailable", "Compilation was unavailable while inspecting generator binding.");
            var extraTrees = compilation.SyntaxTrees.Where(tree => string.IsNullOrWhiteSpace(tree.FilePath) ||
                !documentPaths.Contains(Path.GetFullPath(tree.FilePath))).Take(32769).ToArray();
            if (extraTrees.Length > 32768)
                throw new ProjectContextException("context.generatedSourceLimit", "Generator tree inventory exceeds the supported limit.");
            // Compilation.GetDiagnostics does NOT include generator-driver failures. Run a
            // diagnostic driver over physical trees with the same options/additional inputs;
            // do not silently treat a throwing generator as a successful zero-tree generator.
            if (generators.Count > 0)
            {
                var additionalTexts = new List<AdditionalText>();
                foreach (var document in project.AdditionalDocuments)
                {
                    if (document.FilePath is null)
                        throw new ProjectContextException("context.generatedSourceUnavailable", "A generator additional input has no physical path.");
                    additionalTexts.Add(new PreparedAdditionalText(document.FilePath, await document.GetTextAsync(cancellationToken)));
                }
                GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, additionalTexts, parseOptions,
                    project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
                GeneratorDriverRunResult run;
                using (request.Timing?.Start("worker.generatorValidation"))
                    run = driver.RunGenerators(compilation.RemoveSyntaxTrees(extraTrees), cancellationToken).GetRunResult();
                if (run.Results.Any(result => result.Exception is not null) ||
                    run.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error || d.Id is "CS8784" or "CS8785"))
                    throw new ProjectContextException("context.generatedSourceUnavailable", "A source generator failed during binding inspection.");
                if (run.GeneratedTrees.Length != extraTrees.Length)
                    throw new ProjectContextException("context.generatedSourceUnavailable", "Workspace and diagnostic generator tree inventories disagree.");
            }
            generatorOnlyTreeCount = extraTrees.Length;
            long generatedCharacters = 0;
            var generatedOrdinal = 0;
            foreach (var tree in extraTrees)
            {
                var ordinal = generatedOrdinal++;
                var sourceText = await tree.GetTextAsync(cancellationToken);
                generatedCharacters += sourceText.Length;
                if (generatedCharacters > 16 * 1024 * 1024)
                    throw new ProjectContextException("context.generatedSourceLimit", "Generated text exceeds the private transport limit.");
                ProjectGeneratedSourceDocument? generated = null;
                try { generated = CompilerSourceSet.Capture(tree, Path.GetDirectoryName(projectPath)!, Logical(root, projectPath), cancellationToken); }
                catch (ProjectContextException exception) when (exception.Reason == "compiler.generatorBindingUnsupported")
                {
                    // Ordinary semantic loading does not grant compiler evidence.
                    // Preserve the full tree count: the incomplete byte vector
                    // must still refuse when compiler binding is requested.
                }
                if (generated is not null) generatedDocuments.Add(generated);
                if (request.IncludeGenerated)
                {
                    // Unbound semantic identities remain distinct even for identical
                    // text. Tree spelling is opaque DATA, never a filesystem lookup.
                    var logical = generated?.LogicalPath ?? "<generated>/" + CanonicalIdentity.Tuple(
                        "unbound-generator-tree-v1", Logical(root, projectPath), tree.FilePath.Replace('\\', '/'),
                        ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ".cs";
                    var generatedSource = new ProjectSourceIdentity(logical, logical,
                        generated is null ? ProjectAnalysisContext.ContentHash(sourceText.ToString()) :
                            ProjectAnalysisContext.ContentHash(Convert.FromBase64String(generated.BytesBase64)), true, false)
                    { ResolvedPath = generated?.Path };
                    sources.Add(generatedSource);
                    generatorTreeSources.Add(generatedSource);
                }
            }
        }
        // Keep precise input roles separate from output protection and from public analysis exclusions.
        // Restore imports/assets and analyzer binaries are immutable prepared inputs, not transitions.
        var phaseInputs = new List<ProjectPhaseInput>(metadata.PhaseInputs);
        phaseInputs.AddRange(compilerDocuments.Select(d => new ProjectPhaseInput(d.ResolvedPath!, "source", d.IsGenerated)));
        phaseInputs.AddRange(project.AdditionalDocuments.Where(d => d.FilePath is not null)
            .Select(d => new ProjectPhaseInput(Path.GetFullPath(d.FilePath!), "additionalFile", false)));
        phaseInputs.AddRange(project.AnalyzerConfigDocuments.Where(d => d.FilePath is not null)
            .Select(d => new ProjectPhaseInput(Path.GetFullPath(d.FilePath!), File.Exists(d.FilePath) ? "analyzerConfig" : "analyzerConfigProbe",
                !trackedInputs.Contains(Path.GetFullPath(d.FilePath!)) &&
                !metadata.PhaseInputs.Any(p => PathComparer.Equals(p.Path, Path.GetFullPath(d.FilePath!))) &&
                metadata.IntermediatePaths.Any(output => IsWithin(output, d.FilePath!)) && !TraversesReparsePoint(d.FilePath!))));
        phaseInputs.AddRange(project.AnalyzerReferences.OfType<AnalyzerFileReference>()
            .Select(a => new ProjectPhaseInput(Path.GetFullPath(a.FullPath), "analyzer", false)));
        var adapter = new ProjectAdapterIdentity(request.SdkVersion ?? "unknown", request.MsBuildVersion ?? "unknown",
            typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown");
        IReadOnlyDictionary<string, string> afterInputs;
        using (request.Timing?.Start("worker.authoredAfter"))
            afterInputs = SnapshotInputs(metadata.AuthoredInputs);
        if (!beforeInputs.OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(afterInputs.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
            throw new ProjectContextException("context.inputsMutated", $"Authored inputs changed while loading {projectPath}.");
        var createdContext = ProjectAnalysisContext.Create(Logical(root, projectPath), project.AssemblyName ?? metadata.AssemblyName,
            framework, metadata.Configuration, metadata.Platform, parseOptions.LanguageVersion,
            parseOptions.PreprocessorSymbolNames, parseOptions.Kind,
            metadata.Imports.Where(path => !IsWithin(metadata.ProjectExtensionsPath, path))
                .Select(path => ImportIdentity(root, request.SdkPath, path)).Append(RestoreIdentity(metadata.AssetsFile)),
            sources, new ProjectExclusionPolicy(request.IncludeTests, request.IncludeGenerated), adapter, exclusions);
        return createdContext with
        {
            Capabilities = unresolvedReferences.Count == 0 ? createdContext.Capabilities : new(false, false, false,
                "SDK-declared project references have ambiguous graph producers; semantic/compiler evidence is incomplete."),
            UnresolvedProjectReferencePaths = unresolvedReferences.ToArray(),
            ResolvedProjectPath = Path.GetFullPath(projectPath),
            GeneratorOnlyTreeCount = generatorOnlyTreeCount,
            GeneratedCompilerDocuments = Array.AsReadOnly(generatedDocuments.ToArray()),
            CompilerDocuments = compilerDocuments.OrderBy(source => source.ResolvedPath, StringComparer.Ordinal).ToArray(),
            PhaseInputs = phaseInputs.Distinct().OrderBy(p => p.Path, StringComparer.Ordinal).ThenBy(p => p.Role, StringComparer.Ordinal).ToArray(),
            ReferencePaths = project.MetadataReferences.Select(r => r.Display)
                .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p!))
                .Concat(unresolvedReferences).Distinct(PathComparer).Order(StringComparer.Ordinal).ToArray(),
            ProjectReferenceOutputs = project.ProjectReferences.Select(reference =>
            {
                var referenced = project.Solution.GetProject(reference.ProjectId);
                if (referenced?.FilePath is null || referenced.OutputFilePath is null ||
                    !Path.IsPathFullyQualified(referenced.FilePath) || !Path.IsPathFullyQualified(referenced.OutputFilePath))
                    throw new ProjectContextException("context.projectReferenceUnproven", "Workspace project-reference output identity is unavailable.");
                return new ProjectReferenceOutputIdentity(Path.GetFullPath(referenced.FilePath), Path.GetFullPath(referenced.OutputFilePath));
            }).OrderBy(reference => reference.ProjectPath, StringComparer.Ordinal).ThenBy(reference => reference.OutputPath, StringComparer.Ordinal).ToArray(),
            EvaluatedReferenceAssemblyPath = metadata.ReferenceAssemblyPath,
            EvaluatedTargetPath = metadata.TargetPath,
            EvaluatedIntermediateDirectory = metadata.IntermediateDirectory,
            GeneratedTransitionPaths = project.AnalyzerConfigDocuments.Where(document => document.FilePath is not null)
                .Select(document => Path.GetFullPath(document.FilePath!))
                .Where(path => !trackedInputs.Contains(path) && !metadata.AuthoredInputs.Contains(path, PathComparer) &&
                    metadata.IntermediatePaths.Any(output => IsWithin(output, path)) && !TraversesReparsePoint(path))
                .Distinct(PathComparer).Order(StringComparer.Ordinal).ToArray(),
            ProtectedPaths = metadata.AuthoredInputs.Concat(metadata.Imports)
                .Append(projectPath).Append(metadata.AssetsFile)
                .Concat(project.MetadataReferences.Select(reference => reference.Display)
                    .Where(path => path is not null && File.Exists(path)).Select(path => path!))
                .Concat(sources.Where(source => source.ResolvedPath is not null &&
                    !generatorTreeSources.Contains(source)).Select(source => source.ResolvedPath!))
                .Select(Path.GetFullPath).Distinct(PathComparer).Order(StringComparer.Ordinal).ToArray()
        };
    }

    private sealed class PreparedAdditionalText(string path, Microsoft.CodeAnalysis.Text.SourceText text) : AdditionalText
    {
        public override string Path => path;
        public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return text;
        }
    }

    // One owner per independent worker graph load. Reset the complete solution before
    // every node: no project, compilation, document or acceptance proof is reused.
    // Retain a workspace only for identical effective globals; dispose on every change.
    private sealed class GraphWorkspace(List<string> diagnostics) : IDisposable
    {
        private MSBuildWorkspace? workspace;
        private Dictionary<string, string>? properties;
        private List<WorkspaceDiagnostic> failures = [];

        internal MSBuildWorkspace Acquire(string configuration, string? platform, string framework,
            IReadOnlyDictionary<string, string>? globals, out List<WorkspaceDiagnostic> workspaceDiagnostics)
        {
            var current = WorkspaceProperties(configuration, platform, framework, globals);
            if (workspace is null || properties is null || properties.Count != current.Count ||
                current.Any(pair => !properties.TryGetValue(pair.Key, out var value) ||
                    !string.Equals(value, pair.Value, StringComparison.Ordinal)))
            {
                workspace?.Dispose();
                workspace = CreateWorkspace(configuration, platform, framework, diagnostics, out failures, globals);
                properties = current;
            }
            else
            {
                workspace.CloseSolution();
                failures.Clear();
            }
            workspaceDiagnostics = failures;
            return workspace;
        }

        public void Dispose() => workspace?.Dispose();
    }

    private static Dictionary<string, string> WorkspaceProperties(string configuration, string? platform,
        string? framework, IReadOnlyDictionary<string, string>? globalProperties)
    {
        var properties = globalProperties is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Configuration"] = configuration }
            : new Dictionary<string, string>(globalProperties, StringComparer.OrdinalIgnoreCase);
        if (platform is not null) properties["Platform"] = platform;
        if (framework is not null) properties["TargetFramework"] = framework;
        return properties;
    }

    private static MSBuildWorkspace CreateWorkspace(string configuration, string? platform, string? framework,
        List<string> diagnostics, out List<WorkspaceDiagnostic> workspaceDiagnostics,
        IReadOnlyDictionary<string, string>? globalProperties = null)
    {
        var properties = WorkspaceProperties(configuration, platform, framework, globalProperties);
        var workspace = MSBuildWorkspace.Create(properties);
        workspace.SkipUnrecognizedProjects = false;
        workspace.LoadMetadataForReferencedProjects = true;
        workspaceDiagnostics = [];
        var captured = workspaceDiagnostics;
        workspace.RegisterWorkspaceFailedHandler(args =>
        {
            captured.Add(args.Diagnostic);
            diagnostics.Add($"{args.Diagnostic.Kind}: {args.Diagnostic.Message}");
        });
        return workspace;
    }

    private static EvaluatedMetadata Evaluate(string projectPath, string configuration, string? platform, string? framework,
        IReadOnlyDictionary<string, string>? globalProperties = null)
    {
        var properties = globalProperties is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Configuration"] = configuration }
            : new Dictionary<string, string>(globalProperties, StringComparer.OrdinalIgnoreCase);
        if (platform is not null) properties["Platform"] = platform;
        if (framework is not null) properties["TargetFramework"] = framework;
        using var collection = new ProjectCollection(properties);
        var project = collection.LoadProject(projectPath);
        var frameworks = (project.GetPropertyValue("TargetFrameworks") is { Length: > 0 } many ? many : project.GetPropertyValue("TargetFramework"))
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (frameworks.Length == 0) throw new ProjectContextException("context.frameworkUnavailable", $"No target framework was evaluated for {projectPath}.");
        var assets = project.GetPropertyValue("ProjectAssetsFile");
        if (string.IsNullOrWhiteSpace(assets)) assets = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json");
        else if (!Path.IsPathRooted(assets)) assets = Path.GetFullPath(assets, Path.GetDirectoryName(projectPath)!);
        var projectExtensions = project.GetPropertyValue("MSBuildProjectExtensionsPath");
        if (string.IsNullOrWhiteSpace(projectExtensions)) projectExtensions = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj");
        else if (!Path.IsPathRooted(projectExtensions)) projectExtensions = Path.GetFullPath(projectExtensions, Path.GetDirectoryName(projectPath)!);
        var compileInputs = project.GetItems("Compile")
            .Select(item => Path.GetFullPath(item.EvaluatedInclude, Path.GetDirectoryName(projectPath)!))
            .Where(File.Exists).Distinct(PathComparer).ToArray();
        var authoredInputs = project.Imports.Select(import => import.ImportedProject.FullPath)
            .Append(projectPath)
            .Concat(compileInputs)
            .Where(File.Exists).Distinct(PathComparer).ToArray();
        var phaseInputs = new List<ProjectPhaseInput>
        { new(Path.GetFullPath(projectPath), "project", false), new(Path.GetFullPath(assets), "restoreAssets", false) };
        phaseInputs.AddRange(project.Imports.Select(i => new ProjectPhaseInput(Path.GetFullPath(i.ImportedProject.FullPath), "import", false)));
        foreach (var (itemName, role) in new[] { ("AdditionalFiles", "additionalFile"), ("EditorConfigFiles", "analyzerConfig"),
            ("GlobalAnalyzerConfigFiles", "analyzerConfig"), ("Analyzer", "analyzer") })
            phaseInputs.AddRange(project.GetItems(itemName).Select(i => new ProjectPhaseInput(
                Path.GetFullPath(i.EvaluatedInclude, Path.GetDirectoryName(projectPath)!),
                role == "analyzerConfig" && !File.Exists(Path.GetFullPath(i.EvaluatedInclude, Path.GetDirectoryName(projectPath)!)) ? "analyzerConfigProbe" : role, false)));
        for (var directory = Path.GetDirectoryName(projectPath); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var sdkConfiguration = Path.Combine(directory, "global.json");
            if (File.Exists(sdkConfiguration)) { phaseInputs.Add(new(sdkConfiguration, "sdkConfiguration", false)); break; }
        }
        return new EvaluatedMetadata(frameworks,
            string.Equals(project.GetPropertyValue("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase),
            project.GetPropertyValue("AssemblyName"),
            project.GetPropertyValue("Configuration") is { Length: > 0 } c ? c : configuration,
            project.GetPropertyValue("Platform") is { Length: > 0 } p ? p : platform ?? "AnyCPU",
            project.Imports.Select(import => import.ImportedProject.FullPath).ToArray(), assets, projectExtensions,
            authoredInputs, compileInputs,
            new[] { "IntermediateOutputPath", "BaseIntermediateOutputPath" }
                .Select(project.GetPropertyValue).Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(EvaluatedOutputPath)
                .Distinct(PathComparer).ToArray(),
            EvaluatedPath("TargetPath"), EvaluatedPath("IntermediateOutputPath"),
            string.Equals(project.GetPropertyValue("ProduceReferenceAssembly"), "true", StringComparison.OrdinalIgnoreCase)
                ? EvaluatedPath("TargetRefPath") : null, phaseInputs.ToArray())
        {
            RequiresInnerFrameworkSelection = string.IsNullOrWhiteSpace(project.GetPropertyValue("TargetFramework")),
            HasProjectReferences = project.GetItems("ProjectReference").Any()
        };

        string? EvaluatedPath(string name)
        {
            var value = project.GetPropertyValue(name);
            return string.IsNullOrWhiteSpace(value) ? null : Path.TrimEndingDirectorySeparator(EvaluatedOutputPath(value));
        }

        string EvaluatedOutputPath(string value)
        {
            // SDK/MSBuild output properties accept Windows separators even on Unix
            // (solution mappings supply obj\Configuration). Resolve that evaluated
            // property with SDK path semantics before filesystem containment checks;
            // do not change graph globals, captured authored paths or alias guards.
            return Path.GetFullPath(value.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(projectPath)!);
        }
    }

    private static async Task<HashSet<string>> TrackedInputs(string projectPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(projectPath)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".git")) &&
               !Directory.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        if (directory is null) return new HashSet<string>(PathComparer);
        var result = await ProcessRunner.RunAsync("git", ["--no-optional-locks", "ls-files", "--full-name", "-z"],
            directory.FullName, timeout, cancellationToken);
        if (result.ExitCode != 0)
            throw new ProjectContextException("context.gitInventoryUnavailable", "Cannot independently classify Git-tracked compiler sources.");
        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.GetFullPath(path, directory.FullName)).ToHashSet(PathComparer);
    }

    private static bool TraversesReparsePoint(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private static string Logical(string root, string path) => IsWithin(root, path)
        ? Path.GetRelativePath(root, path).Replace('\\', '/')
        : $"<external>/{ContentIdentity(path)}/{Path.GetFileName(path)}";
    private static string ImportIdentity(string root, string? sdkPath, string path)
    {
        if (IsWithin(root, path)) return $"{Logical(root, path)}:{ContentIdentity(path)}";
        if (sdkPath is not null && IsWithin(sdkPath, path))
            return $"<sdk>/{Path.GetRelativePath(sdkPath, path).Replace('\\', '/')}:{ContentIdentity(path)}";
        return $"<external>/{Path.GetFileName(path)}:{ContentIdentity(path)}";
    }
    private static string ContentIdentity(string path) => File.Exists(path)
        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
        : ProjectAnalysisContext.ContentHash(Path.GetFileName(path));
    private static string RestoreIdentity(string assetsFile)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(assetsFile));
        IEnumerable<string> libraries = document.RootElement.TryGetProperty("libraries", out var value)
            ? value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            : Enumerable.Empty<string>();
        return $"<restore>/{ProjectAnalysisContext.ContentHash(string.Join("\n", libraries))}";
    }
    private static bool IsWithin(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static void EnsurePreparedAssets(EvaluatedMetadata metadata, string projectPath)
    {
        if (!File.Exists(metadata.AssetsFile))
            throw new ProjectContextException("context.assetsUnavailable", $"Restore assets are missing for {projectPath}. Restore declared dependencies before loading project context.");
    }
    private static async Task EnsureProjectSdkAsync(string projectPath, ProjectContextLoadRequest request, CancellationToken cancellationToken)
    {
        if (request.DotNetHostPath is null || request.DotNetRoot is null || request.SdkVersion is null) return;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_ROOT"] = request.DotNetRoot,
            ["DOTNET_HOST_PATH"] = request.DotNetHostPath,
            ["PATH"] = request.DotNetRoot + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        };
        ProcessResult result;
        using (request.Timing?.Start("worker.projectSdk"))
        {
            // This call is already isolated in the deadline-owned loader worker.
            // Observe each project's current selection independently before its
            // evaluation; never memoize by path or replay an earlier SDK result.
            if (request.NativeSdkIdentityProbe)
            {
                var observed = NativeSdkIdentity.TryObserve(Path.GetDirectoryName(projectPath)!, request.DotNetHostPath, cancellationToken);
                if (observed.Status == "failed")
                    throw new ProjectContextException("context.sdkMismatch", $"Project {projectPath} cannot resolve its selected SDK.");
                if (observed.Status == "resolved")
                {
                    using var nativeTiming = request.Timing?.Start("worker.projectSdkNative");
                    if (observed.Version != request.SdkVersion || observed.MsBuildVersion != request.MsBuildVersion ||
                        !PathComparer.Equals(observed.Path, request.SdkPath) || !PathComparer.Equals(observed.DotNetRoot, request.DotNetRoot))
                        throw new ProjectContextException("context.sdkMismatch", $"Project {projectPath} resolves a different SDK/engine identity.");
                    return;
                }
            }
            result = await ProcessRunner.RunWithEnvironmentAsync(request.DotNetHostPath, ["--version"], Path.GetDirectoryName(projectPath)!,
                request.Timeout, cancellationToken, environment);
        }
        if (result.ExitCode != 0 || !result.StandardOutput.Trim().Equals(request.SdkVersion, StringComparison.Ordinal))
            throw new ProjectContextException("context.sdkMismatch",
                $"Project {projectPath} resolves SDK {result.StandardOutput.Trim()}, but the selected target resolved {request.SdkVersion}.");
    }

    private static Dictionary<string, string> SnapshotInputs(IEnumerable<string> paths)
    {
        try
        {
            return paths.ToDictionary(Path.GetFullPath,
                path => ProjectAnalysisContext.ContentHash(File.ReadAllBytes(path)), PathComparer);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProjectContextException("context.inputsMutated", $"An evaluated input changed or became unreadable: {exception.Message}");
        }
    }
    internal static void ThrowOnWorkspaceFailure(IEnumerable<WorkspaceDiagnostic> workspaceDiagnostics, string target)
    {
        var observed = workspaceDiagnostics.ToArray();
        if (observed.Any(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Warning))
        {
            // Workspace removes unresolved references before returning Project.
            // Recognize its localized diagnostic using the exact installed
            // dependency's resources, rather than accepting a reduced closure.
            string[] templates;
            try
            {
                var resources = new System.Resources.ResourceManager(
                    "Microsoft.CodeAnalysis.MSBuild.WorkspaceMSBuildResources", typeof(MSBuildWorkspace).Assembly);
                var cultures = new List<System.Globalization.CultureInfo>
                {
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.CultureInfo.CurrentUICulture
                };
                // A worker can emit diagnostics under a different culture. Use
                // every installed satellite beside this exact Roslyn assembly.
                var assemblyDirectory = Path.GetDirectoryName(typeof(MSBuildWorkspace).Assembly.Location);
                if (assemblyDirectory is not null)
                    foreach (var directory in Directory.EnumerateDirectories(assemblyDirectory))
                    {
                        try { cultures.Add(System.Globalization.CultureInfo.GetCultureInfo(Path.GetFileName(directory))); }
                        catch (System.Globalization.CultureNotFoundException) { }
                    }
                templates = cultures.Select(culture => resources.GetString(
                        "Unresolved_metadata_reference_removed_from_project_0", culture))
                    .Where(template => template is not null).Select(template => template!)
                    .Distinct(StringComparer.Ordinal).ToArray();
            }
            catch (Exception exception) when (exception is System.Resources.MissingManifestResourceException
                or IOException or UnauthorizedAccessException)
            { throw new ProjectContextException("context.metadataUnavailable", "Workspace metadata-reference diagnostics could not be verified."); }
            if (templates.Length == 0)
                throw new ProjectContextException("context.metadataUnavailable", "Workspace metadata-reference diagnostics could not be verified.");
            foreach (var template in templates)
            {
                var marker = template.IndexOf("{0}", StringComparison.Ordinal);
                if (marker <= 0 || template.IndexOf("{0}", marker + 3, StringComparison.Ordinal) >= 0)
                    throw new ProjectContextException("context.metadataUnavailable", "Workspace metadata-reference diagnostics could not be verified.");
                var prefix = template[..marker];
                var suffix = template[(marker + 3)..];
                if (observed.Any(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Warning &&
                    diagnostic.Message.StartsWith(prefix, StringComparison.Ordinal) &&
                    diagnostic.Message.EndsWith(suffix, StringComparison.Ordinal)))
                    throw new ProjectContextException("context.metadataUnavailable", "Workspace removed an unresolved metadata reference; the compiler closure is incomplete.");
            }
        }
        var failures = observed.Where(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure).ToArray();
        if (failures.Length > 0)
            throw new ProjectContextException("context.projectLoadFailed", $"MSBuildWorkspace failed to load {target}: {string.Join(" | ", failures.Select(value => value.Message))}");
    }

    private sealed record EvaluatedMetadata(string[] Frameworks, bool IsTestProject, string AssemblyName,
        string Configuration, string Platform, string[] Imports, string AssetsFile, string ProjectExtensionsPath,
        string[] AuthoredInputs, string[] CompileInputs, string[] IntermediatePaths, string? TargetPath, string? IntermediateDirectory, string? ReferenceAssemblyPath, ProjectPhaseInput[] PhaseInputs)
    {
        public bool RequiresInnerFrameworkSelection { get; init; }
        public bool HasProjectReferences { get; init; }
    }
    private sealed record ProjectSelection(string Path, string Configuration, string? Platform);
}
