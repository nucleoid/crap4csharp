namespace Crap4CSharp.Tests;

internal sealed class TestDirectory : IDisposable
{
    private readonly string rootWithSeparator;
    private bool disposed;

    private TestDirectory(string path)
    {
        Path = path;
        rootWithSeparator = Path.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? Path
            : Path + System.IO.Path.DirectorySeparatorChar;
    }

    public string Path { get; }

    public static TestDirectory Create(string prefix)
    {
        var parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix);
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(parent, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        return new TestDirectory(path);
    }

    public string Write(string relativePath, string contents)
    {
        var path = ResolveOwnedPath(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void DeleteFile(string relativePath)
    {
        var path = ResolveOwnedPath(relativePath);
        RejectReparsePoints(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing to delete a reparse point from the test fixture: {path}");
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    public void Dispose()
    {
        if (disposed) return;

        if (Directory.Exists(Path))
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false
            };
            foreach (var file in Directory.EnumerateFiles(Path, "*", options))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(Path, recursive: true);
        }

        disposed = true;
    }

    private string ResolveOwnedPath(string relativePath)
    {
        if (System.IO.Path.IsPathRooted(relativePath))
            throw new ArgumentException("Test fixture paths must be relative.", nameof(relativePath));

        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
        if (!path.StartsWith(rootWithSeparator, PathComparison) && !string.Equals(path, Path, PathComparison))
            throw new ArgumentException("Test fixture path escapes its owned root.", nameof(relativePath));
        return path;
    }

    private void RejectReparsePoints(string path)
    {
        var relative = System.IO.Path.GetRelativePath(Path, path);
        var current = Path;
        foreach (var segment in relative.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Refusing to traverse a reparse point in the test fixture: {current}");
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
