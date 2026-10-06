using System.Reflection;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerReferenceOutputBindingTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("copy")]
    [InlineData("parameter")]
    [InlineData("duplicate")]
    [InlineData("redirect")]
    [InlineData("alias")]
    [InlineData("implementation")]
    [InlineData("module")]
    [InlineData("malformed")]
    [InlineData("oversize")]
    public void RealReferenceArtifactsRefuseUnprovenCopyOrIdentity(string defect)
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-output-proof");
        var (context, observation, reference) = Valid(fixture);
        switch (defect)
        {
            case "missing": observation = observation with { ReferenceAssembly = null }; break;
            case "hash": observation = observation with { ReferenceAssembly = reference with { Sha256 = new string('0', 64) } }; break;
            case "length": observation = observation with { ReferenceAssembly = reference with { Length = 1 } }; break;
            case "copy": File.AppendAllText(context.EvaluatedReferenceAssemblyPath!, "changed"); break;
            case "parameter": observation = observation with { Parameters = [new("OutputRefAssembly", ["shadow.dll"])] }; break;
            case "duplicate": observation = observation with { Parameters = [.. observation.Parameters, observation.Parameters[0]] }; break;
            case "redirect": context = context with { EvaluatedIntermediateDirectory = Path.Combine(fixture.Path, "elsewhere") }; break;
            case "alias": context = context with { EvaluatedReferenceAssemblyPath = reference.Path }; break;
            case "implementation":
                File.Copy(observation.Assembly!.Path, reference.Path, true); File.Copy(reference.Path, context.EvaluatedReferenceAssemblyPath!, true);
                observation = observation with { ReferenceAssembly = Capture(reference.Path) }; break;
            case "module": context = context with { AssemblyName = "Shadow" }; break;
            case "malformed":
                File.WriteAllBytes(reference.Path, [1, 2, 3]); File.Copy(reference.Path, context.EvaluatedReferenceAssemblyPath!, true);
                observation = observation with { ReferenceAssembly = Capture(reference.Path) }; break;
            case "oversize": using (var stream = File.Create(reference.Path)) stream.SetLength(CompilerPhaseSnapshot.MaximumFileBytes + 1); break;
        }
        Assert.Throws<ProjectContextException>(() => Validate(context, observation, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RealReferenceImageCopyControlDoesNotClaimActualCscOrCompleteCapability()
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-output-positive");
        var (context, observation, _) = Valid(fixture);
        Validate(context, observation, TestContext.Current.CancellationToken);
        Assert.False(context.Capabilities.CompiledInputBindingComplete);
    }

    [Fact]
    public void CancellationPrecedesAllOutputReads()
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-output-cancel");
        var (context, observation, _) = Valid(fixture);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Validate(context, observation, cancelled.Token));
    }

    private static void Validate(ProjectAnalysisContext context, CompilerObservation observation, CancellationToken token)
    {
        // Proposed private gate; the prior implementation has no gate. Missing seam behaves
        // as the prior acceptance path so the material refusal tests execute genuine RED.
        var type = typeof(CompilerEvidence).Assembly.GetType("Crap4CSharp.Core.CompilerReferenceOutputBinding");
        if (type is null) return;
        try { type.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [context, observation, token]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }

    private static (ProjectAnalysisContext, CompilerObservation, CompilerOutput) Valid(TestDirectory fixture)
    {
        var project = fixture.Write("App.csproj", "<Project />");
        var compilation = CSharpCompilation.Create("App", [CSharpSyntaxTree.ParseText("public class Code { public int M() => 42; }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        using var pe = new MemoryStream(); using var referencePe = new MemoryStream();
        Assert.True(compilation.Emit(pe).Success);
        Assert.True(compilation.Emit(referencePe, options: new EmitOptions(metadataOnly: true, includePrivateMembers: false)).Success);
        var assembly = fixture.Write("obj/App.dll", ""); File.WriteAllBytes(assembly, pe.ToArray());
        var reference = fixture.Write("obj/refint/App.dll", ""); File.WriteAllBytes(reference, referencePe.ToArray());
        var target = fixture.Write("obj/ref/App.dll", ""); File.Copy(reference, target, true);
        var context = ProjectAnalysisContext.Create("App.csproj", "App", "net10.0", "Release", "AnyCPU", LanguageVersion.CSharp14,
            [], SourceCodeKind.Regular, [], [], new(false, false), new("sdk", "msbuild", "roslyn")) with
        { ResolvedProjectPath = project, EvaluatedIntermediateDirectory = Path.Combine(fixture.Path, "obj"), EvaluatedReferenceAssemblyPath = target };
        var captured = Capture(reference);
        // Real Roslyn reference/implementation images; parameters are supplied protocol controls,
        // not actual SDK Csc observations. Actual SDK integration lives in destination tests.
        return (context, new(project, [new("OutputRefAssembly", [reference])]) { Assembly = Capture(assembly), ReferenceAssembly = captured }, captured);
    }
    private static CompilerOutput Capture(string path) => new(path, ProjectAnalysisContext.ContentHash(File.ReadAllBytes(path)), new FileInfo(path).Length);
}
