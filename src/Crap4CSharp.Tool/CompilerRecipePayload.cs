using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;

namespace Crap4CSharp.Core;

// Compiler-path identities below are bounded DATA from the actual selected task,
// not permission for a consumer to open paths or execute captured code. All byte
// reads here use already-bound source/reference/phase or parent-selected SDK roles.
// Explicit prior-graph DATA. Unknown option values are absent. Restoring this
// record never asserts that a fresh Csc ran or that a native image was loaded.
internal sealed record CompilerRetainedRecipe(CompilerInvocationObservation Observation,
    IReadOnlyList<string> KnownArguments, IReadOnlyList<CompilerParameter> EmitParameters, IReadOnlyList<string> UnsupportedReasons);

internal static class CompilerRecipePayload
{
    internal const string Kind = "compiler-invocation-recipe";
    internal const string Provider = "completed-csc-typed-input-recipe-v1";
    internal const string UnsupportedKind = "compiler-recipe-unsupported";
    internal const string ImageKind = "compiler-image";
    internal const string ClosureKind = "compiler-observed-input";
    internal sealed record File(string Role, string Path, string Id, ImmutableArray<byte> Bytes)
    {
        internal string Sha256 => CanonicalIdentity.Sha256(Bytes.AsSpan());
        internal string Locator => "inputs/" + Sha256 + ".bin";
    }
    internal sealed record ArgumentRole(int Ordinal, string Role, int? InputOrdinal, string? Name);
    internal sealed record Projection(IReadOnlyList<string> ScalarArguments, IReadOnlyList<string> EmbeddedSourcePaths,
        IReadOnlyList<ArgumentRole> ArgumentRoles, IReadOnlyList<string> UnsupportedReasons)
    {
        internal bool IsSupported => UnsupportedReasons.Count == 0;
    }

    // This narrow reader never follows a path discovered by lexing argv. Each
    // phase path has already passed CompilerInputBinding and phase reconciliation.
    internal sealed record FreshFileCapture(IReadOnlyList<File> Files, bool CompilerImagesChanged);

    // Saved verification keeps exact byte equality. Only the fresh optional
    // caller may replace a current SDK-image drift with unsupported DATA.
    internal static IReadOnlyList<File> CaptureFiles(CompilerObservation observation, ProjectAnalysisContext context,
        string buildId, Func<string, ImmutableArray<byte>> read, CancellationToken token)
    {
        var captured = CaptureFreshFiles(observation, context, buildId, read, token);
        if (captured.CompilerImagesChanged) throw Refuse();
        return captured.Files;
    }

    internal static FreshFileCapture CaptureFreshFiles(CompilerObservation observation, ProjectAnalysisContext context,
        string buildId, Func<string, ImmutableArray<byte>> read, CancellationToken token)
    {
        ValidateObservation(observation, token);
        var images = observation.CompilerImages;
        var count = observation.Parameters.Where(input => input.Name is "Analyzers" or "AnalyzerConfigFiles" or "AdditionalFiles").Sum(input => (long)input.Items.Count);
        if (count + 2 > CompilerPhaseSnapshot.MaximumInputs) throw Refuse();
        var phaseFiles = new List<File>();
        // Validate/read every phase input before SDK drift can select absence.
        // No I/O, cancellation, role mismatch or phase failure is downgraded.
        foreach (var (parameter, role) in new[] { ("Analyzers", "analyzer"), ("AnalyzerConfigFiles", "analyzerConfig"), ("AdditionalFiles", "additionalFile") })
        {
            var actual = observation.Parameters.SingleOrDefault(input => input.Name == parameter)?.Items ?? [];
            var permitted = context.PhaseInputs.Where(input => input.Role == role).Select(input => Canonical(input.Path)).ToHashSet(PathIdentityPolicy.Current.Comparer);
            var paths = actual.Select(path => Resolve(path, observation.ProjectPath)).ToArray();
            if (paths.Distinct(PathIdentityPolicy.Current.Comparer).Count() != paths.Length || !permitted.SetEquals(paths)) throw Refuse();
            for (var index = 0; index < paths.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                phaseFiles.Add(new(role, paths[index], buildId + "-recipe-" + role + "-" + index.ToString("D4", CultureInfo.InvariantCulture), read(paths[index])));
            }
        }
        var result = new List<File>();
        var changed = false;
        foreach (var role in new[] { "compiler-common", "compiler-csharp" })
        {
            token.ThrowIfCancellationRequested();
            var image = images.Single(item => item.Role == role);
            var bytes = read(image.Path);
            changed |= bytes.Length != image.Length || CanonicalIdentity.Sha256(bytes.AsSpan()) != image.Sha256;
            result.Add(new(role, image.Path, buildId + "-recipe-" + role, bytes));
        }
        if (changed) return new([], true);
        result.AddRange(phaseFiles); // Preserve the existing public file order.
        return new(result.ToArray(), false);
    }

