using System.Security.Cryptography;
using System.Text;

namespace Crap4CSharp.Core;

public enum CoveragePathCase { Auto, Sensitive, Insensitive }

public sealed record CoveragePathMapping(string ReportRoot, string LocalRoot);
public sealed record CoveragePathMappingIdentity(string ReportRoot, string LocalRoot, string Id);

public sealed record CoverageSourceRoot(string LocalPath, string PhysicalPath, string? ExternalRootId = null);

public sealed record CoverageSourceEntry(
    string LocalPath,
    string LogicalPath,
    string RootPath,
    string PhysicalPath,
    string RootPhysicalPath,
    string? ExternalRootId);

public sealed class CoverageSourceInventory
{
    public CoverageSourceInventory(
        PathIdentityPolicy pathPolicy,
        IEnumerable<CoverageSourceEntry> entries,
        IEnumerable<CoverageSourceRoot> roots)
    {
        PathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        Entries = entries.Select(entry => entry with
        {
            LocalPath = pathPolicy.Normalize(entry.LocalPath),
            RootPath = pathPolicy.Normalize(entry.RootPath),
            PhysicalPath = pathPolicy.Normalize(entry.PhysicalPath),
            RootPhysicalPath = pathPolicy.Normalize(entry.RootPhysicalPath),
            LogicalPath = entry.LogicalPath.Replace('\\', '/')
        }).OrderBy(entry => entry.LogicalPath, StringComparer.Ordinal).ThenBy(entry => entry.LocalPath, StringComparer.Ordinal).ToArray();
        Roots = roots.Select(root => root with
        {
            LocalPath = pathPolicy.Normalize(root.LocalPath),
            PhysicalPath = pathPolicy.Normalize(root.PhysicalPath)
        }).OrderBy(root => root.LocalPath, StringComparer.Ordinal).ToArray();

        _ = pathPolicy.DistinctOrThrow(Entries.Select(entry => entry.LocalPath), "coverage source inventory");
        foreach (var entry in Entries)
        {
            if (!pathPolicy.Contains(entry.RootPath, entry.LocalPath) ||
                !pathPolicy.Contains(entry.RootPhysicalPath, entry.PhysicalPath))
                throw new CoveragePathException(CoverageReasonCodes.PathOutsideRoot,
                    $"Selected source is outside its captured root: {entry.LogicalPath}");
        }
    }

    public PathIdentityPolicy PathPolicy { get; }
    public IReadOnlyList<CoverageSourceEntry> Entries { get; }
    public IReadOnlyList<CoverageSourceRoot> Roots { get; }
}

public sealed class CoveragePathResolver
{
    private readonly PathIdentityPolicy localPolicy;
    private readonly CoverageSourceInventory inventory;
    private readonly CoveragePathCase pathCase;
    private readonly IReadOnlyList<MappingRule> mappings;
    private readonly PathDialect localDialect;

    public CoveragePathResolver(
        PathIdentityPolicy localPolicy,
        CoverageSourceInventory inventory,
        IEnumerable<CoveragePathMapping> mappings,
        CoveragePathCase pathCase)
    {
        this.localPolicy = localPolicy ?? throw new ArgumentNullException(nameof(localPolicy));
        this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        this.pathCase = pathCase;
        localDialect = inventory.Roots.Select(root => ParseAbsoluteLocal(root.LocalPath).Dialect).FirstOrDefault(
            OperatingSystem.IsWindows() ? PathDialect.WindowsDrive : PathDialect.Posix);
        this.mappings = ValidateMappings(mappings).ToArray();
        MappingIdentities = this.mappings.Select(rule => new CoveragePathMappingIdentity(
            CanonicalForIdentity(rule.ReportRoot), rule.LocalRoot, rule.Id)).ToArray();
    }

    public IReadOnlyList<CoveragePathMappingIdentity> MappingIdentities { get; }

