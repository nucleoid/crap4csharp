using System.Text;

namespace Crap4CSharp.Core;

public static class GitChanges
{
    public static IReadOnlyList<string> ParsePorcelainV1Z(ReadOnlySpan<byte> bytes, string repositoryRoot) =>
        ParsePorcelainV1Z(bytes, repositoryRoot, PathIdentityPolicy.Current);

    public static IReadOnlyList<string> ParsePorcelainV1Z(
        ReadOnlySpan<byte> bytes,
        string repositoryRoot,
        PathIdentityPolicy pathPolicy)
    {
        ArgumentNullException.ThrowIfNull(pathPolicy);
        var normalizedRoot = pathPolicy.Normalize(repositoryRoot);
        var records = SplitNull(bytes);
        var paths = new List<string>();
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (record.Length < 4) continue;
            var status = record[..2];
            var path = record[3..];
            var isRenameOrCopy = status.Contains('R') || status.Contains('C');
            if (isRenameOrCopy && index + 1 < records.Count)
                index++; // In -z format the destination/current path is first; skip the old path.

            if (status.Contains('D')) continue;
            var fullPath = pathPolicy.Normalize(path, normalizedRoot);
            if (!pathPolicy.Contains(normalizedRoot, fullPath))
                throw new InvalidDataException($"Git reported a path outside repository root: {path}");
            if (File.Exists(fullPath) && SourceDiscovery.IsSource(fullPath) &&
                !SourceDiscovery.IsExcludedByDirectory(fullPath, repositoryRoot))
                paths.Add(pathPolicy.NormalizeExisting(fullPath));
        }

        return pathPolicy.DistinctOrThrow(paths, "Git changed source inventory");
    }

    private static List<string> SplitNull(ReadOnlySpan<byte> bytes)
    {
        var result = new List<string>();
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0) continue;
            result.Add(Encoding.UTF8.GetString(bytes[start..i]));
            start = i + 1;
        }
        if (start < bytes.Length) result.Add(Encoding.UTF8.GetString(bytes[start..]));
        return result;
    }
}
