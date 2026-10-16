namespace Crap4CSharp.Core;

// Local to one synchronous retained-proof validation. Callers keep their own
// membership, expectations and byte budgets; no identity survives a boundary.
internal sealed class CompilerFileReadScope(CancellationToken token)
{
    private readonly Dictionary<string, CompilerFileIdentity> identities = new(PathIdentityPolicy.Current.Comparer);
    private readonly CompilerPathBatch paths = new(token, Refuse);
    private bool completed;
    private byte[]? buffer;

    internal byte[] Buffer
    {
        get
        {
            token.ThrowIfCancellationRequested();
            if (completed) throw Refuse();
            return buffer ??= new byte[65536];
        }
    }

    internal bool TryGet(string path, out CompilerFileIdentity identity)
    {
        token.ThrowIfCancellationRequested();
        if (completed) throw Refuse();
        paths.Check(path);
        if (!ArtifactBundle.IsRegularInput(path)) throw Refuse();
        if (!identities.TryGetValue(path, out identity!)) return false;
        try
        {
            var current = new FileInfo(path);
            if (current.Length != identity.Length || current.LastWriteTimeUtc.Ticks != identity.MtimeTicks) throw Refuse();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw Refuse(); }
        return true;
    }

    internal void Remember(string path, CompilerFileIdentity identity)
    {
        token.ThrowIfCancellationRequested();
        if (completed) throw Refuse();
        identities.Add(path, identity);
    }
    internal void Complete()
    {
        if (completed) throw Refuse();
        try { paths.Complete(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw Refuse(); }
        finally
        {
            completed = true; identities.Clear();
            if (buffer is not null) Array.Clear(buffer);
            buffer = null;
        }
    }
    internal void Abandon()
    {
        completed = true;
        identities.Clear();
        if (buffer is not null) Array.Clear(buffer);
        buffer = null;
    }
    private static ProjectContextException Refuse() => new("compiler.retainedProofInvalid",
        "Retained compiler input path ancestry changed within a physical validation.");
}

internal sealed record CompilerFileIdentity(string Sha256, long Length, long MtimeTicks);
