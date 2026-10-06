using System.Text;

namespace Crap4CSharp.Core;

internal static class ProcessTextCapture
{
    internal static async Task WaitForCompletionAsync(Task completion, Task output, Task error, CancellationToken cancellationToken)
    {
        // WhenAll alone waits for the other pipe forever after a failed sink. Signal the
        // first pump fault immediately so invocation ownership can terminate descendants.
        var failure = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (Task pump in new[] { output, error })
            _ = pump.ContinueWith(task =>
            {
                if (task.IsFaulted || task.IsCanceled) failure.TrySetResult(task);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        // Register observation before the bounded wait, including cancellation of that wait.
        _ = completion.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Task winner = await Task.WhenAny(completion, failure.Task).WaitAsync(cancellationToken);
        if (winner == failure.Task) await await failure.Task;
        else await completion;
    }

    internal static async Task ObserveTerminatedPumpsAsync(Task output, Task error)
    {
        Task all = Task.WhenAll(output, error);
        try { await all.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) when (all.IsCompleted)
        {
            // The owner has already terminated its invocation. Observe both failures
            // without replacing the original timeout, cancellation or execution fault.
            // A still-running drain is different: the independent cleanup timeout escapes.
            _ = all.Exception;
        }
    }

    internal static async Task<string> ReadAsync(TextReader reader, string streamName)
    {
        const int limit = ProcessRunner.MaximumCapturedCharactersPerStream;
        var captured = new StringBuilder();
        char[] buffer = new char[4096];
        long observed = 0;
        bool exceeded = false;
        while (true)
        {
            // The owner bounds EOF/drain with its operation deadline and terminates
            // descendants before cleanup. Continue draining after overflow so neither
            // concurrent pipe blocks, without retaining any further protocol content.
            int count = await reader.ReadAsync(buffer.AsMemory());
            if (count == 0) break;
            observed = checked(observed + count);
            if (observed > limit) exceeded = true;
            if (!exceeded) captured.Append(buffer, 0, count);
        }
        if (exceeded)
        {
            var failure = new InvalidDataException($"Process {streamName} exceeded the complete text capture limit of {limit} characters ({observed} observed). Use bounded streaming logs for large command output.");
            failure.Data["stream"] = streamName;
            failure.Data["limitCharacters"] = limit;
            failure.Data["observedCharacters"] = observed;
            throw failure;
        }
        return captured.ToString();
    }
}
