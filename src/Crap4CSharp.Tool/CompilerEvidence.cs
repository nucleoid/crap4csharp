using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crap4CSharp.Core;

// Private execution transport. Only explicitly typed, bound compiler-path DATA is exported by the recipe.
internal sealed record CompilerParameter(string Name, IReadOnlyList<string> Items)
{
    public IReadOnlyList<CompilerReferenceProperties> ReferenceProperties { get; init; } = [];
}
internal sealed record CompilerReferenceProperties(string ItemSpec, string? Aliases, bool EmbedInteropTypes);
internal sealed record CompilerOutput(string Path, string Sha256, long Length);
internal sealed record CompilerCopy(string Source, string Destination);
internal sealed record CompilerObservation(string ProjectPath, IReadOnlyList<CompilerParameter> Parameters)
{
    // Parent-assigned owner after the once-scheduled compiler invocation is bound.
    // Not worker-asserted evidence and not a replacement for parameter/output gates.
    internal string? GraphId { get; init; }
    public IReadOnlyList<CompilerCopy> RuntimeCopies { get; init; } = [];
    internal CompilerOutput? Assembly { get; init; }
    internal CompilerOutput? Pdb { get; init; }
    internal CompilerOutput? ReferenceAssembly { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompilerInvocationObservation? InvocationRecipe { get; init; }
    // Raw argv stays in private worker transport. The exporter admits each known
    // scalar/role before serialization; unknown option values are never exported.
    [JsonIgnore]
    public IReadOnlyList<string> CommandLineArguments { get; init; } = [];
    [JsonIgnore]
    public IReadOnlyList<CompilerParameter> EmitParameters { get; init; } = [];
    [JsonIgnore]
    public IReadOnlyList<CompilerImageFile> CompilerImages { get; init; } = [];
    // Private same-task diagnostic only; never public observation/recipe/prior DATA.
    [JsonIgnore]
    public string? CommandRouteDiagnostic { get; init; }
    // Set only by the existing prior-graph loader after its complete current
    // workspace/parameter/output gates. This is retained DATA, never a fresh Csc.
    [JsonIgnore]
    internal IReadOnlyList<string>? RetainedUnsupportedRecipe { get; init; }
}
internal sealed record CompilerReadRequest(string SdkPath, string Binlog, string ProjectPath)
{
    public string? HostPath { get; init; }
}
// Only this private worker envelope may serialize raw task argv/emit inputs.
internal sealed record CompilerReadResponse(CompilerObservation? Observation, string? FailureReason)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommandRouteDiagnostic { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? CommandLineArguments { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CompilerParameter>? EmitParameters { get; init; }
}
internal static class CompilerEvidence
{
    internal const string Command = "__crap4csharp-compiler-evidence-v1";
    internal const long MaximumBinlogBytes = 64 * 1024 * 1024;
    internal const int MaximumTransportBytes = 8 * 1024 * 1024;

    internal static async Task<CompilerObservation> BuildAsync(ProjectBuildGraphNode node, string ownedDirectory,
        TimeSpan timeout, CancellationToken cancellationToken)
        => await BuildAsync(node, ownedDirectory, timeout, cancellationToken, nativeProbe: false);