    internal static CompilerObservation ImagesChangedUnsupported(CompilerObservation observation, CancellationToken token)
    {
        CompilerRecipeObservation.RequireKnownIntegrity(observation, token);
        return observation with
        {
            CompilerImages = [],
            InvocationRecipe = observation.InvocationRecipe! with
            {
                Images = [],
                UnsupportedReasons = observation.InvocationRecipe.UnsupportedReasons.Append("recipe.compilerImagesChanged")
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            }
        };
    }

    internal static IEnumerable<(string Id, string Kind)> ExpectedFiles(CompilerObservation observation, string buildId)
    {
        yield return (buildId + "-recipe-compiler-common", ImageKind);
        yield return (buildId + "-recipe-compiler-csharp", ImageKind);
        foreach (var (parameter, role) in new[] { ("Analyzers", "analyzer"), ("AnalyzerConfigFiles", "analyzerConfig"), ("AdditionalFiles", "additionalFile") })
        {
            var count = observation.Parameters.SingleOrDefault(input => input.Name == parameter)?.Items.Count ?? 0;
            for (var index = 0; index < count; index++)
                yield return (buildId + "-recipe-" + role + "-" + index.ToString("D4", CultureInfo.InvariantCulture), ClosureKind);
        }
    }

    internal static string FileKind(File file) => file.Role is "compiler-common" or "compiler-csharp" ? ImageKind : ClosureKind;

    internal static ImmutableArray<byte> Create(CompilerObservation observation, string contextId, string buildId,
        string workspaceRoot, IReadOnlyDictionary<string, ManifestInput> sources, IReadOnlyList<ManifestInput> references,
        IReadOnlyList<File> files, CancellationToken token)
        => TryCreate(observation, contextId, buildId, workspaceRoot, sources, references, files, token, out var bytes) ? bytes : throw Refuse();

