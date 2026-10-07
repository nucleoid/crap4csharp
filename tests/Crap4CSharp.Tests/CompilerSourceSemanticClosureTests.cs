using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerSourceSemanticClosureTests
{
    [Fact]
    public void RetainedNonPdbAliasAndTypeTreesSupplySemanticsWithoutBecomingScoredInventory()
    {
        var options = CSharpParseOptions.Default;
        var context = new CallableAnalysisContext("Library.csproj", "net10.0", "Debug", "AnyCPU", "ctx", options);
        var auxiliary = CSharpSyntaxTree.ParseText("global using Alias = Models.Result; namespace Models { public sealed class Result {} }", options, "Globals.cs", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var inventory = CallableInventory.Analyze("public class Library { public static Alias Get(Alias value) => value; }", "Code.cs", context,
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], [auxiliary]);
        var callable = Assert.Single(inventory.Callables, c => c.Applicability == CallableApplicability.Applicable);
        Assert.Equal("Code.cs", callable.Path);
        Assert.Equal("Models.Result", callable.SemanticIdentity!.ReturnType);
        Assert.Equal("Models.Result", Assert.Single(callable.SemanticIdentity.Parameters).Type);
    }
}
