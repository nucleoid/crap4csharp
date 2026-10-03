using Crap4CSharp.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ComplexityRulesTests
{
    [Fact]
    public void RulesetIdsAreImmutableAndUnknownValuesAreRejected()
    {
        Assert.Equal("ordinary-methods-v1", ComplexityRules.OrdinaryMethodsV1);
        Assert.Equal("callables-v1", ComplexityRules.CallablesV1);
        Assert.Same(ComplexityRules.Legacy, ComplexityRules.Parse(ComplexityRules.OrdinaryMethodsV1));
        Assert.Same(ComplexityRules.Modern, ComplexityRules.Parse(ComplexityRules.CallablesV1));
        Assert.Throws<ArgumentException>(() => ComplexityRules.Parse("future"));
    }

    [Theory]
    [InlineData("int M() => 1;", 1)]
    [InlineData("int M(int x) { if (x > 0) return 1; else return 0; }", 2)]
    [InlineData("int M(int x) { for (;;) { break; } return x; }", 2)]
    [InlineData("int M(int? x) { x ??= 1; return x.Value; }", 1)]
    [InlineData("int M(C? x) => x?.Value ?? 0;", 2)]
    [InlineData("int M(int x) => x is not (> 0 and < 10 or 20) ? 1 : 0;", 4)]
    [InlineData("int M(int x) => x switch { > 0 when x > 1 && x < 9 => 1, _ => 0 };", 5)]
    public void CallablesV1HasTheExactDecisionMatrix(string member, int expected)
    {
        Assert.Equal(expected, Complexity(member, ComplexityRules.Modern));
    }

    [Fact]
    public void CatchFilterAndDeconstructionForeachDifferFromLegacyExactly()
    {
        const string catching = "int M(bool a, bool b) { try { return 1; } catch when (a && b) { return 0; } }";
        const string loop = "int M((int,int)[] xs) { foreach (var (a,b) in xs) { } return 0; }";

        Assert.Equal(3, Complexity(catching, ComplexityRules.Legacy));
        Assert.Equal(4, Complexity(catching, ComplexityRules.Modern));
        Assert.Equal(1, Complexity(loop, ComplexityRules.Legacy));
        Assert.Equal(2, Complexity(loop, ComplexityRules.Modern));
    }

    [Fact]
    public void NestedCallableDecisionsNeverLeakIntoTheirParent()
    {
        const string member = "int M(int x) { int Local(int y) => y > 0 ? 1 : 0; System.Func<int,int> f = y => y > 0 ? 1 : 0; return x; }";
        Assert.Equal(1, Complexity(member, ComplexityRules.Modern));
    }

    private static int Complexity(string member, IComplexityRules rules)
    {
        var tree = CSharpSyntaxTree.ParseText($"class C {{ {member} }}");
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        return rules.Calculate(method.Body ?? (SyntaxNode)method.ExpressionBody!.Expression);
    }
}
