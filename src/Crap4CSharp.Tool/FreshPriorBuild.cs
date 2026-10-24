using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Crap4CSharp.Core;

// Explicit prior compiler evidence only: this never skips fresh tests or asserts
// reusable completed-test evidence. Physical paths/properties stay private.
internal static class FreshPriorBuild
{
    internal const string Kind = "prior-compiler-graph";
    internal const string TestRecipeKind = "test-prior-compiler-recipe";
    private const string Provider = "finite-sdk-prior-compiler-v1";
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal const int MaximumOptionalBytes = 4 * 1024 * 1024;
    internal const int MaximumEnvelopeBytes = MaximumBytes + MaximumOptionalBytes;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24
    };
    internal sealed record SavedPath(string? Relative, string Identity);
    internal sealed record SavedParameter(string Name, SavedPath[] Items)
    {
        public SavedReferenceProperties[] ReferenceProperties { get; init; } = [];
    }
    internal sealed record SavedReferenceProperties(SavedPath Item, string? Aliases, bool EmbedInteropTypes);
    internal sealed record SavedOutput(SavedPath Path, string Sha256, long Length);
    internal sealed record SavedCopy(SavedPath Source, SavedPath Destination);
    internal sealed record SavedNode(string Id, SavedParameter[] Parameters, SavedOutput Assembly,
        SavedOutput Pdb, SavedOutput? ReferenceAssembly, SavedCopy[] Copies)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CompilerRetainedRecipe? CompilerRecipe { get; init; }
    }
    internal sealed record Recipe(string Provider, string Workspace, string PhaseHash, SavedNode[] Nodes);

    internal sealed record CapturePlan(Recipe Ordinary, IReadOnlyDictionary<string, CompilerObservation> Observations);

    internal static CapturePlan Prepare(FreshExecutionEvidence execution, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var graph = execution.Compiler.FinalGraph;
        var root = execution.Preflight.Root;
        var targets = graph.GraphContexts.Select(b => b.Context.EvaluatedTargetPath!)
            .ToHashSet(PathIdentityPolicy.Current.Comparer);
        var observations = execution.Compiler.ByGraphId();
        SavedOutput Output(CompilerOutput output) => new(SavePath(root, output.Path), output.Sha256, output.Length);
        var nodes = graph.BuildGraph.Select(node =>
        {
            token.ThrowIfCancellationRequested();
            var observed = observations[node.Id];
            var directory = Path.GetDirectoryName(node.ProjectPath)!;
            return new SavedNode(node.Id, observed.Parameters.Select(p => new SavedParameter(p.Name,
                    p.Items.Select(i => SavePath(root, Path.GetFullPath(i, directory))).ToArray())
            {
                ReferenceProperties = p.ReferenceProperties.Select(reference => new SavedReferenceProperties(
                SavePath(root, Path.GetFullPath(reference.ItemSpec, directory)),
                CompilerReferenceSnapshot.NormalizeAliases(reference.Aliases), reference.EmbedInteropTypes)).ToArray()
            }).ToArray(),
                Output(observed.Assembly ?? throw Refuse()), Output(observed.Pdb ?? throw Refuse()),
                observed.ReferenceAssembly is null ? null : Output(observed.ReferenceAssembly),
                observed.RuntimeCopies.Where(c => targets.Contains(c.Source))
                    .Select(c => new SavedCopy(SavePath(root, c.Source), SavePath(root, c.Destination))).ToArray());
        }).ToArray();
        var ordinary = new Recipe(Provider, PathKey(root), Fingerprint(graph, token), nodes);
        if (!CompilerRecipeBudget.TrySerialize(ordinary, Json, MaximumBytes, out _)) throw Refuse();
        var selected = observations.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var eligible = graph.GraphContexts.Where(binding => !binding.IsTestProject || binding.Context.ExclusionPolicy.IncludeTests)
            .Select(binding => binding.GraphId).ToHashSet(StringComparer.Ordinal);
        var optionalUsed = 0;
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested();
            var observed = selected[node.Id];
            if (!eligible.Contains(node.Id) || observed.InvocationRecipe is null)
            { selected[node.Id] = observed with { InvocationRecipe = null }; continue; }
            // Classify/Retain validates recognized integrity before any drop.
            if (!TryOptionalContribution(observed, root, token, out var contribution) || contribution > MaximumOptionalBytes - optionalUsed)
            {
                observed = CompilerRecipePayload.SizeUnsupported(observed);
                if (!TryOptionalContribution(observed, root, token, out contribution) || contribution > MaximumOptionalBytes - optionalUsed)
                { selected[node.Id] = CompilerRecipeObservation.WithoutOptionalRecipe(observed, token); continue; }
            }
            selected[node.Id] = observed; optionalUsed += contribution;
        }
        return new(ordinary, selected);
    }

    // Exact delta of the nonnull compilerRecipe property on a SavedNode.
    // The wrapper adds two braces; the real node instead adds one comma.
    internal static bool TryOptionalContribution(CompilerObservation observation, string root, CancellationToken token, out int contribution)
    {
        contribution = 0;
        if (observation.InvocationRecipe is null) return true;
        var retained = CompilerRecipePayload.Retain(observation, root, token);
        if (!CompilerRecipeBudget.TrySerialize(new { compilerRecipe = retained }, Json, MaximumEnvelopeBytes, out var data)) return false;
        contribution = data.Length - 1;
        return contribution <= MaximumOptionalBytes;
    }

    // Metadata only for an excluded required test: this is not a scored public
    // compiler recipe or authority to load an image. Existing optional ledgers own it.
    internal static bool HasPathMap(CompilerObservation observation) => observation.CommandLineArguments.Any(argument =>
        argument.Length > 1 && (argument[0] is '/' or '-') && argument[1..].StartsWith("pathmap:", StringComparison.OrdinalIgnoreCase));

    internal static bool TryCaptureTestRecipe(CompilerObservation observation, string root, string contextId, string graphId,
        CancellationToken token, out ImmutableArray<byte> bytes)
    {
        token.ThrowIfCancellationRequested();
        var retained = CompilerRecipePayload.Retain(observation, root, token);
        if (!HasPathMap(observation) || retained.UnsupportedReasons.Count != 0) throw Refuse();
        var fits = CompilerRecipeBudget.TrySerialize(new
        { provider = "required-test-prior-compiler-v1", graphId, contextId, recipe = retained },
            Json, CompilerEvidence.MaximumTransportBytes, out var data);
        bytes = fits ? ImmutableArray.Create(data) : [];
        return fits;
    }

    // Explicit --no-build and saved verification never compile on SDK drift.
    // Only paths selected by the current trusted SDK are read; saved paths never
    // select a compiler image. The original command binding remains historical.
    internal static void RequireCurrentRecipeImages(IReadOnlyList<CompilerImageIdentity> expected,
        ProjectContextSdkResolution sdk, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var paths = CompilerEvidence.CompilerImagePaths(sdk);
        if (expected is null || expected.Count != paths.Count ||
            expected.Where((image, index) => image is null || image.Role != paths[index].Role || image.Length <= 0 ||
                image.Length > CompilerPhaseSnapshot.MaximumFileBytes || image.Sha256 is not { Length: 64 } ||
                !image.Sha256.All(char.IsAsciiHexDigitLower)).Any()) throw Refuse();
        long total = 0;
        foreach (var (image, index) in expected.Select((image, index) => (image, index)))
        {
            token.ThrowIfCancellationRequested();
            ImmutableArray<byte> bytes;
            try { bytes = CompilerOutputBinding.Read(paths[index].Path, ref total, token); }
            catch (Exception failure) when (failure is ProjectContextException or IOException or UnauthorizedAccessException)
            { throw Refuse(); }
            if (bytes.Length != image.Length || CanonicalIdentity.Sha256(bytes.AsSpan()) != image.Sha256) throw Refuse();
        }
        token.ThrowIfCancellationRequested();
    }

    internal static ImmutableArray<byte> Capture(CapturePlan plan, IReadOnlyDictionary<string, CompilerObservation> selected, string root, CancellationToken token)
    {
        var nodes = plan.Ordinary.Nodes.Select(node =>
        {
            token.ThrowIfCancellationRequested();
            var observed = selected[node.Id];
            return node with { CompilerRecipe = observed.InvocationRecipe is null ? null : CompilerRecipePayload.Retain(observed, root, token) };
        }).ToArray();
        var value = plan.Ordinary with { Nodes = nodes };
        if (!CompilerRecipeBudget.TrySerialize(value, Json, MaximumEnvelopeBytes, out var data)) throw Refuse();
        ValidateSlices(value, data.Length);
        return ImmutableArray.Create(data);
    }

    internal static void ValidateSlices(Recipe value, int envelopeLength)
    {
        if (value.Nodes is null || value.Nodes.Any(node => node is null)) throw Refuse();
        var ordinary = value with { Nodes = value.Nodes.Select(node => node with { CompilerRecipe = null }).ToArray() };
        if (!CompilerRecipeBudget.TrySerialize(ordinary, Json, MaximumBytes, out var mandatory) || envelopeLength - mandatory.Length > MaximumOptionalBytes) throw Refuse();
    }

    internal static IReadOnlyDictionary<string, CompilerObservation> Load(ArtifactBundle bundle,
        ProjectContextLoadResult graph, string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ProvenanceVerifier.VerifyCapture(bundle.Manifest, bundle.Bytes).Status != ProvenanceStatus.Captured)
            throw Refuse();
        var artifacts = bundle.Manifest.Artifacts.Where(a => a.Kind == Kind).ToArray();
        if (artifacts.Length != 1 || !bundle.Bytes.TryGetValue(artifacts[0].Locator, out var data) ||
            data.Length > MaximumEnvelopeBytes) throw Refuse();
        Recipe recipe;
        try
        {
            // Duplicate properties are not a supported recipe representation.
            using var document = JsonDocument.Parse(data.AsMemory());
            ValidateJson(document.RootElement);
            recipe = JsonSerializer.Deserialize<Recipe>(data.AsSpan(), Json) ?? throw Refuse();
        }
        catch (JsonException) { throw Refuse(); }
        if (recipe.Provider != Provider || recipe.Workspace != PathKey(root) ||
            recipe.PhaseHash != Fingerprint(graph, token) || recipe.Nodes is null ||
            recipe.Nodes.Length != graph.BuildGraph.Count || recipe.Nodes.Length > CompilerPhaseSnapshot.MaximumInputs)
            throw Refuse();
        ValidateSlices(recipe, data.Length);
        var pool = graph.GraphContexts.SelectMany(b => b.Context.ReferencePaths
                .Concat(b.Context.PhaseInputs.Select(i => i.Path))
                .Concat(b.Context.CompilerDocuments.Select(d => d.ResolvedPath!))
                .Concat(b.Context.ProtectedPaths)).Distinct(PathIdentityPolicy.Current.Comparer)
            .ToDictionary(PathKey, p => p, StringComparer.Ordinal);
        string Resolve(SavedPath path)
        {
            token.ThrowIfCancellationRequested();
            if (path is null || string.IsNullOrWhiteSpace(path.Identity)) throw Refuse();
            string physical;
            if (path.Relative is not null)
            {
                string relative;
                try { relative = CanonicalIdentity.NormalizeLogicalPath(path.Relative); }
                catch (ArgumentException) { throw Refuse(); }
                if (relative != path.Relative || Path.IsPathRooted(relative) || relative.Split('/').Any(p => p is ".." or ".")) throw Refuse();
                physical = Path.GetFullPath(relative, root);
                if (!physical.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw Refuse();
            }
            else if (!pool.TryGetValue(path.Identity, out physical!)) throw Refuse();
            if (PathKey(physical) != path.Identity) throw Refuse();
            return physical;
        }
        CompilerOutput Output(SavedOutput output)
        {
            if (output is null || output.Length < 0 || output.Length > CompilerPhaseSnapshot.MaximumFileBytes ||
                output.Sha256 is null || output.Sha256.Length != 64) throw Refuse();
            return new(Resolve(output.Path), output.Sha256, output.Length);
        }
        var accepted = new Dictionary<string, CompilerObservation>(StringComparer.Ordinal);
        var testRecipeArtifacts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var saved in recipe.Nodes)
        {
            token.ThrowIfCancellationRequested();
            if (saved is null || saved.Parameters is null || saved.Copies is null ||
                saved.Parameters.Length > 7 || saved.Copies.Length > CompilerPhaseSnapshot.MaximumInputs) throw Refuse();
            var node = graph.BuildGraph.SingleOrDefault(n => n.Id == saved.Id) ?? throw Refuse();
            var parameters = saved.Parameters.Select(p =>
            {
                if (p is null || p.Items is null || p.Items.Length > CompilerPhaseSnapshot.MaximumInputs) throw Refuse();
                if (p.ReferenceProperties is null || p.ReferenceProperties.Length > CompilerPhaseSnapshot.MaximumInputs ||
                    p.ReferenceProperties.Length != 0 && (p.Name != "References" || p.ReferenceProperties.Length != p.Items.Length)) throw Refuse();
                var items = p.Items.Select(Resolve).ToArray();
                var references = p.ReferenceProperties.Select((reference, index) =>
                {
                    if (reference is null || Resolve(reference.Item) != items[index]) throw Refuse();
                    return new CompilerReferenceProperties(items[index], CompilerReferenceSnapshot.NormalizeAliases(reference.Aliases), reference.EmbedInteropTypes);
                }).ToArray();
                return new CompilerParameter(p.Name, items) { ReferenceProperties = references };
            }).ToArray();
            var observation = new CompilerObservation(node.ProjectPath, parameters)
            {
                GraphId = node.Id,
                Assembly = Output(saved.Assembly),
                Pdb = Output(saved.Pdb),
                ReferenceAssembly = saved.ReferenceAssembly is null ? null : Output(saved.ReferenceAssembly),
                RuntimeCopies = saved.Copies.Select(c => c is null ? throw Refuse() : new CompilerCopy(Resolve(c.Source), Resolve(c.Destination))).ToArray()
            };
            if (saved.CompilerRecipe is { } retained)
            {
                var binding = graph.GraphContexts.Single(b => b.GraphId == saved.Id);
                var excludedTest = binding.IsTestProject && !binding.Context.ExclusionPolicy.IncludeTests;
                var sdk = graph.SelectedSdk ?? throw Refuse();
                observation = RestoreCurrentRecipe(retained, observation, root, sdk, token);
                if (excludedTest)
                {
                    var id = "test-recipe-" + saved.Id;
                    var metadata = bundle.Manifest.Artifacts.Where(artifact => artifact.Id == id).ToArray();
                    if (metadata.Length != 1 || metadata[0].Kind != TestRecipeKind || metadata[0].ContextId is not null ||
                        metadata[0].BuildId is not null || metadata[0].ExecutionId is not null || metadata[0].Format != "json" ||
                        metadata[0].CoordinateKind is not null || !testRecipeArtifacts.Add(id) ||
                        !bundle.Manifest.Artifacts.Any(artifact => artifact.Id == "required-test-" + saved.Id + "-trx" && artifact.Kind == "required-test-result") ||
                        bundle.Manifest.Artifacts.Any(artifact => artifact.BuildId == "build-" + saved.Id &&
                            artifact.Kind is CompilerRecipePayload.Kind or CompilerRecipePayload.UnsupportedKind or CompilerRecipePayload.ImageKind or CompilerRecipePayload.ClosureKind) ||
                        !TryCaptureTestRecipe(observation, root, LogicalContextIdentity.Create(root, node), saved.Id, token, out var expected) ||
                        !bundle.Bytes.TryGetValue(metadata[0].Locator, out var actual) || !actual.AsSpan().SequenceEqual(expected.AsSpan())) throw Refuse();
                }
                else
                {
                    var supported = bundle.Manifest.Artifacts.Where(artifact => artifact.BuildId == "build-" + saved.Id && artifact.Kind == CompilerRecipePayload.Kind).ToArray();
                    var unsupported = bundle.Manifest.Artifacts.Where(artifact => artifact.BuildId == "build-" + saved.Id && artifact.Kind == CompilerRecipePayload.UnsupportedKind).ToArray();
                    var expectedKind = retained.UnsupportedReasons?.Count == 0 ? CompilerRecipePayload.Kind : CompilerRecipePayload.UnsupportedKind;
                    var selected = supported.Concat(unsupported).ToArray();
                    if (selected.Length != 1 || selected[0].Kind != expectedKind || selected[0].ContextId != LogicalContextIdentity.Create(root, node) ||
                        selected[0].ExecutionId is not null || selected[0].Format != "json" || selected[0].CoordinateKind is not null) throw Refuse();
                }
            }
            else if (bundle.Manifest.Artifacts.Any(artifact => artifact.BuildId == "build-" + saved.Id &&
                artifact.Kind is CompilerRecipePayload.Kind or CompilerRecipePayload.UnsupportedKind or CompilerRecipePayload.ImageKind or CompilerRecipePayload.ClosureKind))
                throw Refuse(); // A required recipe cannot disappear on prior-graph import.
            if (!accepted.TryAdd(saved.Id, observation)) throw Refuse();
        }
        var retainedTests = bundle.Manifest.Artifacts.Where(artifact => artifact.Kind == TestRecipeKind).ToArray();
        if (retainedTests.Length != testRecipeArtifacts.Count || !testRecipeArtifacts.SetEquals(retainedTests.Select(artifact => artifact.Id))) throw Refuse();
        return accepted;
    }

    internal static CompilerObservation RestoreCurrentRecipe(CompilerRetainedRecipe retained, CompilerObservation prior,
        string root, ProjectContextSdkResolution sdk, CancellationToken token)
    {
        // Validate even unsupported retained data before deciding whether any
        // current-image claim exists. Unsupported public DATA has no image files.
        var restored = CompilerRecipePayload.Restore(retained, prior, root, sdk, token);
        if (retained.UnsupportedReasons.Count == 0 && retained.Observation.Images.Count != 0)
            RequireCurrentRecipeImages(retained.Observation.Images, sdk, token);
        return restored;
    }

    private static string Fingerprint(ProjectContextLoadResult graph, CancellationToken token)
    {
        var values = new List<string>();
        var cache = new Dictionary<string, string>(PathIdentityPolicy.Current.Comparer);
        var readPaths = new CompilerPathBatch(token, () => new("compiler.outputsUnproven",
            "A compiler input path changed during prior-build fingerprinting."));
        long total = 0;
        foreach (var node in CompilerGraphExecution.ValidateAndFreeze(graph))
        {
            token.ThrowIfCancellationRequested();
            var context = graph.GraphContexts.Single(b => b.GraphId == node.Id).Context;
            values.Add(CanonicalIdentity.Tuple("node", node.Id, PathKey(node.ProjectPath),
                CanonicalIdentity.Set("dependencies", node.Dependencies), context.ContextId,
                context.LanguageVersion.ToString(), context.SourceKind.ToString(),
                CanonicalIdentity.Set("symbols", context.PreprocessorSymbols)));
            values.AddRange(node.GlobalProperties.Select(p => CanonicalIdentity.Tuple("property", node.Id, p.Key, p.Value)));
            foreach (var input in context.PhaseInputs)
            {
                token.ThrowIfCancellationRequested();
                if (values.Count >= CompilerPhaseSnapshot.MaximumInputs) throw Refuse();
                if (!cache.TryGetValue(input.Path, out var hash))
                {
                    if (Directory.Exists(input.Path)) throw Refuse();
                    // Missing probes are obligations too; an appeared probe changes the digest.
                    hash = File.Exists(input.Path)
                        ? CanonicalIdentity.Sha256(CompilerOutputBinding.Read(input.Path, ref total, token, readPaths).AsSpan()) : "absent";
                    cache.Add(input.Path, hash);
                }
                values.Add(CanonicalIdentity.Tuple("phase", node.Id, input.Role, PathKey(input.Path),
                    input.ExpectedGenerated.ToString(), hash));
            }
            values.AddRange(context.CompilerDocuments.Select(d => CanonicalIdentity.Tuple("document", node.Id,
                PathKey(d.ResolvedPath!), d.ContentIdentity, d.IsGenerated.ToString())));
            foreach (var reference in context.ReferencePaths)
            {
                token.ThrowIfCancellationRequested();
                if (values.Count >= CompilerPhaseSnapshot.MaximumInputs) throw Refuse();
                if (!cache.TryGetValue(reference, out var hash))
                {
                    hash = CanonicalIdentity.Sha256(CompilerOutputBinding.Read(reference, ref total, token, readPaths).AsSpan());
                    cache.Add(reference, hash);
                }
                values.Add(CanonicalIdentity.Tuple("reference", node.Id, PathKey(reference), hash));
            }
        }
        readPaths.Complete();
        return CanonicalIdentity.Set(Provider, values);
    }
    private static SavedPath SavePath(string root, string path)
    {
        var physical = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, physical).Replace('\\', '/');
        var inside = relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        return new(inside ? CanonicalIdentity.NormalizeLogicalPath(relative) : null, PathKey(physical));
    }
    private static string PathKey(string path) => CanonicalIdentity.Tuple("prior-physical-path",
        OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path));
    private static void ValidateJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw Refuse(); ValidateJson(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateJson(item);
    }
    private static PolicyException Refuse() => new("check.priorBuildInvalid",
        "Explicit prior compiler graph evidence is missing, unsupported, stale or does not match the complete current evaluated graph.");
}
