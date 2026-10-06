namespace Crap4CSharp.Core;

public sealed record ProcessLogStream(string Path, long TotalBytes, byte[] Tail, bool TailTruncated);
public sealed record LoggedProcessResult(int ExitCode, ProcessLogStream StandardOutput, ProcessLogStream StandardError);

public static partial class ProcessRunner
{
    internal static Task<ProcessResult> RunWithBytePumpAsync(string fileName, IEnumerable<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken,
        Func<Stream, string, CancellationToken, Task<string>> pump) =>
        RunCoreAsync(fileName, arguments, workingDirectory, timeout, cancellationToken, null, pump);

    // Caller supplies its invocation-owned log directory. Each log is exclusively created;
    // existing files are never overwritten. Failed/cancelled runs retain partial diagnostics.
    public static async Task<LoggedProcessResult> RunLoggedAsync(string fileName, IEnumerable<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken,
        string logDirectory, int tailBytes = 65536)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Process timeout must be positive and finite.");
        if (tailBytes is < 0 or > 1048576) throw new ArgumentOutOfRangeException(nameof(tailBytes));
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(logDirectory)) throw new DirectoryNotFoundException(logDirectory);
        string directory = System.IO.Path.GetFullPath(logDirectory);
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Log directory must not traverse a reparse point.");
        using var stdout = new FileStream(System.IO.Path.Combine(directory, "stdout.log"), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
        using var stderr = new FileStream(System.IO.Path.Combine(directory, "stderr.log"), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
        ProcessLogStream? output = null;
        ProcessLogStream? error = null;
        async Task<string> Pump(Stream source, string name, CancellationToken token)
        {
            FileStream sink = name == "stdout" ? stdout : stderr;
            var captured = await ProcessByteLog.ReadAsync(source, sink, tailBytes, token);
            var result = new ProcessLogStream(sink.Name, captured.TotalBytes, captured.Tail, captured.TotalBytes > captured.Tail.Length);
            if (name == "stdout") output = result; else error = result;
            return string.Empty;
        }
        var process = await RunCoreAsync(fileName, arguments, workingDirectory, timeout, cancellationToken, null, Pump);
        return new LoggedProcessResult(process.ExitCode, output!, error!);
    }
}

internal static class ProcessByteLog
{
    internal sealed record Capture(long TotalBytes, byte[] Tail);
    internal static async Task<Capture> ReadAsync(Stream source, Stream sink, int tailBytes, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        byte[] ring = new byte[tailBytes];
        int next = 0;
        long total = 0;
        while (true)
        {
            int count = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            await sink.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            total = checked(total + count);
            if (tailBytes == 0) continue;
            // Copy at most one tail per block. Retention does not grow with output size.
            int retained = Math.Min(count, tailBytes);
            int start = count - retained;
            if (count >= tailBytes) next = 0;
            int first = Math.Min(retained, tailBytes - next);
            buffer.AsSpan(start, first).CopyTo(ring.AsSpan(next));
            buffer.AsSpan(start + first, retained - first).CopyTo(ring);
            next = (next + retained) % tailBytes;
        }
        await sink.FlushAsync(cancellationToken);
        int length = (int)Math.Min(total, tailBytes);
        byte[] tail = new byte[length];
        int oldest = total < tailBytes ? 0 : next;
        int leading = Math.Min(length, tailBytes - oldest);
        ring.AsSpan(oldest, leading).CopyTo(tail);
        ring.AsSpan(0, length - leading).CopyTo(tail.AsSpan(leading));
        return new Capture(total, tail);
    }
}
