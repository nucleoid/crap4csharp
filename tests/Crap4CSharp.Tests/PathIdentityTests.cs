using System.Text;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class PathIdentityTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "crap4csharp-path-identity", Guid.NewGuid().ToString("N"));

    public PathIdentityTests() => Directory.CreateDirectory(temporary);
    public void Dispose() => Directory.Delete(temporary, recursive: true);

    [Fact]
    public void SensitiveDiscoveryKeepsCaseDistinctFiles()
    {
        var upper = Write("Foo.cs", "class Upper { }");
        var lower = Write("foo.cs", "class Lower { }");

        var files = SourceDiscovery.Discover([], temporary, PathIdentityPolicy.Sensitive);

        Assert.Equal([upper, lower], files);
    }

    [Fact]
    public void InsensitiveDiscoveryRejectsDistinctSpellingsInsteadOfDroppingOne()
    {
        Write("Foo.cs", "class Upper { }");
        Write("foo.cs", "class Lower { }");

        var exception = Assert.Throws<InvalidDataException>(() =>
            SourceDiscovery.Discover([], temporary, PathIdentityPolicy.Insensitive));

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
        var source = Path.Combine(repository, "line\nbreak.cs");
        File.WriteAllText(source, "class C { }");
        var bytes = Encoding.UTF8.GetBytes("?? line\nbreak.cs\0");

        var files = GitChanges.ParsePorcelainV1Z(bytes, repository, PathIdentityPolicy.Sensitive);

        Assert.Equal([source], files);
    }

    private string Write(string relative, string contents)
    {
        var path = Path.Combine(temporary, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return Path.GetFullPath(path);
    }
}
