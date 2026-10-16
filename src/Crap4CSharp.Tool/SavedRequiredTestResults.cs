using System.Collections.Immutable;

namespace Crap4CSharp.Core;

// Saved successful results only. Compiler must be independently bound CURRENT evidence;
// normalized coverage/eligible union and production-execution agreement are still owed.
// This gate never creates process observations or establishes completed-test reuse.
internal static class SavedRequiredTestResults
{
    internal static IReadOnlyList<SavedRequiredTest> Validate(ArtifactBundle bundle,
        CheckExecutionPlan current, CompilerGraphPhaseResult compiler, string root, CancellationToken token)
        => ValidateWithTiming(bundle, current, compiler, root, token, null, "saved");

    internal static IReadOnlyList<SavedRequiredTest> ValidateWithTiming(ArtifactBundle bundle,
        CheckExecutionPlan current, CompilerGraphPhaseResult compiler, string root, CancellationToken token,
        CompletedProofTiming? timing = null, string boundary = "saved")
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (compiler.RetainedProof is null) throw Refuse();
            using (timing?.Start(boundary + ".saved.required.structure"))
                if (ProvenanceVerifier.VerifyCaptureCancellable(bundle.Manifest, bundle.Bytes, null, token).Status != ProvenanceStatus.Captured) throw Refuse();
            using (timing?.Start(boundary + ".saved.required.physicalOpen"))
                compiler.RetainedProof.ValidateCurrent(token);
            CompilerGraphExecution.ValidateFinalGraph(current.PreparedGraph, compiler.FinalGraph);
            var planArtifact = bundle.Manifest.Artifacts.Single(a => a.Kind == CompletedTestPlanRecipe.Kind);
            var plan = Read(planArtifact, CompletedTestPlanRecipe.Kind, "json", null);
            if (planArtifact.Id != "completed-test-plan") throw Refuse();
            var selected = CompletedTestPlanRecipe.ResolveSelectedRoot(plan.ToArray(), root, token);
            CompletedTestPlanRecipe.Validate(plan.ToArray(), current, root, selected, token);
            // The independently required runner schedule must also match the compiler's
            // graph, not just graph IDs declared by the saved inventory.
            var boundPlan = CheckExecutionPlan.Create(current.PreparedGraph, compiler.FinalGraph, token);
            CompletedTestPlanRecipe.Validate(plan.ToArray(), boundPlan, root, selected, token);
            var inventoryArtifact = bundle.Manifest.Artifacts.Single(a => a.Kind == "required-test-inventory");
            if (inventoryArtifact.Id != "required-test-inventory") throw Refuse();
            var inventory = SavedRequiredTestInventory.Parse(Read(inventoryArtifact, "required-test-inventory", "json", null).ToArray(),
                current.Tests.Select(t => t.GraphId).ToArray(), token);
            var expected = new HashSet<string>(StringComparer.Ordinal);
            var bindings = compiler.FinalGraph.GraphContexts.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
            var observations = compiler.ByGraphId();
            if (observations.Count != bindings.Count) throw Refuse();
            long total = 0;
            foreach (var saved in inventory)
            {
                token.ThrowIfCancellationRequested();
                var binding = bindings[saved.GraphId];
                if (!binding.IsTestProject || saved.Project != CanonicalIdentity.NormalizeLogicalPath(
                    Path.GetRelativePath(root, binding.ProjectPath).Replace('\\', '/'))) throw Refuse();
                var observed = observations[binding.GraphId];
                CompilerOutputBinding.Validate(binding.Context, observed, token);
                var pe = Artifact(saved.Assembly, "required-test-assembly", null, null);
                var pdb = Artifact(saved.Pdb, "required-test-pdb", null, null);
                var currentPe = CompilerOutputBinding.Read(observed.Assembly!.Path, ref total, token);
                var currentPdb = CompilerOutputBinding.Read(observed.Pdb!.Path, ref total, token);
                CompilerOutputBinding.Match(observed.Assembly, currentPe);
                CompilerOutputBinding.Match(observed.Pdb, currentPdb);
                if (!pe.AsSpan().SequenceEqual(currentPe.AsSpan()) || !pdb.AsSpan().SequenceEqual(currentPdb.AsSpan())) throw Refuse();
                var inspected = ArtifactEvidenceInspector.InspectBuild(pe, pdb);
                if (saved.ModuleIdentity != inspected.ModuleIdentity || saved.Mvid != inspected.Mvid ||
                    saved.DebugIdentity != inspected.DebugIdentity) throw Refuse();
                var trx = Artifact(saved.Trx, "required-test-result", "trx", null);
                if (TestResultBinding.ValidateDocument(trx, binding.Context.EvaluatedTargetPath!, token) != saved.Passed) throw Refuse();
                TestChildEvidenceExport.ValidateSaved(Artifact(saved.Child, "required-test-child", "json", null), saved.GraphId, token);
                foreach (var module in saved.Modules)
                {
                    // Structural artifact identity only. No coverage meaning or
                    // reachability is inferred here from this saved declaration.
                    _ = Artifact(module.Artifact, "required-test-module-coverage", "opencover", "line");
                }
            }
            var declared = bundle.Manifest.Artifacts.Where(a =>
                (a.Kind.StartsWith("required-test-", StringComparison.Ordinal) && a.Kind != "required-test-inventory") ||
                (a.Id.StartsWith("required-test-", StringComparison.Ordinal) && a.Id != "required-test-inventory")).Select(a => a.Id).ToArray();
            if (declared.Length != expected.Count || !expected.SetEquals(declared)) throw Refuse();
            using (timing?.Start(boundary + ".saved.required.physicalClose"))
                compiler.RetainedProof.ValidateCurrent(token);
            token.ThrowIfCancellationRequested();
            return inventory;

            ImmutableArray<byte> Artifact(string id, string kind, string? format, string? coordinates)
            {
                if (!expected.Add(id)) throw Refuse();
                return Read(bundle.Manifest.Artifacts.Single(a => a.Id == id), kind, format, coordinates);
            }
            ImmutableArray<byte> Read(ManifestArtifact artifact, string kind, string? format, string? coordinates)
            {
                token.ThrowIfCancellationRequested();
                if (artifact.Kind != kind || artifact.Format != format || artifact.CoordinateKind != coordinates ||
                    artifact.ContextId is not null || artifact.BuildId is not null || artifact.ExecutionId is not null ||
                    !bundle.Bytes.TryGetValue(artifact.Locator, out var bytes) || bytes.IsDefaultOrEmpty ||
                    bytes.Length != artifact.Length || CanonicalIdentity.Sha256(bytes.AsSpan()) != artifact.Sha256) throw Refuse();
                return bytes;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is ProjectContextException or PolicyException or IOException or
            UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw Refuse(); }
    }

    private static PolicyException Refuse() => new("provenance.savedRequiredResultsUnproven",
        "Saved required-test artifacts, successful TRX/child or test module identities differ from independently bound current compiler evidence.");
}
