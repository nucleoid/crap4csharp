using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class PortablePdbMappingTests
{
    private const string Source = """
        using System.Collections.Generic;
        using System.Threading.Tasks;
        class C {
          public async Task<int> Async(int x) { await Task.Yield(); return x; }
          public IEnumerable<int> Iterator(int x) { yield return x; }
        }
        """;

    [Fact]
    public void RealPortablePdbAuthoritativelyLinksAsyncAndIteratorStateMachines()
    {
        var artifacts = Compile(Source);
        var inventory = Inventory(Source);
        var binding = Binding(artifacts, Encoding.UTF8.GetBytes(Source));

        foreach (var callable in inventory.Callables.Where(item => item.Name is "Async" or "Iterator"))
        {
            var result = PortablePdbCallableMapper.Map(callable, artifacts.Pe, artifacts.Pdb, binding);
            Assert.True(result.Status == "supported", result.Reason);
            Assert.Equal("portablePdbStateMachine", result.EvidenceKind);
            Assert.NotNull(result.KickoffMethodToken);
            Assert.NotNull(result.GeneratedMethodToken);
        }
    }

    [Fact]
    public void WrongMvidPdbIdentityOrSourceChecksumFailsClosed()
    {
        var artifacts = Compile(Source);
        var callable = Inventory(Source).Callables.Single(item => item.Name == "Async");
        var valid = Binding(artifacts, Encoding.UTF8.GetBytes(Source));

        Assert.Equal(CoverageReasonCodes.ContextMismatch,
            PortablePdbCallableMapper.Map(callable, artifacts.Pe, artifacts.Pdb,
                valid with { ExpectedMvid = Guid.NewGuid() }).Reason);
        Assert.Equal("coverage.pdbIdentityMismatch",
            PortablePdbCallableMapper.Map(callable, artifacts.Pe, artifacts.Pdb,
                valid with { ExpectedPortablePdbId = Convert.ToHexString(new byte[20]).ToLowerInvariant() }).Reason);
        Assert.Equal("coverage.sourceChecksumMismatch",
            PortablePdbCallableMapper.Map(callable, artifacts.Pe, artifacts.Pdb,
                valid with { SourceDocuments = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal)
                { ["Fixture.cs"] = ImmutableArray.Create(Encoding.UTF8.GetBytes(Source + " ")) } }).Reason);
        Assert.Equal("coverage.sourceChecksumMismatch",
            PortablePdbCallableMapper.Map(callable, artifacts.Pe, artifacts.Pdb,
                valid with { SourceDocuments = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal)
                { ["other/Fixture.cs"] = ImmutableArray.Create(Encoding.UTF8.GetBytes(Source)) } }).Reason);
    }

    [Fact]
    public void MissingOrMalformedPdbAndUnsupportedNestedCallableNeverUseNames()
    {
        var artifacts = Compile(Source);
        var inventory = Inventory(Source + "\nclass D { void M() { System.Func<int,int> f = x => x; } }");
        var asyncCallable = inventory.Callables.Single(item => item.Name == "Async");
        var lambda = inventory.Callables.Single(item => item.Kind == CallableKind.Lambda);
        var binding = Binding(artifacts, Encoding.UTF8.GetBytes(Source));

        Assert.Equal("coverage.pdbUnavailable", PortablePdbCallableMapper.Map(asyncCallable, artifacts.Pe, [], binding).Reason);
        Assert.Equal("coverage.pdbMalformed", PortablePdbCallableMapper.Map(asyncCallable, artifacts.Pe, [1, 2, 3], binding).Reason);
        Assert.Equal("coverage.peMalformed", PortablePdbCallableMapper.Map(asyncCallable, [1, 2, 3], artifacts.Pdb, binding).Reason);
        Assert.Equal(CoverageReasonCodes.UnsupportedGeneratedMapping,
            PortablePdbCallableMapper.Map(lambda, artifacts.Pe, artifacts.Pdb, binding).Reason);
    }

    [Fact]
    public void LineRemappingCannotEscapeExactGeneratedDocumentOwnership()
    {
        const string remapped = """
            using System.Threading.Tasks;
            class C {
              public async Task<int> Async(int x) {
            #line 100 "Mapped.cs"
                await Task.Yield(); return x;
            #line default
              }
            }
            """;
        var artifacts = Compile(remapped);
        var callable = Assert.Single(Inventory(remapped).Callables);
        var result = PortablePdbCallableMapper.Map(callable, artifacts.Pe, artifacts.Pdb,
            Binding(artifacts, Encoding.UTF8.GetBytes(remapped)));

        Assert.Equal(CoverageReasonCodes.UnsupportedMultiDocumentMapping, result.Reason);
    }

    private static CallableInventoryResult Inventory(string source) => CallableInventory.Analyze(source, "Fixture.cs",
        new CallableAnalysisContext("Fixture.csproj", "net10.0", "Debug", "AnyCPU", "ctx", CSharpParseOptions.Default));

    private static PortablePdbBinding Binding(Artifacts artifacts, byte[] source)
    {
        using var peReader = new PEReader(new MemoryStream(artifacts.Pe.ToArray(), writable: false));
        var metadata = peReader.GetMetadataReader();
        var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(artifacts.Pdb.ToArray(), writable: false));
        var pdbId = Convert.ToHexString(provider.GetMetadataReader().DebugMetadataHeader!.Id.ToArray()).ToLowerInvariant();
        return new PortablePdbBinding("ctx", mvid, pdbId,
            new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal)
            { ["Fixture.cs"] = ImmutableArray.Create(source) });
    }

    private static Artifacts Compile(string source)
    {
        var sourceText = SourceText.From(source, new UTF8Encoding(false), SourceHashAlgorithm.Sha256);
        var tree = CSharpSyntaxTree.ParseText(sourceText, path: "Fixture.cs");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Fixture", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var emitted = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb,
            pdbFilePath: "Fixture.pdb"));
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return new(ImmutableArray.Create(pe.ToArray()), ImmutableArray.Create(pdb.ToArray()));
    }

    private sealed record Artifacts(ImmutableArray<byte> Pe, ImmutableArray<byte> Pdb);
}
