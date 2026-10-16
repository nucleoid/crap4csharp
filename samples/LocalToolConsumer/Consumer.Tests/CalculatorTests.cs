using SampleConsumer;
using Xunit;

public sealed class CalculatorTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void ClassifiesBothBranches(int value, bool expected)
        => Assert.Equal(expected, Calculator.IsNonNegative(value));
}
