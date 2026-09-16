using System.Text;

namespace Crap4CSharp.Core;

public static class GitChanges
{
    public static IReadOnlyList<string> ParsePorcelainV1Z(ReadOnlySpan<byte> bytes, string repositoryRoot)
    {
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
            var fullPath = Path.GetFullPath(path, repositoryRoot);
            if (File.Exists(fullPath) && SourceDiscovery.IsSource(fullPath) &&
                !SourceDiscovery.IsExcludedByDirectory(fullPath, repositoryRoot)) paths.Add(fullPath);
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
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
