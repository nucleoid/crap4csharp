using System.Security.Cryptography;

namespace Crap4CSharp.Core;

// Private phase-C inventory gate only. Call after phased authored validation; this does not
// prove references, generator-only trees, compiler options or PE/PDB/copy association.
internal static class CompilerInputBinding
{
    internal static void Validate(ProjectAnalysisContext context, CompilerObservation observation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var project = Canonical(context.ResolvedProjectPath);
        if (!paths.Equals(project, Canonical(observation.ProjectPath))) throw Refuse();
        var directory = Path.GetDirectoryName(project)!;
        var parameters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var parameter in observation.Parameters)
        {
            token.ThrowIfCancellationRequested();
            if (parameter.Name is not ("Sources" or "References" or "OutputAssembly" or "OutputRefAssembly" or "AdditionalFiles" or "AnalyzerConfigFiles" or "Analyzers") ||
                !parameters.TryAdd(parameter.Name, parameter.Items)) throw Refuse();
        }
        if (!parameters.ContainsKey("Sources") || !parameters.TryGetValue("OutputAssembly", out var output) || output.Count != 1)
            throw Refuse();
        _ = ActualPath(output[0], directory);
        var documents = new Dictionary<string, ProjectSourceIdentity>(paths);
        foreach (var document in context.CompilerDocuments)
        {
            token.ThrowIfCancellationRequested();
            if (documents.Count >= CompilerPhaseSnapshot.MaximumInputs ||
                !documents.TryAdd(Canonical(document.ResolvedPath), document)) throw Refuse();
        }
        var sources = ActualSet(parameters["Sources"], directory, paths);
        if (!sources.SetEquals(documents.Keys)) throw Refuse();
        var phaseSources = context.PhaseInputs.Where(p => p.Role == "source").Select(p => Canonical(p.Path)).ToHashSet(paths);
        if (!phaseSources.SetEquals(documents.Keys)) throw Refuse();
        var expectedOptional = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["AdditionalFiles"] = Expected(context, "additionalFile", paths),
            ["AnalyzerConfigFiles"] = Expected(context, "analyzerConfig", paths),
            ["Analyzers"] = Expected(context, "analyzer", paths)
        };
        foreach (var (name, expected) in expectedOptional)
        {
            var actual = ActualSet(parameters.GetValueOrDefault(name) ?? [], directory, paths);
            if (!actual.SetEquals(expected)) throw Refuse();
        }
        // Absence candidates are not required compiler inputs. A present probe is not an
        // authorized input; final rediscovery must classify it first.
        foreach (var probe in context.PhaseInputs.Where(p => p.Role == "analyzerConfigProbe"))
        {
            var path = Canonical(probe.Path);
            CheckAncestors(path);
            if (File.Exists(path) || Directory.Exists(path)) throw Refuse();
        }
        var cache = new Dictionary<string, string>(paths);
        long total = 0;
        foreach (var path in sources.Concat(expectedOptional.Values.SelectMany(p => p)))
        {
            token.ThrowIfCancellationRequested();
            if (!cache.TryGetValue(path, out var hash))
            {
                if (cache.Count >= CompilerPhaseSnapshot.MaximumInputs) throw Limit();
                hash = ReadHash(path, ref total, token);
                cache.Add(path, hash);
            }
            if (documents.TryGetValue(path, out var document) && document.ContentIdentity != hash) throw Refuse();
        }
    }

    private static HashSet<string> Expected(ProjectAnalysisContext context, string role, StringComparer comparer) =>
        context.PhaseInputs.Where(p => p.Role == role).Select(p => Canonical(p.Path)).ToHashSet(comparer);

    private static HashSet<string> ActualSet(IReadOnlyList<string> items, string directory, StringComparer comparer)
    {
        if (items.Count > CompilerPhaseSnapshot.MaximumInputs) throw Limit();
        var result = new HashSet<string>(comparer);
        foreach (var item in items) if (!result.Add(ActualPath(item, directory))) throw Refuse();
        return result;
    }

    private static string ActualPath(string? path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path)) throw Refuse();
        try { return Path.GetFullPath(path, directory); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw Refuse(); }
    }

    private static string Canonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw Refuse();
        var resolved = ActualPath(path, Path.GetPathRoot(path)!);
        if (path != resolved) throw Refuse();
        return resolved;
    }

    private static void CheckAncestors(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Refuse(); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string ReadHash(string path, ref long total, CancellationToken token)
    {
        try
        {
            CheckAncestors(path);
            if (!ArtifactBundle.IsRegularInput(path)) throw Refuse();
            var time = File.GetLastWriteTimeUtc(path).Ticks;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            var length = stream.Length;
            if (!stream.CanSeek || length > CompilerPhaseSnapshot.MaximumFileBytes) throw Limit();
            total = checked(total + length);
            if (total > CompilerPhaseSnapshot.MaximumClosureBytes) throw Limit();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
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
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw Refuse(); }
    }
    private static ProjectContextException Refuse() => new("compiler.inputsUnproven", "Actual Csc sources or optional inputs do not match the complete final physical inventory and bytes.");
    private static ProjectContextException Limit() => new("compiler.inputLimit", "Compiler input binding exceeded its finite count or byte limit.");
}