    public CoveragePathResolution Resolve(
        string reportedPath,
        IReadOnlyList<string> sourceRoots,
        string reportPath,
        string? reportId = null,
        string? observationId = null)
    {
        if (!TryParse(reportedPath, null, out var parsed, out var error))
            return Failure(CoveragePathResolutionStatus.Invalid, CoverageReasonCodes.InvalidPath, error!, reportId, observationId);

        var reportDirectory = Path.GetDirectoryName(localPolicy.Normalize(reportPath))
            ?? throw new ArgumentException($"Coverage report has no parent directory: {reportPath}", nameof(reportPath));
        var candidates = new List<LexicalPath>();
        if (parsed!.IsAbsolute)
        {
            candidates.Add(parsed);
        }
        else
        {
            var roots = sourceRoots.Count == 0 ? [reportDirectory] : sourceRoots;
            foreach (var rawRoot in roots)
            {
                if (!TryParse(rawRoot, null, out var parsedRoot, out error))
                    return Failure(CoveragePathResolutionStatus.Invalid, CoverageReasonCodes.InvalidPath, error!, reportId, observationId);
                LexicalPath absoluteRoot;
                if (parsedRoot!.IsAbsolute)
                {
                    absoluteRoot = parsedRoot;
                }
                else
                {
                    var origin = ParseAbsoluteLocal(reportDirectory);
                    if (!TryCombine(origin, rawRoot, requireInsideBase: false, out absoluteRoot, out error))
                        return Failure(CoveragePathResolutionStatus.Invalid, CoverageReasonCodes.InvalidPath, error!, reportId, observationId);
                }
                if (!TryCombine(absoluteRoot, reportedPath, requireInsideBase: true, out var candidate, out error))
                    return Failure(CoveragePathResolutionStatus.OutsideRoot, CoverageReasonCodes.PathOutsideRoot, error!, reportId, observationId);
                candidates.Add(candidate);
            }
        }

        var distinctCandidates = DistinctLexical(candidates);
        var resolved = new List<(CoverageSourceEntry Entry, string? MappingId)>();
        foreach (var candidate in distinctCandidates)
        {
            var candidateResult = ResolveAbsolute(candidate);
            if (candidateResult.HardFailure is not null) return candidateResult.HardFailure;
            resolved.AddRange(candidateResult.Matches);
        }

        var distinct = resolved.GroupBy(item => item.Entry.LocalPath, localPolicy.Comparer)
            .Select(group => group.OrderBy(item => item.Entry.LogicalPath, StringComparer.Ordinal).First())
            .OrderBy(item => item.Entry.LogicalPath, StringComparer.Ordinal).ToArray();
        if (distinct.Length == 1)
            return new CoveragePathResolution(CoveragePathResolutionStatus.Resolved, distinct[0].Entry.LocalPath,
                [distinct[0].Entry.LogicalPath], distinct[0].MappingId, null);
        if (distinct.Length > 1)
        {
            var paths = distinct.Select(item => item.Entry.LogicalPath).Order(StringComparer.Ordinal).ToArray();
            var diagnostic = CoverageDiagnostic.Create(CoverageReasonCodes.AmbiguousPath, CoverageDiagnosticStage.Path,
                CoverageDiagnosticSeverity.Error, CoverageDiagnosticScope.Observation, reportId, observationId,
                candidatePaths: paths, message: "Coverage path resolves to more than one selected source identity.");
            return new CoveragePathResolution(CoveragePathResolutionStatus.Ambiguous, null, paths, null, diagnostic);
        }
        return Failure(CoveragePathResolutionStatus.Missing, CoverageReasonCodes.MissingPath,
            "Coverage path does not resolve to a selected source inventory entry.", reportId, observationId);
    }

