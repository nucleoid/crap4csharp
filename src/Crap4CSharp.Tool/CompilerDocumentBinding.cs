using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Crap4CSharp.Core;

internal sealed record CompilerDocumentPathMapPair(string SourcePrefix, string DestinationPrefix);
internal sealed record CompilerDocumentPathMap(string TaskAssociationSha256,
    ImmutableArray<CompilerDocumentPathMapPair> Pairs);

// Private association from the actual same-task compiler argv to independently
// enumerated and hash-bound physical sources. Never opens a PDB-reported path.
internal sealed class CompilerDocumentBinding
{
    private readonly IReadOnlyDictionary<string, ProjectSourceIdentity> byPdbName;
    private readonly IReadOnlyDictionary<string, ProjectSourceIdentity> byAlias;
    internal IReadOnlySet<string> PhysicalPathsWithPdb { get; }
    internal IReadOnlyList<ManifestReportRootMapping> ReportMappings { get; }
    internal IReadOnlyDictionary<string, string> PdbLogicalPaths { get; }

    private CompilerDocumentBinding(IReadOnlyDictionary<string, ProjectSourceIdentity> names,
        IReadOnlyDictionary<string, ProjectSourceIdentity> aliases, IReadOnlySet<string> physical,
        IReadOnlyList<ManifestReportRootMapping> mappings)
    {
        byPdbName = names; byAlias = aliases; PhysicalPathsWithPdb = physical; ReportMappings = mappings;
        PdbLogicalPaths = names.ToDictionary(p => p.Key, p => p.Value.LogicalPath, StringComparer.Ordinal);
    }

