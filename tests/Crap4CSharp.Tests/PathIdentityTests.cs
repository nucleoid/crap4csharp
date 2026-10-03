using System.Text;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class PathIdentityTests : IDisposable
{
    private readonly TestDirectory fixture = TestDirectory.Create("crap4csharp-path-identity");
    private string temporary => fixture.Path;

    public void Dispose() => fixture.Dispose();

    [Fact]
    public void ExistingPathCanonicalizerReusesDirectoryEntriesAcrossManyFiles()
    {
        var directory = Path.Combine(temporary, "many");
        Directory.CreateDirectory(directory);
        var files = Enumerable.Range(0, 25).Select(index =>
        {
            var path = Path.Combine(directory, $"File{index}.cs");
            File.WriteAllText(path, "class C { }");
            return path;
        }).ToArray();
        var canonicalizer = new ExistingPathCanonicalizer(PathIdentityPolicy.Sensitive);

        _ = canonicalizer.NormalizeExisting(files[0]);
        var cachedAfterFirst = canonicalizer.CachedDirectoryCount;
        foreach (var file in files.Skip(1)) _ = canonicalizer.NormalizeExisting(file);

        Assert.True(cachedAfterFirst > 0);
        Assert.Equal(cachedAfterFirst, canonicalizer.CachedDirectoryCount);
    }

    [Fact]
    public void SensitiveDiscoveryKeepsCaseDistinctFiles()
    {
        if (!TryCreateCaseDistinctFiles(out var upper, out var lower)) return;

        var files = SourceDiscovery.Discover([], temporary, PathIdentityPolicy.Sensitive);

        Assert.Equal([upper, lower], files);
    }

    [Fact]
    public void InsensitiveDiscoveryRejectsDistinctSpellingsInsteadOfDroppingOne()
    {
        if (!TryCreateCaseDistinctFiles(out _, out _)) return;

        var exception = Assert.Throws<InvalidDataException>(() =>
            SourceDiscovery.Discover([], temporary, PathIdentityPolicy.Insensitive));

        Assert.Contains("collision", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InsensitivePolicyRejectsDistinctLogicalSpellingsWithoutFilesystemAssumptions()
    {
        var upper = Path.Combine(temporary, "Policy", "Foo.cs");
        var lower = Path.Combine(temporary, "Policy", "foo.cs");

        var exception = Assert.Throws<InvalidDataException>(() =>
            PathIdentityPolicy.Insensitive.DistinctOrThrow([upper, lower], "injected inventory"));

        Assert.Contains("collision", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InsensitivePolicyCanonicalizesUniqueExistingSpelling()
    {
        var actual = Write("Parent/Foo.cs", "class C { }");
        var differentlyCased = Path.Combine(temporary, "PARENT", "FOO.CS");

        var normalized = PathIdentityPolicy.Insensitive.NormalizeExisting(differentlyCased);
        var sensitiveNormalized = PathIdentityPolicy.Sensitive.NormalizeExisting(differentlyCased);

        Assert.Equal(actual, normalized);
        Assert.Equal(actual, sensitiveNormalized);
    }

    [Fact]
    public void ContainmentUsesCompleteComponents()
    {
        var policy = PathIdentityPolicy.Sensitive;
        var root = Path.Combine(temporary, "repo");
        var sibling = Path.Combine(temporary, "repo-old", "Code.cs");

        Assert.True(policy.Contains(root, Path.Combine(root, "src", "Code.cs")));
        Assert.False(policy.Contains(root, sibling));
    }

    [Fact]
    public void GitPorcelainRejectsEscapingPath()
    {
        var repository = Path.Combine(temporary, "repo");
        Directory.CreateDirectory(repository);
        Write("outside.cs", "class Outside { }");
        var bytes = Encoding.UTF8.GetBytes("?? ../outside.cs\0");

        var exception = Assert.Throws<InvalidDataException>(() =>
            GitChanges.ParsePorcelainV1Z(bytes, repository, PathIdentityPolicy.Sensitive));

        Assert.Contains("outside", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitPorcelainPreservesValidNewlineFilenameBytes()
    {
        var repository = Path.Combine(temporary, "repo");
        Directory.CreateDirectory(repository);
        if (!TryWrite("repo/line\nbreak.cs", "class C { }", out var source)) return;
        var bytes = Encoding.UTF8.GetBytes("?? line\nbreak.cs\0");

        var files = GitChanges.ParsePorcelainV1Z(bytes, repository, PathIdentityPolicy.Sensitive);

        Assert.Equal([source], files);
    }

    private bool TryCreateCaseDistinctFiles(out string upper, out string lower)
    {
        upper = fixture.Write("case-distinct/Foo.cs", "class Upper { }");
        lower = fixture.Write("case-distinct/foo.cs", "class Lower { }");
        var names = Directory.EnumerateFiles(Path.GetDirectoryName(upper)!)
            .Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        return names.Contains("Foo.cs") && names.Contains("foo.cs");
    }

    private bool TryWrite(string relative, string contents, out string path)
    {
        try
        {
            path = fixture.Write(relative, contents);
            return File.Exists(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            path = string.Empty;
            return false;
        }
    }

    private string Write(string relative, string contents) => fixture.Write(relative, contents);
}
