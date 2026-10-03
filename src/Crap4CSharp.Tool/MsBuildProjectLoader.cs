using Microsoft.Build.Evaluation;
using Microsoft.Build.Construction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;

namespace Crap4CSharp.Core;

internal static class MsBuildProjectLoader
{
    public static async Task<ProjectContextLoadResult> LoadAsync(ProjectContextLoadRequest request, CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        var root = Path.GetDirectoryName(request.Target)!;
        var projects = await DiscoverProjects(request.Target, request.Configuration, request.Platform, diagnostics, cancellationToken);
        var contexts = new List<ProjectAnalysisContext>();
        var excludedProjects = new List<ProjectContextProjectExclusion>();
        var matchedFrameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in projects.OrderBy(value => value.Path, StringComparer.Ordinal))
        {
            var projectPath = selection.Path;
            var selectedRequest = request with { Configuration = selection.Configuration, Platform = selection.Platform };
            var metadata = Evaluate(projectPath, selectedRequest.Configuration, selectedRequest.Platform, null);
            EnsurePreparedAssets(metadata, projectPath);
            if (metadata.IsTestProject && !request.IncludeTests)
            {
                diagnostics.Add($"Excluded test project: {Logical(root, projectPath)}");
                excludedProjects.Add(new(Logical(root, projectPath), "testProject.defaultExcluded"));
                continue;
            }
            foreach (var framework in metadata.Frameworks)
            {
                if (request.Frameworks.Count > 0 && !request.Frameworks.Contains(framework, StringComparer.OrdinalIgnoreCase)) continue;
                matchedFrameworks.Add(framework);
                contexts.Add(await LoadContext(root, projectPath, framework, selectedRequest, diagnostics, cancellationToken));
            }
        }
        var unmatched = request.Frameworks.Where(framework => !matchedFrameworks.Contains(framework)).ToArray();
        if (unmatched.Length > 0)
            return new ProjectContextLoadResult(false, contexts, diagnostics.Concat(["Requested framework was not found: " + string.Join(", ", unmatched)]).ToArray(), "context.frameworkNotFound", excludedProjects);
        return new ProjectContextLoadResult(true, contexts.OrderBy(context => context.ProjectPath, StringComparer.Ordinal)
            .ThenBy(context => context.TargetFramework, StringComparer.Ordinal).ToArray(), diagnostics, null, excludedProjects);
    }

    private static async Task<ProjectSelection[]> DiscoverProjects(string target, string configuration, string? platform,
        List<string> diagnostics, CancellationToken cancellationToken)
    {
        if (Path.GetExtension(target).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            return [new ProjectSelection(target, configuration, platform)];
        using var workspace = CreateWorkspace(configuration, platform, null, diagnostics, out var workspaceDiagnostics);
        var solution = await workspace.OpenSolutionAsync(target, cancellationToken: cancellationToken);
        ThrowOnWorkspaceFailure(workspaceDiagnostics, target);
        var paths = solution.Projects.Where(project => project.Language == LanguageNames.CSharp && project.FilePath is not null)
            .Select(project => Path.GetFullPath(project.FilePath!)).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) throw new ProjectContextException("context.noProjects", "The selected solution contains no C# projects.");
        if (!Path.GetExtension(target).Equals(".sln", StringComparison.OrdinalIgnoreCase))
            return paths.Select(path => new ProjectSelection(path, configuration, platform)).ToArray();
        var solutionFile = SolutionFile.Parse(target);
        var solutionConfiguration = solutionFile.SolutionConfigurations
            .Where(value => value.ConfigurationName.Equals(configuration, StringComparison.OrdinalIgnoreCase))
            .Where(value => platform is null || value.PlatformName.Equals(platform, StringComparison.OrdinalIgnoreCase) ||
                value.PlatformName.Replace(" ", string.Empty, StringComparison.Ordinal).Equals(platform.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value.PlatformName, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new ProjectContextException("context.solutionMappingUnsupported", $"Solution configuration {configuration}|{platform ?? "<default>"} is unavailable in {target}.");
        var byPath = solutionFile.ProjectsInOrder.Where(value => value.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat)
            .ToDictionary(value => Path.GetFullPath(value.AbsolutePath), value => value, PathComparer);
        return paths.Select(path =>
        {
            if (!byPath.TryGetValue(path, out var project) || !project.ProjectConfigurations.TryGetValue(solutionConfiguration.FullName, out var mapping))
                throw new ProjectContextException("context.solutionMappingUnsupported", $"No project configuration mapping exists for {path} under {solutionConfiguration.FullName}.");
            return new ProjectSelection(path, mapping.ConfigurationName, mapping.PlatformName.Replace(" ", string.Empty, StringComparison.Ordinal));
        }).ToArray();
    }

    private static async Task<ProjectAnalysisContext> LoadContext(string root, string projectPath, string framework,
        ProjectContextLoadRequest request, List<string> diagnostics, CancellationToken cancellationToken)
    {
        var metadata = Evaluate(projectPath, request.Configuration, request.Platform, framework);
        EnsurePreparedAssets(metadata, projectPath);
        var beforeInputs = SnapshotInputs(metadata.AuthoredInputs);
        using var workspace = CreateWorkspace(request.Configuration, request.Platform, framework, diagnostics, out var workspaceDiagnostics);
        var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
        ThrowOnWorkspaceFailure(workspaceDiagnostics, projectPath);
        if (project.ParseOptions is not CSharpParseOptions parseOptions)
            throw new ProjectContextException("context.parseOptionsUnavailable", $"C# parse options were unavailable for {projectPath}.");
        var sources = new List<ProjectSourceIdentity>();
        var exclusions = new List<ProjectSourceExclusion>();
        var documentPaths = project.Documents.Where(document => document.FilePath is not null)
            .Select(document => Path.GetFullPath(document.FilePath!)).ToHashSet(PathComparer);
        foreach (var document in project.Documents.OrderBy(document => document.FilePath, StringComparer.Ordinal))
        {
            if (document.FilePath is null) throw new ProjectContextException("context.sourcePathUnavailable", $"A document in {projectPath} has no physical path.");
            var text = (await document.GetTextAsync(cancellationToken)).ToString();
            var logical = document.Folders.Count > 0 ? string.Join('/', document.Folders.Append(document.Name)) : document.Name;
            var generated = IsGenerated(document.FilePath, text);
            var contentIdentity = File.Exists(document.FilePath)
                ? ProjectAnalysisContext.ContentHash(await File.ReadAllBytesAsync(document.FilePath, cancellationToken))
                : ProjectAnalysisContext.ContentHash(text);
            var identity = new ProjectSourceIdentity(Logical(root, document.FilePath), logical.Replace('\\', '/'),
                contentIdentity, generated, !IsWithin(root, document.FilePath));
            if (generated && !request.IncludeGenerated) exclusions.Add(new(identity.PhysicalPath, identity.LogicalPath, "generated.defaultExcluded", identity.ContentIdentity));
            else sources.Add(identity);
        }
        if (request.IncludeGenerated)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken)
                ?? throw new ProjectContextException("context.generatedSourceUnavailable", "Compilation was unavailable while generated source inclusion was requested.");
            var generatorFailures = compilation.GetDiagnostics(cancellationToken).Where(diagnostic =>
                diagnostic.Id is "CS8032" or "CS8034" or "CS8784" or "CS8785" && diagnostic.Severity != DiagnosticSeverity.Hidden).ToArray();
            if (generatorFailures.Length > 0)
                throw new ProjectContextException("context.generatedSourceUnavailable", string.Join(Environment.NewLine, generatorFailures.Select(value => value.ToString())));
            foreach (var tree in compilation.SyntaxTrees.Where(tree => string.IsNullOrWhiteSpace(tree.FilePath) || !documentPaths.Contains(Path.GetFullPath(tree.FilePath))))
            {
                var text = (await tree.GetTextAsync(cancellationToken)).ToString();
                var path = string.IsNullOrWhiteSpace(tree.FilePath) ? $"<generated>/{ProjectAnalysisContext.ContentHash(text)}.cs" : Logical(root, tree.FilePath);
                sources.Add(new ProjectSourceIdentity(path, path, ProjectAnalysisContext.ContentHash(text), true, !IsWithin(root, tree.FilePath)));
            }
        }
        var adapter = new ProjectAdapterIdentity(request.SdkVersion ?? "unknown", request.MsBuildVersion ?? "unknown",
            typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown");
        var afterInputs = SnapshotInputs(metadata.AuthoredInputs);
        if (!beforeInputs.OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(afterInputs.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
            throw new ProjectContextException("context.inputsMutated", $"Authored inputs changed while loading {projectPath}.");
        return ProjectAnalysisContext.Create(Logical(root, projectPath), project.AssemblyName ?? metadata.AssemblyName,
            framework, metadata.Configuration, metadata.Platform, parseOptions.LanguageVersion,
            parseOptions.PreprocessorSymbolNames, parseOptions.Kind, metadata.Imports.Select(path => ImportIdentity(root, request.SdkPath, path)),
            sources, new ProjectExclusionPolicy(request.IncludeTests, request.IncludeGenerated), adapter, exclusions);
    }

    private static MSBuildWorkspace CreateWorkspace(string configuration, string? platform, string? framework,
        List<string> diagnostics, out List<WorkspaceDiagnostic> workspaceDiagnostics)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Configuration"] = configuration };
        if (platform is not null) properties["Platform"] = platform;
        if (framework is not null) properties["TargetFramework"] = framework;
        var workspace = MSBuildWorkspace.Create(properties);
        workspace.SkipUnrecognizedProjects = false;
        workspace.LoadMetadataForReferencedProjects = true;
        workspaceDiagnostics = [];
        var captured = workspaceDiagnostics;
        workspace.RegisterWorkspaceFailedHandler(args =>
        {
            captured.Add(args.Diagnostic);
            diagnostics.Add($"{args.Diagnostic.Kind}: {args.Diagnostic.Message}");
        });
        return workspace;
    }

    private static EvaluatedMetadata Evaluate(string projectPath, string configuration, string? platform, string? framework)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Configuration"] = configuration };
        if (platform is not null) properties["Platform"] = platform;
        if (framework is not null) properties["TargetFramework"] = framework;
        using var collection = new ProjectCollection(properties);
        var project = collection.LoadProject(projectPath);
        var frameworks = (project.GetPropertyValue("TargetFrameworks") is { Length: > 0 } many ? many : project.GetPropertyValue("TargetFramework"))
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (frameworks.Length == 0) throw new ProjectContextException("context.frameworkUnavailable", $"No target framework was evaluated for {projectPath}.");
        var assets = project.GetPropertyValue("ProjectAssetsFile");
        if (string.IsNullOrWhiteSpace(assets)) assets = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json");
        else if (!Path.IsPathRooted(assets)) assets = Path.GetFullPath(assets, Path.GetDirectoryName(projectPath)!);
        var authoredInputs = project.Imports.Select(import => import.ImportedProject.FullPath)
            .Append(projectPath)
            .Concat(project.GetItems("Compile").Select(item => Path.GetFullPath(item.EvaluatedInclude, Path.GetDirectoryName(projectPath)!)))
            .Where(File.Exists).Distinct(PathComparer).ToArray();
        return new EvaluatedMetadata(frameworks,
            string.Equals(project.GetPropertyValue("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase),
            project.GetPropertyValue("AssemblyName"),
            project.GetPropertyValue("Configuration") is { Length: > 0 } c ? c : configuration,
            project.GetPropertyValue("Platform") is { Length: > 0 } p ? p : platform ?? "AnyCPU",
            project.Imports.Select(import => import.ImportedProject.FullPath).ToArray(), assets, authoredInputs);
    }

    private static bool IsGenerated(string path, string text) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
        text.AsSpan(0, Math.Min(text.Length, 512)).Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);

    private static string Logical(string root, string path) => IsWithin(root, path)
        ? Path.GetRelativePath(root, path).Replace('\\', '/')
        : $"<external>/{ContentIdentity(path)}/{Path.GetFileName(path)}";
    private static string ImportIdentity(string root, string? sdkPath, string path)
    {
        if (IsWithin(root, path)) return $"{Logical(root, path)}:{ContentIdentity(path)}";
        if (sdkPath is not null && IsWithin(sdkPath, path))
            return $"<sdk>/{Path.GetRelativePath(sdkPath, path).Replace('\\', '/')}:{ContentIdentity(path)}";
        return $"<external>/{Path.GetFileName(path)}:{ContentIdentity(path)}";
    }
    private static string ContentIdentity(string path) => File.Exists(path)
        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
        : ProjectAnalysisContext.ContentHash(Path.GetFileName(path));
    private static bool IsWithin(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static void EnsurePreparedAssets(EvaluatedMetadata metadata, string projectPath)
    {
        if (!File.Exists(metadata.AssetsFile))
            throw new ProjectContextException("context.assetsUnavailable", $"Restore assets are missing for {projectPath}. Restore declared dependencies before loading project context.");
    }
    private static Dictionary<string, string> SnapshotInputs(IEnumerable<string> paths) => paths.ToDictionary(Path.GetFullPath,
        path => ProjectAnalysisContext.ContentHash(File.ReadAllBytes(path)), PathComparer);
    private static void ThrowOnWorkspaceFailure(IEnumerable<WorkspaceDiagnostic> workspaceDiagnostics, string target)
    {
        var failures = workspaceDiagnostics.Where(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure).ToArray();
        if (failures.Length > 0)
            throw new ProjectContextException("context.projectLoadFailed", $"MSBuildWorkspace failed to load {target}: {string.Join(" | ", failures.Select(value => value.Message))}");
    }

    private sealed record EvaluatedMetadata(string[] Frameworks, bool IsTestProject, string AssemblyName,
        string Configuration, string Platform, string[] Imports, string AssetsFile, string[] AuthoredInputs);
    private sealed record ProjectSelection(string Path, string Configuration, string? Platform);
}
