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
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (compiler.RetainedProof is null) throw Refuse();
            compiler.RetainedProof.ValidateCurrent(token);
            var bindings = compiler.FinalGraph.GraphContexts;
            var observations = compiler.Observations.ToDictionary(o => o.ProjectPath, PathIdentityPolicy.Current.Comparer);
            var scored = bindings.Where(b => !b.IsTestProject || b.Context.ExclusionPolicy.IncludeTests).ToArray();
            if (bundle.Manifest.Contexts.Count != scored.Length ||
                !bundle.Manifest.Contexts.Select(c => c.Id).ToHashSet(StringComparer.Ordinal).SetEquals(scored.Select(b => b.GraphId))) throw Refuse();
            long total = 0;
            var testArtifacts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in bindings)
            {
                token.ThrowIfCancellationRequested();
                var current = binding.Context;
                var excludedTest = binding.IsTestProject && !current.ExclusionPolicy.IncludeTests;
                var observation = observations[binding.ProjectPath];
                CompilerOutputBinding.Validate(current, observation, token);
                var documents = new List<(ManifestInput Input, ImmutableArray<byte> Bytes)>();
                var references = new List<(ManifestInput Input, ImmutableArray<byte> Bytes)>();
                foreach (var source in current.CompilerDocuments)
                {
                    var content = Read(source.ResolvedPath!);
                    if (CanonicalIdentity.Sha256(content.AsSpan()) != source.ContentIdentity) throw Refuse();
                    _ = CapturedSource.Create(source.LogicalPath, content.AsSpan());
                    documents.Add((Input("compiler-source", source.LogicalPath, content, source.IsGenerated,
                        excludedTest ? null : Logical(source.ResolvedPath!), "utf-8"), content));
                }
                foreach (var reference in observation.Parameters.Single(p => p.Name == "References").Items.Distinct(PathIdentityPolicy.Current.Comparer))
                {
                    var content = Read(reference);
                    references.Add((Input("reference", "references/" + CanonicalIdentity.Sha256(content.AsSpan()) + ".dll", content), content));
                }
                if (excludedTest)
                {
                    var identities = new List<object>(); var index = 0;
                    foreach (var (input, content) in documents)
                    {
                        Artifact("test-source-" + binding.GraphId + "-" + index++, "test-compiler-source", content);
                        identities.Add(new { role = "compiler-source", logicalPath = input.LogicalPath, locator = input.Locator,
                            sha256 = input.Sha256, length = input.Length, generated = input.Generated });
                    }
                    foreach (var (input, content) in references)
                    {
                        Artifact("test-reference-" + binding.GraphId + "-" + index++, "test-compiler-reference", content);
                        identities.Add(new { role = "reference", logicalPath = input.LogicalPath, locator = input.Locator,
                            sha256 = input.Sha256, length = input.Length });
                    }
                    foreach (var phase in current.PhaseInputs.Where(p => p.Role != "source" && File.Exists(p.Path)))
                    {
                        var content = Read(phase.Path);
                        identities.Add(new { role = phase.Role, logicalPath = "metadata/" + CanonicalIdentity.Tuple("phase-path", phase.Role, phase.Path),
                            sha256 = CanonicalIdentity.Sha256(content.AsSpan()), length = content.Length });
                    }
                    Artifact("test-closure-" + binding.GraphId, "test-compiler-input-closure", Json(new
                        { provider = "validated-csc-inputs-v1", project = Logical(binding.ProjectPath), inputs = identities, reusableRecipe = false }), "json");
                    continue;
                }
                var saved = bundle.Manifest.Contexts.Single(c => c.Id == binding.GraphId);
                if (saved.CurrentRevalidation is not null || saved.ReuseRecipeComplete)
                    CompletedRevalidationDeclaration.Validate(saved, Logical(binding.ProjectPath),
                        Logical(current.EvaluatedTargetPath!), Logical(Path.ChangeExtension(current.EvaluatedTargetPath!, ".pdb")));
                var pe = Read(observation.Assembly!.Path); var pdb = Read(observation.Pdb!.Path);
                CompilerOutputBinding.Match(observation.Assembly, pe); CompilerOutputBinding.Match(observation.Pdb, pdb);
                var inspected = ArtifactEvidenceInspector.InspectBuild(pe, pdb);
                var paths = inspected.Documents.Keys.Select(p => Path.GetFullPath(p, Path.GetDirectoryName(binding.ProjectPath)!))
                    .ToHashSet(PathIdentityPolicy.Current.Comparer);
                var expected = documents.Select(d => d.Input with { Role = paths.Contains(current.CompilerDocuments.Single(s => s.LogicalPath == d.Input.LogicalPath).ResolvedPath!)
                    ? "source" : "compiler-source" }).Concat(references.Select(r => r.Input))
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
                    current.Platform, binding.GraphId, current.ParseOptions);
                var metadata = references.Select(r => MetadataReference.CreateFromImage(r.Bytes)).ToArray();
                var empty = !SemanticEligibility.HasApplicable(eligible, allSources, analysis, metadata);
                if (saved.CoverageApplicability != (empty ? "semantic-empty" : null)) throw Refuse();
                var syntax = allSources.Select(s => CSharpSyntaxTree.ParseText(s.Text, current.ParseOptions, s.LogicalPath)).ToArray();
                foreach (var source in expected.Where(i => i.Role == "compiler-source" && (!i.Generated || current.ExclusionPolicy.IncludeGenerated)))
                    if (CallableInventory.Analyze(CapturedSource.Create(source.LogicalPath, bundle.Bytes[source.Locator].AsSpan()).Text,
                        source.LogicalPath, analysis, metadata, syntax).Callables.Any(c => c.Applicability == CallableApplicability.Applicable)) throw Refuse();
                var phaseIdentities = new List<object>();
                foreach (var phase in current.PhaseInputs.Where(i => i.Role != "source" && File.Exists(i.Path)))
                {
                    var content = Read(phase.Path); var relative = Path.GetRelativePath(root, phase.Path).Replace('\\', '/');
                    var inside = relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
                    phaseIdentities.Add(new { role = phase.Role, logicalPath = inside ? relative : "external/" + CanonicalIdentity.Tuple("phase-path", phase.Role, phase.Path),
                        length = content.Length, sha256 = CanonicalIdentity.Sha256(content.AsSpan()), generated = phase.ExpectedGenerated });
                }
                var closure = bundle.Manifest.Artifacts.Single(a => a.Id == "build-" + binding.GraphId + "-closure");
                if (closure.Kind != "compiler-input-closure" || closure.ContextId != binding.GraphId || closure.BuildId != "build-" + binding.GraphId ||
                    closure.ExecutionId is not null || closure.Format != "json" || closure.CoordinateKind is not null) throw Refuse();
                Match(closure, Json(new { provider = "validated-csc-inputs-v1", metadata = phaseIdentities, reusableRecipe = false }));
            }
            var excluded = bundle.Manifest.Artifacts.Where(a => a.Kind is "test-compiler-source" or "test-compiler-reference" or "test-compiler-input-closure").ToArray();
            if (excluded.Length != testArtifacts.Count || !testArtifacts.SetEquals(excluded.Select(a => a.Id))) throw Refuse();
            compiler.RetainedProof.ValidateCurrent(token);

            string Logical(string path) => CanonicalIdentity.NormalizeLogicalPath(Path.GetRelativePath(root, path).Replace('\\', '/'));
            ImmutableArray<byte> Read(string path) => CompilerOutputBinding.Read(path, ref total, token);
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
