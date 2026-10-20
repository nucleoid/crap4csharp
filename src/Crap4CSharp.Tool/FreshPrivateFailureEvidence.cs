using System.Text;

// Raw exception details stay beside the invocation's existing private logs.
// Diagnostic refusal never replaces the original failure or supplies proof.
internal static class FreshPrivateFailureEvidence
{
    internal const int MaximumBytes = 65536;
    internal const string FileName = "failure-exception.txt";

    internal static void TryRetain(string ownedDirectory, Exception? error, CancellationToken token)
    {
        if (error is null || token.IsCancellationRequested) return;
        try
        {
            token.ThrowIfCancellationRequested();
            for (var parent = Path.GetFullPath(ownedDirectory); parent is not null; parent = Path.GetDirectoryName(parent))
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return;
            var text = "diagnosticStatus=complete\n" + error;
            var length = Encoding.UTF8.GetByteCount(text);
            if (length > MaximumBytes)
                text = "diagnosticStatus=unretained-over-64KiB\nexceptionUtf8Bytes=" + length + "\n";
            var bytes = Encoding.UTF8.GetBytes(text);
            token.ThrowIfCancellationRequested();
            var destination = Path.Combine(ownedDirectory, FileName);
            if (File.Exists(destination)) throw new IOException("Private failure evidence already retained.");
            var pending = Path.Combine(ownedDirectory, FileName + ".pending");
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(bytes);
                stream.Flush();
            }
            token.ThrowIfCancellationRequested();
            File.Move(pending, destination, overwrite: false);
        }
        catch (Exception diagnostic) when (diagnostic is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            // Best-effort private diagnostics cannot replace the primary error.
            error.Data["PrivateFailureDiagnosticFailure"] = diagnostic;
        }
    }
}
