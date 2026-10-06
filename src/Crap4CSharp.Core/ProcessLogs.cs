using System.Text;

namespace Crap4CSharp.Core;

public sealed record ProcessLogStream(string Path, long TotalBytes, byte[] Tail, bool TailTruncated);
public sealed record LoggedProcessResult(int ExitCode, ProcessLogStream StandardOutput, ProcessLogStream StandardError);

public static partial class ProcessRunner
{
    public static async Task<LoggedProcessResult> RunLoggedAsync(string fileName, IEnumerable<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken,
        string logDirectory, int tailBytes = 65536)
    {
        if (tailBytes is < 0 or > 1048576) throw new ArgumentOutOfRangeException(nameof(tailBytes));
        Directory.CreateDirectory(logDirectory);
        var result = await RunAsync(fileName, arguments, workingDirectory, timeout, cancellationToken);
        async Task<ProcessLogStream> Save(string name, string text)
        {
            string path = System.IO.Path.Combine(logDirectory, name);
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            await stream.WriteAsync(bytes, cancellationToken);
            return new ProcessLogStream(path, bytes.LongLength, bytes, false);
        }
        return new LoggedProcessResult(result.ExitCode, await Save("stdout.log", result.StandardOutput),
            await Save("stderr.log", result.StandardError));
    }
}
