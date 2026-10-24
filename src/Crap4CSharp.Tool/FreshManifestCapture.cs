using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Crap4CSharp.Core;

internal sealed record FreshCapturedBundle(RunManifest Manifest, IReadOnlyDictionary<string, ImmutableArray<byte>> Bytes)
{
    internal bool RequiresCompletedProof { get; init; }
    internal FreshCompilerRecipeSelection? OptionalRecipes { get; init; }
}

// Capture from already accepted private execution. No raw command properties/binlogs,
// inferred warm builds or runtime-load attestation. The typed recipe is input DATA,
// not a membership/coverage capability or authorization to load captured code.
internal static class FreshManifestCapture
{
    internal static Task<FreshCapturedBundle> CaptureAsync(FreshExecutionEvidence execution, CancellationToken cancellation,
        CompletedProofTiming? timing = null)
        => CaptureCoreAsync(execution, cancellation, timing, provisional: false);

    // Only the live completed path may prepare provisional bytes. Staging still
    // regates, and final/outer completed validation must prove those exact bytes.
    internal static Task<FreshCapturedBundle> PrepareCompletedAsync(FreshExecutionEvidence execution, CancellationToken cancellation,
        CompletedProofTiming? timing = null)
        => CaptureCoreAsync(execution, cancellation, timing, provisional: true);