    internal static async Task<CompilerObservation> BuildAsync(ProjectBuildGraphNode node, string ownedDirectory,
        TimeSpan timeout, CancellationToken cancellationToken, bool nativeProbe, ProjectContextSdkResolution? frozenSdk = null,
        global::CompletedProofTiming? timing = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        var directory = Path.GetFullPath(ownedDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var log = Path.Combine(directory, "compiler.binlog");
        if (File.Exists(log)) throw new ProjectContextException("compiler.outputExists", "Compiler capture destination already exists.");
        if (log.IndexOfAny([';', ',']) >= 0)
            throw new ProjectContextException("compiler.outputPathUnsupported", "Compiler log path contains a logger delimiter.");
        ProjectContextSdkResolution sdk;
        using (timing?.Start("execution.build.sdk"))
            sdk = await SdkExecutionIdentity.ResolveForEffectAsync(Path.GetDirectoryName(node.ProjectPath)!, timeout, token, nativeProbe, frozenSdk);
        var arguments = new List<string> { "build", node.ProjectPath, "--no-restore", "--no-incremental", "-m:1", "-nr:false" };
        arguments.AddRange(MappedGlobalArguments.Create(node.GlobalProperties));
        arguments.Add("-p:BuildProjectReferences=false");
        arguments.Add("-p:UseSharedCompilation=false");
        arguments.Add("-p:ProvideCommandLineArgs=true");
        arguments.Add("-bl:" + log + ";ProjectImports=None");
        var compilerImages = await CaptureCompilerImages(sdk, token);
        LoggedProcessResult process;
        using (timing?.Start("execution.build.process"))
            process = await ProcessRunner.RunLoggedAsync(sdk.HostPath, arguments, Path.GetDirectoryName(node.ProjectPath)!,
                timeout, token, directory);
        if (!OperatingSystem.IsWindows() && File.Exists(log)) File.SetUnixFileMode(log, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (process.ExitCode != 0) throw new ProjectContextException("compiler.buildFailed", "The declared nonincremental build failed; inspect private owned logs.");
        CompilerObservation observation;
        using (timing?.Start("execution.build.evidence"))
            observation = await ReadAsync(log, node.ProjectPath, timeout, token, nativeProbe, sdk);
        var closingImages = await CaptureCompilerImages(sdk, token);
        var imageUnsupported = compilerImages.UnsupportedReason ?? closingImages.UnsupportedReason ??
            (!compilerImages.Files.SequenceEqual(closingImages.Files) ? "recipe.compilerImagesChanged" : null);
        if (observation.InvocationRecipe is { } recipe)
            observation = observation with { InvocationRecipe = recipe with
                { Images = imageUnsupported is null ? compilerImages.Files.Select(image => new CompilerImageIdentity(image.Role, image.Length, image.Sha256)).ToArray() : [],
                    UnsupportedReasons = imageUnsupported is null ? recipe.UnsupportedReasons :
                        recipe.UnsupportedReasons.Append(imageUnsupported).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() },
                CompilerImages = imageUnsupported is null ? compilerImages.Files : [] };
        using var outputTiming = timing?.Start("execution.build.outputs");
        var output = observation.Parameters.Single(p => p.Name == "OutputAssembly").Items.Single();
        var assembly = Path.GetFullPath(output, Path.GetDirectoryName(node.ProjectPath)!);
        var referenceParameters = observation.Parameters.Where(p => p.Name == "OutputRefAssembly").ToArray();
        if (referenceParameters.Length > 1 || referenceParameters.Any(p => p.Items.Count != 1 || string.IsNullOrWhiteSpace(p.Items[0])))
            throw new ProjectContextException("compiler.referenceOutputUnproven", "Actual reference output association is unsupported.");
        var reference = referenceParameters.Length == 0 ? null :
            await HashOutput(Path.GetFullPath(referenceParameters[0].Items[0], Path.GetDirectoryName(node.ProjectPath)!), token);
        return observation with { Assembly = await HashOutput(assembly, token),
            Pdb = await HashOutput(Path.ChangeExtension(assembly, ".pdb"), token), ReferenceAssembly = reference };
    }

    internal static async Task<CompilerObservation> ReadAsync(string binlog, string project, TimeSpan timeout,
        CancellationToken cancellationToken)
        => await ReadAsync(binlog, project, timeout, cancellationToken, nativeProbe: false);

    internal static async Task<CompilerObservation> ReadAsync(string binlog, string project, TimeSpan timeout,
        CancellationToken cancellationToken, bool nativeProbe, ProjectContextSdkResolution? frozenSdk = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        if (!File.Exists(binlog)) throw new ProjectContextException("compiler.binlogMissing", "The declared compiler log is missing.");
        if (new FileInfo(binlog).Length > MaximumBinlogBytes)
            throw new ProjectContextException("compiler.binlogLimit", "Compiler log is missing or exceeds the private input limit.");
        var sdk = await SdkExecutionIdentity.ResolveForEffectAsync(Path.GetDirectoryName(project)!, timeout, token, nativeProbe, frozenSdk);
        var scratch = Directory.CreateTempSubdirectory("crap4csharp-compiler-").FullName;
        try
        {
            var request = Path.Combine(scratch, "request.json");
            var response = Path.Combine(scratch, "response.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new CompilerReadRequest(sdk.Path,
                Path.GetFullPath(binlog), Path.GetFullPath(project)) { HostPath = sdk.HostPath }, ProjectContextLoader.JsonOptions), token);
            var process = await ProcessRunner.RunProtocolAsync(sdk.HostPath,
                [typeof(CompilerEvidence).Assembly.Location, Command, request, response], Path.GetDirectoryName(project)!, timeout,
                token, maximumBytesPerStream: 4096);
            if (!File.Exists(response) || new FileInfo(response).Length > MaximumTransportBytes)
                throw new ProjectContextException("compiler.protocolInvalid", "Compiler worker did not return bounded evidence.");
            var result = JsonSerializer.Deserialize<CompilerReadResponse>(await File.ReadAllTextAsync(response, token), ProjectContextLoader.JsonOptions);
            if (result?.FailureReason is { } reason) throw new ProjectContextException(reason, "Actual compiler evidence was refused.");
            if (process.ExitCode != 0 || result?.Observation is null)
                throw new ProjectContextException("compiler.protocolInvalid", "Compiler worker failed.");
            return result.Observation with { CommandLineArguments = result.CommandLineArguments ?? [], EmitParameters = result.EmitParameters ?? [],
                CommandRouteDiagnostic = CompilerRecipeObservation.NormalizeCommandRouteDiagnostic(result.CommandRouteDiagnostic) };
        }
        finally { Directory.Delete(scratch, true); }
    }