    internal static bool TryCreate(CompilerObservation observation, string contextId, string buildId,
        string workspaceRoot, IReadOnlyDictionary<string, ManifestInput> sources, IReadOnlyList<ManifestInput> references,
        IReadOnlyList<File> files, CancellationToken token, out ImmutableArray<byte> result)
    {
        token.ThrowIfCancellationRequested();
        var projection = Classify(observation, workspaceRoot, token);
        if (!projection.IsSupported) throw Refuse();
        var observed = observation.InvocationRecipe!;
        var assembly = observation.Assembly ?? throw Refuse();
        var pdb = observation.Pdb ?? throw Refuse();
        var sourceParameter = observation.Parameters.Single(input => input.Name == "Sources");
        var actualReferences = CompilerReferenceSnapshot.ResolveSemanticReferences(observation, token);
        if (sourceParameter.Items.Count != sources.Count || references.Count != actualReferences.Count) throw Refuse();
        if (sources.Count > CompilerPhaseSnapshot.MaximumInputs || references.Count > CompilerPhaseSnapshot.MaximumInputs ||
            files.Count > CompilerPhaseSnapshot.MaximumInputs) throw Refuse();
        var sourceRows = sourceParameter.Items.Select((path, index) =>
        {
            token.ThrowIfCancellationRequested();
            var full = Resolve(path, observation.ProjectPath);
            if (!sources.TryGetValue(full, out var input) || input.Role is not ("source" or "compiler-source") || input.Encoding != "utf-8") throw Refuse();
            return new
            {
                path = full,
                input.Locator,
                input.Length,
                sha256 = "sha256:" + input.Sha256,
                ordinal = index,
                input.Role,
                input.LogicalPath,
                input.Encoding,
                input.Generated
            };
        }).ToArray();
        var referenceRows = references.Select((input, index) =>
        {
            token.ThrowIfCancellationRequested();
            var actual = actualReferences[index];
            if (input.Role != "reference" || input.ReferenceAliases != actual.Aliases || (input.EmbedInteropTypes ?? false) != actual.EmbedInteropTypes ||
                input.LogicalPath != CompilerReferenceSnapshot.LogicalReference(input.Sha256, actual.Aliases, actual.EmbedInteropTypes)) throw Refuse();
            return new
            {
                path = actual.Path,
                input.Locator,
                input.Length,
                sha256 = "sha256:" + input.Sha256,
                aliases = actual.Aliases?.Split(',') ?? [],
                embedInteropTypes = actual.EmbedInteropTypes,
                ordinal = index,
                input.LogicalPath
            };
        }).ToArray();
        object FileRow(File file) => new
        {
            file.Locator,
            length = file.Bytes.Length,
            sha256 = "sha256:" + file.Sha256,
            artifactId = file.Id,
            file.Role,
            compilerPath = file.Path
        };
        if (!files.Select(file => (file.Id, FileKind(file))).SequenceEqual(ExpectedFiles(observation, buildId))) throw Refuse();
        foreach (var file in files.Where(file => file.Role is "compiler-common" or "compiler-csharp"))
        {
            var image = observation.CompilerImages.Single(item => item.Role == file.Role);
            if (!Same(image.Path, file.Path) || file.Bytes.Length != image.Length || file.Sha256 != image.Sha256) throw Refuse();
        }
        var common = files.Single(file => file.Role == "compiler-common");
        var csharp = files.Single(file => file.Role == "compiler-csharp");
        object Output(CompilerOutput output, string id) => new
        {
            locator = "inputs/" + output.Sha256 + ".bin",
            output.Length,
            sha256 = "sha256:" + output.Sha256,
            artifactId = id,
            compilerPath = output.Path
        };
        var supported = CompilerRecipeBudget.TrySerialize(new
        {
            provider = Provider,
            disposition = "typed-input-data-only",
            contextId,
            buildId,
            baseDirectory = Canonical(Path.GetDirectoryName(observation.ProjectPath)!),
            scalarArguments = projection.ScalarArguments,
            sources = sourceRows,
            references = referenceRows,
            embeddedSourcePaths = projection.EmbeddedSourcePaths,
            compilerCommon = FileRow(common),
            compilerCSharp = FileRow(csharp),
            assembly = Output(assembly, buildId + "-pe"),
            pdb = Output(pdb, buildId + "-pdb"),
            referenceAssembly = observation.ReferenceAssembly is { } referenceOutput ? new
            { compilerPath = referenceOutput.Path, referenceOutput.Length, sha256 = "sha256:" + referenceOutput.Sha256 } : null,
            observedClosureArtifacts = files.Where(file => file.Role is not ("compiler-common" or "compiler-csharp")).Select(FileRow).ToArray(),
            observed.TaskAssociationSha256,
            observed.TaskBinding,
            observed.CommandBinding,
            images = observed.Images,
            arguments = observed.Arguments,
            argumentRoles = projection.ArgumentRoles,
            taskOptions = observed.Options,
            // These limits remain explicit: no loaded/prebirth image attestation,
            // body-origin proof or manifest/coverage capability is issued here.
            unsupportedAttestations = observed.UnsupportedReasons
        }, ProjectContextLoader.JsonOptions, CompilerEvidence.MaximumTransportBytes, out var bytes);
        result = supported ? ImmutableArray.Create(bytes) : [];
        return supported;
    }

    internal static Projection Classify(CompilerObservation observation, string workspaceRoot, CancellationToken token)
    {
        ValidateObservation(observation, token);
        if (observation.RetainedUnsupportedRecipe is { } retained) return new([], [], [], retained);
        var recipe = observation.InvocationRecipe!;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        if (recipe.UnsupportedReasons.Contains("recipe.sizeUnsupported", StringComparer.Ordinal)) reasons.Add("recipe.sizeUnsupported");
        if (recipe.UnsupportedReasons.Contains("recipe.compilerImageUnavailable", StringComparer.Ordinal)) reasons.Add("recipe.compilerImageUnavailable");
        if (recipe.UnsupportedReasons.Contains("recipe.compilerImagesChanged", StringComparer.Ordinal)) reasons.Add("recipe.compilerImagesChanged");
        if (recipe.TaskBinding == "unproven" || recipe.CommandBinding == "unproven")
        {
            if (!reasons.Contains("recipe.sizeUnsupported")) reasons.Add("recipe.bindingUnsupported");
            return new([], [], [], reasons.Order(StringComparer.Ordinal).ToArray());
        }
        // A proven same-task command still validates every recognized source,
        // reference and output role even if another optional feature is unsupported.
        var profile = ProjectArguments(observation, workspaceRoot, token);
        reasons.UnionWith(profile.UnsupportedReasons);
        if (recipe.UnsupportedReasons.Any(reason => reason is "recipe.languageVersionUnsupported" or "recipe.scalarOptionUnsupported" or "recipe.scalarOptionAmbiguous"))
            reasons.Add("recipe.scalarProfileUnsupported");
        return profile with { UnsupportedReasons = reasons.Order(StringComparer.Ordinal).ToArray() };
    }

