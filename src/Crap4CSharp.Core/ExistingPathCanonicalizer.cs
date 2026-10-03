namespace Crap4CSharp.Core;

internal sealed class ExistingPathCanonicalizer
{
    private readonly PathIdentityPolicy pathPolicy;
    private readonly Dictionary<string, PathIdentityPolicy.ExistingDirectoryEntries> directoryEntries;

    public ExistingPathCanonicalizer(PathIdentityPolicy pathPolicy)
    {
        this.pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        directoryEntries = new Dictionary<string, PathIdentityPolicy.ExistingDirectoryEntries>(pathPolicy.Comparer);
    }

    public int CachedDirectoryCount => directoryEntries.Count;

    public string NormalizeExisting(string path, string? baseDirectory = null) =>
        pathPolicy.NormalizeExisting(path, baseDirectory, directoryEntries);
}
