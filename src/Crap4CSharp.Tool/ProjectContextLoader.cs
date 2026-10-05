using System.Text.Json;
using System.Text.Json.Serialization;
using Crap4CSharp.Core;

namespace Crap4CSharp.Core;

public sealed record ProjectContextLoadRequest(
    string Target,
    string Configuration,
    string? Platform,
    IReadOnlyList<string> Frameworks,
    bool IncludeTests,
    bool IncludeGenerated,
    TimeSpan Timeout)
{
    public string? SdkPath { get; init; }
    public string? SdkVersion { get; init; }
    public string? MsBuildVersion { get; init; }
    public string? DotNetRoot { get; init; }
    public string? DotNetHostPath { get; init; }
}

public sealed record ProjectContextLoadResult(
    bool Success,
    IReadOnlyList<ProjectAnalysisContext> Contexts,
    IReadOnlyList<string> Diagnostics,
    string? FailureReason = null,
    IReadOnlyList<ProjectContextProjectExclusion>? ExcludedProjects = null);

public sealed record ProjectContextProjectExclusion(string ProjectPath, string Reason);

internal sealed record ProjectContextSdkResolution(
    string Version,
    string Path,
    string MsBuildVersion,
    string DotNetRoot,
    string HostPath);

public sealed class ProjectContextException(string reason, string message) : InvalidOperationException(message)
{
    public string Reason { get; } = reason;
}

public static class ProjectTargetSelector
{
    public static string Select(string workingDirectory, string? explicitTarget)
    {
        if (explicitTarget is not null)
        {
            var selected = Path.GetFullPath(explicitTarget, workingDirectory);
            if (!File.Exists(selected)) throw new ProjectContextException("context.targetNotFound", $"Project target not found: {selected}");
            if (!IsTarget(selected)) throw new ProjectContextException("context.unsupportedTarget", "Project target must be a .csproj, .sln, or .slnx file.");
            return selected;
        }

        var solutions = Directory.EnumerateFiles(workingDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path) is ".sln" or ".slnx").Order(StringComparer.Ordinal).ToArray();
        if (solutions.Length == 1) return solutions[0];
        if (solutions.Length > 1) throw new ProjectContextException("context.targetAmbiguous", "Multiple top-level solutions found; select one explicitly.");
        var projects = Directory.EnumerateFiles(workingDirectory, "*.csproj", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray();
        if (projects.Length == 1) return projects[0];
        if (projects.Length == 0) throw new ProjectContextException("context.targetNotFound", "No top-level solution or project found; nested projects are never guessed.");
        throw new ProjectContextException("context.targetAmbiguous", "Multiple top-level projects found; select one explicitly.");
    }

    private static bool IsTarget(string path) => Path.GetExtension(path) is ".csproj" or ".sln" or ".slnx";
}

public static class ProjectContextLoader
{
    internal const string LoaderCommand = "__crap4csharp-project-context-v1";
    internal static readonly JsonSerializerOptions JsonOptions = CreateTransportOptions();

