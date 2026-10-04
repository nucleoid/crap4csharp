using Crap4CSharp.Core;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CallableCoverageTests
{
    [Fact]
    public void SemanticIdentityDistinguishesSameArityOverloadsAndSpecialMembers()
    {
        const string source = """
            class C {
              C(int x) { }
              int P { get => 1; init { } }
              int M(int x) => x;
              int M(string x) => x.Length;
              static explicit operator int(C x) => x.P;
            }
            """;
        var inventory = Inventory(source);

        Assert.Contains(inventory.Callables, item => item.SemanticIdentity?.MetadataName == ".ctor");
        Assert.Contains(inventory.Callables, item => item.SemanticIdentity?.MetadataName == "get_P");
        Assert.Contains(inventory.Callables, item => item.SemanticIdentity?.MetadataName == "set_P");
        Assert.Contains(inventory.Callables, item => item.SemanticIdentity?.MetadataName == "op_Explicit" &&
            item.SemanticIdentity.ReturnType.Contains("Int32", StringComparison.Ordinal));
        Assert.Equal(2, inventory.Callables.Where(item => item.Name == "M")
            .Select(item => item.SemanticIdentity!.Parameters[0].Type).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ExactSemanticReportIdentityMapsOrdinaryBodies()
    {
        var inventory = Inventory("class C { int M(int x) => x; int M(string x) => x.Length; }");
        var target = inventory.Callables.Single(item => item.Name == "M" && item.SemanticIdentity!.Parameters[0].Type.Contains("Int32"));
        var report = Report(target, [new CoveragePoint(target.Span.StartLine, 1, target.Span.StartColumn,
            target.Span.EndLine, target.Span.EndColumn, 0)]);

        var resolved = CallableCoverageResolver.Resolve(inventory, [report]);

        var match = Assert.Single(resolved.Observations, item => item.CallableId == target.CallableId);
        Assert.Equal("known", match.Status);
        Assert.Equal(1, Assert.Single(match.Points).Visited ? 1 : 0);
        Assert.All(resolved.Observations.Where(item => item.CallableId != target.CallableId), item => Assert.Equal("unknown", item.Status));
    }

    [Fact]
    public void SameLineBodiesWithoutDiscriminatingIdentityAreAmbiguousAndNeverShareAPoint()
    {
        var inventory = Inventory("class C { int A() => 1; int B() => 2; }");
        var report = new CoverageMethod("C.cs", "C", string.Empty, null, [new CoveragePoint(1, 1)], "module")
        {
            ContextId = "ctx",
            DocumentIdentities = ["doc"]
        };

        var resolved = CallableCoverageResolver.Resolve(inventory, [report]);

        Assert.All(resolved.Observations, item => Assert.Equal("unknown", item.Status));
        Assert.Contains(resolved.Diagnostics, item => item.Code == "coverage.ambiguousCallableOwnership");
    }

    [Fact]
    public void ContextAndModuleIdentityNeverUnionAcrossLookalikes()
    {
        var inventory = Inventory("class C { int M() => 1; }");
        var target = Assert.Single(inventory.Callables);
        var valid = Report(target, [new CoveragePoint(1, 1)]) with { ModuleIdentity = "mvid-a" };
        var wrongContext = Report(target, [new CoveragePoint(1, 1)]) with { ContextId = "other", ModuleIdentity = "mvid-a" };
        var wrongModule = Report(target, [new CoveragePoint(1, 1)]) with { ModuleIdentity = "mvid-b" };

        var resolved = CallableCoverageResolver.Resolve(inventory, [valid, wrongContext, wrongModule], "mvid-a");

        Assert.Equal("known", Assert.Single(resolved.Observations).Status);
        Assert.Contains(resolved.Diagnostics, item => item.Code == CoverageReasonCodes.ContextMismatch);
        Assert.Contains(resolved.Diagnostics, item => item.Code == CoverageReasonCodes.ConflictingModule);
    }

    [Fact]
    public void DistinctReportedModulesNeverUnionWhenNoExpectedModuleWasDeclared()
    {
        var inventory = Inventory("class C { int M() => 1; }");
        var target = Assert.Single(inventory.Callables);
        var first = Report(target, [new CoveragePoint(1, 1)]) with { ModuleIdentity = "module-a" };
        var second = Report(target, [new CoveragePoint(1, 1)]) with { ModuleIdentity = "module-b" };

        var resolved = CallableCoverageResolver.Resolve(inventory, [first, second]);

        Assert.Equal("unknown", Assert.Single(resolved.Observations).Status);
        Assert.Equal(CoverageReasonCodes.ConflictingModule, Assert.Single(resolved.Observations).Reason);
        Assert.Contains(resolved.Diagnostics, item => item.Code == CoverageReasonCodes.ConflictingModule);
    }

    [Fact]
    public void GeneratedNamesRemainExplicitlyUnsupported()
    {
        var inventory = Inventory("class C { async System.Threading.Tasks.Task<int> M() => await System.Threading.Tasks.Task.FromResult(1); }");
        var report = new CoverageMethod("C.cs", "C+<M>d__0", "MoveNext", 0, [new CoveragePoint(1, 1)], "module")
        {
            ContextId = "ctx", RawSignature = "System.Void C+<M>d__0::MoveNext()", DocumentIdentities = ["doc"]
        };
        var resolved = CallableCoverageResolver.Resolve(inventory, [report]);
        Assert.Equal("unknown", Assert.Single(resolved.Observations).Status);
        Assert.Contains(resolved.Diagnostics, item => item.Code == CoverageReasonCodes.UnsupportedGeneratedMapping);
    }

    [Fact]
    public void IndistinguishableAnonymousEntitiesRemainSeparateUnknownObservationsWithoutCrashing()
    {
        var inventory = Inventory("class C { void M() { System.Func<int,int> a = x => x + 1; System.Func<int,int> b = x => x + 1; } }");

        var resolved = CallableCoverageResolver.Resolve(inventory, []);

        var lambdas = inventory.Callables.Where(item => item.Kind == CallableKind.Lambda).ToArray();
        Assert.Equal(2, lambdas.Length);
        Assert.Equal(2, resolved.Observations.Count(item => item.CallableId == lambdas[0].CallableId));
        Assert.All(resolved.Observations.Where(item => item.CallableId == lambdas[0].CallableId), item =>
        {
            Assert.Equal("unknown", item.Status);
            Assert.Equal(CoverageReasonCodes.AmbiguousCallableOwnership, item.Reason);
            Assert.NotNull(item.ObservationId);
        });
    }

    [Fact]
    public void LambdaInsidePropertyDoesNotPolluteGetterSemanticIdentity()
    {
        var inventory = Inventory(
            "class C { int[] xs = []; int P => System.Linq.Enumerable.Count(xs, x => x > 0); }");
        var getter = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.PropertyGet);
        var lambda = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.Lambda);
        Assert.NotNull(getter.SemanticIdentity);
        Assert.Null(lambda.SemanticIdentity);

        var resolved = CallableCoverageResolver.Resolve(inventory,
            [Report(getter, [new CoveragePoint(getter.Span.StartLine, 1)])]);

        Assert.Equal("known", Assert.Single(resolved.Observations,
            item => item.ObservationId == getter.ObservationId).Status);
    }

    [Fact]
    public void CecilStyleNestedArrayGenericAndByRefTypesMatchSemanticIdentity()
    {
        var inventory = Inventory("""
            class Outer {
              class Inner<T> { public T Echo(T value) => value; }
              int ArrayLength(int[] values) => values.Length;
              int Sum(System.Collections.Generic.List<int> values) => values.Count;
              void Change(ref int value) => value++;
            }
            """);
        var echo = Assert.Single(inventory.Callables, item => item.Name == "Echo");
        var array = Assert.Single(inventory.Callables, item => item.Name == "ArrayLength");
        var sum = Assert.Single(inventory.Callables, item => item.Name == "Sum");
        var change = Assert.Single(inventory.Callables, item => item.Name == "Change");
        var reports = new[]
        {
            CecilReport(echo, "Outer/Inner`1", "(T)"),
            CecilReport(array, "Outer", "(System.Int32[])"),
            CecilReport(sum, "Outer", "System.Int32 Outer::Sum(System.Collections.Generic.List`1<System.Int32>)"),
            CecilReport(change, "Outer", "System.Void Outer::Change(System.Int32&)")
        };

        var resolved = CallableCoverageResolver.Resolve(inventory, reports);

        Assert.All(resolved.Observations, item => Assert.Equal("known", item.Status));
    }

    private static CallableInventoryResult Inventory(string source) => CallableInventory.Analyze(source, "C.cs",
        new CallableAnalysisContext("App.csproj", "net10.0", "Debug", "AnyCPU", "ctx", CSharpParseOptions.Default));

    private static CoverageMethod Report(CallableEntry target, IReadOnlyList<CoveragePoint> points)
    {
        var semantic = target.SemanticIdentity!;
        return new CoverageMethod("C.cs", semantic.TypeName, semantic.MetadataName, semantic.Parameters.Count, points, "module")
        {
            ContextId = "ctx",
            RawSignature = semantic.ReportSignature,
            GenericArity = semantic.GenericArity,
            DocumentIdentities = ["doc"]
        };
    }

    private static CoverageMethod CecilReport(CallableEntry target, string typeName, string signature) =>
        new("C.cs", typeName, target.SemanticIdentity!.MetadataName, target.SemanticIdentity.Parameters.Count,
            [new CoveragePoint(target.Span.StartLine, 1)], "module")
        {
            ContextId = "ctx",
            RawSignature = signature,
            GenericArity = target.SemanticIdentity.GenericArity,
            DocumentIdentities = ["doc"]
        };
}
