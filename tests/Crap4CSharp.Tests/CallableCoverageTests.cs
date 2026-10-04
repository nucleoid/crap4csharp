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
    public void ColumnlessGetterPointSharingALineWithBodylessSetterIsAmbiguous()
    {
        var inventory = Inventory("interface C { int P { get => 1; set; } }");
        var getter = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.PropertyGet);
        var setter = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.PropertySet);

        var resolved = CallableCoverageResolver.Resolve(inventory,
            [Report(getter, [new CoveragePoint(getter.Span.StartLine, 7)])]);

        var observation = Assert.Single(resolved.Observations,
            item => item.ObservationId == getter.ObservationId);
        Assert.Equal("unknown", observation.Status);
        Assert.Equal(CoverageReasonCodes.AmbiguousCallableOwnership, observation.Reason);
        Assert.Equal("not-applicable", Assert.Single(resolved.Observations,
            item => item.ObservationId == setter.ObservationId).Status);
    }

    [Fact]
    public void ColumnlessPointCannotChooseBetweenSameLineAccessorBodies()
    {
        var inventory = Inventory("class C { int field; int P { get => field; set => field = value; } }");
        var accessors = inventory.Callables.Where(item =>
            item.Kind is CallableKind.PropertyGet or CallableKind.PropertySet).ToArray();
        Assert.Equal(2, accessors.Length);

        var resolved = CallableCoverageResolver.Resolve(inventory,
            accessors.Select(item => Report(item, [new CoveragePoint(item.Span.StartLine, 1)])));

        Assert.All(resolved.Observations.Where(item =>
            accessors.Any(accessor => accessor.ObservationId == item.ObservationId)), item =>
        {
            Assert.Equal("unknown", item.Status);
            Assert.Equal(CoverageReasonCodes.AmbiguousCallableOwnership, item.Reason);
        });
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
            [Report(getter, [new CoveragePoint(getter.Span.StartLine, 1, getter.Span.StartColumn,
                getter.Span.EndLine, getter.Span.EndColumn)])]);

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

    [Fact]
    public void CecilStyleTupleDynamicNestedNullableAndNonGenericFrameworkTypesMatchSemanticIdentity()
    {
        var inventory = Inventory("""
            class Outer {
              int Tuple((int, int) value) => value.Item1;
              int Dynamic(dynamic value) => value.GetHashCode();
              int NullableList(System.Collections.Generic.List<int?> values) => values.Count;
              int Enumerable(System.Collections.IEnumerable values) => 1;
            }
            """);
        var tuple = Assert.Single(inventory.Callables, item => item.Name == "Tuple");
        var dynamic = Assert.Single(inventory.Callables, item => item.Name == "Dynamic");
        var nullable = Assert.Single(inventory.Callables, item => item.Name == "NullableList");
        var enumerable = Assert.Single(inventory.Callables, item => item.Name == "Enumerable");
        var reports = new[]
        {
            CecilReport(tuple, "Outer", "System.Int32 Outer::Tuple(System.ValueTuple`2<System.Int32,System.Int32>)"),
            CecilReport(dynamic, "Outer", "System.Int32 Outer::Dynamic(System.Object)"),
            CecilReport(nullable, "Outer", "System.Int32 Outer::NullableList(System.Collections.Generic.List`1<System.Nullable`1<System.Int32>>)"),
            CecilReport(enumerable, "Outer", "System.Int32 Outer::Enumerable(System.Collections.IEnumerable)")
        };

        var resolved = CallableCoverageResolver.Resolve(inventory, reports);

        Assert.All(resolved.Observations, item => Assert.Equal("known", item.Status));
    }

    [Fact]
    public void CecilStyleContainingGenericArgumentsAndMultidimensionalArraysMatchSemanticIdentity()
    {
        var inventory = Inventory("""
            class Outer<T> { public class Inner { } }
            class LinkedList<T> {
              public class Node { }
              int Remove(Node value) => 1;
            }
            class C {
              int Nested(Outer<int>.Inner value) => 1;
              int Matrix(int[,] values) => values.Length;
            }
            """);
        var remove = Assert.Single(inventory.Callables, item => item.Name == "Remove");
        var nested = Assert.Single(inventory.Callables, item => item.Name == "Nested");
        var matrix = Assert.Single(inventory.Callables, item => item.Name == "Matrix");
        var reports = new[]
        {
            CecilReport(remove, "LinkedList`1", "System.Int32 LinkedList`1::Remove(LinkedList`1/Node<T>)"),
            CecilReport(nested, "C", "System.Int32 C::Nested(Outer`1/Inner<System.Int32>)"),
            CecilReport(matrix, "C", "System.Int32 C::Matrix(System.Int32[0...,0...])")
        };

        var resolved = CallableCoverageResolver.Resolve(inventory, reports);

        Assert.All(resolved.Observations, item => Assert.Equal("known", item.Status));
    }

    [Fact]
    public void ConstructedContainingTypeArgumentsKeepOverloadEntityIdsDistinct()
    {
        var inventory = Inventory("""
            class Outer<T> { public class Inner { } }
            class C {
              int M(Outer<int>.Inner value) => 1;
              int M(Outer<string>.Inner value) => 2;
            }
            """);
        var overloads = inventory.Callables.Where(item => item.Name == "M").ToArray();

        Assert.Equal(2, overloads.Length);
        Assert.Equal(2, overloads.Select(item => item.CallableId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(overloads, item => Assert.False(item.IdentityAmbiguous));
    }

    [Fact]
    public void CecilCustomModifiersDoNotPreventAccessorReturnOrParameterMapping()
    {
        var inventory = Inventory("""
            class C {
              private int value;
              int P {
                get => value;
                init { value = 1; }
              }
              ref readonly int Current => ref value;
              public virtual int M(in int item) => item;
              public virtual int N(string text, in int item) => text.Length + item;
            }
            """);
        var init = Assert.Single(inventory.Callables, item => item.Kind == CallableKind.PropertyInit);
        var current = Assert.Single(inventory.Callables, item => item.Name == "Current.get");
        var method = Assert.Single(inventory.Callables, item => item.Name == "M");
        var multiParameter = Assert.Single(inventory.Callables, item => item.Name == "N");
        var reports = new[]
        {
            CecilReport(init, "C", "System.Void modreq(System.Runtime.CompilerServices.IsExternalInit) C::set_P(System.Int32)"),
            CecilReport(current, "C", "System.Int32& modreq(System.Runtime.InteropServices.InAttribute) C::get_Current()"),
            CecilReport(method, "C", "System.Int32 C::M(System.Int32& modreq(System.Runtime.InteropServices.InAttribute))"),
            CecilReport(multiParameter, "C", "System.Int32 C::N(System.String,System.Int32& modreq(System.Runtime.InteropServices.InAttribute))")
        };

        var resolved = CallableCoverageResolver.Resolve(inventory, reports);

        Assert.All(resolved.Observations.Where(item => reports.Any(report =>
            report.MethodName == inventory.Callables.Single(callable => callable.ObservationId == item.ObservationId)
                .SemanticIdentity?.MetadataName)), item => Assert.Equal("known", item.Status));

        var mangled = new[] { method, multiParameter }.Select(target =>
            new CoverageMethod("C.cs", "C", target.SemanticIdentity!.MetadataName, null,
                [new CoveragePoint(target.Span.StartLine, 1)], "module")
            {
                ContextId = "ctx",
                RawSignature = "(System.Runtime.InteropServices.InAttribute))",
                DocumentIdentities = ["doc"]
            }).ToArray();
        var coberturaResolved = CallableCoverageResolver.Resolve(inventory, mangled);
        Assert.All(coberturaResolved.Observations.Where(item =>
            item.ObservationId == method.ObservationId || item.ObservationId == multiParameter.ObservationId),
            item => Assert.Equal("known", item.Status));
    }

    [Fact]
    public void IncompleteCoberturaSignatureCannotChooseAmongSameNameOverloads()
    {
        var inventory = Inventory("""
            class C {
              public virtual int M(in int item) => item;
              public int M(string item) => item.Length;
            }
            """);
        var target = Assert.Single(inventory.Callables, item =>
            item.Name == "M" && item.SemanticIdentity!.Parameters[0].RefKind == "in");
        var report = new CoverageMethod("C.cs", "C", "M", null,
            [new CoveragePoint(target.Span.StartLine, 1)], "module")
        {
            ContextId = "ctx",
            RawSignature = "(System.Runtime.InteropServices.InAttribute))",
            DocumentIdentities = ["doc"]
        };

        var resolved = CallableCoverageResolver.Resolve(inventory, [report]);

        Assert.Equal("unknown", Assert.Single(resolved.Observations,
            item => item.ObservationId == target.ObservationId).Status);
        Assert.Contains(resolved.Diagnostics, item => item.Code == CoverageReasonCodes.AmbiguousCallableOwnership);
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
