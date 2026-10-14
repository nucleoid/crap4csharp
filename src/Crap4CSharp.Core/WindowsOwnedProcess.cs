using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Crap4CSharp.Core;

// Windows descendants belong to a non-breakaway, kill-on-close job BEFORE the first
// consumer instruction executes. A normal Process.Start followed by assignment is racy.
internal static class WindowsOwnedProcess
{
    internal static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment,
        Func<Stream, string, CancellationToken, Task<string>>? bytePump = null, string? standardInputPath = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var job = CreateJobObject(IntPtr.Zero, null);
        Check(!job.IsInvalid, "Could not create invocation-owned Windows job.");
        var limits = new ExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE; no breakaway.
        Check(SetJobInformation(job, 9, ref limits, Marshal.SizeOf<ExtendedLimitInformation>()),
            "Could not configure invocation-owned Windows job.");

        using var stdout = new OwnedPipe();
        using var stderr = new OwnedPipe();
        using var stdin = new OwnedPipe();
        using var inputFile = standardInputPath is null ? null
            // Windows may finish job accounting before the last inherited handle
            // closes. Permit owned-file deletion while preserving read-only bytes.
            : File.OpenHandle(standardInputPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        SafeFileHandle consumerInput = inputFile ?? stdin.Read;
        if (inputFile is not null)
            Check(SetHandleInformation(inputFile, 1, 1), "Could not mark owned input snapshot inheritable.");
        // stdout/stderr child writes and stdin child reads are the ONLY inherited handles.
        Check(SetHandleInformation(stdout.Read, 1, 0), "Could not protect stdout reader.");
        Check(SetHandleInformation(stderr.Read, 1, 0), "Could not protect stderr reader.");
        Check(SetHandleInformation(stdin.Write, 1, 0), "Could not protect stdin writer.");
        using var attributes = new HandleList([consumerInput, stdout.Write, stderr.Write]);
        var startup = new StartupInformationEx();
        startup.Startup.cb = Marshal.SizeOf<StartupInformationEx>();
        startup.Startup.dwFlags = 0x100; // STARTF_USESTDHANDLES
        startup.Startup.hStdInput = consumerInput.DangerousGetHandle();
        startup.Startup.hStdOutput = stdout.Write.DangerousGetHandle();
        startup.Startup.hStdError = stderr.Write.DangerousGetHandle();
        startup.AttributeList = attributes.Pointer;
        var command = new StringBuilder(string.Join(" ", new[] { fileName }.Concat(arguments).Select(QuoteArgument)));
        using var environmentBlock = new EnvironmentBlock(environment);
        linked.Token.ThrowIfCancellationRequested();
        Check(CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, true,
            0x4 | 0x400 | 0x80000 | 0x08000000, // SUSPENDED | UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO | NO_WINDOW
            environmentBlock.Pointer, workingDirectory, ref startup, out var information),
            $"Could not start {fileName}.");
        using var nativeProcess = new SafeFileHandle(information.Process, true);
        using var nativeThread = new SafeFileHandle(information.Thread, true);
        Task<string>? output = null;
        Task<string>? error = null;
        bool assigned = false;
        try
        {
            // Fail closed while suspended if nested-job restrictions prevent assignment.
            Check(AssignProcessToJobObject(job, nativeProcess), "Could not assign suspended consumer to invocation-owned Windows job.");
            assigned = true;
            stdout.Write.Dispose();
            stderr.Write.Dispose();
            stdin.Read.Dispose();
            stdin.Write.Dispose(); // Compatibility EOF; protocol input uses a bounded owned read-only file.
            output = ReadAsync(stdout.Read, "stdout", bytePump, linked.Token);
            error = ReadAsync(stderr.Read, "stderr", bytePump, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            using var process = Process.GetProcessById(information.ProcessId);
            Check(ResumeThread(nativeThread) != uint.MaxValue, "Could not resume invocation-owned consumer.");
            await ProcessTextCapture.WaitForCompletionAsync(Task.WhenAll(process.WaitForExitAsync(linked.Token), output, error), output, error, linked.Token);
            int exitCode = process.ExitCode;
            // Parent exit is not EOF, and EOF is not descendant exit. Both are bounded by
            // the operation deadline; job termination follows even on successful exit.
            await Task.WhenAll(output, error).WaitAsync(linked.Token);
            await TerminateJobAsync(job);
            Check(WaitForSingleObject(nativeProcess, 5000) == 0, "Consumer process did not finish termination within cleanup deadline.");
            return new ProcessResult(exitCode, await output, await error);
        }
        catch (Exception exception)
        {
            if (assigned)
            {
                await TerminateJobAsync(job);
                Check(WaitForSingleObject(nativeProcess, 5000) == 0, "Consumer process did not finish termination within cleanup deadline.");
            }
            else
            {
                Check(TerminateProcess(nativeProcess, 1), "Could not terminate unassigned suspended consumer.");
                if (WaitForSingleObject(nativeProcess, 5000) != 0)
                    throw new InvalidOperationException("Suspended consumer did not terminate within cleanup deadline.", exception);
            }
            // Close the parent's child-side descriptors even if setup failed before pumps.
            stdout.Write.Dispose();
            stderr.Write.Dispose();
            stdin.Read.Dispose();
            stdin.Write.Dispose();
            if (output is not null && error is not null)
                await ProcessTextCapture.ObserveTerminatedPumpsAsync(output, error);
            if (exception is OperationCanceledException && linked.IsCancellationRequested)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException("The process was cancelled.", exception, cancellationToken);
                throw new ProcessTimeoutException(fileName, timeout);
            }
            throw;
        }
    }

    private static Task<string> ReadAsync(SafeFileHandle handle, string streamName,
        Func<Stream, string, CancellationToken, Task<string>>? bytePump, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        // Anonymous pipe handles are synchronous; separate concurrent reader tasks avoid
        // pretending that CreatePipe supplied overlapped handles.
        using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        if (bytePump is not null) return await bytePump(stream, streamName, cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await ProcessTextCapture.ReadAsync(reader, streamName);
    });

    private static async Task TerminateJobAsync(SafeFileHandle job)
    {
        Check(TerminateJobObject(job, 1), "Could not terminate invocation-owned Windows job.");
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            Check(QueryJobInformation(job, 1, out var accounting, Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero),
                "Could not observe invocation-owned Windows job cleanup.");
            if (accounting.ActiveProcesses == 0) return;
            if (deadline.Elapsed >= TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("Invocation-owned Windows job still has active processes after termination.");
            await Task.Delay(10, CancellationToken.None);
        }
    }

    // The documented CommandLineToArgvW/MSVC quoting convention: preserve empty args,
    // embedded quotes and runs of backslashes, including trailing backslashes.
    internal static string QuoteArgument(string value)
    {
        if (value.IndexOf('\0') >= 0) throw new ArgumentException("Process arguments cannot contain NUL.", nameof(value));
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastPInvokeError(), message);
    }

    private sealed class OwnedPipe : IDisposable
    {
        internal SafeFileHandle Read { get; }
        internal SafeFileHandle Write { get; }
        internal OwnedPipe()
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            Check(CreatePipe(out var read, out var write, ref security, 0), "Could not create invocation-owned pipe.");
            Read = read;
            Write = write;
        }
        public void Dispose() { Read.Dispose(); Write.Dispose(); }
    }

    private sealed class HandleList : IDisposable
    {
        internal IntPtr Pointer { get; private set; }
        private IntPtr handles;
        private bool initialized;
        internal HandleList(SafeFileHandle[] values)
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            Pointer = Marshal.AllocHGlobal(checked((nint)size));
            try
            {
                Check(InitializeProcThreadAttributeList(Pointer, 1, 0, ref size), "Could not initialize inherited handle list.");
                initialized = true;
                handles = Marshal.AllocHGlobal(values.Length * IntPtr.Size);
                for (int index = 0; index < values.Length; index++)
                    Marshal.WriteIntPtr(handles, index * IntPtr.Size, values[index].DangerousGetHandle());
                Check(UpdateProcThreadAttribute(Pointer, 0, (nuint)0x20002, handles,
                    (nuint)(values.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero), "Could not restrict inherited handles.");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (initialized) { DeleteProcThreadAttributeList(Pointer); initialized = false; }
            if (Pointer != IntPtr.Zero) { Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero; }
            if (handles != IntPtr.Zero) { Marshal.FreeHGlobal(handles); handles = IntPtr.Zero; }
        }
    }

    private sealed class EnvironmentBlock : IDisposable
    {
        internal IntPtr Pointer { get; }
        internal EnvironmentBlock(IReadOnlyDictionary<string, string>? overrides)
        {
            var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                values[(string)entry.Key] = (string)entry.Value!;
            if (overrides is not null)
                foreach (var pair in overrides)
                {
                    if (pair.Key.Length == 0 || pair.Key.Contains('=') || pair.Key.Contains('\0') || pair.Value.Contains('\0'))
                        throw new ArgumentException("Invalid process environment entry.", nameof(overrides));
                    values[pair.Key] = pair.Value;
                }
            Pointer = Marshal.StringToHGlobalUni(string.Join('\0', values.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
        }
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInformation
    {
        public int cb;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, dwFlags;
        public short ShowWindow, ReservedBytes;
        public IntPtr Reserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformationEx { public StartupInformation Startup; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime;
        public uint TotalPageFaults, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetJobInformation(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, int length);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryJobInformation(SafeFileHandle job, int informationClass, out BasicAccountingInformation information, int length, IntPtr returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processSecurity,
        IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string directory, ref StartupInformationEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
}
