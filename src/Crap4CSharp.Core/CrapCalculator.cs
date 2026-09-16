namespace Crap4CSharp.Core;

public static class CrapCalculator
{
    public static double Calculate(int complexity, double coverage)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(complexity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(coverage, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(coverage, 1);
        return complexity * complexity * Math.Pow(1 - coverage, 3) + complexity;
    }
}
