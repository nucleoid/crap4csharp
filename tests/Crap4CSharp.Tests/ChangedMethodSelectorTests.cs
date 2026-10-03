using System.Collections.Immutable;
using System.Text;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ChangedMethodSelectorTests
{
    [Fact]
    public void CapturedTextIsParsedWithoutReadingTheLogicalDiskPath()
    {
        var bytes = Encoding.UTF8.GetBytes("class C { int Captured() => 1; }").ToImmutableArray();

        var methods = new SourceAnalyzer().AnalyzeCaptured("missing/logical.cs", bytes);

        Assert.Equal("Captured", Assert.Single(methods).MethodName);
        Assert.Equal("missing/logical.cs", methods[0].File);
    }

    [Fact]
    public void MethodGranularitySelectsOnlyIntersectingMethod()
    {
        const string text = "class C {\n int Changed() => 1;\n int Unrelated() => 2;\n}";
        var methods = new SourceAnalyzer().AnalyzeCaptured("src/C.cs", Encoding.UTF8.GetBytes(text).ToImmutableArray());
        var change = ChangedFile.Modified("src/C.cs", "old", "new", [new LineRange(2, 2)], []);

        var selection = ChangedMethodSelector.Select(change, methods, methods, ScopeGranularity.Method);

        Assert.Equal("Changed", Assert.Single(selection.Methods).MethodName);
        Assert.Empty(selection.Removals);
        Assert.False(selection.ConservativelyWidened);
    }

    [Fact]
    public void DeletedMethodProducesRemovalWithoutSelectingItsNeighbour()
    {
        const string oldText = "class C {\n int Removed() => 1;\n int Kept() => 2;\n}";
        const string newText = "class C {\n int Kept() => 2;\n}";
        var oldMethods = new SourceAnalyzer().AnalyzeCaptured("src/C.cs", Encoding.UTF8.GetBytes(oldText).ToImmutableArray());
        var newMethods = new SourceAnalyzer().AnalyzeCaptured("src/C.cs", Encoding.UTF8.GetBytes(newText).ToImmutableArray());
        var change = ChangedFile.Modified("src/C.cs", "old", "new", [], [new LineRange(2, 2)]);

        var selection = ChangedMethodSelector.Select(change, oldMethods, newMethods, ScopeGranularity.Method);

        Assert.Empty(selection.Methods);
        Assert.Equal("Removed", Assert.Single(selection.Removals).MethodName);
    }

    [Fact]
    public void PureRenameHasIdentityButNoChangedMethodsAtMethodGranularity()
    {
        const string text = "class C { int Same() => 1; }";
        var oldMethods = new SourceAnalyzer().AnalyzeCaptured("src/Old.cs", Encoding.UTF8.GetBytes(text).ToImmutableArray());
        var newMethods = new SourceAnalyzer().AnalyzeCaptured("src/New.cs", Encoding.UTF8.GetBytes(text).ToImmutableArray());
        var change = ChangedFile.Renamed("src/Old.cs", "src/New.cs", "same", "same", [], []);

        var selection = ChangedMethodSelector.Select(change, oldMethods, newMethods, ScopeGranularity.Method);

        Assert.Empty(selection.Methods);
        Assert.Empty(selection.Removals);
    }
}