    internal static CompilerDocumentBinding Create(ProjectAnalysisContext context,
        CompilerObservation observation, InspectedBuildEvidence build, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CompilerRecipeObservation.RequireKnownIntegrity(observation, token);
        var comparer = PathIdentityPolicy.Current.Comparer;
        var directory = Path.GetDirectoryName(Path.GetFullPath(context.ResolvedProjectPath!))!;
        var maps = RequirePathMap(observation, directory, token);
        var resolver = new SourceFileResolver(ImmutableArray<string>.Empty, directory, maps);
        var expected = new Dictionary<string, ProjectSourceIdentity>(maps.IsEmpty ? comparer : StringComparer.Ordinal);
        var aliases = new Dictionary<string, ProjectSourceIdentity>(comparer);
        var mappings = new List<ManifestReportRootMapping>();
        // A restored map without matched argv cannot distinguish two physical
        // owners with identical bytes. Keep checksum agreement from becoming
        // source-path authority, including documents without a selected PDB row.
        var restoredOwners = !maps.IsEmpty && observation.CommandLineArguments.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal) : null;
        var ambiguousContent = restoredOwners is null ? null : new HashSet<string>(StringComparer.Ordinal);
        void Alias(string name, ProjectSourceIdentity source)
        {
            var reportName = name;
            name = name.Replace('\\', '/');
            if (aliases.TryGetValue(name, out var owner))
            {
                if (!comparer.Equals(owner.ResolvedPath, source.ResolvedPath) || owner.LogicalPath != source.LogicalPath)
                    throw Refuse(); // Even identical content cannot authorize a second physical owner.
                return;
            }
            aliases.Add(name, source); mappings.Add(new(reportName, source.LogicalPath));
        }
        foreach (var source in context.CompilerDocuments)
        {
            token.ThrowIfCancellationRequested();
            var physical = Path.GetFullPath(source.ResolvedPath!);
            if (restoredOwners is not null && !restoredOwners.TryAdd(source.ContentIdentity, physical) &&
                !comparer.Equals(restoredOwners[source.ContentIdentity], physical)) ambiguousContent!.Add(source.ContentIdentity);
            var name = (resolver.NormalizePath(physical, null) ?? throw Refuse()).Replace('\\', '/');
            if (!expected.TryAdd(name, source)) throw Refuse();
            Alias(physical, source);
        }
        foreach (var (name, source) in expected)
            if (aliases.TryGetValue(name, out var owner) &&
                !comparer.Equals(owner.ResolvedPath, source.ResolvedPath)) throw Refuse();
        var bound = new Dictionary<string, ProjectSourceIdentity>(StringComparer.Ordinal);
        var physicalWithPdb = new HashSet<string>(comparer);
        foreach (var (name, checksum) in build.Documents.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            // Preserve the original no-map relative-name behavior; mapped names
            // stay compiler strings rather than host-rooted filesystem candidates.
            var expectedName = maps.IsEmpty ? Path.GetFullPath(name, directory).Replace('\\', '/') : name;
            if (!expected.TryGetValue(expectedName, out var source) || source.ContentIdentity != checksum ||
                ambiguousContent?.Contains(checksum) == true ||
                !bound.TryAdd(name, source)) throw Refuse();
            Alias(name, source); physicalWithPdb.Add(source.ResolvedPath!);
        }
        return new(bound, aliases, physicalWithPdb, Array.AsReadOnly(mappings.ToArray()));
    }

    internal IReadOnlySet<string> EligiblePdbNames(IReadOnlyList<ProjectSourceIdentity> sources)
    {
        var physical = sources.Select(s => s.ResolvedPath!).ToHashSet(PathIdentityPolicy.Current.Comparer);
        return byPdbName.Where(p => physical.Contains(p.Value.ResolvedPath!)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
    }

    internal string PhysicalPathForReport(string name)
        => byAlias.TryGetValue(name.Replace('\\', '/'), out var source) ? source.ResolvedPath! : throw Refuse();

    internal static CompilerDocumentPathMap? CapturePathMap(CompilerObservation observation, CancellationToken token)
    {
        CompilerRecipeObservation.RequireKnownIntegrity(observation, token);
        var directory = Path.GetDirectoryName(Path.GetFullPath(observation.ProjectPath))!;
        var pairs = ParsePathMap(observation, directory, token);
        if (pairs.IsEmpty) return null;
        var sourceParameters = observation.Parameters.Where(parameter => parameter.Name == "Sources").ToArray();
        if (sourceParameters.Length != 1 || sourceParameters[0].Items.Count == 0) throw Refuse();
        var sources = sourceParameters[0].Items.Select(source => Path.GetFullPath(source, directory)).ToArray();
        // Preserve the exact order of effective mappings. Unused SDK SourceRoot
        // entries need not persist a foreign absolute prefix. Prove they are
        // inert using Roslyn itself over EVERY actual same-task source before
        // omitting them; do not infer resolver matching from a prefix heuristic.
        var effective = pairs.Where(pair => sources.Any(source =>
        {
            var relative = Path.GetRelativePath(Path.TrimEndingDirectorySeparator(pair.Key), source);
            return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        })).ToImmutableArray();
        var full = new SourceFileResolver(ImmutableArray<string>.Empty, directory, pairs);
        var reduced = new SourceFileResolver(ImmutableArray<string>.Empty, directory, effective);
        foreach (var source in sources)
        {
            var actualName = full.NormalizePath(source, null) ?? throw Refuse();
            if (actualName != reduced.NormalizePath(source, null)) throw Refuse();
        }
        if (effective.IsEmpty) return null;
        return new(observation.InvocationRecipe!.TaskAssociationSha256,
            effective.Select(pair => new CompilerDocumentPathMapPair(pair.Key, pair.Value)).ToImmutableArray());
    }

    internal static bool SamePathMap(CompilerDocumentPathMap? first, CompilerDocumentPathMap? second)
        => first is null ? second is null : second is not null &&
            first.TaskAssociationSha256 == second.TaskAssociationSha256 && first.Pairs.SequenceEqual(second.Pairs);

    internal static ImmutableArray<KeyValuePair<string, string>> RequirePathMap(CompilerObservation observation,
        string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var map = observation.DocumentPathMap;
        if (map is null) return ParsePathMap(observation, directory, token);
        ValidatePathMap(map, token);
        if (observation.InvocationRecipe is { } recipe && recipe.TaskAssociationSha256 != map.TaskAssociationSha256)
            throw Refuse();
        // Raw argv, when retained, must assert exactly the same mandatory map.
        // Unsupported/absent optional recipes never supply or alter this record.
        if (observation.CommandLineArguments.Count != 0 && !SamePathMap(map, CapturePathMap(observation, token)))
            throw Refuse();
        return map.Pairs.Select(pair => new KeyValuePair<string, string>(pair.SourcePrefix, pair.DestinationPrefix)).ToImmutableArray();
    }

    internal static void ValidatePathMap(CompilerDocumentPathMap map, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (map.TaskAssociationSha256 is not { Length: 64 } || !map.TaskAssociationSha256.All(char.IsAsciiHexDigitLower) ||
            map.Pairs.IsDefaultOrEmpty || map.Pairs.Length > 64 || map.Pairs.Any(pair => pair is null ||
                string.IsNullOrEmpty(pair.SourcePrefix) || pair.SourcePrefix.Length > 4096 ||
                !Path.IsPathFullyQualified(pair.SourcePrefix) || Path.GetFullPath(pair.SourcePrefix) != pair.SourcePrefix ||
                pair.SourcePrefix.Any(char.IsControl) || string.IsNullOrEmpty(pair.DestinationPrefix) ||
                pair.DestinationPrefix.Length > 4096 || !pair.DestinationPrefix.StartsWith('/') ||
                pair.DestinationPrefix.Contains('\\') || pair.DestinationPrefix.Any(char.IsControl) ||
                pair.DestinationPrefix.Split('/').Any(segment => segment is "." or "..")) ||
            map.Pairs.Select(pair => pair.SourcePrefix).Distinct(StringComparer.Ordinal).Count() != map.Pairs.Length)
            throw Refuse();
    }

    private static ImmutableArray<KeyValuePair<string, string>> ParsePathMap(CompilerObservation observation,
        string directory, CancellationToken token)
    {
        var switches = observation.CommandLineArguments.Where(argument => argument.Length > 1 &&
            (argument[0] is '/' or '-') && argument[1..].StartsWith("pathmap", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (switches.Length == 0) return [];
        if (switches.Length != 1 || switches[0].Length > 65536 ||
            !switches[0][1..].StartsWith("pathmap:", StringComparison.OrdinalIgnoreCase) ||
            observation.InvocationRecipe is not
            {
                TaskBinding: "selected-sdk-core-target",
                CommandBinding: "selected-host-exec-csc-arguments-match" or "selected-sdk-csc-exe-arguments-match" or "selected-sdk-csc-unix-apphost-arguments-match"
            })
            throw Refuse();
        token.ThrowIfCancellationRequested();
        // Only the selected scalar plus a fixed output marker enter Roslyn.
        // The marker avoids the parser's no-source/no-output error; Parse does
        // not emit it. Never pass the complete argv, response or ruleset inputs.
        var parsed = CSharpCommandLineParser.Default.Parse(
            switches.Append("/out:__crap_pathmap_validation.dll"), directory, sdkDirectory: null);
        if (parsed.Errors.Any(d => d.Severity == DiagnosticSeverity.Error) || parsed.PathMap.Length is < 1 or > 64 ||
            parsed.PathMap.Any(p => !Path.IsPathFullyQualified(p.Key) || string.IsNullOrEmpty(p.Value) || p.Value.Length > 4096 ||
                !p.Value.StartsWith('/') || p.Value.Contains('\\') ||
                p.Value.Split('/').Any(segment => segment is "." or "..")) ||
            parsed.PathMap.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() != parsed.PathMap.Length)
            throw Refuse();
        return parsed.PathMap;
    }

    private static ProjectContextException Refuse() => new("compiler.outputsUnproven",
        "Actual compiler document names do not uniquely match the same-task PathMap and hash-bound physical sources.");
}