    internal sealed record CompilerImageCapture(IReadOnlyList<CompilerImageFile> Files, string? UnsupportedReason);
    internal static async Task<CompilerImageCapture> CaptureCompilerImages(ProjectContextSdkResolution sdk, CancellationToken token)
    {
        var result = new List<CompilerImageFile>();
        foreach (var (role, path) in CompilerImagePaths(sdk))
        {
            token.ThrowIfCancellationRequested();
            CompilerOutput file;
            try { file = await HashOutput(path, token); }
            catch (ProjectContextException failure) when (failure.Reason is "compiler.outputMissing" or "compiler.outputUnproven" or "compiler.outputLimit" or "compiler.outputMutated")
            { return new([], "recipe.compilerImageUnavailable"); }
            result.Add(new(role, file.Path, file.Length, file.Sha256));
        }
        return new(result.AsReadOnly(), null);
    }

    // Exactly the current SDK-selected pool, not paths supplied by a saved recipe.
    internal static IReadOnlyList<(string Role, string Path)> CompilerImagePaths(ProjectContextSdkResolution sdk)
    {
        List<(string, string)> paths = [
            ("dotnet-host", sdk.HostPath),
            ("csc", Path.Combine(sdk.Path, "Roslyn", "bincore", "csc.dll")),
            ("compiler-common", Path.Combine(sdk.Path, "Roslyn", "bincore", "Microsoft.CodeAnalysis.dll")),
            ("compiler-csharp", Path.Combine(sdk.Path, "Roslyn", "bincore", "Microsoft.CodeAnalysis.CSharp.dll")),
            ("compiler-deps", Path.Combine(sdk.Path, "Roslyn", "bincore", "csc.deps.json")),
            ("compiler-runtimeconfig", Path.Combine(sdk.Path, "Roslyn", "bincore", "csc.runtimeconfig.json")),
            ("sdk-csc-target", Path.Combine(sdk.Path, "Roslyn", "Microsoft.CSharp.Core.targets")),
            ("sdk-csc-task", Path.Combine(sdk.Path, "Roslyn", "Microsoft.Build.Tasks.CodeAnalysis.dll"))];
        if (OperatingSystem.IsWindows()) paths.Add(("csc-launcher", Path.Combine(sdk.Path, "Roslyn", "bincore", "csc.exe")));
        return paths;
    }

    private static async Task<CompilerOutput> HashOutput(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ProjectContextException("compiler.outputUnproven", "Reparse-point compiler outputs are unsupported.");
            if (!ArtifactBundle.IsRegularInput(path))
                throw new ProjectContextException("compiler.outputMissing", "An actual compiler output is not a regular file.");
            var time = File.GetLastWriteTimeUtc(path).Ticks;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stream.Length;
            if (!stream.CanSeek || length > CompilerPhaseSnapshot.MaximumFileBytes)
                throw new ProjectContextException("compiler.outputLimit", "Compiler output capture exceeded its finite file byte limit.");
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long total = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, token);
                if (read == 0) break;
                total += read;
                if (total > length) throw new ProjectContextException("compiler.outputMutated", "A compiler output changed during capture.");
                hash.AppendData(buffer, 0, read);
            }
            if (total != length || stream.Length != length || File.GetLastWriteTimeUtc(path).Ticks != time)
                throw new ProjectContextException("compiler.outputMutated", "A compiler output changed during capture.");
            return new(path, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new ProjectContextException("compiler.outputMissing", "An actual compiler output could not be captured."); }
    }

}
