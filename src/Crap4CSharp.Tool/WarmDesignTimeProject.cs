using System.Collections.Immutable;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Crap4CSharp.Core;

// Worker-only post-build acquisition, selected for the entire supported graph
// before targets. Refusal never falls back to Workspace after targets execute.
internal sealed class WarmDesignTimeProject : IDisposable
{
    private readonly AdhocWorkspace workspace;
    internal Microsoft.CodeAnalysis.Project Project { get; }

    private WarmDesignTimeProject(AdhocWorkspace workspace, Microsoft.CodeAnalysis.Project project)
    {
        this.workspace = workspace;
        Project = project;
    }

    internal static async Task<WarmDesignTimeProject> AcquireAsync(string projectPath,
        IReadOnlyDictionary<string, string> effectiveGlobals, List<string> diagnostics, CancellationToken token,
        Action<string>? diagnosticTrace = null, Action<string>? diagnosticMessage = null,
        CompletedProofTiming? timing = null)
    {
        token.ThrowIfCancellationRequested();
        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DesignTimeBuild"] = "True",
            ["NonExistentFile"] = "__NonExistentSubDir__\\__NonExistentFile__",
            ["BuildProjectReferences"] = "False",
            ["BuildingProject"] = "False",
            ["ProvideCommandLineArgs"] = "True",
            ["SkipCompilerExecution"] = "True",
            ["ContinueOnError"] = "ErrorAndContinue",
            ["ShouldUnsetParentConfigurationAndPlatform"] = "False"
        };
        foreach (var pair in effectiveGlobals) globals[pair.Key] = pair.Value;
        using var evaluationTiming = timing?.Start("worker.warmEvaluation");
        var collection = new ProjectCollection(globals);
        using var collectionLifetime = new ObservedLifetime(collection, diagnosticTrace, "collection.disposed");
        var evaluated = collection.LoadProject(projectPath);
        var instance = evaluated.CreateProjectInstance();
        if (!instance.Targets.ContainsKey("Compile") || !instance.Targets.ContainsKey("CoreCompile"))
            throw new ProjectContextException("context.warmAcquisitionUnavailable", "Required design-time targets are unavailable.");
        evaluationTiming?.Dispose();
        var targets = new List<string> { "Compile", "CoreCompile" };
        if (instance.Targets.ContainsKey("DesignTimeMarkupCompilation")) targets.Add("DesignTimeMarkupCompilation");
        using var targetsTiming = timing?.Start("worker.warmTargets");
        var logger = new AcquisitionLogger(diagnosticMessage);
        var manager = new BuildManager();
        using var managerLifetime = new ObservedLifetime(manager, diagnosticTrace, "manager.disposed");
        BuildResult result;
        manager.BeginBuild(new BuildParameters(collection) { Loggers = [logger], EnableNodeReuse = false, MaxNodeCount = 1 });
        try
        {
            var submission = manager.PendBuildRequest(new BuildRequestData(instance, targets.ToArray(), null, BuildRequestDataFlags.ProvideProjectStateAfterBuild));
            var completion = new TaskCompletionSource<BuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            submission.ExecuteAsync(finished =>
            {
                if (finished.BuildResult is { } completed)
                {
                    diagnosticTrace?.Invoke("submission.callback." + completed.OverallResult);
                    completion.TrySetResult(completed);
                }
                else completion.TrySetException(new ProjectContextException("context.warmAcquisitionFailed", "Submission returned no terminal result."));
            }, null);
            // CancelAllSubmissions is asynchronous. Do not cancel completion: await the real
            // callback, then EndBuild, before releasing the collection or returning a result.
            using var registration = token.Register(() =>
            {
                diagnosticTrace?.Invoke("submission.cancel.requested");
                manager.CancelAllSubmissions();
            });
            result = await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            manager.EndBuild();
            diagnosticTrace?.Invoke("build.end.returned");
        }
        targetsTiming?.Dispose();
        token.ThrowIfCancellationRequested();
        diagnostics.AddRange(logger.Messages);
        if (result.OverallResult != BuildResultCode.Success || logger.HasErrors)
            throw new ProjectContextException("context.warmAcquisitionFailed", "Design-time acquisition did not complete successfully.");
        instance = result.ProjectStateAfterBuild
            ?? throw new ProjectContextException("context.warmAcquisitionUnavailable", "Executed project state was not returned.");
        using var parsingTiming = timing?.Start("worker.warmCommandLine");
        var arguments = instance.GetItems("CscCommandLineArgs").Select(item => item.EvaluatedInclude).ToArray();
        if (arguments.Length == 0 || arguments.Length > 131072)
            throw new ProjectContextException("context.warmAcquisitionUnavailable", "A bounded compiler command line was not produced.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var parsed = CSharpCommandLineParser.Default.Parse(arguments, directory, sdkDirectory: null);
        if (parsed.Errors.Any(error => error.Severity == DiagnosticSeverity.Error))
            throw new ProjectContextException("context.warmAcquisitionFailed", "The observed compiler command line contains errors.");
        // CommandLineProject throws before constructing an UnresolvedAnalyzerReference.
        // Preserve the ordinary loader's typed generator refusal for missing actual
        // executed analyzer inputs; never retry ordinary loading after these targets.
        foreach (var analyzer in parsed.AnalyzerReferences)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(analyzer.FilePath, directory);
            if (!File.Exists(path))
                throw new ProjectContextException("context.generatedSourceUnavailable", "An analyzer or generator reference could not be resolved.");
        }
        // Ordinary Workspace can warn and remove a missing resolved metadata input.
        // This probe cannot reconstruct that degraded Project exactly. Refuse the
        // executed input explicitly, without rerunning targets in ordinary loading.
        foreach (var reference in parsed.MetadataReferences)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(reference.Reference, directory);
            if (!File.Exists(path))
                throw new ProjectContextException("context.warmMetadataUnavailable", "An executed metadata reference is unavailable for warm acquisition.");
        }
        parsingTiming?.Dispose();
        using var reconstructionTiming = timing?.Start("worker.warmReconstruction");
        using var workspaceCreationTiming = timing?.Start("worker.warmWorkspaceCreation");
        var workspace = new AdhocWorkspace();
        workspaceCreationTiming?.Dispose();
        try
        {
            using var projectInfoTiming = timing?.Start("worker.warmProjectInfo");
            var info = CommandLineProject.CreateProjectInfo(Path.GetFileNameWithoutExtension(projectPath),
                LanguageNames.CSharp, arguments, directory, workspace);
            projectInfoTiming?.Dispose();
            using var projectInfoAdjustmentTiming = timing?.Start("worker.warmProjectInfoAdjustment");
            if (info.ParseOptions is not CSharpParseOptions parse || info.CompilationOptions is not CSharpCompilationOptions options)
                throw new ProjectContextException("context.warmAcquisitionUnavailable", "C# options are unavailable.");
            info = info.WithFilePath(projectPath)
                .WithOutputFilePath(ExecutedPath("TargetPath"))
                .WithOutputRefFilePath(ExecutedPath("TargetRefPath"))
                .WithDefaultNamespace(instance.GetPropertyValue("RootNamespace"))
                .WithParseOptions(parse.WithDocumentationMode(DocumentationMode.Parse))
                .WithCompilationOptions(options.WithSourceReferenceResolver(new SourceFileResolver([], directory))
                    .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default))
                .WithDocuments(Documents("Compile"))
                .WithAdditionalDocuments(Documents("AdditionalFiles"))
                .WithAnalyzerConfigDocuments(Documents("EditorConfigFiles"));
            // CommandLineProject already preserves the observed command-line checksum
            // algorithm in ProjectInfo. The installed Solution checksum setter is internal.
            projectInfoAdjustmentTiming?.Dispose();
            using var addProjectTiming = timing?.Start("worker.warmAddProject");
            var project = workspace.AddProject(info);
            addProjectTiming?.Dispose();
            reconstructionTiming?.Dispose();
            return new WarmDesignTimeProject(workspace, project);

            string? ExecutedPath(string property)
            {
                var value = instance.GetPropertyValue(property);
                return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Replace('\\', Path.DirectorySeparatorChar), directory);
            }

            IEnumerable<DocumentInfo> Documents(string itemType)
            {
                var items = instance.GetItems(itemType).ToArray();
                if (items.Length > 32768)
                    throw new ProjectContextException("context.warmAcquisitionUnavailable", "Document inventory exceeds the supported limit.");
                foreach (var item in items)
                {
                    var physical = Path.GetFullPath(item.EvaluatedInclude, directory);
                    var link = item.GetMetadataValue("Link");
                    var relative = Path.GetRelativePath(directory, physical);
                    // The ordinary loader places an unlinked absolute external document
                    // at the logical project root; Link and relative item paths remain explicit.
                    var externalAbsolute = Path.IsPathRooted(item.EvaluatedInclude) &&
                        (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                    var logical = !string.IsNullOrWhiteSpace(link) ? link : externalAbsolute ? Path.GetFileName(physical) : relative;
                    var components = logical.Replace('\\', '/').Split('/');
                    yield return DocumentInfo.Create(DocumentId.CreateNewId(info.Id), components[^1],
                        folders: components.Take(components.Length - 1).ToImmutableArray(),
                        loader: new FileTextLoader(physical, parsed.Encoding), filePath: physical);
                }
            }
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public void Dispose() => workspace.Dispose();

    private sealed class ObservedLifetime(IDisposable value, Action<string>? trace, string terminal) : IDisposable
    {
        public void Dispose()
        {
            value.Dispose();
            trace?.Invoke(terminal);
        }
    }

    private sealed class AcquisitionLogger(Action<string>? diagnosticMessage) : ILogger
    {
        private readonly object gate = new();
        private readonly List<string> messages = [];
        private bool hasErrors;
        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Normal;
        public string? Parameters { get; set; }
        internal bool HasErrors { get { lock (gate) return hasErrors; } }
        internal string[] Messages { get { lock (gate) return messages.ToArray(); } }
        public void Initialize(IEventSource source)
        {
            source.MessageRaised += (_, message) => diagnosticMessage?.Invoke(message.Message ?? string.Empty);
            source.ErrorRaised += (_, error) => Record($"Failure: {error.Code}: {error.Message}", true);
            source.WarningRaised += (_, warning) => Record($"Warning: {warning.Code}: {warning.Message}", false);
        }
        private void Record(string message, bool error)
        {
            lock (gate)
            {
                hasErrors |= error;
                if (messages.Count < 4096) messages.Add(message);
            }
        }
        public void Shutdown() { }
    }
}
