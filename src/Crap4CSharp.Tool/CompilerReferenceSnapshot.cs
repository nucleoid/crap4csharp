using System.Security.Cryptography;

namespace Crap4CSharp.Core;

// Private byte/membership gate. The caller must independently resolve the permitted closure
// and stage dependency outputs AFTER their build and BEFORE consumers. This is not PE identity,
// graph authorization or actual event-time attestation, and sets no public capability.
internal sealed class CompilerReferenceSnapshot
{
    private static StringComparer Paths => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Dictionary<string, Identity> inputs;
    private CompilerReferenceSnapshot(Dictionary<string, Identity> inputs) => this.inputs = inputs;

    internal static CompilerReferenceSnapshot Capture(IEnumerable<string> paths, CancellationToken token) =>
        new(ReadNew(paths, new(Paths), token));

    internal CompilerReferenceSnapshot Extend(IEnumerable<string> paths, CancellationToken token)
    {
        VerifyUnchanged(token); // Never rebaseline an earlier prepared input after a build.
        var extended = ReadNew(paths, new(inputs, Paths), token);
        VerifyUnchanged(token);
        return new(extended); // The earlier snapshot retains its own membership.
    }

    internal void VerifyUnchanged(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long total = 0;
        foreach (var (path, expected) in inputs)
        {
            token.ThrowIfCancellationRequested();
            var current = Read(path, ref total, token);
            if (current != expected) throw Refuse();
        }
    }

    internal void Validate(CompilerObservation observation, CancellationToken token)
    {
        VerifyUnchanged(token);
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
        foreach (var suppliedPath in paths)
        {
            token.ThrowIfCancellationRequested();
            if (++supplied > CompilerPhaseSnapshot.MaximumInputs) throw Limit();
            var path = Canonical(suppliedPath);
            if (!seen.Add(path)) throw Refuse();
            if (result.ContainsKey(path)) continue; // Shared fixed inputs keep their ORIGINAL identity.
            if (result.Count >= CompilerPhaseSnapshot.MaximumInputs) throw Limit();
            result.Add(path, Read(path, ref total, token));
        }
        return result;
    }

    private static string Canonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw Refuse();
        try { if (path != Path.GetFullPath(path)) throw Refuse(); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw Refuse(); }
        return path;
    }

    private static Identity Read(string path, ref long total, CancellationToken token)
    {
        try
        {
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Refuse(); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            if (!ArtifactBundle.IsRegularInput(path)) throw Refuse();
            var time = File.GetLastWriteTimeUtc(path).Ticks;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            var length = stream.Length;
            if (!stream.CanSeek || length > CompilerPhaseSnapshot.MaximumFileBytes) throw Limit();
            total = checked(total + length);
            if (total > CompilerPhaseSnapshot.MaximumClosureBytes) throw Limit();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
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

    private sealed record Identity(string Sha256, long Length, long MtimeTicks);
    private static ProjectContextException Refuse() => new("compiler.referencesUnproven", "Prepared/staged reference bytes, timestamps or actual Csc reference membership are unproven.");
    private static ProjectContextException Limit() => new("compiler.inputLimit", "The private compiler reference closure exceeded its finite count or byte limit.");
}
