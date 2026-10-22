using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Crap4CSharp.Core;

// Current compiler context agreement only; not final workspace selection or a
// completed-test reusable provider. Hash-only metadata is never executed as XML.
internal static class SavedCompilerContexts
{
    internal static void Validate(ArtifactBundle bundle, CompilerGraphPhaseResult compiler, string root, CancellationToken token)
        => ValidateObserved(bundle, compiler, root, token, null, "saved");

    internal static void ValidateObserved(ArtifactBundle bundle, CompilerGraphPhaseResult compiler, string root, CancellationToken token,
        global::CompletedProofTiming? timing, string prefix, FreshCompilerRecipeSelection? optionalRecipes = null)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (compiler.RetainedProof is null) throw Refuse();
            using (timing?.StartSynchronous(prefix + ".compiler.retainedOpening"))
                compiler.RetainedProof.ValidateCurrent(token);
            var bindings = compiler.FinalGraph.GraphContexts;
            var logicalIds = compiler.FinalGraph.BuildGraph.ToDictionary(n => n.Id, n => LogicalContextIdentity.Create(root, n), StringComparer.Ordinal);
            var observations = compiler.ByGraphId();
            var scored = bindings.Where(b => !b.IsTestProject || b.Context.ExclusionPolicy.IncludeTests).ToArray();
            if (bundle.Manifest.Contexts.Count != scored.Length ||
                !bundle.Manifest.Contexts.Select(c => c.Id).ToHashSet(StringComparer.Ordinal).SetEquals(scored.Select(b => logicalIds[b.GraphId]))) throw Refuse();
            long total = 0;
            var reads = new CompilerByteReadScope(token);
            var testArtifacts = new HashSet<string>(StringComparer.Ordinal);
            var recipeArtifacts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in bindings)
            {
                token.ThrowIfCancellationRequested();
                var current = binding.Context;
                var excludedTest = binding.IsTestProject && !current.ExclusionPolicy.IncludeTests;
                var observation = observations[binding.GraphId];
                using (timing?.StartSynchronous(prefix + ".compiler.outputBinding"))
                    CompilerOutputBinding.Validate(current, observation, token);
                var documents = new List<(ManifestInput Input, ImmutableArray<byte> Bytes)>();
                var references = new List<(ManifestInput Input, ImmutableArray<byte> Bytes)>();
                using (timing?.StartSynchronous(prefix + ".compiler.documents"))
                    foreach (var source in current.CompilerDocuments)
                    {
                        var content = Read(source.ResolvedPath!);
                        if (CanonicalIdentity.Sha256(content.AsSpan()) != source.ContentIdentity) throw Refuse();
                        _ = CapturedSource.Create(source.LogicalPath, content.AsSpan());
                        documents.Add((Input("compiler-source", source.LogicalPath, content, source.IsGenerated,
                            excludedTest ? null : Logical(source.ResolvedPath!), "utf-8"), content));
                    }
                using (timing?.StartSynchronous(prefix + ".compiler.references"))
                    foreach (var reference in CompilerReferenceSnapshot.ResolveSemanticReferences(observation, token))
                    {
                        var content = Read(reference.Path);
                        references.Add((Input("reference", CompilerReferenceSnapshot.LogicalReference(CanonicalIdentity.Sha256(content.AsSpan()),
                            reference.Aliases, reference.EmbedInteropTypes), content) with
                        { ReferenceAliases = reference.Aliases, EmbedInteropTypes = reference.EmbedInteropTypes ? true : null }, content));
                    }
                if (excludedTest)
                {
                    var identities = new List<object>(); var index = 0;
                    foreach (var (input, content) in documents)
                    {
                        Artifact("test-source-" + binding.GraphId + "-" + index++, "test-compiler-source", content);
                        identities.Add(new
                        {
                            role = "compiler-source",
                            logicalPath = input.LogicalPath,
                            locator = input.Locator,
                            sha256 = input.Sha256,
                            length = input.Length,
                            generated = input.Generated
                        });
                    }
                    foreach (var (input, content) in references)
                    {
                        Artifact("test-reference-" + binding.GraphId + "-" + index++, "test-compiler-reference", content);
                        identities.Add(new
                        {
                            role = "reference",
                            logicalPath = input.LogicalPath,
                            locator = input.Locator,
                            sha256 = input.Sha256,
                            length = input.Length,
                            referenceAliases = input.ReferenceAliases,
                            embedInteropTypes = input.EmbedInteropTypes
                        });
                    }
                    foreach (var phase in current.PhaseInputs.Where(p => p.Role != "source" && File.Exists(p.Path)))
                    {
                        var content = Read(phase.Path);
                        identities.Add(new
                        {
                            role = phase.Role,
                            logicalPath = "metadata/" + CanonicalIdentity.Tuple("phase-path", phase.Role, phase.Path),
                            sha256 = CanonicalIdentity.Sha256(content.AsSpan()),
                            length = content.Length
                        });
                    }
                    Artifact("test-closure-" + binding.GraphId, "test-compiler-input-closure", Json(new
                    { provider = "validated-csc-inputs-v1", project = Logical(binding.ProjectPath), inputs = identities, reusableRecipe = false }), "json");
                    continue;
                }
                var contextId = logicalIds[binding.GraphId];
                var saved = bundle.Manifest.Contexts.Single(c => c.Id == contextId);
                if (saved.CurrentRevalidation is not null || saved.ReuseRecipeComplete)
                    CompletedRevalidationDeclaration.Validate(saved, Logical(binding.ProjectPath),
                        Logical(current.EvaluatedTargetPath!), Logical(Path.ChangeExtension(current.EvaluatedTargetPath!, ".pdb")));
                var pe = Read(observation.Assembly!.Path); var pdb = Read(observation.Pdb!.Path);
                CompilerOutputBinding.Match(observation.Assembly, pe); CompilerOutputBinding.Match(observation.Pdb, pdb);
                var inspected = ArtifactEvidenceInspector.InspectBuild(pe, pdb);
                var paths = inspected.Documents.Keys.Select(p => Path.GetFullPath(p, Path.GetDirectoryName(binding.ProjectPath)!))
                    .ToHashSet(PathIdentityPolicy.Current.Comparer);
                var expected = documents.Select(d => d.Input with
                {
                    Role = paths.Contains(current.CompilerDocuments.Single(s => s.LogicalPath == d.Input.LogicalPath).ResolvedPath!)
                    ? "source" : "compiler-source"
                }).Concat(references.Select(r => r.Input))
                    .GroupBy(i => (i.Role, i.LogicalPath)).Select(g =>
                    { if (g.Select(i => i.Sha256).Distinct(StringComparer.Ordinal).Count() != 1) throw Refuse(); return g.First(); }).ToArray();
                if (saved.Project != Logical(binding.ProjectPath) || saved.Configuration != current.Configuration ||
                    saved.TargetFramework != current.TargetFramework || saved.Platform != current.Platform ||
                    saved.IncludeGenerated != current.ExclusionPolicy.IncludeGenerated || saved.Inputs.Count != expected.Length) throw Refuse();
                foreach (var input in expected)
                {
                    if (saved.Inputs.Single(i => i.Role == input.Role && i.LogicalPath == input.LogicalPath) != input) throw Refuse();
                    var content = bundle.Bytes[input.Locator];
                    if (content.Length != input.Length || CanonicalIdentity.Sha256(content.AsSpan()) != input.Sha256) throw Refuse();
                }
                var parse = saved.ParseOptions ?? throw Refuse();
                if (parse.LanguageVersion != inspected.LanguageVersion || parse.SourceKind != current.SourceKind.ToString() ||
                    !parse.PreprocessorSymbols.SequenceEqual(inspected.PreprocessorSymbols) ||
                    parse.Features.Count != current.ParseOptions.Features.Count() ||
                    current.ParseOptions.Features.Any(p => !parse.Features.TryGetValue(p.Key, out var value) || value != p.Value)) throw Refuse();
                var policy = saved.PathPolicy ?? throw Refuse();
                if (policy.CasePolicy != (OperatingSystem.IsWindows() ? "insensitive" : "sensitive") ||
                    !policy.ReportRootMappings.SequenceEqual(current.CompilerDocuments.Select(d => new ManifestReportRootMapping(d.ResolvedPath!, d.LogicalPath)))) throw Refuse();
                var allSources = documents.Select(d => CapturedSource.Create(d.Input.LogicalPath, d.Bytes.AsSpan())).ToArray();
                var eligible = documents.Where(d => !d.Input.Generated || current.ExclusionPolicy.IncludeGenerated)
                    .Select(d => CapturedSource.Create(d.Input.LogicalPath, d.Bytes.AsSpan())).ToArray();
                var analysis = new CallableAnalysisContext(Logical(binding.ProjectPath), current.TargetFramework, current.Configuration,
                    current.Platform, contextId, current.ParseOptions);
                MetadataReference[] metadata;
                using (timing?.StartSynchronous(prefix + ".compiler.metadata"))
                    metadata = references.Select(r => CompilerReferenceSnapshot.CapturedReference(r.Input, r.Bytes)).ToArray();
                SyntaxTree[] syntax;
                using (timing?.StartSynchronous(prefix + ".compiler.syntax"))
                    syntax = allSources.Select(s => CSharpSyntaxTree.ParseText(s.Text, current.ParseOptions, s.LogicalPath)).ToArray();
                CSharpCompilation compilation;
                using (timing?.StartSynchronous(prefix + ".compiler.compilation"))
                    compilation = CSharpCompilation.Create("Crap4CSharp.CallableInventory", syntax, metadata);
                var inventories = new Dictionary<string, CallableInventoryResult>(StringComparer.Ordinal);
                CallableInventoryResult Inventory(CapturedSource source)
                {
                    if (!inventories.TryGetValue(source.LogicalPath, out var inventory))
                    {
                        using var measurement = timing?.StartSynchronous(prefix + ".compiler.inventory");
                        inventories.Add(source.LogicalPath, inventory = CallableInventory.AnalyzeCaptured(source, analysis, compilation));
                    }
                    return inventory;
                }
                var empty = !eligible.Any(s => Inventory(s).Callables.Any(c => c.Applicability == CallableApplicability.Applicable));
                if (saved.CoverageApplicability != (empty ? "semantic-empty" : null)) throw Refuse();
                foreach (var source in expected.Where(i => i.Role == "compiler-source" && (!i.Generated || current.ExclusionPolicy.IncludeGenerated)))
                    if (Inventory(CapturedSource.Create(source.LogicalPath, bundle.Bytes[source.Locator].AsSpan()))
                        .Callables.Any(c => c.Applicability == CallableApplicability.Applicable)) throw Refuse();
                var phaseIdentities = new List<object>();
                using (timing?.StartSynchronous(prefix + ".compiler.phaseInputs"))
                    foreach (var phase in current.PhaseInputs.Where(i => i.Role != "source" && File.Exists(i.Path)))
                    {
                        var content = Read(phase.Path); var relative = Path.GetRelativePath(root, phase.Path).Replace('\\', '/');
                        var inside = relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
                        phaseIdentities.Add(new
                        {
                            role = phase.Role,
                            logicalPath = inside ? relative : "external/" + CanonicalIdentity.Tuple("phase-path", phase.Role, phase.Path),
                            length = content.Length,
                            sha256 = CanonicalIdentity.Sha256(content.AsSpan()),
                            generated = phase.ExpectedGenerated
                        });
                    }
                // Security boundary: these physical graph IDs bind this saved
                // closure to the current workspace, independently of public IDs.
                var closure = bundle.Manifest.Artifacts.Single(a => a.Id == "build-" + binding.GraphId + "-closure");
                if (closure.Kind != "compiler-input-closure" || closure.ContextId != contextId || closure.BuildId != "build-" + binding.GraphId ||
                    closure.ExecutionId is not null || closure.Format != "json" || closure.CoordinateKind is not null) throw Refuse();
                Match(closure, Json(new { provider = "validated-csc-inputs-v1", metadata = phaseIdentities, reusableRecipe = false }));
                // Mandatory compiler/physical gates above always use originals.
                // Only this optional branch may consume invocation-private loss.
                var recipeObservation = optionalRecipes?.OptionalView(compiler, observation, root, token) ?? observation;
                if (recipeObservation.InvocationRecipe is not null)
                {
                    var buildId = "build-" + binding.GraphId;
                    var profile = CompilerRecipePayload.Classify(recipeObservation, root, token);
                    if (!profile.IsSupported)
                    {
                        var unsupported = bundle.Manifest.Artifacts.Single(artifact => artifact.Id == buildId + "-recipe-unsupported");
                        if (unsupported.Kind != CompilerRecipePayload.UnsupportedKind || unsupported.ContextId != contextId || unsupported.BuildId != buildId ||
                            unsupported.ExecutionId is not null || unsupported.Format != "json" || unsupported.CoordinateKind is not null) throw Refuse();
                        if (!recipeArtifacts.Add(unsupported.Id)) throw Refuse();
                        Match(unsupported, CompilerRecipePayload.Unsupported(recipeObservation, contextId, buildId, profile, token));
                        continue;
                    }
                    var recipe = bundle.Manifest.Artifacts.Single(a => a.Id == buildId + "-recipe");
                    if (recipe.Kind != CompilerRecipePayload.Kind || recipe.ContextId != contextId || recipe.BuildId != buildId ||
                        recipe.ExecutionId is not null || recipe.Format != "json" || recipe.CoordinateKind is not null) throw Refuse();
                    if (!recipeArtifacts.Add(recipe.Id)) throw Refuse();
                    var sourceMap = current.CompilerDocuments.ToDictionary(source => Path.GetFullPath(source.ResolvedPath!),
                        source => expected.Single(input => (input.Role is "source" or "compiler-source") && input.LogicalPath == source.LogicalPath), PathIdentityPolicy.Current.Comparer);
                    var recipeFiles = CompilerRecipePayload.CaptureFiles(recipeObservation, current, buildId, Read, token);
                    foreach (var file in recipeFiles)
                    {
                        var artifact = bundle.Manifest.Artifacts.Single(item => item.Id == file.Id);
                        if (artifact.Kind != CompilerRecipePayload.FileKind(file) || artifact.ContextId != contextId || artifact.BuildId != buildId ||
                            artifact.ExecutionId is not null || artifact.Format is not null || artifact.CoordinateKind is not null) throw Refuse();
                        if (!recipeArtifacts.Add(artifact.Id)) throw Refuse();
                        Match(artifact, file.Bytes);
                    }
                    Match(recipe, CompilerRecipePayload.Create(recipeObservation, contextId, buildId, root, sourceMap,
                        references.Select(reference => reference.Input).ToArray(), recipeFiles, token));
                }
            }
            var excluded = bundle.Manifest.Artifacts.Where(a => a.Kind is "test-compiler-source" or "test-compiler-reference" or "test-compiler-input-closure").ToArray();
            if (excluded.Length != testArtifacts.Count || !testArtifacts.SetEquals(excluded.Select(a => a.Id))) throw Refuse();
            var optionalArtifacts = bundle.Manifest.Artifacts.Where(artifact => artifact.Kind is
                CompilerRecipePayload.Kind or CompilerRecipePayload.UnsupportedKind or CompilerRecipePayload.ImageKind or CompilerRecipePayload.ClosureKind).ToArray();
            if (optionalArtifacts.Length != recipeArtifacts.Count || !recipeArtifacts.SetEquals(optionalArtifacts.Select(artifact => artifact.Id))) throw Refuse();
            reads.Complete();
            using (timing?.StartSynchronous(prefix + ".compiler.retainedClosing"))
                compiler.RetainedProof.ValidateCurrent(token);

            string Logical(string path) => CanonicalIdentity.NormalizeLogicalPath(Path.GetRelativePath(root, path).Replace('\\', '/'));
            ImmutableArray<byte> Read(string path) => reads.Read(path, ref total);
            static ImmutableArray<byte> Json(object value) => ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(value));
            static ManifestInput Input(string role, string logical, ImmutableArray<byte> content, bool generated = false, string? repository = null, string? encoding = null)
            {
                var hash = CanonicalIdentity.Sha256(content.AsSpan());
                return new(role, CanonicalIdentity.NormalizeLogicalPath(logical), "inputs/" + hash + ".bin", content.Length, hash, encoding, generated) { RepositoryPath = repository };
            }
            void Match(ManifestArtifact artifact, ImmutableArray<byte> content)
            {
                token.ThrowIfCancellationRequested();
                if (artifact.Length != content.Length || artifact.Sha256 != CanonicalIdentity.Sha256(content.AsSpan()) ||
                    !bundle.Bytes[artifact.Locator].AsSpan().SequenceEqual(content.AsSpan())) throw Refuse();
            }
            void Artifact(string id, string kind, ImmutableArray<byte> content, string? format = null)
            {
                if (!testArtifacts.Add(id)) throw Refuse();
                var artifact = bundle.Manifest.Artifacts.Single(a => a.Id == id);
                if (artifact.Kind != kind || artifact.ContextId is not null || artifact.BuildId is not null || artifact.ExecutionId is not null ||
                    artifact.Format != format || artifact.CoordinateKind is not null) throw Refuse();
                Match(artifact, content);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is PolicyException or ProjectContextException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw Refuse(); }
    }

    private static PolicyException Refuse() => new("provenance.savedCompilerContextsUnproven",
        "Saved compiler source, reference, parse, path, semantic or metadata context differs from independently current compiler evidence.");
}
