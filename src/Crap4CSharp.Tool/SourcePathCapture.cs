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
        PathIdentityPolicy? pathPolicy = null,
        string? workingDirectory = null)
    {
        pathPolicy ??= PathIdentityPolicy.Current;
        var invocationRoot = pathPolicy.Normalize(workingDirectory ?? Directory.GetCurrentDirectory());
        var explicitSet = explicitFiles.Select(path => pathPolicy.Normalize(path)).ToHashSet(pathPolicy.Comparer);
        var rootIdentities = new Dictionary<string, string?>(pathPolicy.Comparer);
        var normalizedRoots = new List<string>();
        foreach (var rootValue in declaredRoots)
        {
            var normalized = pathPolicy.Normalize(rootValue);
            if (rootIdentities.ContainsKey(normalized)) continue;
            rootIdentities.Add(normalized, pathPolicy.Contains(invocationRoot, normalized)
                ? null
                : ExternalId($"root:{RelativeIdentity(invocationRoot, normalized)}"));
            normalizedRoots.Add(normalized);
        }
        var roots = normalizedRoots.Order(StringComparer.Ordinal).Select(path =>
            {
                if (!Directory.Exists(path))
                    throw new DirectoryNotFoundException($"Selected source root does not exist: {path}");
                return new CoverageSourceRoot(path, ResolvePhysical(path, pathPolicy), rootIdentities[path]);
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
            var physical = ResolvePhysical(localRoot, pathPolicy);
            if (!pathPolicy.Contains(selectedRoot.PhysicalPath, physical))
                throw new CoveragePathException(CoverageReasonCodes.PathOutsideRoot,
                    $"Coverage mapping local root resolves outside its declared selected-source root: {localRootValue}");
        }

        var entries = new List<CoverageSourceEntry>();
        foreach (var fileValue in files.Order(StringComparer.Ordinal))
        {
            var file = pathPolicy.Normalize(fileValue);
            var root = roots.Where(candidate => pathPolicy.Contains(candidate.LocalPath, file))
                .OrderByDescending(candidate => candidate.LocalPath.Length).FirstOrDefault();
            if (root is null)
            {
                var parent = Path.GetDirectoryName(file)!;
                var relativeParent = Path.GetRelativePath(invocationRoot, parent)
                    .Replace(Path.DirectorySeparatorChar, '/');
                root = new CoverageSourceRoot(parent, ResolvePhysical(parent, pathPolicy),
                    ExternalId($"implicit:{relativeParent}"));
                roots.Add(root);
            }

            var physicalFile = ResolvePhysical(file, pathPolicy);
            var physicalRoot = root.PhysicalPath;
            var externalRootId = root.ExternalRootId;
            if (!pathPolicy.Contains(physicalRoot, physicalFile))
            {
                if (!explicitSet.Contains(file))
                    throw new CoveragePathException(CoverageReasonCodes.PathOutsideRoot,
                        $"Selected source resolves outside its recursive source root: {file}");
                physicalRoot = Path.GetDirectoryName(physicalFile)!;
                var linkIdentity = pathPolicy.Contains(invocationRoot, file)
                    ? Path.GetRelativePath(invocationRoot, file).Replace(Path.DirectorySeparatorChar, '/')
                    : $"{root.ExternalRootId}:{Path.GetRelativePath(root.LocalPath, file).Replace(Path.DirectorySeparatorChar, '/')}";
                externalRootId ??= ExternalId($"link:{linkIdentity}");
            }
            var logical = externalRootId is null && pathPolicy.Contains(invocationRoot, file)
                ? Path.GetRelativePath(invocationRoot, file).Replace(Path.DirectorySeparatorChar, '/')
                : pathPolicy.Contains(root.LocalPath, file)
                    ? Path.GetRelativePath(root.LocalPath, file).Replace(Path.DirectorySeparatorChar, '/')
                    : Path.GetFileName(file);
            if (externalRootId is not null) logical = $"../{externalRootId}/{logical}";
            entries.Add(new CoverageSourceEntry(file, logical, root.LocalPath, physicalFile, physicalRoot, externalRootId));
        }

        return new CoverageSourceInventory(pathPolicy, entries, roots);
    }

    private static string ResolvePhysical(string value, PathIdentityPolicy pathPolicy)
        => ResolvePhysical(Path.GetFullPath(value), new HashSet<string>(pathPolicy.Comparer), 0);

    private static string ResolvePhysical(string fullPath, ISet<string> visitedLinks, int depth)
    {
        if (depth > 64)
            throw new CoveragePathException(CoverageReasonCodes.PathOutsideRoot,
                $"Source path link resolution exceeded the safety bound: {fullPath}");
        fullPath = Path.GetFullPath(fullPath);
        var root = Path.GetPathRoot(fullPath) ?? throw new IOException($"Path has no root: {fullPath}");
        var current = root;
        var segments = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var candidate = Path.Combine(current, segments[index]);
            FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            FileSystemInfo? target;
            try
            {
                target = info.ResolveLinkTarget(returnFinalTarget: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new CoveragePathException(CoverageReasonCodes.PathOutsideRoot,
                    $"Unable to resolve source path link '{candidate}': {exception.Message}");
            }
            if (info.LinkTarget is not null && (target is null || !target.Exists))
                throw new CoveragePathException(CoverageReasonCodes.PathOutsideRoot,
                    $"Source path contains a broken link: {candidate}");
            if (target is null)
            {
                current = candidate;
                continue;
            }
            var identity = Path.GetFullPath(candidate);
            if (!visitedLinks.Add(identity))
                throw new CoveragePathException(CoverageReasonCodes.PathOutsideRoot,
                    $"Source path contains a link cycle: {candidate}");
            var remaining = segments.Skip(index + 1).Aggregate(target.FullName, Path.Combine);
            return ResolvePhysical(remaining, visitedLinks, depth + 1);
        }
        return Path.GetFullPath(current);
    }

    private static string ExternalId(string identity)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..12];
        return $"external-{hash}";
    }

    private static string RelativeIdentity(string invocationRoot, string path)
    {
        var relative = Path.GetRelativePath(invocationRoot, path);
        if (Path.IsPathFullyQualified(relative))
        {
            var root = Path.GetPathRoot(relative) ?? string.Empty;
            relative = relative[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }
}