    private static JsonSerializerOptions CreateTransportOptions()
    {
        // Private subprocess transport retains local physical evidence; ordinary result JSON omits it.
        var resolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            var propertyName = info.Type == typeof(ProjectSourceIdentity) ? nameof(ProjectSourceIdentity.ResolvedPath) :
                info.Type == typeof(ProjectAnalysisContext) ? nameof(ProjectAnalysisContext.ProtectedPaths) : null;
            if (propertyName is null) return;
            var member = info.Type.GetProperty(propertyName)!;
            var name = JsonNamingPolicy.CamelCase.ConvertName(propertyName);
            var existing = info.Properties.FirstOrDefault(property => property.Name == name);
            if (existing is not null) info.Properties.Remove(existing);
            var property = info.CreateJsonPropertyInfo(member.PropertyType, name);
            property.Get = member.GetValue;
            property.Set = member.SetValue;
            info.Properties.Add(property);
        });
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { TypeInfoResolver = resolver, Converters = { new JsonStringEnumConverter() } };
    }

    public static Task<ProjectContextLoadResult> LoadAsync(ProjectContextLoadRequest request, CancellationToken cancellationToken) =>
        LoadAsync(request, cancellationToken, ResolveSdkAsync);

    internal static async Task<ProjectContextLoadResult> LoadAsync(
        ProjectContextLoadRequest request,
        CancellationToken cancellationToken,
        Func<string, TimeSpan, CancellationToken, Task<ProjectContextSdkResolution>> resolveSdkAsync)
    {
        string target;
        string root;
        IReadOnlyDictionary<string, string> before;
        try
        {
            target = ProjectTargetSelector.Select(Path.GetDirectoryName(Path.GetFullPath(request.Target))!, request.Target);
            root = Path.GetDirectoryName(target)!;
            before = SnapshotAuthoredInputs(root);
        }
        catch (ProjectContextException exception)
        {
            return new ProjectContextLoadResult(false, [], [exception.Message], exception.Reason);
        }
        using var overallTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallTimeout.CancelAfter(request.Timeout);
        var operationToken = overallTimeout.Token;
        ProjectContextSdkResolution sdk;
        try
        {
            sdk = await resolveSdkAsync(root, request.Timeout, operationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && overallTimeout.IsCancellationRequested)
        {
            return SnapshotFailure(root, before, "context.loaderTimeout", $"Project context loading exceeded {request.Timeout.TotalSeconds:0.###} seconds.");
        }
        catch (ProcessTimeoutException)
        {
            return SnapshotFailure(root, before, "context.loaderTimeout", $"Project context loading exceeded {request.Timeout.TotalSeconds:0.###} seconds.");
        }
        catch (ProjectContextException exception)
        {
            return SnapshotFailure(root, before, exception.Reason, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SnapshotFailure(root, before, "context.sdkResolutionFailed", exception.Message);
        }
        var effective = request with
        {
            Target = target, SdkPath = sdk.Path, SdkVersion = sdk.Version, MsBuildVersion = sdk.MsBuildVersion,
            DotNetRoot = sdk.DotNetRoot, DotNetHostPath = sdk.HostPath
        };
        var scratch = Path.Combine(Path.GetTempPath(), "crap4csharp-context", Guid.NewGuid().ToString("N"));
        var requestPath = Path.Combine(scratch, "request.json");
        var responsePath = Path.Combine(scratch, "response.json");
        try
        {
            Directory.CreateDirectory(scratch);
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(effective, JsonOptions), operationToken);
            var assembly = typeof(ProjectContextLoader).Assembly.Location;
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DOTNET_ROOT"] = sdk.DotNetRoot,
                ["DOTNET_HOST_PATH"] = sdk.HostPath,
                ["PATH"] = sdk.DotNetRoot + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            };
            var result = await ProcessRunner.RunWithEnvironmentAsync(sdk.HostPath, [assembly, LoaderCommand, requestPath, responsePath],
                root, request.Timeout, operationToken, environment);
            ProjectContextLoadResult loaded;
            if (File.Exists(responsePath))
                loaded = JsonSerializer.Deserialize<ProjectContextLoadResult>(await File.ReadAllTextAsync(responsePath, operationToken), JsonOptions)
                    ?? new ProjectContextLoadResult(false, [], ["Loader returned an empty response."], "context.protocolInvalid");
            else
                loaded = new ProjectContextLoadResult(false, [], SplitDiagnostics(result), "context.loaderFailed");
            var after = SnapshotAuthoredInputs(root);
            if (!before.OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(after.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
                return new ProjectContextLoadResult(false, loaded.Contexts,
                    loaded.Diagnostics.Concat(["Authored project/source inputs changed while project context was loading."]).ToArray(),
                    "context.inputsMutated");
            return result.ExitCode == 0 || !loaded.Success ? loaded : new ProjectContextLoadResult(false, loaded.Contexts,
                loaded.Diagnostics.Concat(SplitDiagnostics(result)).ToArray(), "context.loaderFailed", loaded.ExcludedProjects);
        }
        catch (JsonException exception)
        {
            return SnapshotFailure(root, before, "context.protocolInvalid", exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && overallTimeout.IsCancellationRequested)
        {
            return SnapshotFailure(root, before, "context.loaderTimeout", $"Project context loading exceeded {request.Timeout.TotalSeconds:0.###} seconds.");
        }
        catch (ProcessTimeoutException)
        {
            return SnapshotFailure(root, before, "context.loaderTimeout", $"Project context loading exceeded {request.Timeout.TotalSeconds:0.###} seconds.");
        }
        catch (ProjectContextException exception)
        {
            return SnapshotFailure(root, before, exception.Reason, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SnapshotFailure(root, before, "context.loaderFailed", exception.Message);
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
    }

    private static ProjectContextLoadResult SnapshotFailure(string root, IReadOnlyDictionary<string, string> before,
        string reason, string diagnostic)
    {
        var after = SnapshotAuthoredInputs(root);
        return before.OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(after.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            ? new ProjectContextLoadResult(false, [], [diagnostic], reason)
            : new ProjectContextLoadResult(false, [], [diagnostic, "Authored inputs changed while project context was loading."], "context.inputsMutated");
    }

    private static string DotNetHost => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    private static async Task<ProjectContextSdkResolution> ResolveSdkAsync(string root, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var versionResult = await ProcessRunner.RunAsync(DotNetHost, ["--version"], root, timeout, cancellationToken);
        if (versionResult.ExitCode != 0) throw new ProjectContextException("context.sdkResolutionFailed", versionResult.StandardError);
        var version = versionResult.StandardOutput.Trim();
        var list = await ProcessRunner.RunAsync(DotNetHost, ["--list-sdks"], root, timeout, cancellationToken);
        var prefix = version + " [";
        var line = list.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).SingleOrDefault(value => value.Trim().StartsWith(prefix, StringComparison.Ordinal));
        if (line is null) throw new ProjectContextException("context.sdkResolutionFailed", $"Resolved SDK {version} was not present in dotnet --list-sdks.");
        var start = line.IndexOf('[') + 1;
        var end = line.LastIndexOf(']');
        var sdkRoot = line[start..end];
        var msbuild = await ProcessRunner.RunAsync(DotNetHost, ["msbuild", "-version", "-nologo", "-nr:false"], root, timeout, cancellationToken);
        if (msbuild.ExitCode != 0) throw new ProjectContextException("context.sdkResolutionFailed", msbuild.StandardError);
        var msbuildVersion = msbuild.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();
        var dotnetRoot = Directory.GetParent(sdkRoot)?.FullName
            ?? throw new ProjectContextException("context.sdkResolutionFailed", $"Could not determine dotnet root from {sdkRoot}.");
        var hostPath = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(hostPath)) throw new ProjectContextException("context.sdkResolutionFailed", $"Resolved dotnet host is unavailable: {hostPath}");
        return new ProjectContextSdkResolution(version, Path.Combine(sdkRoot, version), msbuildVersion, dotnetRoot, hostPath);
    }

    private static string[] SplitDiagnostics(ProcessResult result) =>
        (result.StandardError + Environment.NewLine + result.StandardOutput).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Dictionary<string, string> SnapshotAuthoredInputs(string root)
    {
        try
        {
            var extensions = new HashSet<string>([".cs", ".csproj", ".props", ".targets", ".sln", ".slnx", ".json", ".projitems", ".shproj", ".rsp", ".editorconfig", ".globalconfig"], StringComparer.OrdinalIgnoreCase);
            var result = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => extensions.Contains(Path.GetExtension(path)) || Path.GetFileName(path).Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase))
                .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
                    .Any(part => part is "bin" or "obj" or ".git" or "TestResults" or "node_modules"))
                .ToDictionary(path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                    path => ProjectAnalysisContext.ContentHash(File.ReadAllBytes(path)), StringComparer.Ordinal);
            for (var directory = Directory.GetParent(root); directory is not null; directory = directory.Parent)
            {
                foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config", ".editorconfig", ".globalconfig" })
                {
                    var path = Path.Combine(directory.FullName, name);
                    if (File.Exists(path)) result[$"<ancestor>/{directory.Name}/{name}"] = ProjectAnalysisContext.ContentHash(File.ReadAllBytes(path));
                }
                if (File.Exists(Path.Combine(directory.FullName, "global.json"))) break;
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProjectContextException("context.inputSnapshotFailed", exception.Message);
        }
    }
}

public static class ProjectBuildPreparation
{
    private static readonly SemaphoreSlim Serial = new(1, 1);

    public static Task<ProcessResult> RestoreAsync(string target, TimeSpan timeout, CancellationToken cancellationToken) =>
        RunSerialized(target, ["restore", Path.GetFullPath(target), "--disable-parallel", "-nr:false"], timeout, cancellationToken);

    public static Task<ProcessResult> BuildAsync(string target, string configuration, string? framework, string? platform,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "build", Path.GetFullPath(target), "-c", configuration, "--no-restore", "--no-incremental", "-m:1", "-nr:false" };
        if (framework is not null) { arguments.Add("-f"); arguments.Add(framework); }
        if (platform is not null) arguments.Add($"-p:Platform={platform}");
        return RunSerialized(target, arguments, timeout, cancellationToken);
    }

    private static async Task<ProcessResult> RunSerialized(string target, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await Serial.WaitAsync(cancellationToken);
        try
        {
            return await ProcessRunner.RunAsync(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", arguments,
                Path.GetDirectoryName(Path.GetFullPath(target))!, timeout, cancellationToken);
        }
        finally { Serial.Release(); }
    }
}