    private (IReadOnlyList<(CoverageSourceEntry Entry, string? MappingId)> Matches, CoveragePathResolution? HardFailure)
        ResolveAbsolute(LexicalPath candidate)
    {
        var matchingRules = mappings.Where(rule => PrefixMatches(rule.ReportRoot, candidate))
            .OrderByDescending(rule => rule.ReportRoot.Components.Count).ToArray();
        if (matchingRules.Length > 0)
        {
            var winningLength = matchingRules[0].ReportRoot.Components.Count;
            var winners = matchingRules.Where(rule => rule.ReportRoot.Components.Count == winningLength).ToArray();
            var destinations = winners.Select(rule => rule.LocalRoot).Distinct(localPolicy.Comparer).ToArray();
            if (destinations.Length != 1)
            {
                var diagnostic = CoverageDiagnostic.Create(CoverageReasonCodes.PathMappingConflict, CoverageDiagnosticStage.Path,
                    CoverageDiagnosticSeverity.Error, CoverageDiagnosticScope.Report,
                    mappingId: string.Join(",", winners.Select(rule => rule.Id).Order(StringComparer.Ordinal)),
                    message: "Equal-specificity coverage path mappings select different local roots.");
                return ([], new CoveragePathResolution(CoveragePathResolutionStatus.MappingConflict, null, [], null, diagnostic));
            }

            var winner = winners.OrderBy(rule => rule.Id, StringComparer.Ordinal).First();
            var suffix = candidate.Components.Skip(winningLength).ToArray();
            var translated = suffix.Aggregate(winner.LocalRoot, Path.Combine);
            translated = localPolicy.Normalize(translated);
            if (!localPolicy.Contains(winner.LocalRoot, translated))
            {
                var diagnostic = CoverageDiagnostic.Create(CoverageReasonCodes.PathOutsideRoot, CoverageDiagnosticStage.Path,
                    CoverageDiagnosticSeverity.Error, CoverageDiagnosticScope.Observation, mappingId: winner.Id,
                    message: "Coverage mapping suffix escapes its local target root.");
                return ([], new CoveragePathResolution(CoveragePathResolutionStatus.OutsideRoot, null, [], winner.Id, diagnostic));
            }
            var mapped = inventory.Entries.Where(entry => localPolicy.Comparer.Equals(entry.LocalPath, translated) &&
                localPolicy.Contains(entry.RootPath, translated)).Select(entry => (entry, (string?)winner.Id)).ToArray();
            return (mapped, null);
        }

        if (candidate.Dialect != localDialect) return ([], null);
        var native = candidate.ToLocalPath();
        var comparison = ForeignComparison(candidate.Dialect);
        var matches = inventory.Entries.Where(entry => string.Equals(entry.LocalPath, native, comparison))
            .Select(entry => (entry, (string?)null)).ToArray();
        return (matches, null);
    }

