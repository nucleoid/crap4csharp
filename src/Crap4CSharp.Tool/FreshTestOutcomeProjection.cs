using Crap4CSharp.Core;

// Public result projection only. The actual schedule, native observations and
// saved evidence continue to carry their physical GraphId unchanged.
internal static class FreshTestOutcomeProjection
{
    internal static IReadOnlyList<TestExecutionOutcomeResult> Create(FreshExecutionEvidence evidence)
        => Create(evidence.Preflight.Root, evidence.Compiler.FinalGraph.BuildGraph,
            evidence.Plan.Tests, evidence.Tests.Outcomes);

    internal static IReadOnlyList<TestExecutionOutcomeResult> Create(string root,
        IReadOnlyList<ProjectBuildGraphNode> graph, IReadOnlyList<ProjectTestRunnerMetadata> tests,
        IReadOnlyList<TestContextOutcome> outcomes)
    {
        var nodes = graph.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var logicalIds = outcomes.Select(o => o.GraphId).Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => LogicalContextIdentity.Create(root, nodes[id]), StringComparer.Ordinal);
        return outcomes.Select(o => new TestExecutionOutcomeResult(logicalIds[o.GraphId],
            tests.Single(t => t.GraphId == o.GraphId).ProjectPath, o.State, o.Process?.ExitCode,
            o.ResultsDirectory, o.LogDirectory)).ToArray();
    }
}
