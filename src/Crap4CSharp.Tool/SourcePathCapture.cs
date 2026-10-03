using System.Security.Cryptography;
using System.Text;
using Crap4CSharp.Core;

internal static class SourcePathCapture
{
    public static CoverageSourceInventory Capture(
        IEnumerable<string> files,
        IEnumerable<string> declaredRoots,
        IEnumerable<string> explicitFiles,
        IEnumerable<string> mappingLocalRoots,
        PathIdentityPolicy? pathPolicy = null)
    {
        pathPolicy ??= PathIdentityPolicy.Current;
        var explicitSet = explicitFiles.Select(path => pathPolicy.Normalize(path)).ToHashSet(pathPolicy.Comparer);
        var roots = declaredRoots.Select(path => pathPolicy.Normalize(path)).Distinct(pathPolicy.Comparer)
            .Order(StringComparer.Ordinal).Select(path =>
            {
                if (!Directory.Exists(path))
                    throw new DirectoryNotFoundException($"Selected source root does not exist: {path}");
                return new CoverageSourceRoot(path, ResolvePhysical(path));
            }).ToList();

        foreach (var localRootValue in mappingLocalRoots)
        {
            var localRoot = pathPolicy.Normalize(localRootValue);
            if (!Directory.Exists(localRoot))
                throw new CoveragePathException(CoverageReasonCodes.MissingPath,
                    $"Coverage mapping local root is not an existing directory: {localRootValue}");
            var selectedRoot = roots.Where(root => pathPolicy.Contains(root.LocalPath, localRoot))
                .OrderByDescending(root => root.LocalPath.Length).FirstOrDefault()
                ?? throw new CoveragePathException(CoverageReasonCodes.PathOutsideRoot,
                    $"Coverage mapping local root is outside the declared selected-source roots: {localRootValue}");
            var physical = ResolvePhysical(localRoot);
            if (!pathPolicy.Contains(selectedRoot.PhysicalPath, physical))
                throw new CoveragePathException(CoverageReasonCodes.PathOutsideRoot,
                    $"Coverage mapping local root resolves outside its declared selected-source root: {localRootValue}");
        }

        var entries = new List<CoverageSourceEntry>();
        var externalIndex = 0;
        foreach (var fileValue in files.Order(StringComparer.Ordinal))
        {
            var file = pathPolicy.Normalize(fileValue);
            var root = roots.Where(candidate => pathPolicy.Contains(candidate.LocalPath, file))
                .OrderByDescending(candidate => candidate.LocalPath.Length).FirstOrDefault();
            if (root is null)
            {
                var parent = Path.GetDirectoryName(file)!;
                root = new CoverageSourceRoot(parent, ResolvePhysical(parent), ExternalId(++externalIndex, parent));
                roots.Add(root);
            }

            var physicalFile = ResolvePhysical(file);
            var physicalRoot = root.PhysicalPath;
            var externalRootId = root.ExternalRootId;
            if (!pathPolicy.Contains(physicalRoot, physicalFile))
            {
                if (!explicitSet.Contains(file))
                    throw new CoveragePathException(CoverageReasonCodes.PathOutsideRoot,
                        $"Selected source resolves outside its recursive source root: {file}");
                physicalRoot = Path.GetDirectoryName(physicalFile)!;
                externalRootId ??= ExternalId(++externalIndex, Path.GetFileName(file));
            }
            var logical = pathPolicy.Contains(root.LocalPath, file)
                ? Path.GetRelativePath(root.LocalPath, file).Replace(Path.DirectorySeparatorChar, '/')
                : Path.GetFileName(file);
            if (externalRootId is not null) logical = $"../{externalRootId}/{logical}";
            entries.Add(new CoverageSourceEntry(file, logical, root.LocalPath, physicalFile, physicalRoot, externalRootId));
        }

        return new CoverageSourceInventory(pathPolicy, entries, roots);
    }

    private static string ResolvePhysical(string value)
    {
        var fullPath = Path.GetFullPath(value);
        var root = Path.GetPathRoot(fullPath) ?? throw new IOException($"Path has no root: {value}");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            FileSystemInfo? target;
            try
            {
                target = info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (IOException exception)
            {
                throw new CoveragePathException(CoverageReasonCodes.PathOutsideRoot,
                    $"Unable to resolve source path link '{candidate}': {exception.Message}");
            }
            if (info.LinkTarget is not null && (target is null || !target.Exists))
                throw new CoveragePathException(CoverageReasonCodes.PathOutsideRoot,
                    $"Source path contains a broken link: {candidate}");
            current = target?.FullName ?? candidate;
        }
        return Path.GetFullPath(current);
    }

    private static string ExternalId(int index, string identity)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..12];
        return $"external-{index}-{hash}";
    }
}
