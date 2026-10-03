using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Crap4CSharp.Core;

public interface IComplexityRules
{
    string Id { get; }
    int Calculate(SyntaxNode? executableRegion);
}

public static class ComplexityRules
{
    public const string OrdinaryMethodsV1 = "ordinary-methods-v1";
    public const string CallablesV1 = "callables-v1";

    public static IComplexityRules Legacy { get; } = new LegacyRules();
    public static IComplexityRules Modern { get; } = new ModernRules();

    public static IComplexityRules Parse(string id) => id switch
    {
        OrdinaryMethodsV1 => Legacy,
        CallablesV1 => Modern,
        _ => throw new ArgumentException($"Unsupported complexity ruleset '{id}'.", nameof(id))
    };

    private sealed class LegacyRules : IComplexityRules
    {
        public string Id => OrdinaryMethodsV1;
        public int Calculate(SyntaxNode? executableRegion)
        {
            var walker = new LegacyWalker();
            walker.Visit(executableRegion);
            return walker.Complexity;
        }
    }

    private sealed class ModernRules : IComplexityRules
    {
        public string Id => CallablesV1;
        public int Calculate(SyntaxNode? executableRegion)
        {
            var walker = new ModernWalker();
            walker.Visit(executableRegion);
            return walker.Complexity;
        }
    }

    private abstract class OwnedRegionWalker : CSharpSyntaxWalker
    {
        public int Complexity { get; protected set; } = 1;

        public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) { }
        public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) { }
        public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) { }
        public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) { }
    }

    private sealed class LegacyWalker : OwnedRegionWalker
    {
        public override void VisitIfStatement(IfStatementSyntax node) { Complexity++; base.VisitIfStatement(node); }
        public override void VisitForStatement(ForStatementSyntax node) { Complexity++; base.VisitForStatement(node); }
        public override void VisitForEachStatement(ForEachStatementSyntax node) { Complexity++; base.VisitForEachStatement(node); }
        public override void VisitWhileStatement(WhileStatementSyntax node) { Complexity++; base.VisitWhileStatement(node); }
        public override void VisitDoStatement(DoStatementSyntax node) { Complexity++; base.VisitDoStatement(node); }
        public override void VisitCatchClause(CatchClauseSyntax node) { Complexity++; base.VisitCatchClause(node); }
        public override void VisitConditionalExpression(ConditionalExpressionSyntax node) { Complexity++; base.VisitConditionalExpression(node); }
        public override void VisitSwitchExpressionArm(SwitchExpressionArmSyntax node) { Complexity++; base.VisitSwitchExpressionArm(node); }
        public override void VisitCaseSwitchLabel(CaseSwitchLabelSyntax node) { Complexity++; base.VisitCaseSwitchLabel(node); }
        public override void VisitCasePatternSwitchLabel(CasePatternSwitchLabelSyntax node) { Complexity++; base.VisitCasePatternSwitchLabel(node); }
        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.LogicalAndExpression) || node.IsKind(SyntaxKind.LogicalOrExpression) ||
                node.IsKind(SyntaxKind.CoalesceExpression)) Complexity++;
            base.VisitBinaryExpression(node);
        }
    }

    private sealed class ModernWalker : OwnedRegionWalker
    {
        public override void VisitIfStatement(IfStatementSyntax node) { Complexity++; base.VisitIfStatement(node); }
        public override void VisitForStatement(ForStatementSyntax node) { Complexity++; base.VisitForStatement(node); }
        public override void VisitForEachStatement(ForEachStatementSyntax node) { Complexity++; base.VisitForEachStatement(node); }
        public override void VisitForEachVariableStatement(ForEachVariableStatementSyntax node) { Complexity++; base.VisitForEachVariableStatement(node); }
        public override void VisitWhileStatement(WhileStatementSyntax node) { Complexity++; base.VisitWhileStatement(node); }
        public override void VisitDoStatement(DoStatementSyntax node) { Complexity++; base.VisitDoStatement(node); }
        public override void VisitCatchClause(CatchClauseSyntax node) { Complexity++; base.VisitCatchClause(node); }
        public override void VisitCatchFilterClause(CatchFilterClauseSyntax node) { Complexity++; base.VisitCatchFilterClause(node); }
        public override void VisitConditionalExpression(ConditionalExpressionSyntax node) { Complexity++; base.VisitConditionalExpression(node); }
        public override void VisitSwitchExpressionArm(SwitchExpressionArmSyntax node) { Complexity++; base.VisitSwitchExpressionArm(node); }
        public override void VisitCaseSwitchLabel(CaseSwitchLabelSyntax node) { Complexity++; base.VisitCaseSwitchLabel(node); }
        public override void VisitCasePatternSwitchLabel(CasePatternSwitchLabelSyntax node) { Complexity++; base.VisitCasePatternSwitchLabel(node); }
        public override void VisitWhenClause(WhenClauseSyntax node) { Complexity++; base.VisitWhenClause(node); }
        public override void VisitBinaryPattern(BinaryPatternSyntax node)
        {
            if (node.IsKind(SyntaxKind.AndPattern) || node.IsKind(SyntaxKind.OrPattern)) Complexity++;
            base.VisitBinaryPattern(node);
        }
        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.LogicalAndExpression) || node.IsKind(SyntaxKind.LogicalOrExpression) ||
                node.IsKind(SyntaxKind.CoalesceExpression)) Complexity++;
            base.VisitBinaryExpression(node);
        }
    }
}
