namespace Mahjong.Advisor;

/// <summary>What an opponent's discards suggest they are collecting.</summary>
public sealed record DiscardRead(
    bool ChasingTerminals,
    int MissingSuit,
    double Confidence)
{
    public static readonly DiscardRead None = new(false, -2, 0.0);
}

/// <summary>
/// Reads what an opponent is building from what they throw away.
///
/// The ordinary danger table assumes a normal hand, where terminals and honours
/// are the safest tiles on the board. Two common hands invert that completely:
///
/// Thirteen orphans needs every terminal and honour, so its owner discards
/// nothing but simples — and the tiles the table calls safest are exactly the
/// ones they are waiting on. That is a yakuman, and feeding one costs more than
/// every other mistake in a game combined.
///
/// A one-suit hand is the same idea in miniature: they shed two suits early and
/// then everything in the third, plus honours, is live.
/// </summary>
public static class DiscardReader
{
    /// <summary>Discards needed before a pattern means anything.</summary>
    public const int MinimumDiscards = 7;

    public static DiscardRead Read(IReadOnlyList<int> discards)
    {
        ArgumentNullException.ThrowIfNull(discards);

        if (discards.Count < MinimumDiscards)
            return DiscardRead.None;

        int terminals = discards.Count(t => Tiles.IsTerminal(t));
        int simples = discards.Count - terminals;

        // Someone who has thrown a long run of simples and almost no terminals
        // is either collecting them or has a hand that cannot use them.
        bool chasingTerminals = terminals <= 1 && simples >= MinimumDiscards;
        double terminalConfidence = chasingTerminals
            ? Math.Min(1.0, (simples - MinimumDiscards + 2) / 6.0)
            : 0.0;

        int missingSuit = FindAbandonedSuit(discards);
        double suitConfidence = missingSuit >= 0 ? 0.6 : 0.0;

        if (!chasingTerminals && missingSuit < 0)
            return DiscardRead.None;

        return new DiscardRead(
            chasingTerminals,
            missingSuit,
            Math.Max(terminalConfidence, suitConfidence));
    }

    /// <summary>
    /// The suit they have kept, if they have clearly shed the other two. Returns
    /// -1 when no single suit stands out.
    /// </summary>
    private static int FindAbandonedSuit(IReadOnlyList<int> discards)
    {
        var perSuit = new int[3];
        foreach (int tile in discards)
        {
            if (!Tiles.IsHonor(tile))
                perSuit[tile / 9]++;
        }

        int suited = perSuit.Sum();
        if (suited < MinimumDiscards)
            return -1;

        // A hand going for one suit throws the other two away, so the kept suit
        // is barely represented in the pond.
        for (int suit = 0; suit < 3; suit++)
        {
            int others = suited - perSuit[suit];
            if (perSuit[suit] <= 1 && others >= 6)
                return suit;
        }

        return -1;
    }

    /// <summary>
    /// How much more dangerous a tile is against this read. 1.0 leaves the
    /// ordinary estimate alone.
    /// </summary>
    public static double DangerMultiplier(int tile, DiscardRead read)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (read.Confidence <= 0)
            return 1.0;

        double multiplier = 1.0;

        if (read.ChasingTerminals && Tiles.IsTerminal(tile))
        {
            // Feeding a thirteen orphans is the single most expensive error
            // available, so err heavily on the side of not doing it.
            multiplier *= 1.0 + (5.0 * read.Confidence);
        }

        if (read.MissingSuit >= 0)
        {
            bool inKeptSuit = !Tiles.IsHonor(tile) && tile / 9 == read.MissingSuit;
            if (inKeptSuit || Tiles.IsHonor(tile))
                multiplier *= 1.0 + (1.5 * read.Confidence);
        }

        return multiplier;
    }

    public static string Describe(DiscardRead read)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (read.ChasingTerminals)
            return "discarding only simples - may be collecting terminals and honours";
        if (read.MissingSuit >= 0)
            return $"has shed everything but {SuitName(read.MissingSuit)} - likely a one-suit hand";
        return "";
    }

    private static string SuitName(int suit) => suit switch
    {
        0 => "Characters",
        1 => "Dots",
        _ => "Bamboo",
    };
}
