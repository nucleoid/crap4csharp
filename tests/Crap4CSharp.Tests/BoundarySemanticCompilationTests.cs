using System.Text;
using System.Text.Json;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class BoundarySemanticCompilationTests
{
    [Fact]
    public void SharedCapturedCompilationPreservesCrossFileIdentitiesAndPartialCallables()
    {
        var sources = new[]
        {
            Source("a.cs", "partial class C<T> { partial void Hook(T value); public int M(int x) => x > 0 ? x : 0; }"),
            Source("b.cs", "partial class C<T> { partial void Hook(T value) { System.Action<T> f = x => Hook(x); f(value); } public int M(string x) => x.Length; }")
        };
        var context = new CallableAnalysisContext("p", "net10.0", "Release", "AnyCPU", "ctx", CSharpParseOptions.Default);
        var references = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s.Text, context.ParseOptions, s.LogicalPath)).ToArray();
        var compilation = CSharpCompilation.Create("Crap4CSharp.CallableInventory", trees, references);
        foreach (var source in sources)
            Assert.Equal(JsonSerializer.Serialize(CallableInventory.Analyze(source.Text, source.LogicalPath, context, references, trees)),
                JsonSerializer.Serialize(CallableInventory.AnalyzeCaptured(source, context, compilation)));
        Assert.Throws<InvalidDataException>(() => CallableInventory.AnalyzeCaptured(Source("a.cs", "class Drift {}"), context, compilation));
    }

    [Fact]
    public void SharedClosureStillRejectsInvalidAuxiliarySyntaxAndAcceptsExternalEligibleSources()
    {
        var context = new CallableAnalysisContext("p", "net10.0", "Release", "AnyCPU", "ctx", CSharpParseOptions.Default);
        var source = Source("a.cs", "class C { int M() => 1; }");
        Assert.Throws<InvalidDataException>(() => SemanticEligibility.HasApplicable([source],
            [source, Source("b.cs", "class {")], context, []));
        Assert.True(SemanticEligibility.HasApplicable([source], [Source("b.cs", "class Other {}")], context, []));
    }

    private static CapturedSource Source(string path, string text) => CapturedSource.Create(path, Encoding.UTF8.GetBytes(text));
}
