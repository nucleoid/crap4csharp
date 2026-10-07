using System.Text.Json;

namespace Crap4CSharp.Core;

// Required schedule identity only, NOT completed-test or reusable evidence. The
// caller must independently reload the current graph/runners without build/test,
// then separately bind compiler phases, outputs, successful TRX and coverage.
internal static class CompletedTestPlanRecipe
{
    internal const string Kind = "completed-test-plan";
    private const string Provider = "finite-sdk-completed-test-plan-v1";
    private const int MaximumBytes = 256 * 1024;

    internal static byte[] Capture(CheckExecutionPlan plan, string workspaceRoot,
        string selectedRoot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(workspaceRoot);
        var selector = Path.GetFullPath(selectedRoot);
        if (root != workspaceRoot || selector != selectedRoot || !Inside(root, selector)) throw Refuse();
        var nodes = CompilerGraphExecution.ValidateAndFreeze(plan.PreparedGraph);
        var bindings = plan.PreparedGraph.GraphContexts.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
        var required = nodes.Where(n => bindings[n.Id].IsTestProject).Select(n => n.Id).ToArray();
        if (required.Length == 0 || !required.SequenceEqual(plan.Tests.Select(t => t.GraphId))) throw Refuse();
        var graph = nodes.Select(node =>
        {
            token.ThrowIfCancellationRequested();
            var binding = bindings[node.Id];
            var context = binding.Context;
            if (!Inside(root, node.ProjectPath)) throw Refuse();
            return new
            {
                id = node.Id, project = PathKey(node.ProjectPath),
                configuration = node.Configuration, platform = node.Platform, framework = node.TargetFramework,
                dependencies = node.Dependencies.Order(StringComparer.Ordinal).ToArray(),
                // Values may contain credentials. Publish identities, never dictionaries.
                propertiesHash = CanonicalIdentity.Set("completed-test-globals-v1", node.GlobalProperties.Select(p =>
                    CanonicalIdentity.Tuple(p.Key.ToUpperInvariant(), p.Value))),
                contextId = context.ContextId, test = binding.IsTestProject,
                includeTests = context.ExclusionPolicy.IncludeTests, includeGenerated = context.ExclusionPolicy.IncludeGenerated,
                language = context.LanguageVersion.ToString(), sourceKind = context.SourceKind.ToString(),
                symbolsHash = CanonicalIdentity.Set("completed-test-symbols-v1", context.PreprocessorSymbols),
                output = PathKey(context.EvaluatedTargetPath!),
                intermediate = PathKey(context.EvaluatedIntermediateDirectory!),
                referenceOutput = PathKey(context.EvaluatedReferenceAssemblyPath!)
            };
        }).ToArray();
        var runners = plan.Tests.Select(r =>
        {
            token.ThrowIfCancellationRequested();
            var node = nodes.Single(n => n.Id == r.GraphId);
            if (!PathIdentityPolicy.Current.Comparer.Equals(node.ProjectPath, r.ProjectPath) ||
                r.Configuration != node.Configuration || r.Platform != node.Platform || r.TargetFramework != node.TargetFramework ||
                r.Runner != "VSTest" || r.TestSdkVersion != "18.0.1" || r.CollectorVersion != "6.0.4") throw Refuse();
            return new { id = r.GraphId, project = PathKey(r.ProjectPath), configuration = r.Configuration,
                platform = r.Platform, framework = r.TargetFramework, runner = r.Runner,
                testSdk = r.TestSdkVersion, collector = r.CollectorVersion };
        }).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { provider = Provider, workspace = PathKey(root),
            selectedRoot = PathKey(selector), graph, runners, completedTestsAttested = false, reusableRecipe = false });
        if (bytes.Length > MaximumBytes) throw Refuse();
        token.ThrowIfCancellationRequested();
        return bytes;
    }

    internal static void Validate(byte[] bytes, CheckExecutionPlan current, string workspaceRoot,
        string selectedRoot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Exact canonical recomputation accepts no duplicate/unknown fields or saved
        // command strings, and avoids parsing an attacker-controlled recipe tree.
        if (bytes is null || bytes.Length > MaximumBytes ||
            !bytes.AsSpan().SequenceEqual(Capture(current, workspaceRoot, selectedRoot, token))) throw Refuse();
        token.ThrowIfCancellationRequested();
    }

    private static bool Inside(string root, string path) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string PathKey(string path) => CanonicalIdentity.Tuple("completed-test-plan-path-v1",
        OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path));
    private static PolicyException Refuse() => new("provenance.completedTestPlanMismatch",
        "Saved required-test plan differs from the independently evaluated current root, graph or runner schedule.");
}
