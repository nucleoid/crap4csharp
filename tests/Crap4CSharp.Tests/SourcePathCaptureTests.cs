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

    [Fact]
    public void MultipleSelectedRootsUseWorkspaceRelativeLogicalPaths()
    {
        var workspace = Path.Combine(temporary, "workspace");
        var firstRoot = Path.Combine(workspace, "App1");
        var secondRoot = Path.Combine(workspace, "App2");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var first = Path.Combine(firstRoot, "Program.cs");
        var second = Path.Combine(secondRoot, "Program.cs");
        File.WriteAllText(first, "class Program { static void Main() { } }");
        File.WriteAllText(second, "class Program { static void Main() { } }");

        var inventory = SourcePathCapture.Capture([first, second], [firstRoot, secondRoot], [], [],
            workingDirectory: workspace);

        Assert.Equal(["App1/Program.cs", "App2/Program.cs"],
            inventory.Entries.Select(entry => entry.LogicalPath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SameNamedExplicitLinksInDifferentDirectoriesHaveDistinctLogicalPaths()
    {
        if (OperatingSystem.IsWindows()) return;
        var workspace = Path.Combine(temporary, "workspace");
        var firstDirectory = Path.Combine(workspace, "App1");
        var secondDirectory = Path.Combine(workspace, "App2");
        var external = Path.Combine(temporary, "external");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        Directory.CreateDirectory(external);
        var firstTarget = Path.Combine(external, "First.cs");
        var secondTarget = Path.Combine(external, "Second.cs");
        File.WriteAllText(firstTarget, "class Program { }");
        File.WriteAllText(secondTarget, "class Program { }");
        var firstLink = Path.Combine(firstDirectory, "Program.cs");
        var secondLink = Path.Combine(secondDirectory, "Program.cs");
        File.CreateSymbolicLink(firstLink, firstTarget);
        File.CreateSymbolicLink(secondLink, secondTarget);

        var inventory = SourcePathCapture.Capture([firstLink, secondLink], [workspace], [firstLink, secondLink], [],
            workingDirectory: workspace);
        var entries = inventory.Entries.OrderBy(entry => entry.LocalPath, StringComparer.Ordinal).ToArray();

        Assert.NotEqual(entries[0].ExternalRootId, entries[1].ExternalRootId);
        Assert.NotEqual(entries[0].LogicalPath, entries[1].LogicalPath);
    }

    [Fact]
    public void ExternalRootIdentityIsStableWhenAnotherSameNamedRootIsSelected()
    {
        var workspace = Path.Combine(temporary, "workspace");
        var firstRoot = Path.Combine(temporary, "outside-a", "shared");
        var secondRoot = Path.Combine(temporary, "outside-b", "shared");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var first = Path.Combine(firstRoot, "Program.cs");
        var second = Path.Combine(secondRoot, "Program.cs");
        File.WriteAllText(first, "class Program { static void Main() { } }");
        File.WriteAllText(second, "class Program { static void Main() { } }");

        var alone = SourcePathCapture.Capture([first], [firstRoot], [first], [], workingDirectory: workspace);
        var together = SourcePathCapture.Capture([second, first], [secondRoot, firstRoot], [second, first], [],
            workingDirectory: workspace);
        var aloneEntry = Assert.Single(alone.Entries);
        var togetherEntries = together.Entries.OrderBy(entry => entry.LocalPath, StringComparer.Ordinal).ToArray();
        var retainedEntry = Assert.Single(togetherEntries, entry => entry.LocalPath == first);

        Assert.Equal(aloneEntry.ExternalRootId, retainedEntry.ExternalRootId);
        Assert.Equal(aloneEntry.LogicalPath, retainedEntry.LogicalPath);
        Assert.NotEqual(togetherEntries[0].ExternalRootId, togetherEntries[1].ExternalRootId);
        Assert.NotEqual(togetherEntries[0].LogicalPath, togetherEntries[1].LogicalPath);
        Assert.DoesNotContain(temporary, retainedEntry.ExternalRootId, StringComparison.Ordinal);
    }
}
