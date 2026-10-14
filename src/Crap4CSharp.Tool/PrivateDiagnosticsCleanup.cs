using System.Globalization;
using System.Text.Json;

internal sealed class PrivateDiagnosticLease : IDisposable
{
    internal const string MarkerName = ".crap4csharp-private-lifecycle.json";
    internal const string Schema = "private-diagnostics-lifecycle-v1";
    internal const string Prefix = "crap4csharp-fresh-private-";
    private readonly FileStream stream;
    private readonly string root;
    private readonly string producer;
    private readonly string invocation = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private bool disposed;

    internal PrivateDiagnosticLease(string directory, string producer)
    {
        root = Path.GetFullPath(directory);
        this.producer = producer;
        stream = new FileStream(Path.Combine(root, MarkerName), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
        try { Write(null); }
        catch { stream.Dispose(); throw; }
    }

    private void Write(DateTimeOffset? finished)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = Schema, directory = root, toolVersion = producer,
            invocation, startedAt = started, finishedAt = finished
        });
        stream.Position = 0;
        stream.SetLength(0);
        stream.Write(bytes);
        stream.Flush(true);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Write(DateTimeOffset.UtcNow); }
        // Lifecycle bookkeeping must not replace a published outcome. An
        // incomplete marker refuses cleanup; it never attests reusable evidence.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally { stream.Dispose(); }
    }
}

internal static class PrivateDiagnosticsCleanup
{
    internal static async Task<int> RunAsync(string[] args, TextWriter output,
        TextWriter error, CancellationToken token)
    {
        try
        {
            string? directory = null;
            var days = 7;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 1; index < args.Length; index++)
            {
                var option = args[index];
                if (option is not ("--private-diagnostics" or "--older-than-days") ||
                    !seen.Add(option) || ++index >= args.Length)
                    throw new ArgumentException("Cleanup requires one exact private root and an optional positive day bound.");
                if (option == "--private-diagnostics") directory = args[index];
                else if (!int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out days))
                    throw new ArgumentException("Cleanup age must be a positive integer.");
            }
            if (directory is null) throw new ArgumentException("Cleanup requires --private-diagnostics <exact-root>.");
            Delete(directory, days, token);
            await output.WriteLineAsync("Removed the named private diagnostic root.");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            UnauthorizedAccessException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            await error.WriteLineAsync("error: private diagnostic cleanup refused: " + exception.Message);
            return 1;
        }
    }

    internal static void Delete(string directory, int days, CancellationToken token,
        DateTimeOffset? observationTime = null)
    {
        token.ThrowIfCancellationRequested();
        if (days is < 1 or > 36500 || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Cleanup requires an absolute root and an age from 1 to 36500 days.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var leaf = Path.GetFileName(root);
        if (!string.Equals(Path.GetDirectoryName(root), temporary, comparison) ||
            !leaf.StartsWith(PrivateDiagnosticLease.Prefix, StringComparison.Ordinal) ||
            leaf.Length <= PrivateDiagnosticLease.Prefix.Length || leaf.Length > 128 || leaf.Any(char.IsControl))
            throw new ArgumentException("Only an exact tool-private root directly inside the applicable temp directory is eligible.");
        RejectLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The named private root is absent.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(root) &
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("The private root is not owner-only.");
        var marker = Path.Combine(root, PrivateDiagnosticLease.MarkerName);
        RejectLinks(marker);
        // Exclusive producer sharing prevents concurrent lifecycle access.
        // Delete sharing permits removal of this marker on Windows while the
        // exclusive lifecycle handle remains held throughout deletion.
        using var lease = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
        if (lease.Length is <= 0 or > 4096) throw new InvalidDataException("Invalid lifecycle marker size.");
        using var document = JsonDocument.Parse(lease, new JsonDocumentOptions { MaxDepth = 8 });
        var value = document.RootElement;
        var fields = new HashSet<string>(StringComparer.Ordinal)
            { "schemaVersion", "directory", "toolVersion", "invocation", "startedAt", "finishedAt" };
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid lifecycle marker.");
        foreach (var property in value.EnumerateObject())
            if (!fields.Remove(property.Name)) throw new InvalidDataException("Duplicate or unknown lifecycle field.");
        if (fields.Count != 0 || value.GetProperty("schemaVersion").GetString() != PrivateDiagnosticLease.Schema ||
            !string.Equals(value.GetProperty("directory").GetString(), root, comparison))
            throw new InvalidDataException("Lifecycle identity differs from the named root.");
        var producer = value.GetProperty("toolVersion").GetString();
        var invocation = value.GetProperty("invocation").GetString();
        if (string.IsNullOrWhiteSpace(producer) || producer.Length > 256 ||
            invocation is null || invocation.Length != 32 || !invocation.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid lifecycle producer identity.");
        var now = observationTime ?? DateTimeOffset.UtcNow;
        var rootCreated = new DateTimeOffset(Directory.GetCreationTimeUtc(root));
        var markerCreated = new DateTimeOffset(File.GetCreationTimeUtc(marker));
        if (rootCreated > now || markerCreated > now ||
            now - rootCreated < TimeSpan.FromDays(days) || now - markerCreated < TimeSpan.FromDays(days))
            throw new IOException("The private root or lifecycle marker is younger than the requested age.");
        if (value.GetProperty("startedAt").ValueKind != JsonValueKind.String ||
            value.GetProperty("finishedAt").ValueKind != JsonValueKind.String ||
            !value.GetProperty("startedAt").TryGetDateTimeOffset(out var started) ||
            !value.GetProperty("finishedAt").TryGetDateTimeOffset(out var finished) ||
            started.Offset != TimeSpan.Zero || finished.Offset != TimeSpan.Zero ||
            finished < started || finished > now || now - finished < TimeSpan.FromDays(days))
            throw new IOException("The invocation is active, unsealed, future-dated or younger than the requested age.");
        var pending = new Stack<string>();
        pending.Push(root);
        var entries = 0;
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                if (++entries > 262144) throw new IOException("Private cleanup exceeded its finite entry bound.");
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked private contents are not eligible for cleanup.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
        }
        token.ThrowIfCancellationRequested();
        RejectLinks(root);
        Directory.Delete(root, recursive: true);
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked cleanup paths are not eligible.");
    }
}
