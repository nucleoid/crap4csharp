using System.Security.Cryptography;

namespace Crap4CSharp.Core;

// Private post-build identity for every physical compiler document, including generated
// documents and documents without sequence points. This is retained across required tests;
// it does not replace authored phase-A or actual compiler/output/reference binding.
internal sealed class CompilerDocumentSnapshot
{
    private readonly Dictionary<DocumentKey, Identity> documents;
    private CompilerDocumentSnapshot(Dictionary<DocumentKey, Identity> documents) => this.documents = documents;

    internal static CompilerDocumentSnapshot Capture(ProjectContextLoadResult graph, CancellationToken token) =>
        new(Read(graph, token));

    internal void Validate(ProjectContextLoadResult graph, CancellationToken token, CompilerFileReadScope? scope = null)
    {
        var current = Read(graph, token, scope);
        if (documents.Count != current.Count || documents.Any(pair =>
                !current.TryGetValue(pair.Key, out var identity) || identity != pair.Value))
            throw Refuse();
    }

    private static Dictionary<DocumentKey, Identity> Read(ProjectContextLoadResult graph, CancellationToken token,
        CompilerFileReadScope? scope = null)
    {
        token.ThrowIfCancellationRequested();
        if (!graph.Success || graph.GraphContexts.Count == 0 || graph.GraphContexts.Count > 4096 ||
            graph.GraphContexts.Select(binding => binding.GraphId).Distinct(StringComparer.Ordinal).Count() != graph.GraphContexts.Count)
            throw Refuse();
        var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var cache = new Dictionary<string, Identity>(paths);
        var result = new Dictionary<DocumentKey, Identity>();
        long total = 0;
        var count = 0;
        foreach (var binding in graph.GraphContexts)
        {
            var membership = new HashSet<string>(paths);
            var sourceSet = CompilerSourceSet.Create(binding.Context, token);
            foreach (var document in sourceSet.Documents)
            {
                token.ThrowIfCancellationRequested();
                if (++count > CompilerPhaseSnapshot.MaximumInputs || string.IsNullOrWhiteSpace(binding.GraphId) ||
                    string.IsNullOrWhiteSpace(document.ResolvedPath) || !Path.IsPathFullyQualified(document.ResolvedPath) ||
                    document.ResolvedPath != Path.GetFullPath(document.ResolvedPath) || !membership.Add(document.ResolvedPath))
                    throw Refuse();
                Identity identity;
                if (sourceSet.IsGeneratorTree(document))
                {
                    var bytes = sourceSet.Read(document, ref total, token);
                    identity = new(CanonicalIdentity.Sha256(bytes.AsSpan()), bytes.Length, 0);
                }
                else if (!cache.TryGetValue(document.ResolvedPath, out identity!))
                {
                    identity = ReadFile(document.ResolvedPath, ref total, token, scope);
                    cache.Add(document.ResolvedPath, identity);
                }
                var key = new DocumentKey(binding.GraphId,
                    OperatingSystem.IsWindows() ? document.ResolvedPath.ToUpperInvariant() : document.ResolvedPath,
                    document.LogicalPath, document.IsGenerated, sourceSet.EncodingIdentity(document));
                if (!result.TryAdd(key, identity) || identity.Sha256 != document.ContentIdentity) throw Refuse();
            }
        }
        return result;
    }

    private static Identity ReadFile(string path, ref long total, CancellationToken token, CompilerFileReadScope? scope)
    {
        try
        {
            if (scope is not null && scope.TryGet(path, out var cached))
            {
                total = checked(total + cached.Length);
                if (cached.Length > CompilerPhaseSnapshot.MaximumFileBytes || total > CompilerPhaseSnapshot.MaximumClosureBytes) throw Limit();
                return new(cached.Sha256, cached.Length, cached.MtimeTicks);
            }
            // Scoped TryGet already checks this file and its first-use ancestors;
            // Complete independently rechecks all ancestors at this boundary.
            if (scope is null)
                for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Refuse();
            if (!ArtifactBundle.IsRegularInput(path)) throw Refuse();
            var time = File.GetLastWriteTimeUtc(path).Ticks;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            var length = stream.Length;
            if (!stream.CanSeek || length > CompilerPhaseSnapshot.MaximumFileBytes) throw Limit();
            total = checked(total + length);
            if (total > CompilerPhaseSnapshot.MaximumClosureBytes) throw Limit();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = scope?.Buffer ?? new byte[65536];
            long readTotal = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                readTotal += read;
                if (readTotal > length) throw Refuse();
                hash.AppendData(buffer, 0, read);
            }
            if (readTotal != length || stream.Length != length || File.GetLastWriteTimeUtc(path).Ticks != time) throw Refuse();
            var identity = new Identity(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length, time);
            scope?.Remember(path, new(identity.Sha256, identity.Length, identity.MtimeTicks));
            return identity;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Refuse(); }
    }

    private sealed record DocumentKey(string GraphId, string Path, string LogicalPath, bool Generated, string? GenerationIdentity = null);
    private sealed record Identity(string Sha256, long Length, long MtimeTicks);
    private static ProjectContextException Refuse() => new("compiler.documentsMutated",
        "Post-build compiler document bytes, timestamps, generated classification or membership changed during required tests.");
    private static ProjectContextException Limit() => new("compiler.inputLimit",
        "The retained compiler document count or byte limit was exceeded.");
}
