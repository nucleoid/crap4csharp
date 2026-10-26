using System.Collections.Immutable;
using System.Text;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerOutputBindingTests
{
    [Theory]
    [InlineData("missing-assembly")]
    [InlineData("missing-pdb")]
    [InlineData("assembly-hash")]
    [InlineData("assembly-length")]
    [InlineData("pdb-hash")]
    [InlineData("pdb-length")]
    [InlineData("changed-intermediate")]
    [InlineData("changed-copy")]
    [InlineData("changed-copy-pdb")]
    [InlineData("missing-copy")]
    [InlineData("output-path")]
    [InlineData("redirect")]
    [InlineData("pdb-path")]
    [InlineData("wrong-module")]
    [InlineData("language")]
    [InlineData("symbols")]
    [InlineData("source-kind")]
    [InlineData("pdb-source")]
    [InlineData("malformed")]
    [InlineData("mixed-pdb")]
    public void RealArtifactsRefuseMissingOrMismatchedProof(string defect)
    {
        using var fixture = TestDirectory.Create("crap4csharp-output-bind");
        var (context, observed) = Valid(fixture);
        switch (defect)
        {
            case "missing-assembly": observed = observed with { Assembly = null }; break;
            case "missing-pdb": observed = observed with { Pdb = null }; break;
            case "assembly-hash": observed = observed with { Assembly = observed.Assembly! with { Sha256 = new string('0', 64) } }; break;
            case "assembly-length": observed = observed with { Assembly = observed.Assembly! with { Length = 1 } }; break;
            case "pdb-hash": observed = observed with { Pdb = observed.Pdb! with { Sha256 = new string('0', 64) } }; break;
            case "pdb-length": observed = observed with { Pdb = observed.Pdb! with { Length = 1 } }; break;
            case "changed-intermediate": File.AppendAllText(observed.Assembly!.Path, "changed"); break;
            case "changed-copy": File.AppendAllText(context.EvaluatedTargetPath!, "changed"); break;
            case "changed-copy-pdb": File.AppendAllText(Path.ChangeExtension(context.EvaluatedTargetPath!, ".pdb"), "changed"); break;
            case "missing-copy": File.Delete(context.EvaluatedTargetPath!); break;
            case "output-path": observed = observed with { Parameters = [observed.Parameters[0], new("OutputAssembly", ["obj/Shadow.dll"])] }; break;
            case "redirect": context = context with { EvaluatedIntermediateDirectory = Path.Combine(fixture.Path, "other") }; break;
            case "pdb-path": observed = observed with { Pdb = observed.Pdb! with { Path = Path.ChangeExtension(context.EvaluatedTargetPath!, ".pdb") } }; break;
            case "wrong-module": context = context with { AssemblyName = "Shadow" }; break;
            case "language": context = context with { LanguageVersion = LanguageVersion.CSharp13 }; break;
            case "symbols": context = context with { PreprocessorSymbols = ["OTHER"] }; break;
            case "source-kind": context = context with { SourceKind = SourceCodeKind.Script }; break;
            case "pdb-source":
                File.WriteAllText(context.CompilerDocuments[0].ResolvedPath!, "public class Changed { public int M() => 3; }", new UTF8Encoding(false));
                context = context with { CompilerDocuments = [context.CompilerDocuments[0] with { ContentIdentity = ProjectAnalysisContext.ContentHash(File.ReadAllBytes(context.CompilerDocuments[0].ResolvedPath!)) }] };
                break;
            case "malformed":
                File.WriteAllBytes(observed.Assembly!.Path, [1, 2, 3]); File.WriteAllBytes(context.EvaluatedTargetPath!, [1, 2, 3]);
                observed = observed with { Assembly = Capture(observed.Assembly.Path) }; break;
            case "mixed-pdb":
                var (_, other) = Valid(fixture, "Other", "public class Different { public int M() => 8; }");
                File.Copy(other.Pdb!.Path, observed.Pdb!.Path, true);
                File.Copy(other.Pdb.Path, Path.ChangeExtension(context.EvaluatedTargetPath!, ".pdb"), true);
                observed = observed with { Pdb = Capture(observed.Pdb.Path) }; break;
        }
        var error = Assert.Throws<ProjectContextException>(() => CompilerOutputBinding.Validate(context, observed, TestContext.Current.CancellationToken));
        Assert.Equal("compiler.outputsUnproven", error.Reason);
    }

    [Theory]
    [InlineData("stamp")]
    [InlineData("age")]
    public void FullPortableDebugIdentityMustMatchNotOnlyGuid(string defect)
    {
        using var fixture = TestDirectory.Create("crap4csharp-output-debug-id");
        var (context, observed) = Valid(fixture);
        var bytes = File.ReadAllBytes(observed.Assembly!.Path);
        using (var reader = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes, writable: false)))
        {
            var entries = reader.ReadDebugDirectory();
            var index = Array.FindIndex(entries.ToArray(), entry => entry.Type == System.Reflection.PortableExecutable.DebugDirectoryEntryType.CodeView);
            Assert.True(index >= 0);
            if (defect == "stamp")
            {
                Assert.True(reader.PEHeaders.TryGetDirectoryOffset(reader.PEHeaders.PEHeader!.DebugTableDirectory, out var offset));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + index * 28 + 4, 4), entries[index].Stamp ^ 1U);
            }
            else
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entries[index].DataPointer + 20, 4), 2);
        }
        File.WriteAllBytes(observed.Assembly.Path, bytes); File.WriteAllBytes(context.EvaluatedTargetPath!, bytes);
        observed = observed with { Assembly = Capture(observed.Assembly.Path) };
        Assert.Equal("compiler.outputsUnproven", Assert.Throws<ProjectContextException>(() =>
            CompilerOutputBinding.Validate(context, observed, TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    public void RealPortableArtifactsAndCopiesBindWithoutClaimingCompleteCapability()
    {
        using var fixture = TestDirectory.Create("crap4csharp-output-positive");
        var (context, observed) = Valid(fixture);
        var inspected = ArtifactEvidenceInspector.InspectBuild([.. File.ReadAllBytes(observed.Assembly!.Path)], [.. File.ReadAllBytes(observed.Pdb!.Path)]);
        Assert.Equal("App", inspected.ModuleIdentity);
        Assert.Equal("14.0", inspected.LanguageVersion);
        Assert.Contains("BOUND", inspected.PreprocessorSymbols);
        Assert.NotEmpty(inspected.Documents);
        CompilerOutputBinding.Validate(context, observed, TestContext.Current.CancellationToken);
        Assert.False(context.Capabilities.CompiledInputBindingComplete);
        Assert.False(context.Capabilities.ReuseRecipeComplete);
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("symlink")]
    public void UnsupportedOutputFilesRefuse(string defect)
    {
        using var fixture = TestDirectory.Create("crap4csharp-output-limits");
        var (context, observed) = Valid(fixture);
        if (defect == "oversized")
        { using (var stream = File.Create(observed.Assembly!.Path)) stream.SetLength(CompilerPhaseSnapshot.MaximumFileBytes + 1); }
        else
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "File symlink creation requires Windows privileges; directory reparse controls exercise junctions.");
            File.Delete(observed.Assembly!.Path); File.CreateSymbolicLink(observed.Assembly.Path, context.EvaluatedTargetPath!);
        }
        var error = Assert.Throws<ProjectContextException>(() => CompilerOutputBinding.Validate(context, observed, TestContext.Current.CancellationToken));
        Assert.Equal(defect == "oversized" ? "compiler.outputLimit" : "compiler.outputsUnproven", error.Reason);
    }

    [Fact]
    public void PreCancellationPrecedesOutputReads()
    {
        using var fixture = TestDirectory.Create("crap4csharp-output-cancel");
        var (context, observed) = Valid(fixture);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CompilerOutputBinding.Validate(context, observed, cancelled.Token));
    }

    [Fact]
    public void ActualRoslynPathMapBindsPhysicalSourcesAndKeepsExecutableMethodsEligible()
    {
        using var fixture = TestDirectory.Create("crap-output-pathmap");
        var (context, observed) = Valid(fixture, pathMap: fixture.Path + "=/_/");
        var output = CompilerOutputBinding.ValidateAndInspect(context, observed, TestContext.Current.CancellationToken);
        var binding = CompilerDocumentBinding.Create(context, observed, output.Build, TestContext.Current.CancellationToken);
        Assert.All(output.Build.Documents.Keys, name => Assert.StartsWith("/_/", name, StringComparison.Ordinal));
        Assert.Contains(context.CompilerDocuments[0].ResolvedPath!, binding.PhysicalPathsWithPdb);
        var eligible = binding.EligiblePdbNames(context.CompilerDocuments);
        Assert.Contains(output.Build.Methods, m => m.SequencePoints.Any(p => eligible.Contains(p.Document)));
        Assert.Equal(context.CompilerDocuments[0].ResolvedPath, binding.PhysicalPathForReport("/_/App/Code.cs"));
        Assert.False(context.Capabilities.CompiledInputBindingComplete);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("missing-task")]
    [InlineData("unproven-command")]
    [InlineData("argument-drift")]
    public void ActualMappedArtifactsStillRefuseUnboundOrChangedSources(string defect)
    {
        using var fixture = TestDirectory.Create("crap-output-pathmap-negative");
        var (context, observed) = Valid(fixture, pathMap: fixture.Path + "=/_/");
        if (defect == "checksum")
        {
            File.AppendAllText(context.CompilerDocuments[0].ResolvedPath!, " // changed");
            context = context with
            {
                CompilerDocuments = [context.CompilerDocuments[0] with
                { ContentIdentity = ProjectAnalysisContext.ContentHash(File.ReadAllBytes(context.CompilerDocuments[0].ResolvedPath!)) }]
            };
        }
        else if (defect == "missing-task") observed = observed with { InvocationRecipe = null };
        else if (defect == "unproven-command")
            observed = observed with
            {
                InvocationRecipe = observed.InvocationRecipe! with
                { CommandBinding = "unproven", UnsupportedReasons = observed.InvocationRecipe.UnsupportedReasons.Append("recipe.commandEventMissing").ToArray() }
            };
        else observed = observed with { CommandLineArguments = ["/pathmap:" + fixture.Path + "=/other/"] };
        Assert.Throws<ProjectContextException>(() => CompilerOutputBinding.Validate(context, observed, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoslynLongestPrefixAndDirectoryBoundaryArePreserved(bool nested)
    {
        using var fixture = TestDirectory.Create("crap-output-pathmap-prefix");
        var map = nested ? fixture.Path + "=/_/," + Path.Combine(fixture.Path, "App") + "=/specific/" :
            Path.Combine(fixture.Path, "App") + "=/_/";
        var (context, observed) = Valid(fixture, name: nested ? "App" : "App2", pathMap: map);
        var output = CompilerOutputBinding.ValidateAndInspect(context, observed, TestContext.Current.CancellationToken);
        if (nested) Assert.Contains("/specific/Code.cs", output.Build.Documents.Keys);
        else Assert.Contains(output.Build.Documents.Keys, name => name.EndsWith("/App2/Code.cs", StringComparison.Ordinal) && !name.StartsWith("/_/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MappedNamesCannotHaveTwoPhysicalOwnersEvenWhenBytesMatch(bool physicalAlias)
    {
        Assert.SkipWhen(physicalAlias && OperatingSystem.IsWindows(), "The supported POSIX mapped-name profile cannot equal a Windows drive-qualified physical filename.");
        using var fixture = TestDirectory.Create("crap-output-pathmap-collision");
        var map = physicalAlias ? Path.Combine(fixture.Path, "App") + "=" + Path.Combine(fixture.Path, "Other").Replace('\\', '/') + "/" :
            Path.Combine(fixture.Path, "App") + "=/_/," + Path.Combine(fixture.Path, "Other") + "=/_/";
        var (context, observed) = Valid(fixture, pathMap: map);
        var same = fixture.Write("Other/Code.cs", File.ReadAllText(context.CompilerDocuments[0].ResolvedPath!));
        context = context with
        {
            CompilerDocuments = context.CompilerDocuments.Append(context.CompilerDocuments[0] with
            { LogicalPath = "Other.cs", ResolvedPath = same }).ToArray()
        };
        var build = ArtifactEvidenceInspector.InspectBuild([.. File.ReadAllBytes(observed.Assembly!.Path)], [.. File.ReadAllBytes(observed.Pdb!.Path)]);
        Assert.Equal("compiler.outputsUnproven", Assert.Throws<ProjectContextException>(() =>
            CompilerDocumentBinding.Create(context, observed, build, TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    public void DeclaredLineChecksumCannotInventCompilerDocumentOwnership()
    {
        using var fixture = TestDirectory.Create("crap-output-pathmap-line");
        var source = "#pragma checksum \"Foreign.cs\" \"{8829d00f-11b8-4213-878b-770e8597ac16}\" \"" + new string('a', 64) + "\"\n" +
            "#line 100 \"Foreign.cs\"\npublic class Code { public int M() => 1; }";
        var (context, observed) = Valid(fixture, source: source, pathMap: fixture.Path + "=/_/");
        var build = ArtifactEvidenceInspector.InspectBuild([.. File.ReadAllBytes(observed.Assembly!.Path)], [.. File.ReadAllBytes(observed.Pdb!.Path)]);
        Assert.Contains(build.Documents, document => document.Key.EndsWith("Foreign.cs", StringComparison.Ordinal) && document.Value == new string('a', 64));
        Assert.Equal("compiler.outputsUnproven", Assert.Throws<ProjectContextException>(() =>
            CompilerOutputBinding.Validate(context, observed, TestContext.Current.CancellationToken)).Reason);
    }

    [Theory]
    [InlineData("retained-argv")]
    [InlineData("unique-dropped-argv")]
    [InlineData("unmapped")]
    public void RealDuplicateContentDocumentsRequireIndependentMapOwnership(string mode)
    {
        // Retain these small actual Roslyn artifacts if any assertion fails.
        var fixture = TestDirectory.Create("crap-output-restored-map-positive");
        const string code = "file class Local { public int M() => 1; }";
        var map = mode == "unmapped" ? null : fixture.Path + "=/_/";
        var (context, observed) = Valid(fixture, pathMap: map, documents:
        [
            ("A/Code.cs", code),
            ("B/Code.cs", mode == "unique-dropped-argv" ? code.Replace("=> 1", "=> 2", StringComparison.Ordinal) : code)
        ]);
        var build = ArtifactEvidenceInspector.InspectBuild([.. File.ReadAllBytes(observed.Assembly!.Path)], [.. File.ReadAllBytes(observed.Pdb!.Path)]);
        Assert.Equal(2, build.Documents.Count);
        if (mode == "unique-dropped-argv")
        {
            observed = observed with { DocumentPathMap = CompilerDocumentBinding.CapturePathMap(observed, TestContext.Current.CancellationToken) };
            observed = CompilerRecipeObservation.WithoutOptionalRecipe(observed, TestContext.Current.CancellationToken);
        }
        var binding = CompilerDocumentBinding.Create(context, observed, build, TestContext.Current.CancellationToken);
        Assert.Equal(2, binding.PhysicalPathsWithPdb.Count);
        var eligible = binding.EligiblePdbNames(context.CompilerDocuments);
        Assert.Contains(build.Methods, method => method.SequencePoints.Any(point => eligible.Contains(point.Document)));
        foreach (var source in context.CompilerDocuments)
        {
            var name = map is null ? source.ResolvedPath!.Replace('\\', '/') : "/_/App/" + source.LogicalPath;
            Assert.Equal(source.ResolvedPath, binding.PhysicalPathForReport(name));
        }
        Assert.False(context.Capabilities.CompiledInputBindingComplete);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("unsupported")]
    [InlineData("size")]
    [InlineData("swapped")]
    [InlineData("one-pdb-row")]
    public void RestoredMappedChecksumCannotChooseBetweenDistinctPhysicalOwners(string mode)
    {
        var fixture = TestDirectory.Create("crap-output-restored-map-ambiguous");
        const string code = "file class Local { public int M() => 1; }";
        var (context, observed) = Valid(fixture, pathMap: fixture.Path + "=/_/", documents:
            [("A/Code.cs", code), ("B/Code.cs", code)]);
        var build = ArtifactEvidenceInspector.InspectBuild([.. File.ReadAllBytes(observed.Assembly!.Path)], [.. File.ReadAllBytes(observed.Pdb!.Path)]);
        Assert.Equal(2, build.Documents.Count);
        Assert.Single(build.Documents.Values.Distinct(StringComparer.Ordinal));
        // Positive first: the real compiler map and retained matched argv bind both.
        Assert.Equal(2, CompilerDocumentBinding.Create(context, observed, build, TestContext.Current.CancellationToken).PhysicalPathsWithPdb.Count);
        observed = observed with { DocumentPathMap = CompilerDocumentBinding.CapturePathMap(observed, TestContext.Current.CancellationToken) };
        observed = mode == "size" ? CompilerRecipePayload.SizeUnsupported(observed) : mode == "unsupported"
            ? observed with { CommandLineArguments = [], RetainedUnsupportedRecipe = ["recipe.scalarProfileUnsupported"] }
            : CompilerRecipeObservation.WithoutOptionalRecipe(observed, TestContext.Current.CancellationToken);
        if (mode == "swapped")
            observed = observed with
            {
                DocumentPathMap = observed.DocumentPathMap! with
                {
                    Pairs =
                    [
                        new(Path.Combine(fixture.Path, "App", "A") + Path.DirectorySeparatorChar, "/_/App/B/"),
                        new(Path.Combine(fixture.Path, "App", "B") + Path.DirectorySeparatorChar, "/_/App/A/")
                    ]
                }
            };
        if (mode == "one-pdb-row")
            // A selected-row DATA mutation proves the other enumerated owner is
            // still considered. It does not claim this subset is an actual PDB.
            build = build with { Documents = build.Documents.Take(1).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) };
        CompilerRecipeObservation.RequireKnownIntegrity(observed, TestContext.Current.CancellationToken);
        Assert.False(CompilerDocumentBinding.RequirePathMap(observed, Path.GetDirectoryName(context.ResolvedProjectPath!)!, TestContext.Current.CancellationToken).IsEmpty);
        Assert.Equal("compiler.outputsUnproven", Assert.Throws<ProjectContextException>(() =>
            CompilerDocumentBinding.Create(context, observed, build, TestContext.Current.CancellationToken)).Reason);
        Assert.False(context.Capabilities.CompiledInputBindingComplete);
    }

    private static CompilerObservation WithMapObservation(CompilerObservation observed, string root, string map)
    {
        var sdk = Path.Combine(root, "sdk"); var host = Path.Combine(root, "dotnet");
        var capture = new CompilerRecipeObservation(sdk, host, "1:2:3:4:5", Path.Combine(sdk, "Roslyn", "Microsoft.CSharp.Core.targets"));
        string[] arguments = ["/pathmap:" + map];
        capture.Arguments(arguments, TestContext.Current.CancellationToken);
        capture.Command("\"" + host + "\" exec \"" + Path.Combine(sdk, "Roslyn", "bincore", "csc.dll") + "\" " +
            string.Join(" ", arguments.Select(argument => "\"" + argument + "\"")), TestContext.Current.CancellationToken);
        capture.Close();
        // Same-task protocol DATA for real emitted Roslyn artifacts; this is not
        // an installed-SDK or process conformance claim.
        return observed with { InvocationRecipe = capture.Finish(), CommandLineArguments = arguments };
    }

    private static (ProjectAnalysisContext, CompilerObservation) Valid(TestDirectory fixture, string name = "App", string source = "public class Code { public int M() => 1; }", string? pathMap = null,
        IReadOnlyList<(string LogicalPath, string Text)>? documents = null)
    {
        var project = fixture.Write(name + "/" + name + ".csproj", "<Project />");
        documents ??= [("Code.cs", source)];
        var physical = documents.Select(document => (document.LogicalPath, document.Text,
            Path: fixture.Write(name + "/" + document.LogicalPath, document.Text))).ToArray();
        var trees = physical.Select(document => CSharpSyntaxTree.ParseText(SourceText.From(document.Text, new UTF8Encoding(false), SourceHashAlgorithm.Sha256),
            new CSharpParseOptions(LanguageVersion.CSharp14, preprocessorSymbols: ["BOUND"]), document.Path)).ToArray();
        var compilation = CSharpCompilation.Create(name, trees, [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug, deterministic: true)
            .WithSourceReferenceResolver(pathMap is null ? null : new SourceFileResolver([], fixture.Path,
                CSharpCommandLineParser.Default.Parse(["/pathmap:" + pathMap], fixture.Path, sdkDirectory: null).PathMap)));
        using var pe = new MemoryStream(); using var pdb = new MemoryStream();
        var emitted = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: name + ".pdb"));
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var assembly = fixture.Write(name + "/obj/" + name + ".dll", ""); File.WriteAllBytes(assembly, pe.ToArray());
        var symbols = fixture.Write(name + "/obj/" + name + ".pdb", ""); File.WriteAllBytes(symbols, pdb.ToArray());
        var target = fixture.Write(name + "/bin/" + name + ".dll", ""); File.WriteAllBytes(target, pe.ToArray());
        var targetPdb = fixture.Write(name + "/bin/" + name + ".pdb", ""); File.WriteAllBytes(targetPdb, pdb.ToArray());
        var context = ProjectAnalysisContext.Create(name + ".csproj", name, "net10.0", "Release", "AnyCPU", LanguageVersion.CSharp14, ["BOUND"], SourceCodeKind.Regular, [], [], new(false, false), new("sdk", "msbuild", "roslyn")) with
        {
            ResolvedProjectPath = project,
            EvaluatedTargetPath = target,
            EvaluatedIntermediateDirectory = Path.GetDirectoryName(assembly),
            CompilerDocuments = physical.Select(document => new ProjectSourceIdentity(document.LogicalPath, document.LogicalPath,
                ProjectAnalysisContext.ContentHash(File.ReadAllBytes(document.Path)), false, false)
            { ResolvedPath = document.Path }).ToArray(),
            PhaseInputs = [new(project, "project", false), .. physical.Select(document => new ProjectPhaseInput(document.Path, "source", false))]
        };
        // Real Roslyn artifacts; supplied parameters here are protocol mutations, not an actual SDK Csc event.
        var observed = new CompilerObservation(project, [new("Sources", physical.Select(document => document.Path).ToArray()), new("OutputAssembly", [assembly])])
        { Assembly = Capture(assembly), Pdb = Capture(symbols) };
        return (context, pathMap is null ? observed : WithMapObservation(observed, fixture.Path, pathMap));
    }

    private static CompilerOutput Capture(string path) => new(path, ProjectAnalysisContext.ContentHash(File.ReadAllBytes(path)), new FileInfo(path).Length);
}
