using System.Collections.Immutable;

namespace Crap4CSharp.Core;

// Production-linked execution agreement only. Context input/parse/semantic agreement,
// final rediscovery and live workspace selection remain separate obligations.
internal static class SavedProductionExecutions
{
    internal static void Validate(ArtifactBundle bundle, CheckExecutionPlan current,
        CompilerGraphPhaseResult compiler, string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var required = SavedRequiredTestResults.Validate(bundle, current, compiler, root, token);
            var coverage = CoverageGraphBinding.ValidateSavedNormalized(bundle, current, compiler, root, token);
            var nodes = CompilerGraphExecution.ValidateAndFreeze(compiler.FinalGraph).ToDictionary(n => n.Id, StringComparer.Ordinal);
            var bindings = compiler.FinalGraph.GraphContexts.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
            var observed = compiler.Observations.ToDictionary(o => o.ProjectPath, PathIdentityPolicy.Current.Comparer);
            var production = bindings.Values.Where(b => !b.IsTestProject || b.Context.ExclusionPolicy.IncludeTests).ToArray();
            if (bundle.Manifest.Contexts.Count != production.Length ||
                !bundle.Manifest.Contexts.Select(c => c.Id).ToHashSet(StringComparer.Ordinal).SetEquals(production.Select(b => b.GraphId)) ||
                bundle.Manifest.Builds.Count != production.Length) throw Refuse();
            var expectedExecutions = new HashSet<string>(StringComparer.Ordinal);
            var expectedArtifacts = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var binding in production)
            {
                token.ThrowIfCancellationRequested();
                var context = bundle.Manifest.Contexts.Single(c => c.Id == binding.GraphId);
                if (context.Project != Logical(binding.ProjectPath) || context.Configuration != binding.Context.Configuration ||
                    context.TargetFramework != binding.Context.TargetFramework || context.Platform != binding.Context.Platform) throw Refuse();
                var buildId = "build-" + binding.GraphId;
                var build = bundle.Manifest.Builds.Single(b => b.ContextId == binding.GraphId);
                var observation = observed[binding.ProjectPath];
                CompilerOutputBinding.Validate(binding.Context, observation, token);
                var pe = CompilerOutputBinding.Read(observation.Assembly!.Path, ref total, token);
                var pdb = CompilerOutputBinding.Read(observation.Pdb!.Path, ref total, token);
                CompilerOutputBinding.Match(observation.Assembly, pe); CompilerOutputBinding.Match(observation.Pdb, pdb);
                var inspected = ArtifactEvidenceInspector.InspectBuild(pe, pdb);
                if (build.Id != buildId || build.ModuleIdentity != inspected.ModuleIdentity || build.Mvid != inspected.Mvid ||
                    build.DebugIdentity != inspected.DebugIdentity || build.AssemblySha256 != CanonicalIdentity.Sha256(pe.AsSpan()) ||
                    build.PdbSha256 != CanonicalIdentity.Sha256(pdb.AsSpan())) throw Refuse();
                Match(buildId + "-pe", "assembly", pe, null, null, null);
                Match(buildId + "-pdb", "pdb", pdb, null, null, null);
                // Closure metadata is not executable XML and is not a reusable input recipe.
                Read(buildId + "-closure", "compiler-input-closure", null, "json", null);
                foreach (var saved in required.Where(t => Reaches(t.GraphId, binding.GraphId, new(StringComparer.Ordinal))))
                {
                    var id = "test-" + binding.GraphId + "-" + saved.GraphId;
                    if (!expectedExecutions.Add(id)) throw Refuse();
                    var execution = bundle.Manifest.Executions.Single(e => e.Id == id);
                    var trx = Bytes(saved.Trx); var testPe = Bytes(saved.Assembly); var testPdb = Bytes(saved.Pdb);
                    var counters = ArtifactEvidenceInspector.InspectTrx(trx);
                    if (execution.ContextId != binding.GraphId || execution.BuildId != buildId || !execution.Completed || execution.ExitCode != 0 ||
                        execution.TotalTests != counters.Total || execution.PassedTests != counters.Passed || execution.FailedTests != counters.Failed ||
                        execution.SkippedTests != counters.Skipped || execution.TestProject != saved.Project || execution.TestModuleIdentity != saved.ModuleIdentity ||
                        execution.TestMvid != saved.Mvid || execution.TestDebugIdentity != saved.DebugIdentity ||
                        execution.TestAssemblySha256 != CanonicalIdentity.Sha256(testPe.AsSpan()) ||
                        execution.TestPdbSha256 != CanonicalIdentity.Sha256(testPdb.AsSpan())) throw Refuse();
                    Match(id + "-trx", "test-result", trx, id, "trx", null);
                    Match(id + "-pe", "test-assembly", testPe, id, null, null);
                    Match(id + "-pdb", "test-pdb", testPdb, id, null, null);
                    Match(id + "-child", "test-child-build", Bytes(saved.Child), id, "json", null);
                    var module = coverage.Invocations.Single(i => i.GraphId == saved.GraphId).Modules.SingleOrDefault(m => m.GraphId == binding.GraphId);
                    if (context.CoverageApplicability == "semantic-empty")
                    {
                        // This marker is independently derived by the captured verifier;
                        // it is not sufficient to establish current context agreement.
                        if (module is not null) throw Refuse();
                    }
                    else
                    {
                        if (module is null) throw Refuse();
                        Match(id + "-coverage", "coverage", module.LineProjection, id, "opencover", "line");
                    }
                }
                ImmutableArray<byte> Read(string id, string kind, string? execution, string? format, string? coordinates)
                {
                    if (!expectedArtifacts.Add(id)) throw Refuse();
                    var artifact = bundle.Manifest.Artifacts.Single(a => a.Id == id);
                    if (artifact.Kind != kind || artifact.ContextId != binding.GraphId || artifact.BuildId != buildId ||
                        artifact.ExecutionId != execution || artifact.Format != format || artifact.CoordinateKind != coordinates) throw Refuse();
                    return Bytes(id);
                }
                void Match(string id, string kind, ImmutableArray<byte> bytes, string? execution, string? format, string? coordinates)
                {
                    if (!Read(id, kind, execution, format, coordinates).AsSpan().SequenceEqual(bytes.AsSpan())) throw Refuse();
                }
            }
            if (bundle.Manifest.Executions.Count != expectedExecutions.Count ||
                !expectedExecutions.SetEquals(bundle.Manifest.Executions.Select(e => e.Id))) throw Refuse();
            var contextual = bundle.Manifest.Artifacts.Where(a => a.ContextId is not null || a.BuildId is not null || a.ExecutionId is not null).ToArray();
            if (contextual.Length != expectedArtifacts.Count || !expectedArtifacts.SetEquals(contextual.Select(a => a.Id))) throw Refuse();
            compiler.RetainedProof!.ValidateCurrent(token);
            token.ThrowIfCancellationRequested();

            string Logical(string path) => CanonicalIdentity.NormalizeLogicalPath(Path.GetRelativePath(root, path).Replace('\\', '/'));
            bool Reaches(string id, string target, HashSet<string> seen)
                => id == target || seen.Add(id) && nodes[id].Dependencies.Any(d => Reaches(d, target, seen));
            ImmutableArray<byte> Bytes(string id)
            {
                token.ThrowIfCancellationRequested();
                var artifact = bundle.Manifest.Artifacts.Single(a => a.Id == id);
                if (!bundle.Bytes.TryGetValue(artifact.Locator, out var bytes) || bytes.IsDefaultOrEmpty || bytes.Length != artifact.Length ||
                    CanonicalIdentity.Sha256(bytes.AsSpan()) != artifact.Sha256) throw Refuse();
                return bytes;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is PolicyException or ProjectContextException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw Refuse(); }
    }

    private static PolicyException Refuse() => new("provenance.savedProductionExecutionsUnproven",
        "Saved production builds, required reachability pairs or linked execution artifacts differ from independently current compiler and required-test evidence.");
}
