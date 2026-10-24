using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Crap4CSharp.Core;

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
        var maps = ParsePathMap(observation, directory, token);
        var resolver = new SourceFileResolver(ImmutableArray<string>.Empty, directory, maps);
        var expected = new Dictionary<string, ProjectSourceIdentity>(maps.IsEmpty ? comparer : StringComparer.Ordinal);
        var aliases = new Dictionary<string, ProjectSourceIdentity>(comparer);
        var mappings = new List<ManifestReportRootMapping>();
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
        // Only one literal scalar option enters Roslyn. Never pass the complete
        // argv: Parse may otherwise open response/ruleset files or resolve inputs.
        var parsed = CSharpCommandLineParser.Default.Parse(switches, directory, sdkDirectory: null);
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
