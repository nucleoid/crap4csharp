using System.Collections.Immutable;

namespace Crap4CSharp.Core;

// One synchronous validation only. The caller still independently checks its
// original physical snapshot after Complete; this is not a closing byte proof.
internal sealed class CompilerByteReadScope(CancellationToken token)
{
    private readonly Dictionary<string, (ImmutableArray<byte> Bytes, long Time)> files = new(PathIdentityPolicy.Current.Comparer);
    private readonly CompilerPathBatch paths = new(token, Refuse);
    private bool completed;

    internal ImmutableArray<byte> Read(string path, ref long total)
    {
        token.ThrowIfCancellationRequested();
        if (completed) throw Refuse();
        if (!files.TryGetValue(path, out var saved))
        {
            var bytes = CompilerOutputBinding.Read(path, ref total, token, paths);
            files.Add(path, (bytes, File.GetLastWriteTimeUtc(path).Ticks));
            return bytes;
        }
        paths.Check(path);
        if (!ArtifactBundle.IsRegularInput(path) || new FileInfo(path).Length != saved.Bytes.Length ||
            File.GetLastWriteTimeUtc(path).Ticks != saved.Time) throw Refuse();
        total = checked(total + saved.Bytes.Length);
        if (total > CompilerPhaseSnapshot.MaximumClosureBytes)
            throw new ProjectContextException("compiler.outputLimit", "Compiler output binding exceeded its finite closure byte limit.");
        return saved.Bytes;
    }

    internal void Complete()
    {
        if (completed) throw Refuse();
        try { paths.Complete(); }
        finally { completed = true; files.Clear(); }
    }

    private static ProjectContextException Refuse() => new("compiler.outputsUnproven", "Compiler input path or file state changed within a synchronous validation.");
}
