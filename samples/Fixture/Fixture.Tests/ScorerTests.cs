namespace Fixture.Tests;

using Xunit;

public sealed class ScorerTests
{
    [Fact]
    public void ScoresSmallPositiveValue() => Assert.Equal(1, Scorer.Score(1));
}
