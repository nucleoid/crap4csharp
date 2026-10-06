namespace Crap4CSharp.Core;

public sealed record ProcessProtocolResult(int ExitCode, byte[] StandardOutput, byte[] StandardError);

public static partial class ProcessRunner
{
    // Complete binary responses, not diagnostic tails. Overflow is a typed refusal.
    public static async Task<ProcessProtocolResult> RunProtocolAsync(string fileName, IEnumerable<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken,
        int maximumBytesPerStream = 4194304)
    {
        if (maximumBytesPerStream is < 0 or > 16777216) throw new ArgumentOutOfRangeException(nameof(maximumBytesPerStream));
        byte[]? output = null;
        byte[]? error = null;
        async Task<string> Pump(Stream source, string name, CancellationToken token)
        {
            byte[] result = await ProcessByteProtocol.ReadAsync(source, name, maximumBytesPerStream, token);
            if (name == "stdout") output = result; else error = result;
            return string.Empty;
        }
        var process = await RunCoreAsync(fileName, arguments, workingDirectory, timeout, cancellationToken, null, Pump);
        return new(process.ExitCode, output!, error!);
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
