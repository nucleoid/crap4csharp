using System.Text;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class SemanticEligibilityTests
{
    [Theory]
    [InlineData("public static class C {}", false)]
    [InlineData("public static class C { public const int Value = 42; }", false)]
    [InlineData("public abstract class C { public abstract void M(); }", false)]
    [InlineData("#line hidden\npublic static class C { public static int M() => 42; }", true)]
    [InlineData("public static class C { private static int Value = M(); static int M() => 42; }", true)]
    public void ExecutableSyntaxNotReportOrPdbEmptinessDefinesEligibility(string code, bool expected)
    {
        var source = CapturedSource.Create("Code.cs", Encoding.UTF8.GetBytes(code));
        var context = new CallableAnalysisContext("App.csproj", "net10.0", "Debug", "AnyCPU", "ctx", CSharpParseOptions.Default);
        Assert.Equal(expected, SemanticEligibility.HasApplicable([source], [source], context,
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
    }
}
