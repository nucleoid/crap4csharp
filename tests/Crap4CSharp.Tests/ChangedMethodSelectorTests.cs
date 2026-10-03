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

    [Fact]
    public void DisabledPreprocessorChangeIsVisibleAsIncompleteContext()
    {
        const string oldText = "#if DEBUG\nclass C { int Hidden() => 1; }\n#endif";
        const string newText = "#if DEBUG\nclass C { int Hidden() => 2; }\n#endif";
        var oldSource = CapturedSource.Create("src/C.cs", Encoding.UTF8.GetBytes(oldText));
        var newSource = CapturedSource.Create("src/C.cs", Encoding.UTF8.GetBytes(newText));
        var change = new ChangedFile("src/C.cs", "src/C.cs", ScopeChangeKind.Modified,
            oldSource.ContentIdentity, newSource.ContentIdentity, oldSource, newSource,
            [new LineRange(2, 2)], [new LineRange(2, 2)]);

        var selection = ChangedMethodSelector.Select(change, [], [], ScopeGranularity.Method);

        Assert.Contains(selection.ScopeLimitations, limitation =>
            limitation.Code == "scope.contextIncomplete" && limitation.SyntaxKind == "DisabledTextTrivia");
    }

    [Fact]
    public void IfDirectiveOnlyActivationAndDeactivationCannotProduceEmptyCompleteSelection()
    {
        AssertDirectiveInventoryChange(
            "#if X\nclass C { int Conditional() => 1; }\n#endif",
            "#if !X\nclass C { int Conditional() => 1; }\n#endif",
            directiveLine: 1);
    }

    [Fact]
    public void DefineAndUndefOnlyActivationAndDeactivationCannotProduceEmptyCompleteSelection()
    {
        AssertDirectiveInventoryChange(
            "#undef X\n#if X\nclass C { int Conditional() => 1; }\n#endif",
            "#define X\n#if X\nclass C { int Conditional() => 1; }\n#endif",
            directiveLine: 1);
    }

    [Fact]
    public void DeletionOnlySignatureEditSelectsSurvivingMethod()
    {
        const string oldText = "class C {\n int M(\n  int removed,\n  int kept) => kept;\n}";
        const string newText = "class C {\n int M(\n  int kept) => kept;\n}";
        var oldMethods = new SourceAnalyzer().AnalyzeText("src/C.cs", oldText);
        var newMethods = new SourceAnalyzer().AnalyzeText("src/C.cs", newText);
        var oldSource = CapturedSource.Create("src/C.cs", Encoding.UTF8.GetBytes(oldText));
        var newSource = CapturedSource.Create("src/C.cs", Encoding.UTF8.GetBytes(newText));
        var change = new ChangedFile("src/C.cs", "src/C.cs", ScopeChangeKind.Modified,
            oldSource.ContentIdentity, newSource.ContentIdentity, oldSource, newSource, [], [new LineRange(3, 3)]);

        var selection = ChangedMethodSelector.Select(change, oldMethods, newMethods, ScopeGranularity.Method);

        Assert.Equal("M", Assert.Single(selection.Methods).MethodName);
        Assert.Empty(selection.Removals);
    }

    private static void AssertDirectiveInventoryChange(string inactiveText, string activeText, int directiveLine)
    {
        var analyzer = new SourceAnalyzer();
        var inactiveSource = CapturedSource.Create("src/C.cs", Encoding.UTF8.GetBytes(inactiveText));
        var activeSource = CapturedSource.Create("src/C.cs", Encoding.UTF8.GetBytes(activeText));

        var activation = new ChangedFile("src/C.cs", "src/C.cs", ScopeChangeKind.Modified,
            inactiveSource.ContentIdentity, activeSource.ContentIdentity, inactiveSource, activeSource,
            [new LineRange(directiveLine, directiveLine)], [new LineRange(directiveLine, directiveLine)]);
        var activated = ChangedMethodSelector.Select(activation,
            analyzer.AnalyzeCaptured(inactiveSource.LogicalPath, inactiveSource.Bytes),
            analyzer.AnalyzeCaptured(activeSource.LogicalPath, activeSource.Bytes), ScopeGranularity.Method);

        Assert.Equal("Conditional", Assert.Single(activated.Methods).MethodName);
        Assert.Empty(activated.Removals);
        Assert.Contains(activated.ScopeLimitations, limitation =>
            limitation.Code == "scope.contextIncomplete" && limitation.SyntaxKind.EndsWith("DirectiveTrivia", StringComparison.Ordinal));

        var deactivation = new ChangedFile("src/C.cs", "src/C.cs", ScopeChangeKind.Modified,
            activeSource.ContentIdentity, inactiveSource.ContentIdentity, activeSource, inactiveSource,
            [new LineRange(directiveLine, directiveLine)], [new LineRange(directiveLine, directiveLine)]);
        var deactivated = ChangedMethodSelector.Select(deactivation,
            analyzer.AnalyzeCaptured(activeSource.LogicalPath, activeSource.Bytes),
            analyzer.AnalyzeCaptured(inactiveSource.LogicalPath, inactiveSource.Bytes), ScopeGranularity.Method);

        Assert.Empty(deactivated.Methods);
        Assert.Equal("Conditional", Assert.Single(deactivated.Removals).MethodName);
        Assert.Contains(deactivated.ScopeLimitations, limitation =>
            limitation.Code == "scope.contextIncomplete" && limitation.SyntaxKind.EndsWith("DirectiveTrivia", StringComparison.Ordinal));
    }
}
