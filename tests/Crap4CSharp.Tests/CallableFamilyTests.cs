using Crap4CSharp.Core;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CallableFamilyTests
{
    [Fact]
    public void NestedDecisionsRemainInFamilyGuardWithoutEnteringLeafTotals()
    {
        const string source = """
            class C { int M(int x) {
              int L(int y) { if(y>0){} if(y>1){} if(y>2){} if(y>3){} if(y>4){} if(y>5){} if(y>6){} if(y>7){} if(y>8){} return y; }
              return x;
            } }
            """;
        var inventory = Inventory(source);
        var root = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.Method);
        var child = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.LocalFunction);
        var coverage = new[]
        {
            Known(root.CallableId, "p0", true),
            Known(child.CallableId, "p1", true)
        };

        var family = Assert.Single(CallableFamilyEvaluator.Evaluate(inventory, coverage, threshold: 8));

        Assert.Equal("crap.nestedFamilyRisk", family.Rule);
        Assert.Equal(10, family.Complexity);
        Assert.Equal(1, family.Coverage);
        Assert.Equal(10, family.Crap);
        Assert.True(family.IsViolation);
        Assert.Equal(1, root.Complexity);
        Assert.Equal(10, child.Complexity);
    }

    [Fact]
    public void FamilyCoverageIsAnOwnedPointUnionAndUnknownChildFailsClosed()
    {
        var inventory = Inventory("class C { int M() { int L() => 1; return L(); } }");
        var root = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.Method);
        var child = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.LocalFunction);
        var duplicate = new CallableCoveragePoint("ctx", "doc", 1, 1, 1, 2, 0, false);
        var observations = new[]
        {
            new CallableCoverageObservation(root.CallableId, "known", [duplicate], null),
            new CallableCoverageObservation(child.CallableId, "known", [duplicate with { Visited = true }], null)
        };
        var family = Assert.Single(CallableFamilyEvaluator.Evaluate(inventory, observations, 8));
        Assert.Equal(1, family.EligiblePoints);
        Assert.Equal(1, family.VisitedPoints);
        Assert.Equal(1, family.Coverage);

        var unknown = CallableFamilyEvaluator.Evaluate(inventory,
            [Known(root.CallableId, "p", true), new(child.CallableId, "unknown", [], "coverage.unsupportedGeneratedMapping")], 8);
        var incomplete = Assert.Single(unknown);
        Assert.Null(incomplete.Coverage);
        Assert.Null(incomplete.Crap);
        Assert.Contains(child.CallableId, incomplete.IncompleteCallableIds);
    }

    [Fact]
    public void ChangedChildSelectsItsDistinctFamilyGuard()
    {
        var inventory = Inventory("class C { int M() { int L() => 1; return L(); } int U() => 0; }");
        var child = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.LocalFunction);
        var family = Assert.Single(CallableFamilyEvaluator.Evaluate(inventory, [], 8));

        var selected = CallableChangeSelector.Select(inventory, [child.CallableId]);

        Assert.Equal([child.CallableId], selected.CallableIds);
        Assert.Equal([family.FamilyId], selected.FamilyIds);
    }

    [Fact]
    public void MultiLevelNestingIsIncludedInTheOuterExecutableFamily()
    {
        var inventory = Inventory(
            "class C { int M() { int L() { int D() => 1; return D(); } return L(); } }");
        var families = CallableFamilyEvaluator.Evaluate(inventory, [], 8);

        var family = Assert.Single(families);
        Assert.Equal(3, family.MemberObservationIds.Count);
        Assert.Equal(3, family.MemberCallableIds.Count);
    }

    private static CallableInventoryResult Inventory(string source) => CallableInventory.Analyze(source, "C.cs",
        new CallableAnalysisContext("App.csproj", "net10.0", "Debug", "AnyCPU", "ctx", CSharpParseOptions.Default));

    private static CallableCoverageObservation Known(string id, string point, bool visited) => new(id, "known",
        [new CallableCoveragePoint("ctx", "doc", 1, 1, 1, 2, point.GetHashCode(), visited)], null);
}
