using System.Security.Cryptography;
using System.Collections.Immutable;

namespace Crap4CSharp.Core;

// Private byte/membership gate. The caller must independently resolve the permitted closure
// and stage dependency outputs AFTER their build and BEFORE consumers. This is not PE identity,
// graph authorization or actual event-time attestation, and sets no public capability.
internal sealed class CompilerReferenceSnapshot
{
    private static StringComparer Paths => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Dictionary<string, Identity> inputs;
    private CompilerReferenceSnapshot(Dictionary<string, Identity> inputs) => this.inputs = inputs;

    // Keep raw Csc evidence unchanged; resolve file reads against its actual
    // project directory, never the tool process's working directory.
    internal static IReadOnlyList<string> ResolvePaths(CompilerObservation observation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var project = Canonical(observation.ProjectPath);
        var parameters = observation.Parameters.Where(parameter => parameter.Name == "References").ToArray();
        if (parameters.Length != 1) throw Refuse();
        if (parameters[0].Items.Count > CompilerPhaseSnapshot.MaximumInputs) throw Limit();
        var result = new List<string>();
        var seen = new HashSet<string>(Paths);
        foreach (var item in parameters[0].Items)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item)) throw Refuse();
            string path;
            try { path = Path.GetFullPath(item, Path.GetDirectoryName(project)!); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            { throw Refuse(); }
            if (!seen.Add(path)) throw Refuse();
            result.Add(path);
        }
        return result.AsReadOnly();
    }

    internal static CompilerReferenceSnapshot Capture(IEnumerable<string> paths, CancellationToken token) =>
        new(ReadNew(paths, new(Paths), token));

    internal sealed record SemanticReference(string Path, string? Aliases, bool EmbedInteropTypes)
    {
        internal Microsoft.CodeAnalysis.MetadataReferenceProperties Properties =>
            SemanticProperties(Aliases, EmbedInteropTypes);
    }

    internal static IReadOnlyList<SemanticReference> ResolveSemanticReferences(CompilerObservation observation, CancellationToken token)
    {
        var paths = ResolvePaths(observation, token);
        var parameter = observation.Parameters.Single(p => p.Name == "References");
        if (parameter.ReferenceProperties.Count != 0 && parameter.ReferenceProperties.Count != paths.Count) throw Refuse();
        var result = new List<SemanticReference>();
        for (var index = 0; index < paths.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var properties = parameter.ReferenceProperties.Count == 0 ? null : parameter.ReferenceProperties[index];
            if (properties is not null && properties.ItemSpec != parameter.Items[index]) throw Refuse();
            var aliases = NormalizeAliases(properties?.Aliases);
            result.Add(new(paths[index], aliases, properties?.EmbedInteropTypes ?? false));
        }
        return result.AsReadOnly();
    }

    internal static string? NormalizeAliases(string? aliases)
    {
        if (string.IsNullOrEmpty(aliases)) return null;
        if (aliases.Length > 32768) throw Limit();
        var names = aliases.Split(',').Select(value => value.Trim()).ToArray();
        if (names.Any(name => name != "global" && !Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(name)) ||
            names.Any(string.IsNullOrEmpty) || names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw Refuse();
        Array.Sort(names, StringComparer.Ordinal);
        return names.Length == 1 && names[0] == "global" ? null : string.Join(',', names);
    }

    internal static Microsoft.CodeAnalysis.MetadataReferenceProperties SemanticProperties(string? aliases, bool embedded)
    {
        var normalized = NormalizeAliases(aliases);
        return new Microsoft.CodeAnalysis.MetadataReferenceProperties(Microsoft.CodeAnalysis.MetadataImageKind.Assembly,
            normalized is null ? [] : normalized.Split(',').ToImmutableArray(), embedded);
    }

    internal static string LogicalReference(string sha256, string? aliases, bool embedded) =>
        SemanticEligibility.ReferenceLogicalPath(sha256, aliases, embedded);

    internal static Microsoft.CodeAnalysis.PortableExecutableReference CapturedReference(ManifestInput input,
        System.Collections.Immutable.ImmutableArray<byte> bytes)
    {
        try { return SemanticEligibility.CapturedReference(input, bytes); }
        catch (InvalidDataException) { throw Refuse(); }
    }

    internal CompilerReferenceSnapshot Extend(IEnumerable<string> paths, CancellationToken token)
    {
        VerifyUnchanged(token); // Never rebaseline an earlier prepared input after a build.
        var extended = ReadNew(paths, new(inputs, Paths), token);
        VerifyUnchanged(token);
        return new(extended); // The earlier snapshot retains its own membership.
    }

    internal void VerifyUnchanged(CancellationToken token)
    {
        var scope = new ReadScope(token);
        VerifyUnchanged(token, scope);
        scope.Complete();
    }

    private void VerifyUnchanged(CancellationToken token, ReadScope scope)
    {
        token.ThrowIfCancellationRequested();
        long total = 0;
        foreach (var (path, expected) in inputs)
        {
            token.ThrowIfCancellationRequested();
            var current = scope.Read(path, ref total);
            if (current != expected) throw Refuse();
        }
    }

    internal void Validate(CompilerObservation observation, CancellationToken token)
    {
        var scope = new ReadScope(token);
        Validate(observation, token, scope);
        scope.Complete();
    }

    internal void Validate(CompilerObservation observation, CancellationToken token, ReadScope scope)
    {
        VerifyUnchanged(token, scope);
        var project = Canonical(observation.ProjectPath);
        var parameters = observation.Parameters.Where(p => p.Name == "References").ToArray();
        if (parameters.Length > 1) throw Refuse();
        var items = parameters.Length == 0 ? Array.Empty<string>() : parameters[0].Items;
        if (items.Count > CompilerPhaseSnapshot.MaximumInputs) throw Limit();
        var actual = new HashSet<string>(Paths);
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item)) throw Refuse();
            string path;
            try { path = Path.GetFullPath(item, Path.GetDirectoryName(project)!); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw Refuse(); }
            if (!actual.Add(path)) throw Refuse();
        }
        if (!actual.SetEquals(inputs.Keys)) throw Refuse();
    }

    private static Dictionary<string, Identity> ReadNew(IEnumerable<string> paths, Dictionary<string, Identity> result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long total = result.Values.Sum(i => i.Length);
        var seen = new HashSet<string>(Paths);
        var supplied = 0;
        var batch = new CompilerPathBatch(token, Refuse);
        var buffer = new byte[65536];
        try
        {
            foreach (var suppliedPath in paths)
            {
                token.ThrowIfCancellationRequested();
                if (++supplied > CompilerPhaseSnapshot.MaximumInputs) throw Limit();
                var path = Canonical(suppliedPath);
                if (!seen.Add(path)) throw Refuse();
                if (result.ContainsKey(path)) continue; // Shared fixed inputs keep their ORIGINAL identity.
                if (result.Count >= CompilerPhaseSnapshot.MaximumInputs) throw Limit();
                result.Add(path, Read(path, ref total, token, batch, buffer));
            }
            Complete(batch);
            return result;
        }
        finally { Array.Clear(buffer); }
    }

    private static string Canonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw Refuse();
        try { if (path != Path.GetFullPath(path)) throw Refuse(); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw Refuse(); }
        return path;
    }

    private static void Complete(CompilerPathBatch batch)
    {
        try { batch.Complete(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw Refuse(); }
    }

    private static Identity Read(string path, ref long total, CancellationToken token, CompilerPathBatch batch, byte[] buffer)
    {
        try
        {
            batch.Check(path);
            if (!ArtifactBundle.IsRegularInput(path)) throw Refuse();
            var time = File.GetLastWriteTimeUtc(path).Ticks;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            var length = stream.Length;
            if (!stream.CanSeek || length > CompilerPhaseSnapshot.MaximumFileBytes) throw Limit();
            total = checked(total + length);
            if (total > CompilerPhaseSnapshot.MaximumClosureBytes) throw Limit();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long count = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                count += read;
                if (count > length) throw Refuse();
                hash.AppendData(buffer, 0, read);
            }
            if (count != length || stream.Length != length || File.GetLastWriteTimeUtc(path).Ticks != time) throw Refuse();
            return new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length, time);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw Refuse(); }
    }

    internal sealed record Identity(string Sha256, long Length, long MtimeTicks);

    // Shared only by snapshots participating in ONE physical validation call.
    // Every snapshot still checks its own expectation, membership and byte cap.
    internal sealed class ReadScope(CancellationToken token)
    {
        private readonly Dictionary<string, Identity> identities = new(Paths);
        private readonly CompilerPathBatch paths = new(token, Refuse);
        private readonly byte[] buffer = new byte[65536];
        private bool completed;
        internal Identity Read(string path, ref long total)
        {
            token.ThrowIfCancellationRequested();
            if (completed) throw Refuse();
            if (identities.TryGetValue(path, out var current))
            {
                total = checked(total + current.Length);
                if (total > CompilerPhaseSnapshot.MaximumClosureBytes) throw Limit();
                return current;
            }
            current = CompilerReferenceSnapshot.Read(path, ref total, token, paths, buffer);
            identities.Add(path, current);
            return current;
        }
        internal void Complete()
        {
            if (completed) throw Refuse();
            try { CompilerReferenceSnapshot.Complete(paths); }
            finally { completed = true; identities.Clear(); Array.Clear(buffer); }
        }
        internal void Abandon()
        {
            completed = true;
            identities.Clear();
            Array.Clear(buffer);
        }
    }
    private static ProjectContextException Refuse() => new("compiler.referencesUnproven", "Prepared/staged reference bytes, timestamps or actual Csc reference membership are unproven.");
    private static ProjectContextException Limit() => new("compiler.inputLimit", "The private compiler reference closure exceeded its finite count or byte limit.");
}
