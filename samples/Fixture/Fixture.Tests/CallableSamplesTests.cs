namespace Fixture.Tests;

using Xunit;

public sealed class CallableSamplesTests
{
    [Fact]
    public async Task ExercisesAuthoredCallableShapes()
    {
        var sample = new CallableSamples(1) { Value = 2 };
        EventHandler handler = (_, _) => { };
        sample.Changed += handler;
        sample.Changed -= handler;

        Assert.Equal(4, sample.Doubled);
        sample.Doubled = -1;
        Assert.Equal(2, await sample.TaskValueAsync(2));
        Assert.Equal(2, await sample.ValueTaskValueAsync(2));
        Assert.Equal([0, 1], sample.Values(2));
        var asyncValues = new List<int>();
        await foreach (var value in sample.ValuesAsync(2)) asyncValues.Add(value);
        Assert.Equal([0, 1], asyncValues);
        Assert.Equal(2, sample.Identity(2));
        Assert.Equal(2, sample.Identity(2, 1));
        Assert.Equal(8, sample.Nested(2));
    }
}
