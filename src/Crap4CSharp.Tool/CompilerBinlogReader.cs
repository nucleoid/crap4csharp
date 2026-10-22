using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace Crap4CSharp.Core;

internal static class CompilerBinlogReader
{
    internal static CompilerObservation Read(CompilerReadRequest request, CancellationToken token)
    {
        using var stream = File.OpenRead(request.Binlog);
        if (stream.Length > CompilerEvidence.MaximumBinlogBytes)
            throw new ProjectContextException("compiler.binlogLimit", "Private compiler log limit exceeded.");
        var events = new BinaryLogReplayEventSource();
        var tasks = new Dictionary<string, Observation>();
        var copies = new Dictionary<string, Observation>();
        var targets = new Dictionary<string, string>();
        static string TargetKey(BuildEventContext? context) => context is null ? "null" :
            $"{context.SubmissionId}:{context.NodeId}:{context.ProjectContextId}:{context.TargetId}";
        events.TargetStarted += (_, value) => targets[TargetKey(value.BuildEventContext)] = value.TargetName;
        events.TargetFinished += (_, value) => targets.Remove(TargetKey(value.BuildEventContext));
        int eventCount = 0, itemCount = 0;
        long characters = 0;
        var optionalBudget = new CompilerRecipeBudget();
        bool buildCompleted = false;
        static string Key(BuildEventContext? context) => context is null ? "null" :
            $"{context.SubmissionId}:{context.NodeId}:{context.ProjectContextId}:{context.TargetId}:{context.TaskId}";
        events.BuildFinished += (_, value) => buildCompleted = value.Succeeded;
        events.TaskStarted += (_, value) =>
        {
            if (value.TaskName == "Copy" && targets.GetValueOrDefault(TargetKey(value.BuildEventContext)) == "_CopyFilesMarkedCopyLocal")
            {
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (string.IsNullOrWhiteSpace(value.ProjectFile) || !Path.GetFullPath(value.ProjectFile).Equals(request.ProjectPath, comparison))
                    throw new ProjectContextException("compiler.associationInvalid", "Actual copy task belongs to a different compiler project.");
                if (copies.Count >= 4096 || string.IsNullOrWhiteSpace(value.TaskFile) ||
                    !Path.GetFullPath(value.TaskFile).Equals(Path.Combine(request.SdkPath, "Microsoft.Common.CurrentVersion.targets"), comparison) ||
                    !copies.TryAdd(Key(value.BuildEventContext), new Observation(value.ProjectFile)))
                    throw new ProjectContextException("compiler.runtimeCopiesUnproven", "Unexpected selected-SDK copy task association.");
            }
            if (value.TaskName != "Csc") return;
            if (tasks.Count >= 16 || !tasks.TryAdd(Key(value.BuildEventContext), new Observation(value.ProjectFile)
                { Recipe = new(request.SdkPath, request.HostPath, Key(value.BuildEventContext), value.TaskFile) }))
                throw new ProjectContextException("compiler.associationInvalid", "Unexpected compiler task association.");
        };
        events.TaskFinished += (_, value) =>
        {
            if (tasks.TryGetValue(Key(value.BuildEventContext), out var observed))
            { observed.Succeeded = value.Succeeded; observed.Recipe?.Close(); }
            if (copies.TryGetValue(Key(value.BuildEventContext), out var copied)) copied.Succeeded = value.Succeeded;
        };
        events.AnyEventRaised += (_, value) =>
        {
            token.ThrowIfCancellationRequested();
            if (++eventCount > 200000) throw new ProjectContextException("compiler.evidenceLimit", "Compiler event limit exceeded.");
            if (value is TaskCommandLineEventArgs command && command.TaskName == "Csc" &&
                tasks.TryGetValue(Key(command.BuildEventContext), out var commanded))
            {
                if (!optionalBudget.TryCharge(command.CommandLine.Length))
                { commanded.Recipe?.DeclineSize(); commanded.Recipe?.Command("", token); }
                else commanded.Recipe?.Command(command.CommandLine, token);
                return;
            }
            if (value is not TaskParameterEventArgs parameter) return;
            if (tasks.TryGetValue(Key(parameter.BuildEventContext), out var recipeTask) &&
                (parameter.Kind == TaskParameterMessageKind.TaskOutput && parameter.ParameterName == "CommandLineArgs" ||
                 parameter.Kind == TaskParameterMessageKind.TaskInput && CompilerRecipeObservation.ProjectsOption(parameter.ParameterName)))
            {
                var values = new List<string>();
                foreach (var item in parameter.Items)
                {
                    token.ThrowIfCancellationRequested();
                    var text = item is IItemData itemData ? itemData.EvaluatedInclude : item is ITaskItem taskItem ? taskItem.ItemSpec : item?.ToString() ?? "";
                    if (!optionalBudget.TryCharge(text.Length))
                    { recipeTask.Recipe?.DeclineSize(); values.Clear(); break; }
                    values.Add(text);
                }
                if (parameter.Kind == TaskParameterMessageKind.TaskOutput) recipeTask.Recipe?.Arguments(values, token);
                else recipeTask.Recipe?.Option(parameter.ParameterName, values, token);
                return;
            }
            if (parameter.Kind != TaskParameterMessageKind.TaskInput) return;
            if (tasks.TryGetValue(Key(parameter.BuildEventContext), out var emitTask) &&
                parameter.ParameterName is "EmbeddedFiles" or "GeneratedFilesOutputPath" or "PdbFile")
            {
                if (emitTask.Recipe?.BeginEmitParameter(parameter.ParameterName) != true ||
                    emitTask.EmitParameters.Any(input => input.Name == parameter.ParameterName))
                    throw new ProjectContextException("compiler.associationInvalid", "Late or duplicate compiler emit parameter.");
                var values = new List<string>();
                foreach (var item in parameter.Items)
                {
                    token.ThrowIfCancellationRequested();
                    var text = item is IItemData data ? data.EvaluatedInclude : item is ITaskItem taskItem ? taskItem.ItemSpec : item?.ToString() ?? "";
                    if (!optionalBudget.TryCharge(text.Length))
                    { emitTask.Recipe?.DeclineSize(); values.Clear(); break; }
                    values.Add(text);
                }
                if (!optionalBudget.Exceeded) emitTask.EmitParameters.Add(new(parameter.ParameterName, values));
                return;
            }
            Observation? observed;
            if (copies.TryGetValue(Key(parameter.BuildEventContext), out var copy))
            {
                if (parameter.ParameterName is not ("SourceFiles" or "DestinationFiles")) return;
                observed = copy;
            }
            else if (!tasks.TryGetValue(Key(parameter.BuildEventContext), out observed) ||
                parameter.ParameterName is not ("Sources" or "References" or "OutputAssembly" or "OutputRefAssembly" or "Analyzers" or "AdditionalFiles" or "AnalyzerConfigFiles")) return;
            if (observed.Parameters.Any(p => p.Name == parameter.ParameterName))
                throw new ProjectContextException("compiler.associationInvalid", "Duplicate compiler input parameter.");
            var items = new List<string>();
            var referenceProperties = new List<CompilerReferenceProperties>();
            foreach (var item in parameter.Items)
            {
                var text = item is IItemData itemData ? itemData.EvaluatedInclude :
                    item is ITaskItem taskItem ? taskItem.ItemSpec : item?.ToString() ?? "";
                if (parameter.ParameterName == "References")
                {
                    if (item is not (IItemData or ITaskItem))
                        throw new ProjectContextException("compiler.associationInvalid", "Compiler reference item metadata is unavailable.");
                    var metadata = new ItemData("reference", item).EnumerateMetadata()
                        .Where(pair => pair.Key.Equals("Aliases", StringComparison.OrdinalIgnoreCase) ||
                            pair.Key.Equals("EmbedInteropTypes", StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    var aliases = metadata.GetValueOrDefault("Aliases", "");
                    var embedded = metadata.GetValueOrDefault("EmbedInteropTypes", "");
                    if (!string.IsNullOrEmpty(embedded) && !bool.TryParse(embedded, out var parsedEmbedded))
                        throw new ProjectContextException("compiler.associationInvalid", "Compiler embedded-reference metadata is invalid.");
                    characters += aliases.Length + embedded.Length;
                    referenceProperties.Add(new(text, string.IsNullOrEmpty(aliases) ? null : aliases,
                        !string.IsNullOrEmpty(embedded) && bool.Parse(embedded)));
                }
                if (++itemCount > 32768 || (characters += text.Length) > 2 * 1024 * 1024)
                    throw new ProjectContextException("compiler.evidenceLimit", "Compiler input limit exceeded.");
                items.Add(text);
            }
            observed.Parameters.Add(new(parameter.ParameterName, items) { ReferenceProperties = referenceProperties.ToArray() });
        };
        events.Replay(stream, token);
        if (!buildCompleted || tasks.Count != 1)
            throw new ProjectContextException("compiler.observationMissing", "Expected one successful actual compiler invocation.");
        var task = tasks.Values.Single();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (task.Succeeded != true || task.Project is null ||
            !Path.GetFullPath(task.Project).Equals(request.ProjectPath, comparison) ||
            new[] { "Sources", "References", "OutputAssembly" }.Any(name =>
                !task.Parameters.Any(p => p.Name == name && p.Items.Count > 0)) ||
            task.Parameters.Single(p => p.Name == "OutputAssembly").Items.Count != 1)
            throw new ProjectContextException("compiler.associationInvalid", "Actual Csc project, completion or required inputs were not established.");
        var runtime = new List<CompilerCopy>();
        foreach (var copy in copies.Values)
        {
            var source = copy.Parameters.SingleOrDefault(p => p.Name == "SourceFiles")?.Items;
            var destination = copy.Parameters.SingleOrDefault(p => p.Name == "DestinationFiles")?.Items;
            if (copy.Succeeded != true || source is null || destination is null || source.Count != destination.Count)
                throw new ProjectContextException("compiler.runtimeCopiesUnproven", "Actual copy source/destination membership is incomplete.");
            for (var i = 0; i < source.Count; i++)
                runtime.Add(new(Path.GetFullPath(source[i], Path.GetDirectoryName(request.ProjectPath)!),
                    Path.GetFullPath(destination[i], Path.GetDirectoryName(request.ProjectPath)!)));
        }
        var result = new CompilerObservation(request.ProjectPath, task.Parameters) { RuntimeCopies = runtime, InvocationRecipe = task.Recipe?.Finish(),
            CommandLineArguments = task.Recipe?.RawArguments ?? [], EmitParameters = task.Recipe?.SizeDeclined == true ? [] : task.EmitParameters.ToArray() };
        CompilerRecipeObservation.RequireKnownIntegrity(result, token);
        return result;
    }

    private sealed class Observation(string? project)
    {
        internal CompilerRecipeObservation? Recipe { get; init; }
        internal string? Project { get; } = project;
        internal bool? Succeeded { get; set; }
        internal List<CompilerParameter> Parameters { get; } = [];
        internal List<CompilerParameter> EmitParameters { get; } = [];
    }
}
