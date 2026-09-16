namespace Crap4CSharp.Core;

public static class SourceDiscovery
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "packages", "TestResults", "node_modules"
    };

    public static IReadOnlyList<string> Discover(IEnumerable<string> inputs, string workingDirectory)
    {
        var resolvedInputs = inputs.Any() ? inputs : [workingDirectory];
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in resolvedInputs)
        {
            var path = Path.GetFullPath(input, workingDirectory);
            if (File.Exists(path))
            {
                if (!IsSource(path)) throw new ArgumentException($"Explicit input is not an eligible C# source file: {input}");
                files.Add(path);
                continue;
            }

            if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Input does not exist: {input}");
            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                if (IsSource(file) && !IsExcludedByDirectory(file, path)) files.Add(Path.GetFullPath(file));
            }
        }

        return files.Order(StringComparer.Ordinal).ToArray();
    }

    public static bool IsSource(string path)
    {
        var name = Path.GetFileName(path);
        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExcludedByDirectory(string file, string root)
    {
        var relative = Path.GetRelativePath(root, file);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .SkipLast(1).Any(segment => ExcludedDirectories.Contains(segment)
                || segment.Equals("test", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("tests", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".Test", StringComparison.OrdinalIgnoreCase));
    }
}
