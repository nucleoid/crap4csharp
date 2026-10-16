using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Crap4CSharp.Core;

// Ownership is established by the SDK graph and executed reference metadata
// before this helper is called. It only substitutes those exact Csc inputs.
internal sealed record MappedReferenceArguments(string[] Arguments,
    IReadOnlyDictionary<string, MetadataReferenceProperties> References)
{
    internal static MappedReferenceArguments RemoveOwned(IReadOnlyList<string> arguments,
        string directory, IReadOnlySet<string> ownedPaths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = PathIdentityPolicy.Current.Comparer;
        if (arguments.Count > 131072 || ownedPaths.Count > 4096 || !Path.IsPathFullyQualified(directory) ||
            ownedPaths.Any(path => !Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path)) throw Refuse();
        var expected = ownedPaths.ToHashSet(paths);
        if (expected.Count != ownedPaths.Count) throw Refuse();
        var retained = new List<string>();
        var removed = new Dictionary<string, MetadataReferenceProperties>(paths);
        foreach (var argument in arguments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var references = CSharpCommandLineParser.Default.Parse([argument], directory, sdkDirectory: null).MetadataReferences;
            var mapped = references.Where(reference => expected.Contains(Path.GetFullPath(reference.Reference, directory))).ToArray();
            if (mapped.Length == 0) { retained.Add(argument); continue; }
            // Preserve unowned arguments verbatim. Never drop an entire mixed
            // option because one member happened to belong to this graph.
            if (references.Length != 1 || mapped.Length != 1) throw Refuse();
            var reference = mapped[0];
            if (!removed.TryAdd(Path.GetFullPath(reference.Reference, directory), reference.Properties)) throw Refuse();
        }
        if (removed.Count != expected.Count) throw Refuse();
        return new(retained.ToArray(), removed.ToImmutableDictionary(paths));
    }

    private static InvalidDataException Refuse() => new("Mapped compiler references require exact, unique SDK-owned argument membership.");
}
