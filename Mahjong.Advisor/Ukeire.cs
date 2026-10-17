namespace Mahjong.Advisor;

public readonly record struct AcceptedTile(int Tile, int Live);

public sealed record UkeireResult(int Shanten, IReadOnlyList<AcceptedTile> Accepted)
{
    /// <summary>Total live tiles that reduce shanten. The headline efficiency number.</summary>
    public int Width => Accepted.Sum(a => a.Live);
}

public static class UkeireCalculator
{
    /// <summary>
    /// Which draws reduce shanten, and how many of each remain unseen.
    /// </summary>
    /// <param name="hand">Concealed hand, 3n+1 tiles.</param>
    /// <param name="visible">
    /// Every tile seen anywhere from this seat: own hand, all four discard ponds,
    /// all open melds, and dora indicators. Own hand must be included.
    /// </param>
    /// <param name="calledMelds">Number of melds already called from this hand.</param>
    public static UkeireResult Calculate(int[] hand, int[] visible, int calledMelds = 0)
    {
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(visible);

        int baseShanten = Shanten.Calculate(hand, calledMelds);
        var accepted = new List<AcceptedTile>();
        var work = (int[])hand.Clone();

        for (int t = 0; t < Tiles.Kinds; t++)
        {
            if (work[t] >= 4)
                continue;

            int live = 4 - visible[t];
            if (live <= 0)
                continue;

            work[t]++;
            bool improves = Shanten.Calculate(work, calledMelds) < baseShanten;
            work[t]--;

            if (improves)
                accepted.Add(new AcceptedTile(t, live));
        }

        return new UkeireResult(baseShanten, accepted);
    }

    /// <summary>
    /// Convenience: builds a visible array from the hand alone. Only correct
    /// before any discards are on the table -- prefer passing a real table view.
    /// </summary>
    public static int[] VisibleFromHandOnly(int[] hand)
    {
        ArgumentNullException.ThrowIfNull(hand);
        return (int[])hand.Clone();
    }
}
