namespace Fixture;

public sealed class CallableSamples(int seed)
{
    private EventHandler? changed;

    public int Value { get; init; } = seed > 0 ? seed : 0;

    public int Doubled
    {
        get => Value * 2;
        set => _ = value > 0 ? value : 0;
    }

    public event EventHandler Changed
    {
        add => changed += value;
        remove => changed -= value;
    }

    public async Task<int> TaskValueAsync(int value)
    {
        await Task.Yield();
        return value > 0 ? value : 0;
    }

    public async ValueTask<int> ValueTaskValueAsync(int value)
    {
        await Task.Yield();
        return value > 0 ? value : 0;
    }

    public IEnumerable<int> Values(int count)
    {
        for (var index = 0; index < count; index++) yield return index;
    }

    public async IAsyncEnumerable<int> ValuesAsync(int count)
    {
        for (var index = 0; index < count; index++)
        {
            await Task.Yield();
            yield return index;
        }
    }

    public T Identity<T>(T value) => value;
    public T Identity<T>(T value, T fallback) => value is null ? fallback : value;

    public int Nested(int value)
    {
        int Capturing(int item) => item > seed ? item : seed;
        static int StaticLocal(int item) => item > 0 ? item : 0;
        Func<int, int> lambda = item => item > 0 ? item : 0;
        Func<int, int> staticLambda = static item => item > 0 ? item : 0;
        return Capturing(value) + StaticLocal(value) + lambda(value) + staticLambda(value);
    }
}