    private static async Task<FreshCapturedBundle> CaptureCoreAsync(FreshExecutionEvidence execution, CancellationToken cancellation,
        CompletedProofTiming? timing, bool provisional)
    {
        var preflight = execution.Preflight;
        if (execution.Coverage is null || execution.Tests.Failure is not null || execution.Compiler.RetainedProof is null)
            throw Refuse();
        if (provisional)
        {
            if (execution.Tests.Count != execution.Plan.Tests.Count || execution.Tests.Any(test => test.Process.ExitCode != 0))
                throw Refuse();
            using (timing?.Start("public.capture.originalPhysicalBefore"))
                execution.Compiler.RetainedProof.ValidateCurrent(cancellation);
        }
        else
            using (timing?.Start("public.capture.initialRegate"))
                await RegateAsync(execution, cancellation, timing, "public.capture.initialRegate");
        var graph = execution.Compiler.FinalGraph;
        var bindings = graph.GraphContexts.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
        var logicalIds = graph.BuildGraph.ToDictionary(n => n.Id, n => LogicalContextIdentity.Create(preflight.Root, n), StringComparer.Ordinal);
        var prior = FreshPriorBuild.Prepare(execution, cancellation);
        var observations = prior.Observations.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var referenceEdges = CompilerReferenceGraph.Observe(graph, cancellation);
        var bytes = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        var artifacts = new List<ManifestArtifact>();
        var contexts = new List<ManifestContext>(); var builds = new List<ManifestBuild>();
        var executions = new List<ManifestExecution>();
        long readTotal = 0;
        var optional = new List<Action>();
        var mandatoryPriorLength = 0; var optionalPriorBytes = 0;
        var optionalReadsExhausted = false;
        // This synchronous construction still rereads every file. Check shared
        // ancestors once on entry and again before the independent closing proof.
        var readPaths = new CompilerPathBatch(cancellation, RefuseReadPath);
        ImmutableArray<byte> ReadCaptured(string path, ref long total, CancellationToken token)
            => CompilerOutputBinding.Read(path, ref total, token, readPaths);
        static ProjectContextException RefuseReadPath() => new("compiler.outputsUnproven",
            "A compiler input path changed during capture construction.");
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
        bool AdmitOptional(CompilerObservation candidate, IReadOnlyList<(string Id, string Kind, ImmutableArray<byte> Bytes)> staged,
            out int priorBytes)
        {
            if (!FreshPriorBuild.TryOptionalContribution(candidate, preflight.Root, cancellation, out priorBytes) ||
                priorBytes > FreshPriorBuild.MaximumOptionalBytes - optionalPriorBytes) return false;
            var extra = staged.Select(item => item.Bytes).GroupBy(content => CanonicalIdentity.Sha256(content.AsSpan()), StringComparer.Ordinal)
                .Where(group => !bytes.ContainsKey("inputs/" + group.Key + ".bin")).Sum(group => (long)group.First().Length);
            return bytes.Values.Sum(content => (long)content.Length) + extra + mandatoryPriorLength + optionalPriorBytes + priorBytes <= 192L * 1024 * 1024;
        }
        string RepositoryPath(string physical)
        {
            var relative = Path.GetRelativePath(preflight.Root, physical).Replace('\\', '/');
            return CanonicalIdentity.NormalizeLogicalPath(relative);
        }
        if (preflight.GitSnapshot is null)
            Artifact(SavedLocalMembership.Kind, SavedLocalMembership.Kind, ImmutableArray.Create(
                SavedLocalMembership.Capture(preflight.LocalSnapshot ?? throw Refuse(), cancellation)), format: "json");
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
            var observed = observations[invocation.GraphId];
            var boundResult = TestResultBinding.ValidateSuccessfulTrx(invocation, binding.Context, observed, cancellation);
            var pe = ReadCaptured(observed.Assembly!.Path, ref readTotal, cancellation);
            var pdb = ReadCaptured(observed.Pdb!.Path, ref readTotal, cancellation);
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
            requiredTests.Add(new
            {
                graphId = invocation.GraphId,
                project = RepositoryPath(invocation.ProjectPath),
                trx = id + "-trx",
                assembly = id + "-pe",
                pdb = id + "-pdb",
                child = id + "-child",
                moduleIdentity = inspected.ModuleIdentity,
                mvid = inspected.Mvid,
                debugIdentity = inspected.DebugIdentity,
                passed = boundResult.Passed,
                rawExitCode = invocation.Process.ExitCode,
                modules
            });
        }
        var inventoryBytes = JsonSerializer.SerializeToUtf8Bytes(new
        { provider = "finite-sdk-required-test-inventory-v1", tests = requiredTests, reusableRecipe = false });
        _ = SavedRequiredTestInventory.Parse(inventoryBytes, requiredIds, cancellation);
        Artifact("required-test-inventory", "required-test-inventory", ImmutableArray.Create(inventoryBytes), format: "json");
        foreach (var binding in graph.GraphContexts.Where(b => !b.IsTestProject || b.Context.ExclusionPolicy.IncludeTests))
        {
            cancellation.ThrowIfCancellationRequested();
            var contextId = logicalIds[binding.GraphId];
            var context = binding.Context; var observation = observations[binding.GraphId];
            CompilerOutputBinding.Validate(context, observation, cancellation);
            var assembly = ReadCaptured(observation.Assembly!.Path, ref readTotal, cancellation);
            var pdb = ReadCaptured(observation.Pdb!.Path, ref readTotal, cancellation);
            CompilerOutputBinding.Match(observation.Assembly, assembly); CompilerOutputBinding.Match(observation.Pdb, pdb);
            var inspected = ArtifactEvidenceInspector.InspectBuild(assembly, pdb);
            var documentBinding = CompilerDocumentBinding.Create(context, observation, inspected, cancellation);
            var pdbPaths = documentBinding.PhysicalPathsWithPdb;
            var inputs = new List<ManifestInput>();
            var recipeSources = new Dictionary<string, ManifestInput>(PathIdentityPolicy.Current.Comparer);
            var recipeReferences = new List<ManifestInput>();
            foreach (var source in context.CompilerDocuments)
            {
                var content = ReadCaptured(source.ResolvedPath!, ref readTotal, cancellation);
                if (CanonicalIdentity.Sha256(content.AsSpan()) != source.ContentIdentity) throw Refuse();
                _ = CapturedSource.Create(source.LogicalPath, content.AsSpan());
                // A valid Csc source need not have a PDB document. Preserve it in the
                // closure without inventing a document or asking replay to bind it.
                var input = Input(pdbPaths.Contains(source.ResolvedPath!) ? "source" : "compiler-source",
                    source.LogicalPath, content, source.IsGenerated, RepositoryPath(source.ResolvedPath!));
                inputs.Add(input); recipeSources.Add(Path.GetFullPath(source.ResolvedPath!), input);
            }
            foreach (var reference in CompilerReferenceSnapshot.ResolveSemanticReferences(observation, cancellation))
            {
                var content = ReadCaptured(reference.Path, ref readTotal, cancellation);
                var input = Input("reference", CompilerReferenceSnapshot.LogicalReference(CanonicalIdentity.Sha256(content.AsSpan()),
                    reference.Aliases, reference.EmbedInteropTypes), content) with
                { ReferenceAliases = reference.Aliases, EmbedInteropTypes = reference.EmbedInteropTypes ? true : null };
                inputs.Add(input); recipeReferences.Add(input);
            }
            var phaseIdentities = new List<object>();
            foreach (var phase in context.PhaseInputs.Where(i => i.Role != "source"))
            {
                if (!File.Exists(phase.Path)) continue; // absent probes remain a non-reusable private phase obligation
                var content = ReadCaptured(phase.Path, ref readTotal, cancellation);
                var relative = Path.GetRelativePath(preflight.Root, phase.Path).Replace('\\', '/');
                var inside = relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
                var logical = inside ? relative : "external/" + CanonicalIdentity.Tuple("phase-path", phase.Role, phase.Path);
                // Project/import/restore/config properties can contain secrets. Retain
                // independently observed byte identities, never raw metadata XML/properties.
                phaseIdentities.Add(new
                {
                    role = phase.Role,
                    logicalPath = logical,
                    length = content.Length,
                    sha256 = CanonicalIdentity.Sha256(content.AsSpan()),
                    generated = phase.ExpectedGenerated
                });
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
                CompilerReferenceSnapshot.CapturedReference(i, bytes[i.Locator])).ToArray();
            var analysis = new CallableAnalysisContext(RepositoryPath(binding.ProjectPath), context.TargetFramework, context.Configuration,
                context.Platform, contextId, context.ParseOptions);
            // This compilation belongs only to these captured bytes and this
            // context. Later current boundaries rebuild their own compilation.
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "Crap4CSharp.CallableInventory", syntax, metadataReferences);
            var inventories = new Dictionary<string, CallableInventoryResult>(StringComparer.Ordinal);
            CallableInventoryResult Inventory(CapturedSource source)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!inventories.TryGetValue(source.LogicalPath, out var inventory))
                    inventories.Add(source.LogicalPath, inventory = CallableInventory.AnalyzeCaptured(source, analysis, compilation));
                return inventory;
            }
            foreach (var source in inputs.Where(i => i.Role == "compiler-source" && (!i.Generated || context.ExclusionPolicy.IncludeGenerated)))
                if (Inventory(CapturedSource.Create(source.LogicalPath, bytes[source.Locator].AsSpan()))
                    .Callables.Any(c => c.Applicability == CallableApplicability.Applicable))
                    throw new PolicyException("coverage.pdbUnavailable", "An authored callable has no independently bound PDB document; it cannot be silently omitted or labelled N/A.");
            var eligibleSources = inputs.Where(i => (i.Role is "source" or "compiler-source") && (!i.Generated || context.ExclusionPolicy.IncludeGenerated))
                .Select(i => CapturedSource.Create(i.LogicalPath, bytes[i.Locator].AsSpan())).ToArray();
            var semanticEmpty = !eligibleSources.Any(source => Inventory(source).Callables
                .Any(callable => callable.Applicability == CallableApplicability.Applicable));
            var captured = new ManifestContext(contextId, RepositoryPath(binding.ProjectPath), context.TargetFramework,
                context.Configuration, context.Platform, "", "", "", true, false, inputs)
            {
                CoverageApplicability = semanticEmpty ? "semantic-empty" : null,
                IncludeGenerated = context.ExclusionPolicy.IncludeGenerated,
                ParseOptions = new(inspected.LanguageVersion, context.SourceKind.ToString(), inspected.PreprocessorSymbols,
                    context.ParseOptions.Features.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)),
                PathPolicy = new(OperatingSystem.IsWindows() ? "insensitive" : "sensitive",
                    documentBinding.ReportMappings)
            };
            contexts.Add(captured);
            var buildId = "build-" + binding.GraphId;
            if (observation.InvocationRecipe is not null)
            {
                // Validate known task/argv/role errors before capacity can omit
                // optional DATA. Emission is deferred until mandatory capture ends.
                var profile = CompilerRecipePayload.Classify(observation, preflight.Root, cancellation);
                optional.Add(() =>
                {
                    if (bytes.Values.Sum(content => (long)content.Length) + mandatoryPriorLength + optionalPriorBytes >= 192L * 1024 * 1024)
                        return; // Even a new status byte cannot fit; do not read optional files.
                    var candidate = observation;
                    var staged = new List<(string Id, string Kind, ImmutableArray<byte> Bytes)>();
                    if (profile.IsSupported && !optionalReadsExhausted)
                    {
                        IReadOnlyList<CompilerRecipePayload.File>? recipeFiles = null;
                        var imagesChanged = false;
                        try
                        {
                            var capturedFiles = CompilerRecipePayload.CaptureFreshFiles(candidate, context, buildId,
                                path => ReadCaptured(path, ref readTotal, cancellation), cancellation);
                            imagesChanged = capturedFiles.CompilerImagesChanged;
                            if (imagesChanged) candidate = CompilerRecipePayload.ImagesChangedUnsupported(candidate, cancellation);
                            else recipeFiles = capturedFiles.Files;
                        }
                        catch (ProjectContextException failure) when (failure.Reason == "compiler.outputLimit")
                        {
                            // The same read ledger is never reset. All mandatory
                            // reads have completed; optional quota failure is DATA.
                            optionalReadsExhausted = true;
                        }
                        if (recipeFiles is not null && CompilerRecipePayload.TryCreate(candidate, contextId, buildId, preflight.Root,
                            recipeSources, recipeReferences, recipeFiles, cancellation, out var recipeBytes))
                        {
                            staged.AddRange(recipeFiles.Select(file => (file.Id, CompilerRecipePayload.FileKind(file), file.Bytes)));
                            staged.Add((buildId + "-recipe", CompilerRecipePayload.Kind, recipeBytes));
                        }
                        else if (!imagesChanged) candidate = CompilerRecipePayload.SizeUnsupported(candidate);
                    }
                    else if (profile.IsSupported) candidate = CompilerRecipePayload.SizeUnsupported(candidate);
                    if (staged.Count == 0)
                        staged.Add((buildId + "-recipe-unsupported", CompilerRecipePayload.UnsupportedKind,
                            CompilerRecipePayload.Unsupported(candidate, contextId, buildId,
                                CompilerRecipePayload.Classify(candidate, preflight.Root, cancellation), cancellation)));
                    if (!AdmitOptional(candidate, staged, out var contribution))
                    {
                        candidate = CompilerRecipePayload.SizeUnsupported(candidate);
                        staged.Clear(); staged.Add((buildId + "-recipe-unsupported", CompilerRecipePayload.UnsupportedKind,
                            CompilerRecipePayload.Unsupported(candidate, contextId, buildId,
                                CompilerRecipePayload.Classify(candidate, preflight.Root, cancellation), cancellation)));
                        if (!AdmitOptional(candidate, staged, out contribution)) return;
                    }
                    observations[binding.GraphId] = candidate; optionalPriorBytes += contribution;
                    foreach (var item in staged) Artifact(item.Id, item.Kind, item.Bytes, contextId, buildId,
                        format: item.Kind == CompilerRecipePayload.Kind || item.Kind == CompilerRecipePayload.UnsupportedKind ? "json" : null);
                });
            }
            Artifact(buildId + "-closure", "compiler-input-closure", ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(new
            { provider = "validated-csc-inputs-v1", metadata = phaseIdentities, reusableRecipe = false })), contextId, buildId, format: "json");
            builds.Add(new(buildId, contextId, inspected.ModuleIdentity, CanonicalIdentity.Sha256(assembly.AsSpan()),
                inspected.Mvid, CanonicalIdentity.Sha256(pdb.AsSpan()), inspected.DebugIdentity));
            Artifact(buildId + "-pe", "assembly", assembly, contextId, buildId);
            Artifact(buildId + "-pdb", "pdb", pdb, contextId, buildId);
            bool Reaches(string id, HashSet<string> seen)
            {
                if (id == binding.GraphId) return true;
                if (!seen.Add(id)) return false;
                return referenceEdges[id].Any(d => Reaches(d, seen));
            }
            foreach (var report in execution.Coverage.Invocations.Where(r => Reaches(r.GraphId, new(StringComparer.Ordinal))))
            {
                var invocation = execution.Tests.Single(t => t.GraphId == report.GraphId);
                var testContext = bindings[report.GraphId].Context; var testObservation = observations[invocation.GraphId];
                var testPe = ReadCaptured(testObservation.Assembly!.Path, ref readTotal, cancellation);
                var testPdb = ReadCaptured(testObservation.Pdb!.Path, ref readTotal, cancellation);
                CompilerOutputBinding.Match(testObservation.Assembly, testPe); CompilerOutputBinding.Match(testObservation.Pdb, testPdb);
                var tested = ArtifactEvidenceInspector.InspectBuild(testPe, testPdb);
                var trx = TestResultBinding.ReadTrx(Path.Combine(invocation.ResultsDirectory, "tests.trx"), cancellation);
                var counters = ArtifactEvidenceInspector.InspectTrx(trx);
                var id = "test-" + binding.GraphId + "-" + report.GraphId;
                executions.Add(new(id, contextId, buildId, true, invocation.Process.ExitCode,
                    counters.Total, counters.Passed, counters.Failed, counters.Skipped)
                {
                    TestProject = RepositoryPath(invocation.ProjectPath),
                    TestModuleIdentity = tested.ModuleIdentity,
                    TestAssemblySha256 = CanonicalIdentity.Sha256(testPe.AsSpan()),
                    TestMvid = tested.Mvid,
                    TestPdbSha256 = CanonicalIdentity.Sha256(testPdb.AsSpan()),
                    TestDebugIdentity = tested.DebugIdentity
                });
                Artifact(id + "-trx", "test-result", trx, contextId, buildId, id, "trx");
                Artifact(id + "-pe", "test-assembly", testPe, contextId, buildId, id);
                Artifact(id + "-pdb", "test-pdb", testPdb, contextId, buildId, id);
                if (!semanticEmpty)
                    Artifact(id + "-coverage", "coverage", report.Modules.Single(m => m.GraphId == binding.GraphId).LineProjection,
                        contextId, buildId, id, "opencover", "line");
                Artifact(id + "-child", "test-child-build", TestChildEvidenceExport.Capture(invocation,
                    execution.Tests.Outcomes.Single(o => o.GraphId == report.GraphId), cancellation), contextId, buildId, id, "json");
            }
        }
        // Preserve required test Csc inputs too, without making excluded tests scored
        // manifest contexts or publishing their raw project/SDK property dictionaries.
        foreach (var test in graph.GraphContexts.Where(b => b.IsTestProject && !b.Context.ExclusionPolicy.IncludeTests))
        {
            var identities = new List<object>(); var index = 0;
            foreach (var source in test.Context.CompilerDocuments)
            {
                var content = ReadCaptured(source.ResolvedPath!, ref readTotal, cancellation);
                if (CanonicalIdentity.Sha256(content.AsSpan()) != source.ContentIdentity) throw Refuse();
                _ = CapturedSource.Create(source.LogicalPath, content.AsSpan());
                var locator = Store(content);
                Artifact("test-source-" + test.GraphId + "-" + index++, "test-compiler-source", content);
                identities.Add(new
                {
                    role = "compiler-source",
                    logicalPath = source.LogicalPath,
                    locator,
                    sha256 = source.ContentIdentity,
                    length = content.Length,
                    generated = source.IsGenerated
                });
            }
            foreach (var reference in CompilerReferenceSnapshot.ResolveSemanticReferences(observations[test.GraphId], cancellation))
            {
                var content = ReadCaptured(reference.Path, ref readTotal, cancellation);
                var hash = CanonicalIdentity.Sha256(content.AsSpan()); var locator = Store(content);
                Artifact("test-reference-" + test.GraphId + "-" + index++, "test-compiler-reference", content);
                identities.Add(new
                {
                    role = "reference",
                    logicalPath = CompilerReferenceSnapshot.LogicalReference(hash, reference.Aliases, reference.EmbedInteropTypes),
                    locator,
                    sha256 = hash,
                    length = content.Length,
                    referenceAliases = reference.Aliases,
                    embedInteropTypes = reference.EmbedInteropTypes ? (bool?)true : null
                });
            }
            foreach (var phase in test.Context.PhaseInputs.Where(p => p.Role != "source" && File.Exists(p.Path)))
            {
                var content = ReadCaptured(phase.Path, ref readTotal, cancellation);
                identities.Add(new
                {
                    role = phase.Role,
                    logicalPath = "metadata/" + CanonicalIdentity.Tuple("phase-path", phase.Role, phase.Path),
                    sha256 = CanonicalIdentity.Sha256(content.AsSpan()),
                    length = content.Length
                });
            }
            Artifact("test-closure-" + test.GraphId, "test-compiler-input-closure", ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(new
            { provider = "validated-csc-inputs-v1", project = RepositoryPath(test.ProjectPath), inputs = identities, reusableRecipe = false })), format: "json");
        }
        var available = contexts.SelectMany(c => c.Inputs.Where(i => i.Role == "source" && (!i.Generated || c.IncludeGenerated))
            .Select(i => i.RepositoryPath!)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var selected = preflight.Scope is null || preflight.Scope.Completeness == ScopeCompleteness.ContextIncomplete ? available :
            preflight.Scope.Files.Select(f => f.NewPath).Where(p => p is not null && available.Contains(p, StringComparer.Ordinal))
                .Select(p => p!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var scopeBytes = ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            sources = selected,
            sourceState = "worktree",
            granularity = preflight.Request.Granularity,
            requestedScope = preflight.Policy.Scope,
            revision = preflight.GitSnapshot?.Revision
        }));
        // Tiny replay policy is effective threshold only; base policy/overlays are independently
        // retained and the caller must run the pure trusted policy gate, not trust replay's verdict.
        var policyBytes = ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            threshold = preflight.Policy.Threshold,
            allowMissingCoverage = false
        }));
        Artifact("scope", "scope", scopeBytes, format: "json"); Artifact("policy", "policy", policyBytes, format: "json");
        Artifact("trusted-policy", "trusted-policy", ImmutableArray.Create(preflight.Trusted.Policy.CanonicalBytes), format: "json");
        foreach (var exemption in preflight.Trusted.ExemptionBytes)
            Artifact("exemption-" + CanonicalIdentity.Tuple("exemption", exemption.Key), "trusted-exemption", ImmutableArray.Create(exemption.Value), format: "json");
        if (preflight.Trusted.Baseline is { } baseline)
            Artifact("trusted-baseline", "trusted-baseline", ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(baseline)), format: "json");
        // Mandatory graph bytes are reserved before optional artifacts. Absence
        // is the existing legacy-compatible shape when even status DATA cannot fit.
        foreach (var id in observations.Keys.ToArray())
            observations[id] = CompilerRecipeObservation.WithoutOptionalRecipe(observations[id], cancellation);
        var mandatoryPrior = FreshPriorBuild.Capture(prior, observations, preflight.Root, cancellation);
        mandatoryPriorLength = mandatoryPrior.Length;
        if (bytes.Values.Sum(content => (long)content.Length) + mandatoryPriorLength > 192L * 1024 * 1024) throw Refuse();
        optionalReadsExhausted = readTotal >= CompilerPhaseSnapshot.MaximumClosureBytes;
        foreach (var emit in optional) { cancellation.ThrowIfCancellationRequested(); emit(); }
        var selectedPrior = FreshPriorBuild.Capture(prior, observations, preflight.Root, cancellation);
        if (selectedPrior.Length != mandatoryPriorLength + optionalPriorBytes) throw Refuse();
        Artifact("prior-compiler-graph", FreshPriorBuild.Kind, selectedPrior);
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
        readPaths.Complete();
        if (provisional)
            using (timing?.Start("public.capture.originalPhysicalAfter"))
                execution.Compiler.RetainedProof.ValidateCurrent(cancellation);
        else
            using (timing?.Start("public.capture.finalRegate"))
                await RegateAsync(execution, cancellation, timing, "public.capture.finalRegate");
        using (timing?.Start("public.capture.verifyCapture"))
        {
            var valid = ProvenanceVerifier.VerifyCaptureCancellable(manifest, bytes, preflight.Policy.Ruleset, cancellation);
            if (valid.Status != ProvenanceStatus.Captured) throw new PolicyException("check.captureUnproven", string.Join(", ", valid.Reasons));
        }
        return new(manifest, bytes)
        {
            RequiresCompletedProof = provisional,
            OptionalRecipes = FreshCompilerRecipeSelection.Create(execution.Compiler, observations, preflight.Root, cancellation)
        };
    }

    internal static async Task<string> PublishAsync(FreshExecutionEvidence execution, FreshCapturedBundle captured,
        string destination, CancellationToken cancellation, CompletedProofTiming? timing = null,
        string timingPrefix = "publication")
    {
        RejectProvisional(captured);
        return (await StageAsync(execution, captured, destination, cancellation, timing, timingPrefix)).CommitVerifiedCapture(cancellation);
    }

    internal static Task<StagedArtifactPublication> StageAsync(FreshExecutionEvidence execution, FreshCapturedBundle captured,
        string destination, CancellationToken cancellation, CompletedProofTiming? timing = null,
        string timingPrefix = "publication", Action<string>? retainCreatedDirectory = null)
    {
        RejectProvisional(captured);
        return StageCoreAsync(execution, captured, destination, cancellation, timing, timingPrefix, retainCreatedDirectory);
    }

    internal static Task<StagedArtifactPublication> StageForCompletedAsync(FreshExecutionEvidence execution, FreshCapturedBundle captured,
        string destination, CancellationToken cancellation, CompletedProofTiming? timing = null,
        string timingPrefix = "publication", Action<string>? retainCreatedDirectory = null)
    {
        if (!captured.RequiresCompletedProof || captured.Manifest.Contexts.Any(context => !context.ReuseRecipeComplete ||
            context.CurrentRevalidation is null)) throw ProvisionalRefusal();
        return StageCoreAsync(execution, captured, destination, cancellation, timing, timingPrefix, retainCreatedDirectory);
    }

    private static async Task<StagedArtifactPublication> StageCoreAsync(FreshExecutionEvidence execution, FreshCapturedBundle captured,
        string destination, CancellationToken cancellation, CompletedProofTiming? timing,
        string timingPrefix, Action<string>? retainCreatedDirectory)
    {
        using (timing?.Start(timingPrefix + ".verifyCapture"))
            if (ProvenanceVerifier.VerifyCaptureCancellable(captured.Manifest, captured.Bytes,
                execution.Preflight.Policy.Ruleset, cancellation).Status != ProvenanceStatus.Captured) throw Refuse();
        var request = execution.Preflight.Request with { ArtifactOutput = destination };
        var preflight = execution.Preflight with { Request = request };
        FreshCheckPreflightLoader.ValidateDestinations(request, preflight.Root,
            preflight.Trusted.ContentHashes.Keys.Concat(preflight.Policy.ProductionProjects).Concat(preflight.Policy.TestProjects));
        FreshCheckExecution.ProtectPreparedDestinations(preflight, execution.Compiler.FinalGraph);
        if (captured.RequiresCompletedProof)
        {
            // Unpublishable preparation uses ORIGINAL physical/selection/test
            // evidence. The complete independently current intermediate/final
            // graph/runner/trust proof remains mandatory at the actual writer.
            using (timing?.Start(timingPrefix + ".originalPhysicalBefore"))
                execution.Compiler.RetainedProof!.ValidateCurrent(cancellation);
            await ValidateSelectionAndTestsAsync(execution, cancellation, timing, timingPrefix + ".provisional");
            using (timing?.Start(timingPrefix + ".originalPhysicalAfter"))
                execution.Compiler.RetainedProof!.ValidateCurrent(cancellation);
        }
        else
            await RegateAsync(execution, cancellation, timing, timingPrefix + ".regate");
        FreshCheckExecution.ProtectPreparedDestinations(preflight, execution.Compiler.FinalGraph);
        cancellation.ThrowIfCancellationRequested();
        using (timing?.Start(timingPrefix + ".publishAdapter"))
        {
            var staged = ArtifactCaptureAdapter.StageNewCancellable(captured.Manifest, captured.Bytes,
                Path.GetFullPath(destination, preflight.Root), cancellation, retainCreatedDirectory);
            if (captured.RequiresCompletedProof) staged.RequireCompletedProof();
            return staged;
        }
    }

    private static void RejectProvisional(FreshCapturedBundle captured)
    {
        if (captured.RequiresCompletedProof) throw ProvisionalRefusal();
    }

    private static PolicyException ProvisionalRefusal() => new("check.provisionalCaptureRequiresCompletedProof",
        "Provisional capture requires complete writer proof before publication.");

    internal static async Task RegateAsync(FreshExecutionEvidence execution, CancellationToken cancellation,
        CompletedProofTiming? timing = null, string timingPrefix = "regate",
        IReadOnlySet<string>? provenBundleFiles = null)
    {
        await execution.Compiler.RetainedProof!.ValidateFinalAsync(cancellation, timing, timingPrefix + ".compiler");
        await ValidateSelectionAndTestsAsync(execution, cancellation, timing, timingPrefix, provenBundleFiles);
    }

    private static async Task ValidateSelectionAndTestsAsync(FreshExecutionEvidence execution, CancellationToken cancellation,
        CompletedProofTiming? timing, string timingPrefix, IReadOnlySet<string>? provenBundleFiles = null)
    {
        var graph = execution.Compiler.FinalGraph;
        var footprint = graph.GraphContexts.SelectMany(b =>
            new[] { b.Context.EvaluatedIntermediateDirectory!, Path.GetDirectoryName(b.Context.EvaluatedTargetPath!)! }
                .Concat(b.Context.PhaseInputs.Where(i => i.Role == "restoreAssets").Select(i => Path.GetDirectoryName(i.Path)!)))
            .Distinct(PathIdentityPolicy.Current.Comparer).ToArray();
        using (timing?.Start(timingPrefix + ".revision"))
            await FreshGitProof.ValidateAsync(execution.Preflight, cancellation, footprint, provenBundleFiles);
        using (timing?.Start(timingPrefix + ".aggregate"))
            _ = CoverageGraphBinding.ValidateAggregate(execution.Tests, execution.Compiler, cancellation);
        using (timing?.Start(timingPrefix + ".children"))
            foreach (var test in execution.Tests)
                _ = TestChildEvidenceExport.Capture(test, execution.Tests.Outcomes.Single(o => o.GraphId == test.GraphId), cancellation);
    }

    private static PolicyException Refuse() => new("check.captureUnproven", "Fresh capture requires complete unchanged accepted compiler/test/coverage evidence.");
}
