using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Crap4CSharp.Core;

internal sealed record FreshCapturedBundle(RunManifest Manifest, IReadOnlyDictionary<string, ImmutableArray<byte>> Bytes);

// Capture from already accepted private execution. No raw command properties/binlogs,
// inferred warm builds, runtime-load attestation or reusable membership recipe.
internal static class FreshManifestCapture
{
    internal static async Task<FreshCapturedBundle> CaptureAsync(FreshExecutionEvidence execution, CancellationToken cancellation)
    {
        var preflight = execution.Preflight;
        if (execution.Coverage is null || execution.Tests.Failure is not null || execution.Compiler.RetainedProof is null)
            throw Refuse();
        await RegateAsync(execution, cancellation);
        var graph = execution.Compiler.FinalGraph;
        var bindings = graph.GraphContexts.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
        var observations = execution.Compiler.Observations.ToDictionary(o => o.ProjectPath, PathIdentityPolicy.Current.Comparer);
        var bytes = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        var artifacts = new List<ManifestArtifact>();
        var contexts = new List<ManifestContext>(); var builds = new List<ManifestBuild>();
        var executions = new List<ManifestExecution>();
        long readTotal = 0;
        string Store(ImmutableArray<byte> content)
        {
            cancellation.ThrowIfCancellationRequested();
            var locator = "inputs/" + CanonicalIdentity.Sha256(content.AsSpan()) + ".bin";
            bytes.TryAdd(locator, content);
            if (bytes.Values.Sum(v => (long)v.Length) > 192 * 1024 * 1024) throw Refuse();
            return locator;
        }
        ManifestInput Input(string role, string logical, ImmutableArray<byte> content, bool generated = false, string? repository = null)
            => new(role, CanonicalIdentity.NormalizeLogicalPath(logical), Store(content), content.Length,
                CanonicalIdentity.Sha256(content.AsSpan()), role is "source" or "compiler-source" ? "utf-8" : null, generated)
                { RepositoryPath = repository };
        void Artifact(string id, string kind, ImmutableArray<byte> content, string? context = null, string? build = null,
            string? invocation = null, string? format = null, string? coordinates = null)
            => artifacts.Add(new(id, kind, Store(content), content.Length, CanonicalIdentity.Sha256(content.AsSpan()),
                context, build, invocation, format, coordinates));
        string RepositoryPath(string physical)
        {
            var relative = Path.GetRelativePath(preflight.Root, physical).Replace('\\', '/');
            return CanonicalIdentity.NormalizeLogicalPath(relative);
        }
        Artifact("prior-compiler-graph", FreshPriorBuild.Kind, FreshPriorBuild.Capture(execution, cancellation));
        Artifact("completed-test-plan", CompletedTestPlanRecipe.Kind, ImmutableArray.Create(
            CompletedTestPlanRecipe.Capture(execution.Plan, preflight.Root,
                ProjectTargetSelector.Select(preflight.Root, preflight.Request.Project), cancellation)), format: "json");
        // Save every required invocation independently of its reachability to a scored
        // production context. Production-linked replay executions below remain intact.
        var requiredIds = execution.Plan.Tests.Select(t => t.GraphId).ToArray();
        if (!requiredIds.SequenceEqual(execution.Tests.Select(t => t.GraphId)) ||
            !requiredIds.SequenceEqual(execution.Coverage.Invocations.Select(t => t.GraphId))) throw Refuse();
        var requiredTests = new List<object>();
        foreach (var invocation in execution.Tests)
        {
            cancellation.ThrowIfCancellationRequested();
            var binding = bindings[invocation.GraphId];
            if (!binding.IsTestProject) throw Refuse();
            var observed = observations[invocation.ProjectPath];
            var boundResult = TestResultBinding.ValidateSuccessfulTrx(invocation, binding.Context, observed, cancellation);
            var pe = CompilerOutputBinding.Read(observed.Assembly!.Path, ref readTotal, cancellation);
            var pdb = CompilerOutputBinding.Read(observed.Pdb!.Path, ref readTotal, cancellation);
            CompilerOutputBinding.Match(observed.Assembly, pe); CompilerOutputBinding.Match(observed.Pdb, pdb);
            var inspected = ArtifactEvidenceInspector.InspectBuild(pe, pdb);
            var trx = TestResultBinding.ReadTrx(Path.Combine(invocation.ResultsDirectory, "tests.trx"), cancellation);
            if (CanonicalIdentity.Sha256(trx.AsSpan()) != boundResult.TrxSha256) throw Refuse();
            var child = TestChildEvidenceExport.Capture(invocation,
                execution.Tests.Outcomes.Single(o => o.GraphId == invocation.GraphId), cancellation);
            TestChildEvidenceExport.ValidateSaved(child, invocation.GraphId, cancellation);
            var id = "required-test-" + invocation.GraphId;
            Artifact(id + "-trx", "required-test-result", trx, format: "trx");
            Artifact(id + "-pe", "required-test-assembly", pe);
            Artifact(id + "-pdb", "required-test-pdb", pdb);
            Artifact(id + "-child", "required-test-child", child, format: "json");
            var report = execution.Coverage.Invocations.Single(r => r.GraphId == invocation.GraphId);
            var modules = report.Modules.Select(module =>
            {
                var moduleId = id + "-coverage-" + module.GraphId;
                Artifact(moduleId, "required-test-module-coverage", module.LineProjection, format: "opencover", coordinates: "line");
                return new { graphId = module.GraphId, artifact = moduleId };
            }).ToArray();
            requiredTests.Add(new { graphId = invocation.GraphId, project = RepositoryPath(invocation.ProjectPath),
                trx = id + "-trx", assembly = id + "-pe", pdb = id + "-pdb", child = id + "-child",
                moduleIdentity = inspected.ModuleIdentity, mvid = inspected.Mvid, debugIdentity = inspected.DebugIdentity,
                passed = boundResult.Passed, rawExitCode = invocation.Process.ExitCode, modules });
        }
        var inventoryBytes = JsonSerializer.SerializeToUtf8Bytes(new
            { provider = "finite-sdk-required-test-inventory-v1", tests = requiredTests, reusableRecipe = false });
        _ = SavedRequiredTestInventory.Parse(inventoryBytes, requiredIds, cancellation);
        Artifact("required-test-inventory", "required-test-inventory", ImmutableArray.Create(inventoryBytes), format: "json");
        foreach (var binding in graph.GraphContexts.Where(b => !b.IsTestProject || b.Context.ExclusionPolicy.IncludeTests))
        {
            cancellation.ThrowIfCancellationRequested();
            var context = binding.Context; var observation = observations[binding.ProjectPath];
            CompilerOutputBinding.Validate(context, observation, cancellation);
            var assembly = CompilerOutputBinding.Read(observation.Assembly!.Path, ref readTotal, cancellation);
            var pdb = CompilerOutputBinding.Read(observation.Pdb!.Path, ref readTotal, cancellation);
            CompilerOutputBinding.Match(observation.Assembly, assembly); CompilerOutputBinding.Match(observation.Pdb, pdb);
            var inspected = ArtifactEvidenceInspector.InspectBuild(assembly, pdb);
            var pdbPaths = inspected.Documents.Keys.Select(p => Path.GetFullPath(p, Path.GetDirectoryName(binding.ProjectPath)!))
                .ToHashSet(PathIdentityPolicy.Current.Comparer);
            var inputs = new List<ManifestInput>();
            foreach (var source in context.CompilerDocuments)
            {
                var content = CompilerOutputBinding.Read(source.ResolvedPath!, ref readTotal, cancellation);
                if (CanonicalIdentity.Sha256(content.AsSpan()) != source.ContentIdentity) throw Refuse();
                _ = CapturedSource.Create(source.LogicalPath, content.AsSpan());
                // A valid Csc source need not have a PDB document. Preserve it in the
                // closure without inventing a document or asking replay to bind it.
                inputs.Add(Input(pdbPaths.Contains(source.ResolvedPath!) ? "source" : "compiler-source",
                    source.LogicalPath, content, source.IsGenerated, RepositoryPath(source.ResolvedPath!)));
            }
            foreach (var reference in observation.Parameters.Single(p => p.Name == "References").Items.Distinct(PathIdentityPolicy.Current.Comparer))
            {
                var content = CompilerOutputBinding.Read(reference, ref readTotal, cancellation);
                inputs.Add(Input("reference", "references/" + CanonicalIdentity.Sha256(content.AsSpan()) + ".dll", content));
            }
            var phaseIdentities = new List<object>();
            foreach (var phase in context.PhaseInputs.Where(i => i.Role != "source"))
            {
                if (!File.Exists(phase.Path)) continue; // absent probes remain a non-reusable private phase obligation
                var content = CompilerOutputBinding.Read(phase.Path, ref readTotal, cancellation);
                var relative = Path.GetRelativePath(preflight.Root, phase.Path).Replace('\\', '/');
                var inside = relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
                var logical = inside ? relative : "external/" + CanonicalIdentity.Tuple("phase-path", phase.Role, phase.Path);
                // Project/import/restore/config properties can contain secrets. Retain
                // independently observed byte identities, never raw metadata XML/properties.
                phaseIdentities.Add(new { role = phase.Role, logicalPath = logical, length = content.Length,
                    sha256 = CanonicalIdentity.Sha256(content.AsSpan()), generated = phase.ExpectedGenerated });
            }
            inputs = inputs.GroupBy(i => (i.Role, i.LogicalPath)).Select(g =>
            {
                if (g.Select(i => i.Sha256).Distinct(StringComparer.Ordinal).Count() != 1) throw Refuse();
                return g.First();
            }).ToList();
            var syntax = context.CompilerDocuments.Select(d => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                CapturedSource.Create(d.LogicalPath, bytes[inputs.Single(i => i.LogicalPath == d.LogicalPath && i.Role is "source" or "compiler-source").Locator].AsSpan()).Text,
                context.ParseOptions, d.LogicalPath)).ToArray();
            var metadataReferences = inputs.Where(i => i.Role == "reference").Select(i =>
                Microsoft.CodeAnalysis.MetadataReference.CreateFromImage(bytes[i.Locator])).ToArray();
            var analysis = new CallableAnalysisContext(RepositoryPath(binding.ProjectPath), context.TargetFramework, context.Configuration,
                context.Platform, binding.GraphId, context.ParseOptions);
            foreach (var source in inputs.Where(i => i.Role == "compiler-source" && (!i.Generated || context.ExclusionPolicy.IncludeGenerated)))
                if (CallableInventory.Analyze(CapturedSource.Create(source.LogicalPath, bytes[source.Locator].AsSpan()).Text,
                    source.LogicalPath, analysis, metadataReferences, syntax).Callables.Any(c => c.Applicability == CallableApplicability.Applicable))
                    throw new PolicyException("coverage.pdbUnavailable", "An authored callable has no independently bound PDB document; it cannot be silently omitted or labelled N/A.");
            var compilerSources = inputs.Where(i => i.Role is "source" or "compiler-source")
                .Select(i => CapturedSource.Create(i.LogicalPath, bytes[i.Locator].AsSpan())).ToArray();
            var eligibleSources = inputs.Where(i => (i.Role is "source" or "compiler-source") && (!i.Generated || context.ExclusionPolicy.IncludeGenerated))
                .Select(i => CapturedSource.Create(i.LogicalPath, bytes[i.Locator].AsSpan())).ToArray();
            var semanticEmpty = !SemanticEligibility.HasApplicable(eligibleSources, compilerSources, analysis, metadataReferences);
            var captured = new ManifestContext(binding.GraphId, RepositoryPath(binding.ProjectPath), context.TargetFramework,
                context.Configuration, context.Platform, "", "", "", true, false, inputs)
            {
                CoverageApplicability = semanticEmpty ? "semantic-empty" : null,
                IncludeGenerated = context.ExclusionPolicy.IncludeGenerated,
                ParseOptions = new(inspected.LanguageVersion, context.SourceKind.ToString(), inspected.PreprocessorSymbols,
                    context.ParseOptions.Features.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)),
                PathPolicy = new(OperatingSystem.IsWindows() ? "insensitive" : "sensitive",
                    context.CompilerDocuments.Select(d => new ManifestReportRootMapping(d.ResolvedPath!, d.LogicalPath)).ToArray())
            };
            contexts.Add(captured);
            var buildId = "build-" + binding.GraphId;
            Artifact(buildId + "-closure", "compiler-input-closure", ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(new
                { provider = "validated-csc-inputs-v1", metadata = phaseIdentities, reusableRecipe = false })), binding.GraphId, buildId, format: "json");
            builds.Add(new(buildId, binding.GraphId, inspected.ModuleIdentity, CanonicalIdentity.Sha256(assembly.AsSpan()),
                inspected.Mvid, CanonicalIdentity.Sha256(pdb.AsSpan()), inspected.DebugIdentity));
            Artifact(buildId + "-pe", "assembly", assembly, binding.GraphId, buildId);
            Artifact(buildId + "-pdb", "pdb", pdb, binding.GraphId, buildId);
            bool Reaches(string id, HashSet<string> seen)
            {
                if (id == binding.GraphId) return true;
                if (!seen.Add(id)) return false;
                return graph.BuildGraph.Single(n => n.Id == id).Dependencies.Any(d => Reaches(d, seen));
            }
            foreach (var report in execution.Coverage.Invocations.Where(r => Reaches(r.GraphId, new(StringComparer.Ordinal))))
            {
                var invocation = execution.Tests.Single(t => t.GraphId == report.GraphId);
                var testContext = bindings[report.GraphId].Context; var testObservation = observations[invocation.ProjectPath];
                var testPe = CompilerOutputBinding.Read(testObservation.Assembly!.Path, ref readTotal, cancellation);
                var testPdb = CompilerOutputBinding.Read(testObservation.Pdb!.Path, ref readTotal, cancellation);
                CompilerOutputBinding.Match(testObservation.Assembly, testPe); CompilerOutputBinding.Match(testObservation.Pdb, testPdb);
                var tested = ArtifactEvidenceInspector.InspectBuild(testPe, testPdb);
                var trx = TestResultBinding.ReadTrx(Path.Combine(invocation.ResultsDirectory, "tests.trx"), cancellation);
                var counters = ArtifactEvidenceInspector.InspectTrx(trx);
                var id = "test-" + binding.GraphId + "-" + report.GraphId;
                executions.Add(new(id, binding.GraphId, buildId, true, invocation.Process.ExitCode,
                    counters.Total, counters.Passed, counters.Failed, counters.Skipped)
                {
                    TestProject = RepositoryPath(invocation.ProjectPath), TestModuleIdentity = tested.ModuleIdentity,
                    TestAssemblySha256 = CanonicalIdentity.Sha256(testPe.AsSpan()), TestMvid = tested.Mvid,
                    TestPdbSha256 = CanonicalIdentity.Sha256(testPdb.AsSpan()), TestDebugIdentity = tested.DebugIdentity
                });
                Artifact(id + "-trx", "test-result", trx, binding.GraphId, buildId, id, "trx");
                Artifact(id + "-pe", "test-assembly", testPe, binding.GraphId, buildId, id);
                Artifact(id + "-pdb", "test-pdb", testPdb, binding.GraphId, buildId, id);
                if (!semanticEmpty)
                    Artifact(id + "-coverage", "coverage", report.Modules.Single(m => m.GraphId == binding.GraphId).LineProjection,
                        binding.GraphId, buildId, id, "opencover", "line");
                Artifact(id + "-child", "test-child-build", TestChildEvidenceExport.Capture(invocation,
                    execution.Tests.Outcomes.Single(o => o.GraphId == report.GraphId), cancellation), binding.GraphId, buildId, id, "json");
            }
        }
        // Preserve required test Csc inputs too, without making excluded tests scored
        // manifest contexts or publishing their raw project/SDK property dictionaries.
        foreach (var test in graph.GraphContexts.Where(b => b.IsTestProject && !b.Context.ExclusionPolicy.IncludeTests))
        {
            var identities = new List<object>(); var index = 0;
            foreach (var source in test.Context.CompilerDocuments)
            {
                var content = CompilerOutputBinding.Read(source.ResolvedPath!, ref readTotal, cancellation);
                if (CanonicalIdentity.Sha256(content.AsSpan()) != source.ContentIdentity) throw Refuse();
                _ = CapturedSource.Create(source.LogicalPath, content.AsSpan());
                var locator = Store(content);
                Artifact("test-source-" + test.GraphId + "-" + index++, "test-compiler-source", content);
                identities.Add(new { role = "compiler-source", logicalPath = source.LogicalPath, locator,
                    sha256 = source.ContentIdentity, length = content.Length, generated = source.IsGenerated });
            }
            foreach (var reference in observations[test.ProjectPath].Parameters.Single(p => p.Name == "References").Items.Distinct(PathIdentityPolicy.Current.Comparer))
            {
                var content = CompilerOutputBinding.Read(reference, ref readTotal, cancellation);
                var hash = CanonicalIdentity.Sha256(content.AsSpan()); var locator = Store(content);
                Artifact("test-reference-" + test.GraphId + "-" + index++, "test-compiler-reference", content);
                identities.Add(new { role = "reference", logicalPath = "references/" + hash + ".dll", locator,
                    sha256 = hash, length = content.Length });
            }
            foreach (var phase in test.Context.PhaseInputs.Where(p => p.Role != "source" && File.Exists(p.Path)))
            {
                var content = CompilerOutputBinding.Read(phase.Path, ref readTotal, cancellation);
                identities.Add(new { role = phase.Role, logicalPath = "metadata/" + CanonicalIdentity.Tuple("phase-path", phase.Role, phase.Path),
                    sha256 = CanonicalIdentity.Sha256(content.AsSpan()), length = content.Length });
            }
            Artifact("test-closure-" + test.GraphId, "test-compiler-input-closure", ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(new
                { provider = "validated-csc-inputs-v1", project = RepositoryPath(test.ProjectPath), inputs = identities, reusableRecipe = false })), format: "json");
        }
        var available = contexts.SelectMany(c => c.Inputs.Where(i => i.Role == "source" && (!i.Generated || c.IncludeGenerated))
            .Select(i => i.RepositoryPath!)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var selected = preflight.Scope is null || preflight.Scope.Completeness == ScopeCompleteness.ContextIncomplete ? available :
            preflight.Scope.Files.Select(f => f.NewPath).Where(p => p is not null && available.Contains(p, StringComparer.Ordinal))
                .Select(p => p!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var scopeBytes = ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(new { version = 1, sources = selected,
            sourceState = "worktree", granularity = preflight.Request.Granularity, requestedScope = preflight.Policy.Scope,
            revision = preflight.GitSnapshot?.Revision }));
        // Tiny replay policy is effective threshold only; base policy/overlays are independently
        // retained and the caller must run the pure trusted policy gate, not trust replay's verdict.
        var policyBytes = ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(new { version = 1,
            threshold = preflight.Policy.Threshold, allowMissingCoverage = false }));
        Artifact("scope", "scope", scopeBytes, format: "json"); Artifact("policy", "policy", policyBytes, format: "json");
        Artifact("trusted-policy", "trusted-policy", ImmutableArray.Create(preflight.Trusted.Policy.CanonicalBytes), format: "json");
        foreach (var exemption in preflight.Trusted.ExemptionBytes)
            Artifact("exemption-" + CanonicalIdentity.Tuple("exemption", exemption.Key), "trusted-exemption", ImmutableArray.Create(exemption.Value), format: "json");
        if (preflight.Trusted.Baseline is { } baseline)
            Artifact("trusted-baseline", "trusted-baseline", ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(baseline)), format: "json");
        var revision = preflight.GitSnapshot?.Revision;
        var stateHash = preflight.GitSnapshot is { } git ? FreshGitProof.Identity(git) :
            FreshLocalProof.Identity(preflight.LocalSnapshot ?? throw new InvalidDataException("Local workspace fingerprint is missing."));
        var manifest = ManifestIdentity.Seal(new(ManifestIdentity.SchemaVersion, CanonicalIdentity.Algorithm,
            new("crap4csharp", typeof(AnalyzeCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
                preflight.Policy.Ruleset, ProjectAnalysisContext.ProtocolVersion, ManifestIdentity.CoverageProtocol, ManifestIdentity.PathProtocol),
            new("completed", true, true, []), new(revision is null ? "none" : "git", CanonicalIdentity.Tuple("fresh-repository", preflight.Root),
                CanonicalIdentity.Tuple("fresh-workspace", preflight.Root), revision?.CurrentHead,
                revision is null ? null : preflight.Trusted.Revision, revision?.ResolvedHead)
                { StateHash = stateHash },
            [new("workspace", "workspace", OperatingSystem.IsWindows() ? "insensitive" : "sensitive")], contexts, builds, executions, artifacts,
            new(CanonicalIdentity.Sha256(scopeBytes.AsSpan()), CanonicalIdentity.Sha256(policyBytes.AsSpan()), null, null), null));
        await RegateAsync(execution, cancellation);
        var valid = ProvenanceVerifier.VerifyCaptureCancellable(manifest, bytes, preflight.Policy.Ruleset, cancellation);
        if (valid.Status != ProvenanceStatus.Captured) throw new PolicyException("check.captureUnproven", string.Join(", ", valid.Reasons));
        return new(manifest, bytes);
    }

    internal static async Task<string> PublishAsync(FreshExecutionEvidence execution, FreshCapturedBundle captured,
        string destination, CancellationToken cancellation)
    {
        if (ProvenanceVerifier.VerifyCaptureCancellable(captured.Manifest, captured.Bytes,
            execution.Preflight.Policy.Ruleset, cancellation).Status != ProvenanceStatus.Captured) throw Refuse();
        var request = execution.Preflight.Request with { ArtifactOutput = destination };
        var preflight = execution.Preflight with { Request = request };
        FreshCheckPreflightLoader.ValidateDestinations(request, preflight.Root,
            preflight.Trusted.ContentHashes.Keys.Concat(preflight.Policy.ProductionProjects).Concat(preflight.Policy.TestProjects));
        FreshCheckExecution.ProtectPreparedDestinations(preflight, execution.Compiler.FinalGraph);
        await RegateAsync(execution, cancellation);
        FreshCheckExecution.ProtectPreparedDestinations(preflight, execution.Compiler.FinalGraph);
        cancellation.ThrowIfCancellationRequested();
        return ArtifactCaptureAdapter.PublishNewCancellable(captured.Manifest, captured.Bytes,
            Path.GetFullPath(destination, preflight.Root), cancellation);
    }

    internal static async Task RegateAsync(FreshExecutionEvidence execution, CancellationToken cancellation)
    {
        await execution.Compiler.RetainedProof!.ValidateFinalAsync(cancellation);
        var graph = execution.Compiler.FinalGraph;
        var footprint = graph.GraphContexts.SelectMany(b =>
            new[] { b.Context.EvaluatedIntermediateDirectory!, Path.GetDirectoryName(b.Context.EvaluatedTargetPath!)! }
                .Concat(b.Context.PhaseInputs.Where(i => i.Role == "restoreAssets").Select(i => Path.GetDirectoryName(i.Path)!)))
            .Distinct(PathIdentityPolicy.Current.Comparer).ToArray();
        await FreshGitProof.ValidateAsync(execution.Preflight, cancellation, footprint);
        _ = CoverageGraphBinding.ValidateAggregate(execution.Tests, execution.Compiler, cancellation);
        foreach (var test in execution.Tests)
            _ = TestChildEvidenceExport.Capture(test, execution.Tests.Outcomes.Single(o => o.GraphId == test.GraphId), cancellation);
    }

    private static PolicyException Refuse() => new("check.captureUnproven", "Fresh capture requires complete unchanged accepted compiler/test/coverage evidence.");
}
