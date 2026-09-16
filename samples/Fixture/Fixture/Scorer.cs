namespace Fixture;

public static class Scorer
{
    public static int Score(int value)
    {
        var score = 0;
        if (value > 0) score++;
        if (value > 10) score++;
        if (value > 100) score++;
        return score;
    }

    public static int Identity(int value) => value;
}
