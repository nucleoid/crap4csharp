namespace Crap4CSharp.Core;

// One synchronous read batch only. Files are checked on every read; shared
// directory ancestors are checked before their first read and again at completion.
// This does not make filesystem reads atomic or retain evidence between gates.
internal sealed class CompilerPathBatch(CancellationToken token, Func<ProjectContextException> refuse)
{
    private readonly HashSet<string> ancestors = new(PathIdentityPolicy.Current.Comparer);

    internal void Check(string path)
    {
        CheckAttributes(path);
        for (var current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current))
            if (ancestors.Add(current)) CheckAttributes(current);
    }

    internal void Complete()
    {
        foreach (var path in ancestors) CheckAttributes(path);
        token.ThrowIfCancellationRequested();
    }

    private void CheckAttributes(string path)
    {
        token.ThrowIfCancellationRequested();
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw refuse();
    }
}
