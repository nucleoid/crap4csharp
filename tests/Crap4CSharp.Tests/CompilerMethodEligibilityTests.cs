using System.Collections.Immutable;
using System.Text;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerMethodEligibilityTests
{
    [Theory]
    [InlineData("record")]
    [InlineData("lambda")]
    [InlineData("annotated")]
    [InlineData("foreign-document")]
    [InlineData("foreign-type")]
    [InlineData("ordinary")]
    [InlineData("positional-record")]
    [InlineData("generic-record")]
    [InlineData("authored-constructor")]
    [InlineData("authored-copy-constructor")]
    [InlineData("record-auto-accessors")]
    [InlineData("authored-record-getter")]
    public void ActualGeneratedMetadataCannotHideAuthoredMethods(string variant)
    {
        var source = """
            public record Value
            {
                public int Read() => 43;
                [System.Runtime.CompilerServices.CompilerGenerated]
                public int Annotated() => 44;
            }
            public static class Library
            {
                public static int Other()
                {
                    System.Func<int> value = () =>
                    {
                        return 43;
                    };
                    return value();
                }
            }
            """;
        if (variant == "positional-record") source = source.Replace("record Value", "record Value(int Number)", StringComparison.Ordinal);
        if (variant == "generic-record") source = source.Replace("record Value", "record Value<T>", StringComparison.Ordinal);
        if (variant == "record-auto-accessors") source = source.Replace("public int Read() => 43;",
            "public int Number { get; init; } = 43; public int Read() => Number;", StringComparison.Ordinal);
        if (variant == "authored-record-getter") source = source.Replace("public int Read() => 43;",
            "public int Number { [System.Runtime.CompilerServices.CompilerGenerated] get { return 43; } } public int Read() => Number;", StringComparison.Ordinal);
        if (variant == "authored-constructor") source = source.Replace("public int Read() => 43;",
            "[System.Runtime.CompilerServices.CompilerGenerated] public Value(int value) { if (value < 0) throw new System.ArgumentException(); } public int Read() => 43;",
            StringComparison.Ordinal);
        if (variant == "authored-copy-constructor") source = source.Replace("public int Read() => 43;",
            "[System.Runtime.CompilerServices.CompilerGenerated] protected Value(Value other) { if (other is null) throw new System.ArgumentException(); } public int Read() => 43;",
            StringComparison.Ordinal);
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8, SourceHashAlgorithm.Sha256),
            new CSharpParseOptions(LanguageVersion.CSharp14, preprocessorSymbols: ["DEBUG"]), "Code.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("EligibilityFixture", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var pe = new MemoryStream(); using var pdb = new MemoryStream();
        var emitted = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var build = ArtifactEvidenceInspector.InspectBuild(ImmutableArray.CreateRange(pe.ToArray()),
            ImmutableArray.CreateRange(pdb.ToArray()));
        var documents = new Dictionary<string, string>(StringComparer.Ordinal) { ["Code.cs"] = "Code.cs" };
        if (variant == "foreign-document") documents = new() { ["other.cs"] = "Code.cs" };
        if (variant == "foreign-type") build = build with
            { Methods = build.Methods.Select(method => method with { TypeName = "Other.Value" }).ToArray() };
        var classified = CompilerMethodEligibility.Classify(compilation, build, documents, TestContext.Current.CancellationToken);
        var read = Assert.Single(build.Methods, method => method.MethodName == "Read");
        var annotated = Assert.Single(build.Methods, method => method.MethodName == "Annotated");
        var outer = Assert.Single(build.Methods, method => method.MethodName == "Other");
        Assert.True(annotated.CompilerGenerated);
        Assert.DoesNotContain(read.MetadataToken, classified);
        Assert.DoesNotContain(annotated.MetadataToken, classified);
        Assert.DoesNotContain(outer.MetadataToken, classified);
        if (variant is "record-auto-accessors" or "authored-record-getter")
        {
            var getter = Assert.Single(build.Methods, method => method.MethodName == "get_Number");
            Assert.True(getter.CompilerGenerated);
            if (variant == "record-auto-accessors")
            {
                Assert.Contains(getter.MetadataToken, classified);
                Assert.Contains(Assert.Single(build.Methods, method => method.MethodName == "set_Number").MetadataToken, classified);
            }
            else Assert.DoesNotContain(getter.MetadataToken, classified);
        }
        if (variant is "authored-constructor" or "authored-copy-constructor")
        {
            var wanted = variant == "authored-constructor" ? "System.Int32" : "Value";
            var authored = Assert.Single(build.Methods, method => method.MethodName == ".ctor" &&
                method.ParameterTypes!.SequenceEqual([wanted]));
            Assert.True(authored.CompilerGenerated);
            Assert.DoesNotContain(authored.MetadataToken, classified);
        }
        if (variant is "record" or "positional-record" or "generic-record")
        {
            var synthesized = Assert.Single(build.Methods, method => method.TypeName.StartsWith("Value", StringComparison.Ordinal) &&
                method.MethodName == ".ctor" && method.ParameterTypes?.Any(parameter => parameter.StartsWith("Value", StringComparison.Ordinal)) == true);
            Assert.True(synthesized.CompilerGenerated);
            Assert.Contains(synthesized.MetadataToken, classified);
        }
        if (variant == "positional-record")
            Assert.Contains(Assert.Single(build.Methods, method => method.MethodName == ".ctor" &&
                method.ParameterTypes!.SequenceEqual(["System.Int32"])).MetadataToken, classified);
        if (variant == "lambda")
        {
            var generated = Assert.Single(build.Methods, method => method.TypeName.Contains("<>c", StringComparison.Ordinal) &&
                method.SequencePoints.Any(point => point.StartLine == 13));
            Assert.True(generated.CompilerGenerated);
            Assert.Contains(generated.MetadataToken, classified);
        }
        if (variant == "foreign-document") Assert.Empty(classified);
        if (variant == "foreign-type") Assert.DoesNotContain(build.Methods.Single(method => method.MethodName == ".ctor").MetadataToken, classified);
    }
}
