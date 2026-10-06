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
        int eventCount = 0, itemCount = 0;
        long characters = 0;
        bool buildCompleted = false;
        static string Key(BuildEventContext? context) => context is null ? "null" :
            $"{context.SubmissionId}:{context.NodeId}:{context.ProjectContextId}:{context.TargetId}:{context.TaskId}";
        events.BuildFinished += (_, value) => buildCompleted = value.Succeeded;
        events.TaskStarted += (_, value) =>
        {
            if (value.TaskName != "Csc") return;
            if (tasks.Count >= 16 || !tasks.TryAdd(Key(value.BuildEventContext), new Observation(value.ProjectFile)))
                throw new ProjectContextException("compiler.associationInvalid", "Unexpected compiler task association.");
        };
        events.TaskFinished += (_, value) =>
        {
            if (tasks.TryGetValue(Key(value.BuildEventContext), out var observed)) observed.Succeeded = value.Succeeded;
        };
        events.AnyEventRaised += (_, value) =>
        {
            token.ThrowIfCancellationRequested();
            if (++eventCount > 200000) throw new ProjectContextException("compiler.evidenceLimit", "Compiler event limit exceeded.");
            if (value is not TaskParameterEventArgs parameter || parameter.Kind != TaskParameterMessageKind.TaskInput ||
                !tasks.TryGetValue(Key(parameter.BuildEventContext), out var observed) ||
                parameter.ParameterName is not ("Sources" or "References" or "OutputAssembly" or "OutputRefAssembly" or "Analyzers" or "AdditionalFiles" or "AnalyzerConfigFiles")) return;
            if (observed.Parameters.Any(p => p.Name == parameter.ParameterName))
                throw new ProjectContextException("compiler.associationInvalid", "Duplicate compiler input parameter.");
            var items = new List<string>();
            foreach (var item in parameter.Items)
            {
                var text = item is ITaskItem taskItem ? taskItem.ItemSpec : item?.ToString() ?? "";
                if (++itemCount > 32768 || (characters += text.Length) > 2 * 1024 * 1024)
                    throw new ProjectContextException("compiler.evidenceLimit", "Compiler input limit exceeded.");
                items.Add(text);
            }
            observed.Parameters.Add(new(parameter.ParameterName, items));
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
        return new(request.ProjectPath, task.Parameters);
    }

    private sealed class Observation(string? project)
    {
        internal string? Project { get; } = project;
        internal bool? Succeeded { get; set; }
        internal List<CompilerParameter> Parameters { get; } = [];
    }
}