    internal static CompilerObservation SizeUnsupported(CompilerObservation observation) =>
        CompilerRecipeObservation.OmitForSize(observation) with { RetainedUnsupportedRecipe = ["recipe.sizeUnsupported"] };

    internal static ImmutableArray<byte> Unsupported(CompilerObservation observation, string contextId, string buildId,
        Projection projection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (projection.IsSupported || !SafeReasons(projection.UnsupportedReasons)) throw Refuse();
        // Fixed codes only: no raw command, option name/value, image/phase byte
        // artifact or falsely consumable recipe is published for this profile.
        return ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(new
        {
            provider = Provider,
            disposition = "unsupported-input-profile",
            contextId,
            buildId,
            taskAssociationSha256 = observation.InvocationRecipe!.TaskAssociationSha256,
            reasons = projection.UnsupportedReasons
        }, ProjectContextLoader.JsonOptions));
    }

    internal static CompilerRetainedRecipe Retain(CompilerObservation observation, string root, CancellationToken token)
    {
        var profile = Classify(observation, root, token);
        return new(observation.InvocationRecipe!, profile.IsSupported ? observation.CommandLineArguments.ToArray() : [],
            profile.IsSupported ? observation.EmitParameters.Select(input => new CompilerParameter(input.Name,
                input.Items.Select(path => Resolve(path, observation.ProjectPath)).ToArray())).ToArray() : [], profile.UnsupportedReasons.ToArray());
    }

    internal static CompilerObservation Restore(CompilerRetainedRecipe retained, CompilerObservation prior, string root,
        ProjectContextSdkResolution selectedSdk, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (retained.Observation is null || retained.KnownArguments is null || retained.EmitParameters is null || retained.UnsupportedReasons is null ||
            retained.KnownArguments.Count > 32768 || retained.EmitParameters.Count > 3 || retained.UnsupportedReasons.Count > 6 ||
            retained.UnsupportedReasons.Count != 0 && (!SafeReasons(retained.UnsupportedReasons) || retained.KnownArguments.Count != 0 || retained.EmitParameters.Count != 0)) throw Refuse();
        var pool = CompilerEvidence.CompilerImagePaths(selectedSdk);
        var identities = retained.Observation.Images;
        var noImages = retained.UnsupportedReasons.Any(reason => reason is "recipe.sizeUnsupported" or "recipe.compilerImageUnavailable" or "recipe.compilerImagesChanged") && identities?.Count == 0;
        if (identities is null || !noImages && (identities.Count != pool.Count || !identities.Select(image => image.Role).SequenceEqual(pool.Select(image => image.Role)))) throw Refuse();
        var restored = prior with
        {
            InvocationRecipe = retained.Observation,
            CommandLineArguments = retained.KnownArguments.ToArray(),
            EmitParameters = retained.EmitParameters.ToArray(),
            CompilerImages = noImages ? [] : pool.Select((image, index) => new CompilerImageFile(image.Role, image.Path, identities[index].Length, identities[index].Sha256)).ToArray(),
            RetainedUnsupportedRecipe = retained.UnsupportedReasons.Count == 0 ? null : retained.UnsupportedReasons.ToArray()
        };
        var profile = Classify(restored, root, token);
        if (!profile.UnsupportedReasons.SequenceEqual(retained.UnsupportedReasons)) throw Refuse();
        return restored;
    }

    private static bool SafeReasons(IReadOnlyList<string> reasons) => reasons.Count is > 0 and <= 6 &&
        reasons.SequenceEqual(reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) &&
        reasons.All(reason => reason is "recipe.scalarProfileUnsupported" or "recipe.emitProfileUnsupported" or "recipe.bindingUnsupported" or
            "recipe.sizeUnsupported" or "recipe.compilerImageUnavailable" or "recipe.compilerImagesChanged");

    private static void ValidateObservation(CompilerObservation observation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var recipe = observation.InvocationRecipe ?? throw Refuse();
        if (recipe.Arguments is null || recipe.Options is null || recipe.Images is null || recipe.UnsupportedReasons is null ||
            recipe.Arguments.Any(argument => argument is null) || recipe.Images.Any(image => image is null) ||
            observation.CommandLineArguments is null || observation.EmitParameters is null || observation.CompilerImages is null ||
            recipe.TaskBinding is not ("selected-sdk-core-target" or "unproven") || recipe.CommandBinding is not
                ("selected-host-exec-csc-arguments-match" or "selected-sdk-csc-exe-arguments-match" or "unproven") ||
            recipe.UnsupportedReasons.Count is < 2 or > 16 ||
            !recipe.UnsupportedReasons.Contains("recipe.loadedCompilerAndTaskImageNotAttested", StringComparer.Ordinal) ||
            !recipe.UnsupportedReasons.Contains("recipe.runtimeLoadedImageNotAttested", StringComparer.Ordinal) ||
            recipe.UnsupportedReasons.Distinct(StringComparer.Ordinal).Count() != recipe.UnsupportedReasons.Count ||
            recipe.UnsupportedReasons.Any(reason => reason is not ("recipe.loadedCompilerAndTaskImageNotAttested" or "recipe.runtimeLoadedImageNotAttested" or
                "recipe.languageVersionUnsupported" or "recipe.scalarOptionUnsupported" or "recipe.scalarOptionAmbiguous" or
                "recipe.selectedSdkTaskFileUnproven" or "recipe.compilerCommandPrefixUnproven" or "recipe.argumentOutputMissing" or
                "recipe.commandEventMissing" or "recipe.commandArgumentsUnproven" or "recipe.sizeUnsupported" or
                "recipe.compilerImageUnavailable" or "recipe.compilerImagesChanged")) ||
            !CompilerRecipeObservation.SafeOptions(recipe.Options, token) || !Digest(recipe.TaskAssociationSha256) ||
            observation.Assembly is null || observation.Pdb is null) throw Refuse();
        // Missing/foreign optional task/prefix is not a recipe. Contradictory
        // binding assertions, late/duplicate terminals, and a PROVEN same-task
        // argv mismatch remain integrity errors; no broad exception downgrade.
        var bindingUnavailable = recipe.UnsupportedReasons.Any(reason => reason is "recipe.selectedSdkTaskFileUnproven" or
            "recipe.compilerCommandPrefixUnproven" or "recipe.argumentOutputMissing" or "recipe.commandEventMissing" or "recipe.sizeUnsupported");
        if ((recipe.TaskBinding == "unproven") != recipe.UnsupportedReasons.Contains("recipe.selectedSdkTaskFileUnproven", StringComparer.Ordinal) ||
            recipe.CommandBinding != "unproven" && recipe.Arguments.Count == 0 ||
            recipe.CommandBinding == "unproven" && !bindingUnavailable ||
            recipe.CommandBinding != "unproven" && (recipe.TaskBinding == "unproven" || bindingUnavailable ||
                recipe.UnsupportedReasons.Contains("recipe.commandArgumentsUnproven", StringComparer.Ordinal))) throw Refuse();
        if (observation.RetainedUnsupportedRecipe is { } retainedReasons)
        {
            if (observation.CommandLineArguments.Count != 0 || observation.EmitParameters.Count != 0 || !SafeReasons(retainedReasons) ||
                recipe.Arguments.Count > 32768 || recipe.Arguments.Sum(argument => (long)argument.Utf8Length) > 2 * 1024 * 1024 ||
                recipe.Arguments.Where((argument, index) => argument.Ordinal != index || argument.Utf8Length <= 0 || !Digest(argument.Sha256)).Any()) throw Refuse();
        }
        else if (!CompilerRecipeObservation.Identities(observation.CommandLineArguments, token).SequenceEqual(recipe.Arguments)) throw Refuse();
        foreach (var output in new[] { observation.Assembly, observation.Pdb, observation.ReferenceAssembly }.OfType<CompilerOutput>())
            if (output.Length <= 0 || output.Length > CompilerPhaseSnapshot.MaximumFileBytes || !Digest(output.Sha256) || Canonical(output.Path) != output.Path) throw Refuse();
        string[] roles = ["dotnet-host", "csc", "compiler-common", "compiler-csharp", "compiler-deps", "compiler-runtimeconfig", "sdk-csc-target", "sdk-csc-task"];
        var expected = OperatingSystem.IsWindows() ? roles.Append("csc-launcher").ToArray() : roles;
        var images = observation.CompilerImages;
        var unavailableImages = recipe.UnsupportedReasons.Any(reason => reason is "recipe.sizeUnsupported" or "recipe.compilerImageUnavailable" or "recipe.compilerImagesChanged") && images.Count == 0 && recipe.Images.Count == 0;
        if (!unavailableImages && (!images.Select(image => image.Role).SequenceEqual(expected) ||
            !images.Select(image => new CompilerImageIdentity(image.Role, image.Length, image.Sha256)).SequenceEqual(recipe.Images))) throw Refuse();
        foreach (var image in images)
            if (image.Length <= 0 || image.Length > CompilerPhaseSnapshot.MaximumFileBytes || !Digest(image.Sha256) || Canonical(image.Path) != image.Path) throw Refuse();
    }
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);

    internal static Projection ProjectArguments(CompilerObservation observation, string workspaceRoot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = CompilerRecipeObservation.Identities(observation.CommandLineArguments, token);
        var root = CanonicalDirectory(workspaceRoot);
        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        var sourcePaths = observation.Parameters.Single(input => input.Name == "Sources").Items.Select(path => Resolve(path, observation.ProjectPath)).ToArray();
        var references = CompilerReferenceSnapshot.ResolveSemanticReferences(observation, token);
        var embedded = observation.EmitParameters.SingleOrDefault(input => input.Name == "EmbeddedFiles")?.Items.Select(path => Resolve(path, observation.ProjectPath)).ToArray() ?? [];
        var optional = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["analyzer"] = Paths("Analyzers"),
            ["analyzerconfig"] = Paths("AnalyzerConfigFiles"),
            ["additionalfile"] = Paths("AdditionalFiles")
        };
        if (observation.EmitParameters.Count > 3 || observation.EmitParameters.Select(input => input.Name).Distinct(StringComparer.Ordinal).Count() != observation.EmitParameters.Count ||
            observation.EmitParameters.Any(input => input.Name is not ("EmbeddedFiles" or "PdbFile" or "GeneratedFilesOutputPath") ||
                input.ReferenceProperties.Count != 0 || input.Items.Count > CompilerPhaseSnapshot.MaximumInputs)) throw Refuse();
        foreach (var input in observation.EmitParameters.Where(input => input.Name != "EmbeddedFiles"))
        {
            if (input.Items.Count > 1) throw Refuse();
            foreach (var item in input.Items)
            {
                var path = Resolve(item, observation.ProjectPath);
                if (input.Name == "PdbFile" && !Same(path, observation.Pdb?.Path ?? throw Refuse())) throw Refuse();
                if (input.Name == "GeneratedFilesOutputPath" && !Inside(path, Path.GetDirectoryName(observation.Assembly?.Path ?? throw Refuse())!))
                    unsupported.Add("recipe.emitProfileUnsupported");
            }
        }
        var indices = optional.Keys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        var sourceIndex = 0; var referenceIndex = 0; var embeddedIndex = 0; var generated = 0;
        var scalar = new List<string>(); var roles = new List<ArgumentRole>();
        HashSet<string>? remainingReferenceAliases = null;
        if (sourcePaths.Distinct(PathIdentityPolicy.Current.Comparer).Count() != sourcePaths.Length ||
            embedded.Distinct(PathIdentityPolicy.Current.Comparer).Count() != embedded.Length) throw Refuse();
        if (embedded.Any(path => !sourcePaths.Contains(path, PathIdentityPolicy.Current.Comparer))) unsupported.Add("recipe.emitProfileUnsupported");
        var embedAll = false;
        for (var ordinal = 0; ordinal < observation.CommandLineArguments.Count; ordinal++)
        {
            token.ThrowIfCancellationRequested();
            var argument = observation.CommandLineArguments[ordinal];
            if (argument.Length is 0 or > 65536 || argument.Any(char.IsControl) || argument[0] == '@') throw Refuse();
            var colon = argument.IndexOf(':');
            var name = argument[0] == '/' ? argument[1..(colon < 0 ? argument.Length : colon)] : "";
            if (name == "embed" && colon < 0)
            { embedAll = true; unsupported.Add("recipe.emitProfileUnsupported"); roles.Add(new(ordinal, "unsupported-embedded-all", null, null)); continue; }
            var value = colon < 0 ? "" : name is "reference" or "link" ? argument[(colon + 1)..] : Unquote(argument[(colon + 1)..]);
            if (name is "reference" or "link")
            {
                if (referenceIndex >= references.Count) throw Refuse();
                var actual = references[referenceIndex]; var equals = value.IndexOf('=');
                var aliases = equals < 0 ? null : CompilerReferenceSnapshot.NormalizeCommandAliases(value[..equals]);
                var path = Unquote(equals < 0 ? value : value[(equals + 1)..]);
                if (!Same(Resolve(path, observation.ProjectPath), actual.Path) || (name == "link") != actual.EmbedInteropTypes) throw Refuse();
                remainingReferenceAliases ??= new(actual.Aliases?.Split(',') ?? ["global"], StringComparer.Ordinal);
                if (actual.Aliases?.Contains(',') == true) unsupported.Add("recipe.emitProfileUnsupported");
                // The actual SDK Csc emits one switch per alias; global has
                // no alias prefix. Require the exact set once before advancing.
                foreach (var alias in aliases?.Split(',') ?? ["global"])
                    if (!remainingReferenceAliases.Remove(alias)) throw Refuse();
                roles.Add(new(ordinal, "reference", referenceIndex, name));
                if (remainingReferenceAliases.Count == 0) { referenceIndex++; remainingReferenceAliases = null; }
                continue;
            }
            if (optional.TryGetValue(name, out var inputs))
            {
                var index = indices[name];
                if (index >= inputs.Length || !Same(Resolve(value, observation.ProjectPath), inputs[index])) throw Refuse();
                roles.Add(new(ordinal, name, index, name)); indices[name]++; continue;
            }
            if (name == "embed")
            {
                if (embeddedIndex >= embedded.Length || !Same(Resolve(value, observation.ProjectPath), embedded[embeddedIndex])) throw Refuse();
                roles.Add(new(ordinal, "embedded-source", embeddedIndex++, name)); continue;
            }
            if (name == "generatedfilesout")
            {
                var declared = observation.EmitParameters.SingleOrDefault(input => input.Name == "GeneratedFilesOutputPath")?.Items;
                if (++generated != 1 || declared?.Count != 1 || !Same(Resolve(value, observation.ProjectPath), Resolve(declared[0], observation.ProjectPath))) throw Refuse();
                if (!Inside(Resolve(value, observation.ProjectPath), Path.GetDirectoryName(observation.Assembly?.Path ?? throw Refuse())!))
                    unsupported.Add("recipe.emitProfileUnsupported");
                roles.Add(new(ordinal, "generated-output", null, name)); continue;
            }
            // A full Unix source starts with '/', so source membership is checked
            // before classifying an unfamiliar slash token as an unknown switch.
            if (sourceIndex < sourcePaths.Length && (colon < 0 || argument[0] != '/') && Same(Resolve(argument, observation.ProjectPath), sourcePaths[sourceIndex]))
            { roles.Add(new(ordinal, "source", sourceIndex++, null)); continue; }
            if (name is "out" or "refout" or "pdb")
            {
                if (!SafeScalar(argument, name, value, observation, root)) throw Refuse();
            }
            else if (!SafeScalar(argument, name, value, observation, root))
            {
                if (argument[0] != '/' || name.Length == 0) throw Refuse();
                // A repeated known Unix source is an integrity mismatch, not
                // an unfamiliar slash-option unsupported profile. Supported
                // scalar flags never enter this diagnostic-free branch.
                // Longer unknown switches keep their unsupported profile without
                // entering Resolve's stricter path extent. Absolute Unix source
                // tokens containing ':' retain the existing syntax limitation.
                if (argument.Length <= 16384 && colon < 0 && sourcePaths.Contains(Resolve(argument, observation.ProjectPath), PathIdentityPolicy.Current.Comparer)) throw Refuse();
                unsupported.Add("recipe.scalarProfileUnsupported");
                roles.Add(new(ordinal, "unsupported-scalar", null, null));
                continue; // Do not retain an unknown value in a projected object.
            }
            roles.Add(new(ordinal, "scalar", scalar.Count, name)); scalar.Add(argument);
        }
        if (sourceIndex != sourcePaths.Length || referenceIndex != references.Count || !embedAll && embeddedIndex != embedded.Length ||
            generated != (observation.EmitParameters.SingleOrDefault(input => input.Name == "GeneratedFilesOutputPath")?.Items.Count ?? 0) ||
            optional.Any(pair => indices[pair.Key] != pair.Value.Length) ||
            scalar.Count(argument => argument.StartsWith("/out:", StringComparison.Ordinal)) != 1 ||
            scalar.Count(argument => argument.StartsWith("/refout:", StringComparison.Ordinal)) != (observation.ReferenceAssembly is null ? 0 : 1)) throw Refuse();
        if (!scalar.Contains("/noconfig", StringComparer.Ordinal) || !scalar.Contains("/nostdlib+", StringComparer.Ordinal) ||
            !scalar.Contains("/target:library", StringComparer.Ordinal) || !scalar.Contains("/debug:portable", StringComparer.Ordinal) ||
            !scalar.Contains("/deterministic+", StringComparer.Ordinal)) unsupported.Add("recipe.emitProfileUnsupported");
        return new(scalar.ToArray(), embedded, roles.ToArray(), unsupported.Order(StringComparer.Ordinal).ToArray());
        string[] Paths(string parameter) => observation.Parameters.SingleOrDefault(input => input.Name == parameter)?.Items.Select(path => Resolve(path, observation.ProjectPath)).ToArray() ?? [];
    }

    private static bool SafeScalar(string argument, string name, string value, CompilerObservation observation, string root)
    {
        if (FlagArguments.Contains(argument)) return true;
        if (!argument.StartsWith('/')) return false;
        switch (name)
        {
            case "nowarn": case "warnaserror+": return value.Length > 0 && value.Split(',').All(DiagnosticId);
            case "errorreport": return value is "none" or "prompt" or "queue" or "send";
            case "warn": return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var warning) && warning is >= 0 and <= 9999;
            case "filealign": return value is "512" or "1024" or "2048" or "4096" or "8192";
            case "define": return value.Length > 0 && value.Split([';', ',']).All(SyntaxFacts.IsValidIdentifier);
            case "nullable": return value is "enable" or "disable" or "warnings" or "annotations";
            case "debug": return value == "portable";
            case "target": return value == "library";
            case "langversion": return CompilerRecipeObservation.SupportedLanguage(value);
            case "checksumalgorithm": return value is "SHA256" or "sha256" or "SHA1" or "sha1";
            case "features":
                var feature = value.Split('=', 2);
                return feature.Length == 2 && feature[0] is "InterceptorsNamespaces" or "InterceptorsPreviewNamespaces" &&
                    feature[1].Split(';').All(item => item.Length == 0 || item.Split('.').All(SyntaxFacts.IsValidIdentifier));
            case "out": return observation.Assembly is { } assembly && Same(Resolve(value, observation.ProjectPath), assembly.Path);
            case "refout": return observation.ReferenceAssembly is { } reference && Same(Resolve(value, observation.ProjectPath), reference.Path);
            case "pdb":
                var pdb = observation.EmitParameters.SingleOrDefault(input => input.Name == "PdbFile")?.Items;
                return observation.Pdb is { } output && pdb?.Count == 1 && Same(Resolve(value, observation.ProjectPath), output.Path) &&
                    Same(Resolve(pdb[0], observation.ProjectPath), output.Path);
            case "pathmap":
                var mappings = value.Split(',');
                return mappings.Length is > 0 and <= 64 && mappings.All(mapping =>
                {
                    var pair = mapping.Split('=', 2);
                    return pair.Length == 2 && Path.IsPathFullyQualified(pair[0]) && PlainMapPrefix(pair[0], root) && pair[1].StartsWith('/') &&
                        pair[1].Length <= 4096 && !pair[1].Contains('\\') && !pair[1].Split('/').Any(segment => segment is "." or "..");
                });
            default: return false;
        }
    }
    private static bool PlainMapPrefix(string path, string root)
    {
        // Unsupported raw compiler-option spelling is not a claimed canonical
        // input identity. Do not open it or infer an external root.
        try { return path == Path.GetFullPath(path) && Same(Path.TrimEndingDirectorySeparator(path), root); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static readonly HashSet<string> FlagArguments = new(StringComparer.Ordinal)
    {
        "/noconfig", "/nologo", "/unsafe-", "/unsafe+", "/checked-", "/checked+", "/fullpaths", "/nostdlib+",
        "/highentropyva+", "/highentropyva-", "/debug-", "/debug+", "/optimize+", "/optimize-", "/warnaserror+", "/warnaserror-",
        "/utf8output", "/deterministic+"
    };
    private static bool DiagnosticId(string value) => value.Length is > 0 and <= 64 && value.All(char.IsAsciiLetterOrDigit);
    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        if (value.Length == 0 || value.Contains('"') || value.Contains('\0')) throw Refuse();
        return value;
    }
    private static string Resolve(string value, string project)
    {
        value = Unquote(value);
        if (value.Length > 16384 || value.Any(char.IsControl)) throw Refuse();
        try { return Canonical(Path.GetFullPath(value, Path.GetDirectoryName(project)!)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { throw Refuse(); }
    }
    private static string CanonicalDirectory(string path) => Path.TrimEndingDirectorySeparator(Canonical(path));
    private static string Canonical(string path)
    {
        if (path.Length is 0 or > 16384 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) throw Refuse();
        try { if (path != Path.GetFullPath(path)) throw Refuse(); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { throw Refuse(); }
        return path;
    }
    private static bool Same(string first, string second) => PathIdentityPolicy.Current.Comparer.Equals(first, second);
    private static bool Inside(string path, string root) => Same(path, root) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static ProjectContextException Refuse() => new("compiler.recipeBindingUnproven", "Compiler recipe inputs, known scalar profile or current output association is unproven.");
}
