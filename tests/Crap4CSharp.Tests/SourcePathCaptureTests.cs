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

    [Fact]
    public void FileLinkCannotHideEscapeThroughTargetParentLink()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(temporary, "root");
        var external = Path.Combine(temporary, "external");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "C.cs"), "class C { }");
        var parentLink = Path.Combine(root, "escape");
        Directory.CreateSymbolicLink(parentLink, external);
        var fileLink = Path.Combine(root, "C.cs");
        File.CreateSymbolicLink(fileLink, Path.Combine(parentLink, "C.cs"));

        var exception = Assert.Throws<CoveragePathException>(() =>
            SourcePathCapture.Capture([fileLink], [root], [], []));

        Assert.Equal(CoverageReasonCodes.PathOutsideRoot, exception.Code);
    }

    [Fact]
    public void ExternalLinkIdentityDoesNotDependOnOtherSelections()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(temporary, "root");
        var external = Path.Combine(temporary, "external");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(external);
        var firstTarget = Path.Combine(external, "C.cs");
        var secondTarget = Path.Combine(external, "D.cs");
        File.WriteAllText(firstTarget, "class C { }");
        File.WriteAllText(secondTarget, "class D { }");
        var firstLink = Path.Combine(root, "C.cs");
        var secondLink = Path.Combine(root, "D.cs");
        File.CreateSymbolicLink(firstLink, firstTarget);
        File.CreateSymbolicLink(secondLink, secondTarget);

        var alone = SourcePathCapture.Capture([firstLink], [root], [firstLink], []);
        var together = SourcePathCapture.Capture([secondLink, firstLink], [root], [secondLink, firstLink], []);
        var aloneEntry = Assert.Single(alone.Entries);
        var togetherEntry = Assert.Single(together.Entries, entry => entry.LocalPath == firstLink);

        Assert.Equal(aloneEntry.ExternalRootId, togetherEntry.ExternalRootId);
        Assert.Equal(aloneEntry.LogicalPath, togetherEntry.LogicalPath);
        Assert.DoesNotContain(temporary, aloneEntry.ExternalRootId, StringComparison.Ordinal);
    }
}
