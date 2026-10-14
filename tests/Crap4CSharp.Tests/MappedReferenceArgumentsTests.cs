using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class MappedReferenceArgumentsTests
{
    [Fact]
    public void CancelledSubstitutionDoesNotParseOrReturnEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MappedReferenceArguments.RemoveOwned(
            ["/reference:missing.dll"], Path.GetTempPath(), new HashSet<string>(), cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactOwnedReferencePreservesAliasesAndInteropWhileOtherInputsStayVerbatim(bool embedded)
    {
        using var fixture = TestDirectory.Create("crap4csharp-mapped-arguments");
        var mapped = Path.Combine(fixture.Path, "mapped.dll");
        var unowned = Path.Combine(fixture.Path, "unowned missing.dll");
        var mappedArgument = $"/{(embedded ? "link" : "reference")}:Mapped=\"{mapped}\"";
        var otherArgument = $"/reference:\"{unowned}\"";
        var arguments = new[] { "/define:LEFT", mappedArgument, otherArgument, "Code.cs" };
        var result = MappedReferenceArguments.RemoveOwned(arguments, fixture.Path, new HashSet<string> { mapped }, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "/define:LEFT", otherArgument, "Code.cs" }, result.Arguments);
        var properties = Assert.Single(result.References).Value;
        Assert.Equal(new[] { "Mapped" }, properties.Aliases);
        Assert.Equal(embedded, properties.EmbedInteropTypes);
        Assert.False(File.Exists(mapped));
        Assert.False(File.Exists(unowned));
    }

    [Theory]
    [InlineData("mixed")]
    [InlineData("duplicated")]
    [InlineData("absent")]
    public void AmbiguousOrMissingOwnedArgumentMembershipRefuses(string mode)
    {
        using var fixture = TestDirectory.Create("crap4csharp-mapped-argument-refusal");
        var mapped = Path.Combine(fixture.Path, "mapped.dll");
        var unowned = Path.Combine(fixture.Path, "unowned.dll");
        string[] arguments = mode switch
        {
            "mixed" => [$"/reference:\"{mapped}\",\"{unowned}\""],
            "duplicated" => [$"/reference:\"{mapped}\"", $"/reference:\"{mapped}\""],
            _ => [$"/reference:\"{unowned}\""]
        };
        Assert.Throws<InvalidDataException>(() => MappedReferenceArguments.RemoveOwned(arguments,
            fixture.Path, new HashSet<string> { mapped }, TestContext.Current.CancellationToken));
    }
}
