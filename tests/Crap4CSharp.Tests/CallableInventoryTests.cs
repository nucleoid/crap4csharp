using Crap4CSharp.Core;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CallableInventoryTests
{
    [Fact]
    public void ModernInventoryContainsEveryAuthoredExecutableKindAndOwnsNestedBodies()
    {
        const string source = """
            using System;
            class C(int seed) : B(seed > 0 ? seed : 0) {
              static int S = seed > 0 ? 1 : 0;
              int f = seed > 0 ? 1 : 0;
              int P { get => f > 0 ? f : 0; init { if (value > 0) f = value; } }
              int Q { get; set; } = seed > 0 ? 1 : 0;
              int this[int i] => i > 0 ? i : 0;
              event Action E { add { if (value != null) { } } remove { } }
              C() : this(1) { if (f > 0) { } }
              C(int x) { }
              ~C() { if (f > 0) { } }
              static C operator +(C a, C b) => a.f > 0 ? a : b;
              static explicit operator int(C a) => a.f > 0 ? a.f : 0;
              int M(int x) {
                int Local(int y) => y > 0 ? 1 : 0;
                Func<int,int> one = y => y > 0 ? 1 : 0;
                Func<int,int> two = delegate(int y) { return y > 0 ? 1 : 0; };
                return x > 0 ? Local(x) : one(x);
              }
              abstract class N { public abstract void Missing(); }
            }
            class B(int x) { }
            """;

        var inventory = CallableInventory.Analyze(source, "src/C.cs", Context());

        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.PrimaryConstructorBaseArguments);
        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.FieldInitializer);
        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.PropertyInitializer);
        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.Constructor);
        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.Destructor);
        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.PropertyGet);
        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.PropertyInit);
        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.IndexerGet);
        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.EventAdd);
        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.EventRemove);
        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.Operator);
        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.Conversion);
        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.LocalFunction);
        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.Lambda);
        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.AnonymousMethod);
        Assert.Contains(inventory.Callables, item => item.Kind == CallableKind.Method && item.Applicability == CallableApplicability.NotApplicable);

        var method = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.Method && item.Name == "M");
        Assert.Equal(2, method.Complexity);
        Assert.All(inventory.Callables.Where(item => item.Kind is CallableKind.LocalFunction or CallableKind.Lambda or CallableKind.AnonymousMethod),
            item => Assert.Equal(method.CallableId, item.ParentId));
        Assert.All(inventory.Callables.Where(item => item.Applicability == CallableApplicability.Applicable),
            item => Assert.Equal(ComplexityRules.CallablesV1, item.Ruleset));
    }

    [Fact]
    public void TopLevelStatementsAreOneRootAndNestedLambdaIsItsChild()
    {
        const string source = "System.Func<int,int> f = x => x > 0 ? 1 : 0; if (f(1) > 0) System.Console.WriteLine(1);";
        var inventory = CallableInventory.Analyze(source, "Program.cs", Context());
        var root = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.TopLevel);
        var lambda = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.Lambda);
        Assert.Equal(2, root.Complexity);
        Assert.Equal(2, lambda.Complexity);
        Assert.Equal(root.CallableId, lambda.ParentId);
    }

    [Fact]
    public void NamedEntitySurvivesLineMovementButObservationBindsContentAndSpan()
    {
        var first = Assert.Single(CallableInventory.Analyze("class C { int M() => 1; }", "C.cs", Context()).Callables,
            item => item.Kind == CallableKind.Method);
        var moved = Assert.Single(CallableInventory.Analyze("\nclass C { int M() => 1; }", "C.cs", Context()).Callables,
            item => item.Kind == CallableKind.Method);

        Assert.Equal(first.CallableId, moved.CallableId);
        Assert.NotEqual(first.ObservationId, moved.ObservationId);
    }

    [Fact]
    public void AnonymousIdentityUsesBodyFingerprintAndRejectsIndistinguishableCandidates()
    {
        const string duplicate = "class C { void M() { System.Func<int,int> a = x => x + 1; System.Func<int,int> b = x => x + 1; } }";
        var duplicates = CallableInventory.Analyze(duplicate, "C.cs", Context()).Callables
            .Where(item => item.Kind == CallableKind.Lambda).ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.All(duplicates, item => Assert.True(item.IdentityAmbiguous));

        var before = Assert.Single(CallableInventory.Analyze(
            "class C { void M() { System.Func<int,int> f = x => x + 1; } }", "C.cs", Context()).Callables,
            item => item.Kind == CallableKind.Lambda);
        var reordered = Assert.Single(CallableInventory.Analyze(
            "class C { int P => 1; void M() { System.Func<int,int> f = x => x + 1; } }", "C.cs", Context()).Callables,
            item => item.Kind == CallableKind.Lambda);
        var edited = Assert.Single(CallableInventory.Analyze(
            "class C { void M() { System.Func<int,int> f = x => x + 2; } }", "C.cs", Context()).Callables,
            item => item.Kind == CallableKind.Lambda);
        Assert.Equal(before.CallableId, reordered.CallableId);
        Assert.NotEqual(before.CallableId, edited.CallableId);
    }

    [Fact]
    public void PartialDefinitionAndImplementationProduceOneExecutableEntry()
    {
        const string source = "partial class C { partial void M(); partial void M() { if (true) { } } }";
        var methods = CallableInventory.Analyze(source, "C.cs", Context()).Callables
            .Where(item => item.Kind == CallableKind.Method && item.Name == "M").ToArray();
        Assert.Single(methods);
        Assert.Equal(2, methods[0].Complexity);
        Assert.Equal(CallableApplicability.Applicable, methods[0].Applicability);
    }

    [Fact]
    public void LocalVariableInitializersAreOwnedStatementsNotFieldInitializerCallables()
    {
        var inventory = CallableInventory.Analyze(
            "class C { int field = 1; void M() { var local = 2; System.Func<int> f = () => 3; } }",
            "C.cs", Context());

        Assert.Single(inventory.Callables, item => item.Kind == CallableKind.FieldInitializer);
        Assert.Single(inventory.Callables, item => item.Kind == CallableKind.Lambda);
        Assert.DoesNotContain(inventory.Callables, item => item.Kind == CallableKind.FieldInitializer && item.ParentId is not null);
    }

    private static CallableAnalysisContext Context() => new(
        "repo/App.csproj", "net10.0", "Debug", "AnyCPU", "context-a", CSharpParseOptions.Default);
}
