using System.Text;

namespace Crap4CSharp.Core;

public sealed record ProcessProtocolResult(int ExitCode, byte[] StandardOutput, byte[] StandardError);

public static partial class ProcessRunner
{
    public static async Task<ProcessProtocolResult> RunProtocolAsync(string fileName, IEnumerable<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken,
        int maximumBytesPerStream = 4194304)
    {
        if (maximumBytesPerStream is < 0 or > 16777216) throw new ArgumentOutOfRangeException(nameof(maximumBytesPerStream));
        var result = await RunAsync(fileName, arguments, workingDirectory, timeout, cancellationToken);
        return new(result.ExitCode, Encoding.UTF8.GetBytes(result.StandardOutput), Encoding.UTF8.GetBytes(result.StandardError));
    }
}
