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

    [Fact]
    public void CopySelectsEveryMethodRegardlessOfSimilarityHunks()
    {
        const string text = "class C { int One() => 1; int Two() => 2; }";
        var source = CapturedSource.Create("src/Copy.cs", Encoding.UTF8.GetBytes(text));
        var methods = new SourceAnalyzer().AnalyzeCaptured(source.LogicalPath, source.Bytes);
        var change = new ChangedFile("src/Original.cs", "src/Copy.cs", ScopeChangeKind.Copied,
            "old", "new", source with { LogicalPath = "src/Original.cs" }, source, [], []);

        var selection = ChangedMethodSelector.Select(change, methods, methods, ScopeGranularity.Method);

        Assert.Equal(2, selection.Methods.Count);
    }

    [Fact]
    public void ConstructorChangeIsVisibleAsUnsupportedExecutableScope()
    {
        const string oldText = "class C { C() { Value = 1; } int Value; }";
        const string newText = "class C { C() { Value = 2; } int Value; }";
        var oldSource = CapturedSource.Create("src/C.cs", Encoding.UTF8.GetBytes(oldText));
        var newSource = CapturedSource.Create("src/C.cs", Encoding.UTF8.GetBytes(newText));
        var change = new ChangedFile("src/C.cs", "src/C.cs", ScopeChangeKind.Modified,
            oldSource.ContentIdentity, newSource.ContentIdentity, oldSource, newSource,
            [new LineRange(1, 1)], [new LineRange(1, 1)]);

        var selection = ChangedMethodSelector.Select(change, [], [], ScopeGranularity.Method);

        Assert.Empty(selection.Methods);
        Assert.Contains(selection.ScopeLimitations, limitation =>
            limitation.Code == "scope.unsupportedChangedCallable" && limitation.SyntaxKind == "ConstructorDeclaration");
    }
}
