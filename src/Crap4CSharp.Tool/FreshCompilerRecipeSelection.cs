namespace Crap4CSharp.Core;

// Invocation-private loss of optional DATA only. Never serialized, reconstructed
// from a bundle, or substituted for the original compiler/physical/native proof.
internal sealed class FreshCompilerRecipeSelection
{
    private enum Loss { Original, SizeUnsupported, ImagesChanged, SizeAfterImagesChanged, Absent }
    private sealed record Entry(CompilerObservation Original, CompilerObservation Snapshot, Loss Disposition);
    private readonly IReadOnlyList<CompilerObservation> ownerObservations;
    private readonly CompilerRetainedProof? ownerProof;
    private readonly IReadOnlyDictionary<string, Entry> entries;
    private FreshCompilerRecipeSelection(CompilerGraphPhaseResult owner, Dictionary<string, Entry> selected)
    { ownerObservations = owner.Observations; ownerProof = owner.RetainedProof; entries = selected; }

    internal static FreshCompilerRecipeSelection Create(CompilerGraphPhaseResult owner,
        IReadOnlyDictionary<string, CompilerObservation> selected, string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var originals = owner.ByGraphId();
        if (originals.Count != selected.Count || !originals.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(selected.Keys)) throw Refuse();
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var (id, original) in originals)
        {
            token.ThrowIfCancellationRequested();
            var candidate = selected[id];
            if (candidate.ProjectPath != original.ProjectPath || candidate.GraphId != original.GraphId ||
                !ReferenceEquals(candidate.Parameters, original.Parameters) || !ReferenceEquals(candidate.RuntimeCopies, original.RuntimeCopies) ||
                candidate.Assembly != original.Assembly || candidate.Pdb != original.Pdb || candidate.ReferenceAssembly != original.ReferenceAssembly) throw Refuse();
            CompilerRecipeObservation.RequireKnownIntegrity(original, token);
            if (original.InvocationRecipe is not null) _ = CompilerRecipePayload.Classify(original, root, token);
            Loss loss;
            if (OptionalEqual(original, candidate)) loss = Loss.Original;
            else if (OptionalEqual(CompilerRecipeObservation.WithoutOptionalRecipe(original, token), candidate)) loss = Loss.Absent;
            else if (original.InvocationRecipe is not null && OptionalEqual(CompilerRecipePayload.SizeUnsupported(original), candidate)) loss = Loss.SizeUnsupported;
            else if (original.InvocationRecipe is not null && CompilerRecipePayload.Classify(original, root, token).IsSupported &&
                OptionalEqual(CompilerRecipePayload.ImagesChangedUnsupported(original, token), candidate)) loss = Loss.ImagesChanged;
            else if (original.InvocationRecipe is not null && CompilerRecipePayload.Classify(original, root, token).IsSupported &&
                OptionalEqual(CompilerRecipePayload.SizeUnsupported(CompilerRecipePayload.ImagesChangedUnsupported(original, token)), candidate)) loss = Loss.SizeAfterImagesChanged;
            else throw Refuse();
            entries.Add(id, new(original, Snapshot(original), loss));
        }
        return new(owner, entries);
    }

    internal CompilerObservation OptionalView(CompilerGraphPhaseResult current, CompilerObservation original,
        string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(current.Observations, ownerObservations) || !ReferenceEquals(current.RetainedProof, ownerProof) ||
            original.GraphId is null || !entries.TryGetValue(original.GraphId, out var entry) ||
            !ReferenceEquals(entry.Original, original) || !OptionalEqual(entry.Snapshot, original)) throw Refuse();
        CompilerRecipeObservation.RequireKnownIntegrity(original, token);
        if (original.InvocationRecipe is not null) _ = CompilerRecipePayload.Classify(original, root, token);
        return entry.Disposition switch
        {
            Loss.Original => original,
            Loss.Absent => CompilerRecipeObservation.WithoutOptionalRecipe(original, token),
            Loss.SizeUnsupported => CompilerRecipePayload.SizeUnsupported(original),
            Loss.ImagesChanged when CompilerRecipePayload.Classify(original, root, token).IsSupported =>
                CompilerRecipePayload.ImagesChangedUnsupported(original, token),
            Loss.SizeAfterImagesChanged when CompilerRecipePayload.Classify(original, root, token).IsSupported =>
                CompilerRecipePayload.SizeUnsupported(CompilerRecipePayload.ImagesChangedUnsupported(original, token)),
            _ => throw Refuse()
        };
    }

    private static CompilerObservation Snapshot(CompilerObservation value) => value with
    {
        CommandLineArguments = value.CommandLineArguments.ToArray(),
        EmitParameters = value.EmitParameters.Select(parameter => parameter with
        { Items = parameter.Items.ToArray(), ReferenceProperties = parameter.ReferenceProperties.ToArray() }).ToArray(),
        CompilerImages = value.CompilerImages.ToArray(),
        RetainedUnsupportedRecipe = value.RetainedUnsupportedRecipe?.ToArray(),
        InvocationRecipe = value.InvocationRecipe is not { } recipe ? null : recipe with
        { Arguments = recipe.Arguments.ToArray(), Options = recipe.Options.ToArray(), Images = recipe.Images.ToArray(), UnsupportedReasons = recipe.UnsupportedReasons.ToArray() }
    };

    private static bool OptionalEqual(CompilerObservation first, CompilerObservation second)
    {
        if (!first.CommandLineArguments.SequenceEqual(second.CommandLineArguments, StringComparer.Ordinal) ||
            !first.CompilerImages.SequenceEqual(second.CompilerImages) || first.EmitParameters.Count != second.EmitParameters.Count ||
            (first.RetainedUnsupportedRecipe is null) != (second.RetainedUnsupportedRecipe is null) ||
            first.RetainedUnsupportedRecipe is not null && !first.RetainedUnsupportedRecipe.SequenceEqual(second.RetainedUnsupportedRecipe!, StringComparer.Ordinal)) return false;
        for (var index = 0; index < first.EmitParameters.Count; index++)
        {
            var a = first.EmitParameters[index]; var b = second.EmitParameters[index];
            if (a.Name != b.Name || !a.Items.SequenceEqual(b.Items, StringComparer.Ordinal) || !a.ReferenceProperties.SequenceEqual(b.ReferenceProperties)) return false;
        }
        var x = first.InvocationRecipe; var y = second.InvocationRecipe;
        return x is null ? y is null : y is not null && x.TaskAssociationSha256 == y.TaskAssociationSha256 &&
            x.TaskBinding == y.TaskBinding && x.CommandBinding == y.CommandBinding && x.Arguments.SequenceEqual(y.Arguments) &&
            x.Options.SequenceEqual(y.Options) && x.Images.SequenceEqual(y.Images) && x.UnsupportedReasons.SequenceEqual(y.UnsupportedReasons, StringComparer.Ordinal);
    }
    private static ProjectContextException Refuse() => new("compiler.captureSelectionUnproven",
        "Optional capture selection must only remove DATA from the same original compiler/task/argv observation.");
}
