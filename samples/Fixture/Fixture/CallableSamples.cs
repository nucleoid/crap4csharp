namespace Fixture;

public interface IProbe
{
    int Read();
}

public sealed class CallableSamples(int seed) : IProbe
{
    private EventHandler? changed;

    public CallableSamples() : this(0) { }

    public int Value { get; init; } = seed > 0 ? seed : 0;

    public int Doubled
    {
        get => Value * 2;
        set => _ = value > 0 ? value : 0;
    }

    public int this[int index]
    {
        get => Value + index;
        set => _ = value - index;
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

    int IProbe.Read() => Value;

    public static CallableSamples operator +(CallableSamples left, CallableSamples right) =>
        new(left.Value + right.Value);

    public static explicit operator int(CallableSamples value) => value.Value;

    public sealed class Box<T>
    {
        public T Echo(T value) => value;
    }

    public int ArrayLength(int[] values) => values.Length;
    public int Sum(System.Collections.Generic.List<int> values) => values.Sum();
    public string Describe(int value) => $"int:{value}";
    public string Describe(string value) => $"string:{value}";

    public void BlockBody()
    {
        changed = null;
    }

    public void Empty() { }
    public int Zero() => 0;
    static CallableSamples() { }
}
