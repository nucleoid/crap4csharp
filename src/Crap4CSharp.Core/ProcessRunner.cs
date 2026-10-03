using System.Diagnostics;

namespace Crap4CSharp.Core;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ProcessTimeoutException(string fileName, TimeSpan timeout)
    : TimeoutException($"{fileName} timed out after {timeout.TotalSeconds:0.###} seconds.")
{
    public string FileName { get; } = fileName;
    public TimeSpan Timeout { get; } = timeout;
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        await RunCoreAsync(fileName, arguments, workingDirectory, timeout, cancellationToken, null);

    public static async Task<ProcessResult> RunWithEnvironmentAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string> environment) =>
        await RunCoreAsync(fileName, arguments, workingDirectory, timeout, cancellationToken, environment);

    private static async Task<ProcessResult> RunCoreAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Process timeout must be positive and finite.");

        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (environment is not null)
            foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
            return new ProcessResult(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
        {
            await TerminateAsync(process);
            try
            {
                await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException($"Could not finish draining output from process {process.Id} after cancellation.", exception);
            }
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("The process was cancelled.", cancellationToken);
            throw new ProcessTimeoutException(fileName, timeout);
        }
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException($"Could not terminate process {process.Id} after cancellation.", exception);
        }
    }
}