    private IEnumerable<MappingRule> ValidateMappings(IEnumerable<CoveragePathMapping> supplied)
    {
        var output = new List<MappingRule>();
        foreach (var mapping in supplied)
        {
            if (!TryParse(mapping.ReportRoot, null, out var reportRoot, out var error) || !reportRoot!.IsAbsolute)
                throw new CoveragePathException(CoverageReasonCodes.InvalidPath,
                    error ?? $"Coverage mapping report root must be absolute: {mapping.ReportRoot}");
            var localRoot = localPolicy.Normalize(mapping.LocalRoot);
            if (!inventory.Roots.Any(root => localPolicy.Contains(root.LocalPath, localRoot)))
                throw new CoveragePathException(CoverageReasonCodes.PathOutsideRoot,
                    $"Coverage mapping local root is outside the declared selected-source roots: {mapping.LocalRoot}");
            var equivalent = output.Where(rule => LexicalEquals(rule.ReportRoot, reportRoot)).ToArray();
            if (equivalent.Any(rule => !localPolicy.Comparer.Equals(rule.LocalRoot, localRoot)))
                throw new CoveragePathException(CoverageReasonCodes.PathMappingConflict,
                    $"Equivalent coverage report roots map to different local roots: {mapping.ReportRoot}");
            if (equivalent.Length > 0) continue;
            var rootIdentity = inventory.Roots.First(root => localPolicy.Contains(root.LocalPath, localRoot));
            var logicalDestination = rootIdentity.ExternalRootId ??
                Path.GetRelativePath(rootIdentity.LocalPath, localRoot).Replace(Path.DirectorySeparatorChar, '/');
            var identityText = $"{CanonicalForIdentity(reportRoot)}\n{logicalDestination}\n{pathCase}";
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityText))).ToLowerInvariant();
            output.Add(new MappingRule(reportRoot, localRoot, id));
        }
        return output.OrderBy(rule => CanonicalForIdentity(rule.ReportRoot), StringComparer.Ordinal)
            .ThenBy(rule => rule.Id, StringComparer.Ordinal);
    }

    private CoveragePathResolution Failure(CoveragePathResolutionStatus status, string code, string message,
        string? reportId, string? observationId)
    {
        var diagnostic = CoverageDiagnostic.Create(code, CoverageDiagnosticStage.Path, CoverageDiagnosticSeverity.Error,
            CoverageDiagnosticScope.Observation, reportId, observationId, message: message);
        return new CoveragePathResolution(status, null, [], null, diagnostic);
    }

    private IReadOnlyList<LexicalPath> DistinctLexical(IEnumerable<LexicalPath> paths)
    {
        var output = new List<LexicalPath>();
        foreach (var path in paths.OrderBy(CanonicalForIdentity, StringComparer.Ordinal))
            if (!output.Any(existing => LexicalEquals(existing, path))) output.Add(path);
        return output;
    }

    private bool PrefixMatches(LexicalPath prefix, LexicalPath path)
    {
        if (prefix.Dialect != path.Dialect || prefix.IsAbsolute != path.IsAbsolute) return false;
        var comparison = ForeignComparison(path.Dialect);
        if (!string.Equals(prefix.Root, path.Root, comparison) || prefix.Components.Count > path.Components.Count) return false;
        return prefix.Components.Select((component, index) => string.Equals(component, path.Components[index], comparison)).All(value => value);
    }

    private bool LexicalEquals(LexicalPath left, LexicalPath right)
    {
        if (left.Dialect != right.Dialect || left.IsAbsolute != right.IsAbsolute || left.Components.Count != right.Components.Count)
            return false;
        var comparison = ForeignComparison(left.Dialect);
        return string.Equals(left.Root, right.Root, comparison) &&
            left.Components.Select((component, index) => string.Equals(component, right.Components[index], comparison)).All(value => value);
    }

    private StringComparison ForeignComparison(PathDialect dialect) => pathCase switch
    {
        CoveragePathCase.Sensitive => StringComparison.Ordinal,
        CoveragePathCase.Insensitive => StringComparison.OrdinalIgnoreCase,
        _ => dialect is PathDialect.WindowsDrive or PathDialect.WindowsUnc
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal
    };

    private string CanonicalForIdentity(LexicalPath path)
    {
        var value = path.Canonical;
        return ForeignComparison(path.Dialect) == StringComparison.OrdinalIgnoreCase ? value.ToUpperInvariant() : value;
    }

    private static LexicalPath ParseAbsoluteLocal(string value)
    {
        if (!TryParse(value, null, out var parsed, out var error) || !parsed!.IsAbsolute)
            throw new ArgumentException(error ?? $"Local path is not absolute: {value}");
        return parsed;
    }

    private static bool TryParse(string value, PathDialect? inheritedDialect, out LexicalPath? path, out string? error)
    {
        path = null;
        error = null;
        if (value.IndexOf('\0') >= 0) { error = "Coverage path contains NUL."; return false; }
        if (value.Length == 0) { error = "Coverage path is empty."; return false; }
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal) || value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            value.StartsWith("//?/", StringComparison.Ordinal) || value.StartsWith("//./", StringComparison.Ordinal))
        { error = "Windows device namespace paths are unsupported."; return false; }
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0 && value[..scheme].All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '-' or '.'))
        { error = "URI coverage paths are unsupported."; return false; }

        if (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        {
            if (value.Length < 3 || value[2] is not ('/' or '\\')) { error = "Drive-relative coverage paths are unsupported."; return false; }
            return Build(PathDialect.WindowsDrive, char.ToUpperInvariant(value[0]) + ":", value[3..], true, out path, out error);
        }
        if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
        {
            var parts = value[2..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] is "." or ".." || parts[1] is "." or "..")
            { error = "UNC coverage path requires server and share roots."; return false; }
            return Build(PathDialect.WindowsUnc, $"//{parts[0]}/{parts[1]}", string.Join('/', parts.Skip(2)), true, out path, out error);
        }
        if (value[0] == '\\') { error = "Root-relative Windows coverage paths are unsupported."; return false; }
        if (value[0] == '/')
            return Build(PathDialect.Posix, "/", value.TrimStart('/'), true, out path, out error);

        return Build(inheritedDialect ?? PathDialect.Relative, string.Empty, value, false, out path, out error);
    }

    private static bool Build(PathDialect dialect, string root, string remainder, bool absolute,
        out LexicalPath? path, out string? error)
    {
        path = null;
        error = null;
        var separators = dialect is PathDialect.WindowsDrive or PathDialect.WindowsUnc ? new[] { '/', '\\' } : ['/'];
        var components = new List<string>();
        foreach (var component in remainder.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".") continue;
            if (component == "..")
            {
                if (components.Count == 0)
                {
                    if (absolute) { error = "Coverage path escapes its lexical root."; return false; }
                    components.Add(component);
                }
                else if (components[^1] == "..") components.Add(component);
                else components.RemoveAt(components.Count - 1);
                continue;
            }
            components.Add(component);
        }
        path = new LexicalPath(dialect, root, components, absolute);
        return true;
    }

    private static bool TryCombine(LexicalPath absoluteBase, string relativeValue, bool requireInsideBase,
        out LexicalPath combined, out string? error)
    {
        combined = absoluteBase;
        error = null;
        if (!absoluteBase.IsAbsolute) { error = "Coverage path base is not absolute."; return false; }
        if (!TryParse(relativeValue, absoluteBase.Dialect, out var relative, out error)) return false;
        if (relative!.IsAbsolute) { combined = relative; return true; }
        var components = absoluteBase.Components.ToList();
        var floor = requireInsideBase ? components.Count : 0;
        var separators = absoluteBase.Dialect is PathDialect.WindowsDrive or PathDialect.WindowsUnc ? new[] { '/', '\\' } : ['/'];
        foreach (var component in relativeValue.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".") continue;
            if (component == "..")
            {
                if (components.Count <= floor) { error = "Coverage path escapes its declared source root."; return false; }
                components.RemoveAt(components.Count - 1);
            }
            else components.Add(component);
        }
        combined = absoluteBase with { Components = components };
        return true;
    }

    private enum PathDialect { Relative, Posix, WindowsDrive, WindowsUnc }

    private sealed record LexicalPath(PathDialect Dialect, string Root, IReadOnlyList<string> Components, bool IsAbsolute)
    {
        public string Canonical => Dialect switch
        {
            PathDialect.Posix => "/" + string.Join('/', Components),
            PathDialect.WindowsDrive => Root + "/" + string.Join('/', Components),
            PathDialect.WindowsUnc => Root + (Components.Count == 0 ? string.Empty : "/" + string.Join('/', Components)),
            _ => string.Join('/', Components)
        };

        public string ToLocalPath()
        {
            var value = Dialect switch
            {
                PathDialect.Posix => Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, Components),
                PathDialect.WindowsDrive => Root + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, Components),
                PathDialect.WindowsUnc => new string(Path.DirectorySeparatorChar, 2) +
                    Root.TrimStart('/').Replace('/', Path.DirectorySeparatorChar) +
                    (Components.Count == 0 ? string.Empty : Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, Components)),
                _ => string.Join(Path.DirectorySeparatorChar, Components)
            };
            return Path.GetFullPath(value);
        }
    }

    private sealed record MappingRule(LexicalPath ReportRoot, string LocalRoot, string Id);
}
