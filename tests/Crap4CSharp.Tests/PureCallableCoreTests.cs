using System.Collections.Immutable;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class PureCallableCoreTests
{
    [Fact]
    public void CapturedByteApisDoNotReadWriteOrDiscoverFilesystemArtifacts()
    {
        using var temporary = TestDirectory.Create("crap4csharp-pure-core");
        var logicalSource = Path.Combine(temporary.Path, "never-created.cs");
        var inventory = CallableInventory.Analyze("class C { async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.Yield(); } }",
            logicalSource, new CallableAnalysisContext("App.csproj", "net10.0", "Debug", "AnyCPU", "ctx",
                CSharpParseOptions.Default));
        var callable = Assert.Single(inventory.Callables);
        var binding = new PortablePdbBinding("ctx", Guid.Empty, "missing",
            new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal)
            { [logicalSource] = ImmutableArray.Create<byte>(1, 2, 3) });

        var mapping = PortablePdbCallableMapper.Map(callable, [], [], binding);
        var coverage = CallableCoverageResolver.Resolve(inventory, []);
        _ = CallableFamilyEvaluator.Evaluate(inventory, coverage.Observations, 8);

        Assert.Equal(CoverageReasonCodes.PeUnavailable, mapping.Reason);
        Assert.False(File.Exists(logicalSource));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }
}
