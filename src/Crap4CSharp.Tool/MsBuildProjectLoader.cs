using Microsoft.Build.Evaluation;
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
        var projectPaths = await DiscoverProjects(request.Target, request.Configuration, request.Platform, diagnostics, cancellationToken);
        var contexts = new List<ProjectAnalysisContext>();
        var excludedProjects = new List<ProjectContextProjectExclusion>();
        var matchedFrameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectPath in projectPaths.Order(StringComparer.Ordinal))
        {
            var metadata = Evaluate(projectPath, request.Configuration, request.Platform, null);
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
                contexts.Add(await LoadContext(root, projectPath, framework, request, diagnostics, cancellationToken));
            }
        }
        var unmatched = request.Frameworks.Where(framework => !matchedFrameworks.Contains(framework)).ToArray();
        if (unmatched.Length > 0)
            return new ProjectContextLoadResult(false, contexts, diagnostics.Concat(["Requested framework was not found: " + string.Join(", ", unmatched)]).ToArray(), "context.frameworkNotFound", excludedProjects);
        return new ProjectContextLoadResult(true, contexts.OrderBy(context => context.ProjectPath, StringComparer.Ordinal)
            .ThenBy(context => context.TargetFramework, StringComparer.Ordinal).ToArray(), diagnostics, null, excludedProjects);
    }

    private static async Task<string[]> DiscoverProjects(string target, string configuration, string? platform,
        List<string> diagnostics, CancellationToken cancellationToken)
    {
        if (Path.GetExtension(target).Equals(".csproj", StringComparison.OrdinalIgnoreCase)) return [target];
        using var workspace = CreateWorkspace(configuration, platform, null, diagnostics);
        var solution = await workspace.OpenSolutionAsync(target, cancellationToken: cancellationToken);
        var paths = solution.Projects.Where(project => project.Language == LanguageNames.CSharp && project.FilePath is not null)
            .Select(project => Path.GetFullPath(project.FilePath!)).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) throw new ProjectContextException("context.noProjects", "The selected solution contains no C# projects.");
        return paths;
    }

    private static async Task<ProjectAnalysisContext> LoadContext(string root, string projectPath, string framework,
        ProjectContextLoadRequest request, List<string> diagnostics, CancellationToken cancellationToken)
    {
        var metadata = Evaluate(projectPath, request.Configuration, request.Platform, framework);
        using var workspace = CreateWorkspace(request.Configuration, request.Platform, framework, diagnostics);
        var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
        if (project.ParseOptions is not CSharpParseOptions parseOptions)
            throw new ProjectContextException("context.parseOptionsUnavailable", $"C# parse options were unavailable for {projectPath}.");
        var sources = new List<ProjectSourceIdentity>();
        var exclusions = new List<ProjectSourceExclusion>();
        foreach (var document in project.Documents.OrderBy(document => document.FilePath, StringComparer.Ordinal))
        {
            if (document.FilePath is null) throw new ProjectContextException("context.sourcePathUnavailable", $"A document in {projectPath} has no physical path.");
            var text = (await document.GetTextAsync(cancellationToken)).ToString();
            var logical = document.Folders.Count > 0 ? string.Join('/', document.Folders.Append(document.Name)) : document.Name;
            var generated = IsGenerated(document.FilePath, text);
            var identity = new ProjectSourceIdentity(Logical(root, document.FilePath), logical.Replace('\\', '/'),
                ProjectAnalysisContext.ContentHash(text), generated, !IsWithin(root, document.FilePath));
            if (generated && !request.IncludeGenerated) exclusions.Add(new(identity.PhysicalPath, identity.LogicalPath, "generated.defaultExcluded", identity.ContentIdentity));
            else sources.Add(identity);
        }
        if (request.IncludeGenerated)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken)
                ?? throw new ProjectContextException("context.generatedSourceUnavailable", "Compilation was unavailable while generated source inclusion was requested.");
            foreach (var tree in compilation.SyntaxTrees.Where(tree => sources.All(source => !PathsEqual(root, source.PhysicalPath, tree.FilePath))))
            {
                var text = (await tree.GetTextAsync(cancellationToken)).ToString();
                var path = string.IsNullOrWhiteSpace(tree.FilePath) ? $"<generated>/{ProjectAnalysisContext.ContentHash(text)}.cs" : Logical(root, tree.FilePath);
                sources.Add(new ProjectSourceIdentity(path, path, ProjectAnalysisContext.ContentHash(text), true, !IsWithin(root, tree.FilePath)));
            }
        }
        var adapter = new ProjectAdapterIdentity(request.SdkVersion ?? "unknown", request.MsBuildVersion ?? "unknown",
            typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown");
        return ProjectAnalysisContext.Create(Logical(root, projectPath), project.AssemblyName ?? metadata.AssemblyName,
            framework, request.Configuration, request.Platform ?? metadata.Platform, parseOptions.LanguageVersion,
            parseOptions.PreprocessorSymbolNames, parseOptions.Kind, metadata.Imports.Select(path => ImportIdentity(root, request.SdkPath, path)),
            sources, new ProjectExclusionPolicy(request.IncludeTests, request.IncludeGenerated), adapter, exclusions);
    }

    private static MSBuildWorkspace CreateWorkspace(string configuration, string? platform, string? framework, List<string> diagnostics)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Configuration"] = configuration };
        if (platform is not null) properties["Platform"] = platform;
        if (framework is not null) properties["TargetFramework"] = framework;
        var workspace = MSBuildWorkspace.Create(properties);
        workspace.SkipUnrecognizedProjects = false;
        workspace.LoadMetadataForReferencedProjects = false;
        workspace.RegisterWorkspaceFailedHandler(args => diagnostics.Add($"{args.Diagnostic.Kind}: {args.Diagnostic.Message}"));
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
        return new EvaluatedMetadata(frameworks,
            string.Equals(project.GetPropertyValue("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase),
            project.GetPropertyValue("AssemblyName"), project.GetPropertyValue("PlatformTarget") is { Length: > 0 } p ? p : platform ?? "AnyCPU",
            project.Imports.Select(import => import.ImportedProject.FullPath).ToArray());
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
    private static bool PathsEqual(string root, string stored, string candidate) => string.Equals(
        stored.StartsWith("<external>/", StringComparison.Ordinal) ? stored : Path.GetFullPath(stored, root), Path.GetFullPath(candidate),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record EvaluatedMetadata(string[] Frameworks, bool IsTestProject, string AssemblyName, string Platform, string[] Imports);
}
