namespace Fixture;
public sealed class Multi
{
#if NET10_0
    public int Net10() => 10;
#else
    public int NetStandard() => 21;
#endif
}
