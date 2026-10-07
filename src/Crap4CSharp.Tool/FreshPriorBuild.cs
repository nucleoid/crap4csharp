using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Crap4CSharp.Core;

// Explicit prior compiler evidence only: this never skips fresh tests or asserts
// reusable completed-test evidence. Physical paths/properties stay private.
internal static class FreshPriorBuild
{
    internal const string Kind = "prior-compiler-graph";
    private const string Provider = "finite-sdk-prior-compiler-v1";
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24
    };
    internal sealed record SavedPath(string? Relative, string Identity);
    internal sealed record SavedParameter(string Name, SavedPath[] Items);
    internal sealed record SavedOutput(SavedPath Path, string Sha256, long Length);
    internal sealed record SavedCopy(SavedPath Source, SavedPath Destination);
    internal sealed record SavedNode(string Id, SavedParameter[] Parameters, SavedOutput Assembly,
        SavedOutput Pdb, SavedOutput? ReferenceAssembly, SavedCopy[] Copies);
    internal sealed record Recipe(string Provider, string Workspace, string PhaseHash, SavedNode[] Nodes);

    internal static ImmutableArray<byte> Capture(FreshExecutionEvidence execution, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var graph = execution.Compiler.FinalGraph;
        var root = execution.Preflight.Root;
        var targets = graph.GraphContexts.Select(b => b.Context.EvaluatedTargetPath!)
            .ToHashSet(PathIdentityPolicy.Current.Comparer);
        var observations = execution.Compiler.Observations.ToDictionary(o => o.ProjectPath, PathIdentityPolicy.Current.Comparer);
        SavedOutput Output(CompilerOutput output) => new(SavePath(root, output.Path), output.Sha256, output.Length);
        var nodes = graph.BuildGraph.Select(node =>
        {
            token.ThrowIfCancellationRequested();
            var observed = observations[node.ProjectPath];
            var directory = Path.GetDirectoryName(node.ProjectPath)!;
            return new SavedNode(node.Id, observed.Parameters.Select(p => new SavedParameter(p.Name,
                    p.Items.Select(i => SavePath(root, Path.GetFullPath(i, directory))).ToArray())).ToArray(),
                Output(observed.Assembly ?? throw Refuse()), Output(observed.Pdb ?? throw Refuse()),
                observed.ReferenceAssembly is null ? null : Output(observed.ReferenceAssembly),
                observed.RuntimeCopies.Where(c => targets.Contains(c.Source))
                    .Select(c => new SavedCopy(SavePath(root, c.Source), SavePath(root, c.Destination))).ToArray());
        }).ToArray();
        var data = JsonSerializer.SerializeToUtf8Bytes(new Recipe(Provider, PathKey(root), Fingerprint(graph, token), nodes), Json);
        if (data.Length > MaximumBytes) throw Refuse();
        return ImmutableArray.Create(data);
    }

    internal static IReadOnlyDictionary<string, CompilerObservation> Load(ArtifactBundle bundle,
        ProjectContextLoadResult graph, string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ProvenanceVerifier.VerifyCapture(bundle.Manifest, bundle.Bytes).Status != ProvenanceStatus.Captured)
            throw Refuse();
        var artifacts = bundle.Manifest.Artifacts.Where(a => a.Kind == Kind).ToArray();
        if (artifacts.Length != 1 || !bundle.Bytes.TryGetValue(artifacts[0].Locator, out var data) ||
            data.Length > MaximumBytes) throw Refuse();
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
        foreach (var saved in recipe.Nodes)
        {
            token.ThrowIfCancellationRequested();
            if (saved is null || saved.Parameters is null || saved.Copies is null ||
                saved.Parameters.Length > 7 || saved.Copies.Length > CompilerPhaseSnapshot.MaximumInputs) throw Refuse();
            var node = graph.BuildGraph.SingleOrDefault(n => n.Id == saved.Id) ?? throw Refuse();
            var parameters = saved.Parameters.Select(p =>
            {
                if (p is null || p.Items is null || p.Items.Length > CompilerPhaseSnapshot.MaximumInputs) throw Refuse();
                return new CompilerParameter(p.Name, p.Items.Select(Resolve).ToArray());
            }).ToArray();
            var observation = new CompilerObservation(node.ProjectPath, parameters)
            {
                Assembly = Output(saved.Assembly), Pdb = Output(saved.Pdb),
                ReferenceAssembly = saved.ReferenceAssembly is null ? null : Output(saved.ReferenceAssembly),
                RuntimeCopies = saved.Copies.Select(c => c is null ? throw Refuse() : new CompilerCopy(Resolve(c.Source), Resolve(c.Destination))).ToArray()
            };
            if (!accepted.TryAdd(saved.Id, observation)) throw Refuse();
        }
        return accepted;
    }

    private static string Fingerprint(ProjectContextLoadResult graph, CancellationToken token)
    {
        var values = new List<string>();
        var cache = new Dictionary<string, string>(PathIdentityPolicy.Current.Comparer);
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
                        ? CanonicalIdentity.Sha256(CompilerOutputBinding.Read(input.Path, ref total, token).AsSpan()) : "absent";
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
                    hash = CanonicalIdentity.Sha256(CompilerOutputBinding.Read(reference, ref total, token).AsSpan());
                    cache.Add(reference, hash);
                }
                values.Add(CanonicalIdentity.Tuple("reference", node.Id, PathKey(reference), hash));
            }
        }
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
