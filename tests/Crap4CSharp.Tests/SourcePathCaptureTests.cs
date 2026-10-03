using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class SourcePathCaptureTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "crap4csharp-source-capture", Guid.NewGuid().ToString("N"));

    public SourcePathCaptureTests() => Directory.CreateDirectory(temporary);
    public void Dispose() => Directory.Delete(temporary, recursive: true);

    [Fact]
    public void RecursiveSelectionRejectsSymlinkEscapeButExplicitLinkGetsExternalIdentity()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(temporary, "root");
        var external = Path.Combine(temporary, "external");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(external);
        var target = Path.Combine(external, "C.cs");
        File.WriteAllText(target, "class C { }");
        var link = Path.Combine(root, "C.cs");
        File.CreateSymbolicLink(link, target);

        var exception = Assert.Throws<CoveragePathException>(() =>
            SourcePathCapture.Capture([link], [root], [], []));
        var explicitInventory = SourcePathCapture.Capture([link], [root], [link], []);

        Assert.Equal(CoverageReasonCodes.PathOutsideRoot, exception.Code);
        Assert.NotNull(Assert.Single(explicitInventory.Entries).ExternalRootId);
    }

    [Fact]
    public void MappingDestinationCannotEscapeThroughSymlinkedDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(temporary, "root");
        var external = Path.Combine(temporary, "external");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(external);
        var link = Path.Combine(root, "mapped");
        Directory.CreateSymbolicLink(link, external);

        var exception = Assert.Throws<CoveragePathException>(() =>
            SourcePathCapture.Capture([], [root], [], [link]));

        Assert.Equal(CoverageReasonCodes.PathOutsideRoot, exception.Code);
    }
}
