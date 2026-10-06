using System.Diagnostics;
using System.Runtime.InteropServices;

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

        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
            return await WindowsOwnedProcess.RunAsync(fileName, arguments, workingDirectory, timeout, cancellationToken, environment);
        // On Linux a retained session leader owns the group even when the consumer exits.
        // The start handshake prevents the consumer from spawning before ownership is verified.
        bool ownedSession = OperatingSystem.IsLinux();
        string consumerExecutable = ownedSession ? ResolveLinuxExecutable(fileName, workingDirectory, environment) : fileName;
        using var sessionStatus = ownedSession ? new SessionStatus() : null;
        var startInfo = new ProcessStartInfo(ownedSession ? "/usr/bin/setsid" : fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = ownedSession,
            UseShellExecute = false
        };
        if (environment is not null)
            foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;
        if (ownedSession)
        {
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("/bin/sh");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("status=$1; shift; read -r start || exit 125; \"$@\" & child=$!; exec >/dev/null 2>&1; wait \"$child\"; result=$?; printf '%s\\n' \"$result\" > \"$status\"; : > \"$status.done\"; read -r release; exit \"$result\"");
            startInfo.ArgumentList.Add("crap4csharp-owned-session");
            startInfo.ArgumentList.Add(sessionStatus!.Path);
            startInfo.ArgumentList.Add(consumerExecutable);
        }
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        bool sessionVerified = false;
        try
        {
            if (ownedSession)
            {
                while (GetProcessGroup(process.Id) != process.Id)
                {
                    if (process.HasExited)
                        throw new InvalidOperationException("Could not establish invocation-owned Linux session.");
                    await Task.Delay(1, linkedSource.Token);
                }
                sessionVerified = true;
                await process.StandardInput.WriteLineAsync("start".AsMemory(), linkedSource.Token);
                await process.StandardInput.FlushAsync(linkedSource.Token);
                // The supervisor has closed its own output descriptors. EOF now proves that
                // all inherited consumer pipes completed, not merely that the parent exited.
                await Task.WhenAll(output, error).WaitAsync(linkedSource.Token);
                int consumerExitCode = await sessionStatus!.ReadAsync(process, linkedSource.Token);
                // EOF alone does not prove descendant exit: children can close or redirect
                // both pipes and remain alive. Retain the group leader until cleanup ends.
                TerminateSession(process);
                await TerminateAsync(process);
                await WaitForSessionTerminationAsync(process.Id);
                return new ProcessResult(consumerExitCode, await output, await error);
            }
            await process.WaitForExitAsync(linkedSource.Token);
            await Task.WhenAll(output, error).WaitAsync(linkedSource.Token);
            return new ProcessResult(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
        {
            if (sessionVerified && !process.HasExited) TerminateSession(process);
            await TerminateAsync(process);
            if (sessionVerified) await WaitForSessionTerminationAsync(process.Id);
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
        catch
        {
            // Setup, sink and pipe failures own the same cleanup duty as cancellation.
            if (sessionVerified && !process.HasExited) TerminateSession(process);
            await TerminateAsync(process);
            if (sessionVerified) await WaitForSessionTerminationAsync(process.Id);
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5));
            throw;
        }
    }

    private static string ResolveLinuxExecutable(string fileName, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
    {
        // Resolve an external executable, not a shell builtin, and preserve ordinary OS
        // launch diagnostics for missing/non-executable consumers. This is a preflight,
        // not an atomic exec attestation: later removal still yields a nonzero shell exit.
        if (string.IsNullOrEmpty(fileName) || fileName.Contains('\0'))
            throw new ArgumentException("Executable name must be nonempty and contain no NUL.", nameof(fileName));
        IEnumerable<string> candidates;
        if (fileName.Contains('/')) candidates = [Path.GetFullPath(fileName, workingDirectory)];
        else
        {
            string? path = environment is not null && environment.TryGetValue("PATH", out string? overridePath)
                ? overridePath : Environment.GetEnvironmentVariable("PATH");
            candidates = (path ?? "/bin:/usr/bin").Split(':').Select(directory =>
                Path.GetFullPath(Path.Combine(directory.Length == 0 ? workingDirectory : directory, fileName), workingDirectory));
        }
        bool denied = false;
        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate)) { denied = true; continue; }
            if (!File.Exists(candidate)) continue;
            if (CheckExecutableAccess(candidate, 1) == 0) return candidate;
            denied = true;
        }
        throw new System.ComponentModel.Win32Exception(denied ? 13 : 2, $"Could not start {fileName}: executable is {(denied ? "not executable" : "missing")}.");
    }

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int CheckExecutableAccess(string path, int mode);

    private sealed class SessionStatus : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("crap4csharp-owned-session-").FullName;
        public string Path => System.IO.Path.Combine(directory, "status");

        public SessionStatus()
        {
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public async Task<int> ReadAsync(Process supervisor, CancellationToken cancellationToken)
        {
            while (!File.Exists(Path + ".done"))
            {
                if (supervisor.HasExited)
                    throw new InvalidOperationException("Invocation-owned supervisor exited before publishing consumer status.");
                await Task.Delay(1, cancellationToken);
            }
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 2 or > 4)
                throw new InvalidOperationException("Invocation-owned consumer status is invalid.");
            using var reader = new StreamReader(stream);
            string value = await reader.ReadToEndAsync(cancellationToken);
            if (!int.TryParse(value.TrimEnd('\n'), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int result) || result is < 0 or > 255)
                throw new InvalidOperationException("Invocation-owned consumer status is invalid.");
            return result;
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static extern int GetProcessGroup(int processId);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int processId, int signal);

    private static void TerminateSession(Process supervisor)
    {
        // A live retained leader prevents group-ID reuse. Never signal an unrelated group.
        if (supervisor.HasExited)
            throw new InvalidOperationException("Invocation-owned session leader exited before containment cleanup.");
        if (GetProcessGroup(supervisor.Id) != supervisor.Id)
            throw new InvalidOperationException("Invocation-owned session identity changed before containment cleanup.");
        if (SendSignal(-supervisor.Id, 9) != 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Could not terminate invocation-owned session.");
    }

    private static async Task WaitForSessionTerminationAsync(int groupId)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            bool liveMember = false;
            foreach (string directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out _)) continue;
                string stat;
                try { stat = File.ReadAllText(Path.Combine(directory, "stat")); }
                catch (IOException) when (!Directory.Exists(directory)) { continue; }
                string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture) == groupId &&
                    fields[0] is not "Z" and not "X")
                {
                    liveMember = true;
                    break;
                }
            }
            if (!liveMember) return;
            if (deadline.Elapsed >= TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("Invocation-owned session still has live descendants after termination.");
            await Task.Delay(10, CancellationToken.None);
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
