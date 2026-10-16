namespace Crap4CSharp.Core;

public sealed record ProcessProtocolResult(int ExitCode, byte[] StandardOutput, byte[] StandardError);

public static partial class ProcessRunner
{
    // Complete binary responses, not diagnostic tails. Overflow is a typed refusal.
    public static async Task<ProcessProtocolResult> RunProtocolAsync(string fileName, IEnumerable<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken,
        int maximumBytesPerStream = 4194304, ReadOnlyMemory<byte>? standardInput = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (maximumBytesPerStream is < 0 or > 16777216) throw new ArgumentOutOfRangeException(nameof(maximumBytesPerStream));
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        if (standardInput?.Length > ProcessInputSnapshot.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(standardInput), "Protocol input exceeds 16 MiB.");
        // Bounded finite protocols use a private immutable input snapshot, not a pipe
        // writer. Consumers may fill BOTH output pipes before reading without deadlock;
        // no blocked write task survives termination. This is not interactive stdin.
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var input = standardInput is null ? null : new ProcessInputSnapshot();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        byte[]? output = null;
        byte[]? error = null;
        async Task<string> Pump(Stream source, string name, CancellationToken token)
        {
            byte[] result = await ProcessByteProtocol.ReadAsync(source, name, maximumBytesPerStream, token);
            if (name == "stdout") output = result; else error = result;
            return string.Empty;
        }
        try
        {
            if (input is not null) await input.WriteAsync(standardInput!.Value, operation.Token);
            TimeSpan remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new OperationCanceledException(operation.Token);
            var process = await RunCoreAsync(fileName, arguments, workingDirectory, remaining,
                operation.Token, environment, Pump, input?.Path);
            return new(process.ExitCode, output!, error!);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("The process was cancelled.", cancellationToken);
            throw new ProcessTimeoutException(fileName, timeout);
        }
        catch (ProcessTimeoutException)
        {
            throw new ProcessTimeoutException(fileName, timeout);
        }
    }
}

internal static class ProcessByteProtocol
{
    internal static async Task<byte[]> ReadAsync(Stream source, string name, int limit, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        // Bounded complete response with no text transcoding. Keep draining after overflow
        // so independent pipes cannot block; the owner still bounds EOF by its deadline.
        using var captured = new MemoryStream();
        long total = 0;
        bool exceeded = false;
        while (true)
        {
            int count = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            total = checked(total + count);
            if (total > limit) exceeded = true;
            if (!exceeded) captured.Write(buffer, 0, count);
        }
        if (exceeded)
        {
            var failure = new InvalidDataException($"Process {name} exceeded the complete protocol limit of {limit} bytes ({total} observed). Use streaming logs for large diagnostics.");
            failure.Data["stream"] = name;
            failure.Data["limitBytes"] = limit;
            failure.Data["observedBytes"] = total;
            throw failure;
        }
        return captured.ToArray();
    }
}

internal sealed class ProcessInputSnapshot : IDisposable
{
    internal const int MaximumBytes = 16777216;
    private readonly string directory = Directory.CreateTempSubdirectory("crap4csharp-owned-input-").FullName;
    internal string Path => System.IO.Path.Combine(directory, "stdin");
    internal ProcessInputSnapshot()
    {
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    internal async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(bytes), "Protocol input exceeds 16 MiB.");
        await using var stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            8192, FileOptions.Asynchronous);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
