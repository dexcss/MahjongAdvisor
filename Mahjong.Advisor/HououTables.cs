namespace Mahjong.Advisor;

/// <summary>
/// Win rates and opponent readiness measured from roughly 1.2 million games in
/// Tenhou's top lobby, rather than derived from a probability model.
///
/// The binomial estimate this replaces treated every draw as independent and
/// interchangeable, which is not how a hand behaves — it ignores that a wide
/// one-shanten converts far better than a narrow one, and that late turns are
/// worse than the arithmetic suggests.
///
/// Source: chienshyong/houou-statistics, "Projected Winrate", "Shanten &amp;
/// Width" and "Open Tenpai" (forked from Euophrys/houou-analysis).
/// </summary>
public static class HououTables
{
    /// <summary>Turns the tables are sampled at.</summary>
    private static readonly int[] Turns = [4, 6, 8, 10, 12];

    /// <summary>
    /// Ukeire bands separating a narrow hand from a wide one, per shanten.
    /// Below the first number is "kanchan", between is "perfect", above is
    /// "sticky". Tenpai splits at a 5-tile wait.
    /// </summary>
    private static readonly (int Narrow, int Wide)[] WidthBands =
    [
        (5, 9),    // tenpai: bad < 5, good 5-8, very good 9+
        (16, 24),  // 1-shanten
        (24, 40),  // 2-shanten
        (32, 52),  // 3-shanten
    ];

    // Rows: tenpai good, tenpai bad, 1-sticky, 1-perfect, 1-kanchan,
    //       2-sticky, 2-perfect, 2-kanchan, 3-sticky, 3-perfect, 3-kanchan, 4+.
    private static readonly double[][] ClosedWinRate =
    [
        [0.6687, 0.6660, 0.5831, 0.5236, 0.4932],
        [0.5181, 0.4406, 0.3812, 0.3260, 0.2890],
        [0.4433, 0.3710, 0.2960, 0.2147, 0.1618],
        [0.3922, 0.3173, 0.2531, 0.1927, 0.1404],
        [0.2950, 0.2207, 0.1673, 0.1247, 0.0808],
        [0.2459, 0.1752, 0.1242, 0.0777, 0.0287],
        [0.2060, 0.1498, 0.1058, 0.0600, 0.0314],
        [0.1552, 0.1051, 0.0640, 0.0373, 0.0193],
        [0.1365, 0.0809, 0.0467, 0.0153, 0.0021],
        [0.1061, 0.0642, 0.0280, 0.0092, 0.0010],
        [0.0740, 0.0404, 0.0225, 0.0153, 0.0010],
        [0.0639, 0.0321, 0.0155, 0.0063, 0.0005],
    ];

    private static readonly double[][] OpenWinRate =
    [
        [0.7151, 0.6542, 0.5914, 0.5178, 0.4518],
        [0.5517, 0.5016, 0.4186, 0.3675, 0.3022],
        [0.4798, 0.3920, 0.3178, 0.2269, 0.1385],
        [0.4487, 0.3753, 0.2850, 0.2356, 0.1742],
        [0.3646, 0.2799, 0.2242, 0.1741, 0.1059],
        [0.2964, 0.2208, 0.1536, 0.0799, 0.0421],
        [0.2615, 0.1891, 0.1453, 0.0981, 0.0493],
        [0.2275, 0.1733, 0.1243, 0.0707, 0.0258],
        [0.1756, 0.1305, 0.0353, 0.0125, 0.0192],
        [0.1252, 0.0612, 0.0500, 0.0633, 0.0050],
        [0.1217, 0.0659, 0.0377, 0.0588, 0.0050],
        [0.0845, 0.0495, 0.0100, 0.0050, 0.0010],
    ];

    /// <summary>Chance a closed opponent with no calls is quietly tenpai.</summary>
    private static readonly (int Turn, double Chance)[] DamaTenpai =
        [(4, 0.0063), (8, 0.0446), (12, 0.1176), (16, 0.1946)];

    /// <summary>Chance an opponent is tenpai, by how many melds they have called.</summary>
    private static readonly (int Turn, double OneCall, double TwoCalls, double ThreeCalls)[] OpenTenpai =
    [
        (4, 0.0699, 0.2764, 0.7690),
        (8, 0.2676, 0.5328, 0.8848),
        (12, 0.4578, 0.6904, 0.9192),
        (16, 0.5522, 0.7341, 0.9056),
    ];

    /// <summary>Measured chance of completing this hand. Turn 1 is the first discard.</summary>
    public static double WinRate(int shanten, int ukeire, int turn, bool closed)
    {
        if (shanten < 0)
            return 1.0;

        // No live tile can improve this hand, so it cannot be completed. The old
        // fallthrough picked a normal row and gave a dead hand a real win rate.
        if (ukeire <= 0)
            return 0.0;

        shanten = Math.Min(shanten, 4);

        int row = RowFor(shanten, ukeire);
        var table = closed ? ClosedWinRate : OpenWinRate;
        return Interpolate(table[row], turn);
    }

    /// <summary>Measured chance an opponent is ready, given their calls and the turn.</summary>
    public static double TenpaiChance(int melds, int turn)
    {
        if (melds <= 0)
            return InterpolatePairs(DamaTenpai, turn);

        var points = OpenTenpai
            .Select(p => (p.Turn, Chance: melds switch
            {
                1 => p.OneCall,
                2 => p.TwoCalls,
                _ => p.ThreeCalls,
            }))
            .ToArray();

        return InterpolatePairs(points, turn);
    }

    /// <summary>Which row of the table this hand shape falls in.</summary>
    private static int RowFor(int shanten, int ukeire)
    {
        if (shanten >= 4)
            return 11;

        var (narrow, wide) = WidthBands[shanten];

        // Tenpai rows are ordered good then bad; the others narrow-first.
        if (shanten == 0)
            return ukeire >= narrow ? 0 : 1;

        int band = ukeire < narrow ? 2 : ukeire < wide ? 1 : 0;
        return 2 + ((shanten - 1) * 3) + band;
    }

    private static double Interpolate(double[] row, int turn)
    {
        if (turn <= Turns[0])
            return row[0];
        if (turn >= Turns[^1])
            return row[^1];

        for (int i = 1; i < Turns.Length; i++)
        {
            if (turn > Turns[i])
                continue;
            double span = Turns[i] - Turns[i - 1];
            double t = (turn - Turns[i - 1]) / span;
            return row[i - 1] + ((row[i] - row[i - 1]) * t);
        }

        return row[^1];
    }

    private static double InterpolatePairs((int Turn, double Chance)[] points, int turn)
    {
        if (turn <= points[0].Turn)
            return points[0].Chance;
        if (turn >= points[^1].Turn)
            return points[^1].Chance;

        for (int i = 1; i < points.Length; i++)
        {
            if (turn > points[i].Turn)
                continue;
            double span = points[i].Turn - points[i - 1].Turn;
            double t = (turn - points[i - 1].Turn) / span;
            return points[i - 1].Chance + ((points[i].Chance - points[i - 1].Chance) * t);
        }

        return points[^1].Chance;
    }

    /// <summary>Turn number from the wall, where turn 1 is the first discard.</summary>
    public static int TurnFromWall(int wallRemaining) =>
        Math.Clamp((70 - wallRemaining) / 4, 1, 18);
}
