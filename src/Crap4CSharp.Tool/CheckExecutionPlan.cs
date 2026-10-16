namespace Crap4CSharp.Core;

// Internal static scheduling proof only. Actual once builds/tests and post-test
// provenance remain separate mandatory gates; no public capability is asserted here.
internal sealed class CheckExecutionPlan
{
    public ProjectContextLoadResult PreparedGraph { get; }
    public IReadOnlyList<ProjectTestRunnerMetadata> Tests { get; }

    private CheckExecutionPlan(ProjectContextLoadResult graph, IReadOnlyList<ProjectTestRunnerMetadata> tests)
    { PreparedGraph = graph; Tests = tests; }

    internal static CheckExecutionPlan Create(ProjectContextLoadResult discovery, ProjectContextLoadResult loaded,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Reuse every structural/output/dependency guard before accepting ANY schedule.
        var nodes = CompilerGraphExecution.ValidateAndFreeze(loaded);
        if (!discovery.Success || discovery.BuildGraph.Count != nodes.Count || discovery.BuildGraph.Count == 0)
            throw Refuse("Independent runner discovery did not return the complete prepared graph.");
        var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var independent = new Dictionary<string, ProjectBuildGraphNode>(StringComparer.Ordinal);
        foreach (var node in discovery.BuildGraph)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(node.Id) || !independent.TryAdd(node.Id, node) ||
                !Canonical(node.ProjectPath) || node.Dependencies.Distinct(StringComparer.Ordinal).Count() != node.Dependencies.Count ||
                node.GlobalProperties.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != node.GlobalProperties.Count)
                throw Refuse("Independent graph node identities, paths, globals or dependency membership are invalid.");
        }
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested();
            if (!independent.TryGetValue(node.Id, out var current) || !paths.Equals(current.ProjectPath, node.ProjectPath) ||
                current.Configuration != node.Configuration || current.Platform != node.Platform ||
                current.TargetFramework != node.TargetFramework ||
                !current.Dependencies.Order(StringComparer.Ordinal).SequenceEqual(node.Dependencies.Order(StringComparer.Ordinal)) ||
                current.GlobalProperties.Count != node.GlobalProperties.Count ||
                current.GlobalProperties.Any(p => !node.GlobalProperties.TryGetValue(p.Key, out var value) || value != p.Value))
                throw Refuse("Independent runner graph differs from prepared Workspace graph or mapped global properties.");
        }
        var byId = loaded.GraphContexts.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
        var required = nodes.Where(n => byId[n.Id].IsTestProject).ToArray();
        // Empty analysis scope does not waive this requirement. No implicit no-tests policy.
        if (required.Length == 0 || discovery.TestRunners.Count != required.Length)
            throw Refuse("Every required test binding needs exactly one independently supported runner, and at least one test binding is required.");
        var runners = new Dictionary<string, ProjectTestRunnerMetadata>(StringComparer.Ordinal);
        foreach (var runner in discovery.TestRunners)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(runner.GraphId) || !runners.TryAdd(runner.GraphId, runner) ||
                !Canonical(runner.ProjectPath) || runner.Runner != "VSTest" || runner.TestSdkVersion != "18.0.1" ||
                runner.CollectorVersion != "6.0.4")
                throw Refuse("Runner identities or support contract are invalid or duplicated.");
        }
        foreach (var node in required)
            if (!runners.TryGetValue(node.Id, out var runner) || !paths.Equals(runner.ProjectPath, node.ProjectPath) ||
                runner.Configuration != node.Configuration || runner.Platform != node.Platform || runner.TargetFramework != node.TargetFramework)
                throw Refuse("Independent runner does not match its required prepared test binding.");
        // All nested collections are owned read-only copies. Immutable record shells alone
        // are insufficient: callers could otherwise downcast arrays or mutate dictionaries.
        var bindings = ReadOnly(nodes.Select(n => byId[n.Id] with { Context = FreezeContext(byId[n.Id].Context) }));
        var tests = ReadOnly(required.Select(n => runners[n.Id] with { }));
        var graph = loaded with
        {
            BuildGraph = nodes,
            GraphContexts = bindings,
            Contexts = ReadOnly(bindings.Select(b => b.Context)),
            TestRunners = tests,
            Diagnostics = ReadOnly(loaded.Diagnostics),
            Metadata = ReadOnly(loaded.Metadata.Select(m => m with { Frameworks = ReadOnly(m.Frameworks) })),
            ExcludedProjects = loaded.ExcludedProjects is null ? null : ReadOnly(loaded.ExcludedProjects)
        };
        token.ThrowIfCancellationRequested();
        return new(graph, tests); // Dependency-first order preserved, every test exactly once.
    }

    private static ProjectAnalysisContext FreezeContext(ProjectAnalysisContext context) => context with
    {
        PreprocessorSymbols = ReadOnly(context.PreprocessorSymbols), Imports = ReadOnly(context.Imports),
        Sources = ReadOnly(context.Sources), Exclusions = ReadOnly(context.Exclusions),
        CompilerDocuments = ReadOnly(context.CompilerDocuments), PhaseInputs = ReadOnly(context.PhaseInputs),
        ReferencePaths = ReadOnly(context.ReferencePaths), ProtectedPaths = ReadOnly(context.ProtectedPaths),
        GeneratedTransitionPaths = ReadOnly(context.GeneratedTransitionPaths)
    };
    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
    private static bool Canonical(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && path == Path.GetFullPath(path);
    private static ProjectContextException Refuse(string message) => new("test.planInvalid", message);
}
