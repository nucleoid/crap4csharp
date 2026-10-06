using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class WindowsProcessQuotingTests
{
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("plain", "\"plain\"")]
    [InlineData("space separated", "\"space separated\"")]
    [InlineData("a\\b", "\"a\\b\"")]
    [InlineData("trailing\\", "\"trailing\\\\\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("$(echo injected);π", "\"$(echo injected);π\"")]
    public void NativeCommandLinePreservesDocumentedArgumentBoundaries(string input, string expected) =>
        Assert.Equal(expected, WindowsOwnedProcess.QuoteArgument(input));

    [Fact]
    public void NativeCommandLineRejectsNulRatherThanTruncatingConsumerArguments() =>
        Assert.Throws<ArgumentException>(() => WindowsOwnedProcess.QuoteArgument("prefix\0suffix"));
}
