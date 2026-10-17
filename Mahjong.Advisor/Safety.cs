namespace Mahjong.Advisor;

public enum SafetyReason
{
    Genbutsu,
    NoChance,
    OneChance,
    Suji,
    HonorMostlyDead,
    NonSuji,
    Unknown,
}

public readonly record struct TileRisk(int Tile, double DealInRate, SafetyReason Reason)
{
    /// <summary>Points expected to be lost, already weighted by each opponent's threat.</summary>
    public double ExpectedCost { get; init; }

    public string Explain() => Reason switch
    {
        SafetyReason.Genbutsu => "already in their pond - cannot deal in",
        SafetyReason.NoChance => "no-chance: every tile that could form the wait is visible",
        SafetyReason.OneChance => "one-chance: only one blocker left",
        SafetyReason.Suji => "suji - the matching outer wait is dead",
        SafetyReason.HonorMostlyDead => "honor, most copies already visible",
        SafetyReason.NonSuji => "live non-suji tile",
        _ => "unclassified",
    };
}

/// <summary>
/// Heuristic deal-in estimates against a declared riichi. These are approximate
/// published averages, not a solved model -- good enough to rank discards, not
/// precise enough to quote as probabilities.
/// </summary>
public static class Safety
{
    private static readonly int[] KabeOffsets = [-2, 2];

    public static TileRisk Evaluate(int tile, IReadOnlySet<int> genbutsu, int[] visible)
    {
        ArgumentNullException.ThrowIfNull(genbutsu);
        ArgumentNullException.ThrowIfNull(visible);

        if (genbutsu.Contains(tile))
            return new TileRisk(tile, 0.0, SafetyReason.Genbutsu);

        if (Tiles.IsHonor(tile))
        {
            int seen = visible[tile];
            double rate = seen switch
            {
                >= 3 => 0.008,
                2 => 0.030,
                1 => 0.055,
                _ => 0.070,
            };
            var why = seen >= 2 ? SafetyReason.HonorMostlyDead : SafetyReason.NonSuji;
            return new TileRisk(tile, rate, why);
        }

        int chance = ChanceLevel(tile, visible);
        if (chance == 2)
            return new TileRisk(tile, 0.020, SafetyReason.NoChance);

        bool suji = IsSuji(tile, genbutsu);
        int rank = Tiles.Rank(tile);
        double baseRate = rank switch
        {
            1 or 9 => suji ? 0.035 : 0.052,
            2 or 8 => suji ? 0.045 : 0.085,
            3 or 7 => suji ? 0.050 : 0.105,
            _ => suji ? 0.055 : 0.125,
        };

        if (chance == 1)
        {
            baseRate *= 0.6;
            return new TileRisk(tile, baseRate, SafetyReason.OneChance);
        }

        return new TileRisk(tile, baseRate, suji ? SafetyReason.Suji : SafetyReason.NonSuji);
    }

    /// <summary>
    /// Combined risk across every opponent, weighted by how likely each is to be
    /// waiting. Scoring only against declared riichi treats silent tenpai and open
    /// hands as harmless, which is where most deal-ins actually come from.
    /// </summary>
    public static TileRisk EvaluateAgainstTable(int tile, TableState table, ThreatModel? model = null)
    {
        ArgumentNullException.ThrowIfNull(table);

        var visible = table.BuildVisible();
        var threats = (model ?? new ThreatModel()).Evaluate(table);

        double survives = 1.0;
        double expectedCost = 0.0;
        var worstReason = SafetyReason.Genbutsu;
        double worstSeatRate = 0.0;

        foreach (var threat in threats)
        {
            var seatRisk = Evaluate(tile, table.GenbutsuAgainst(threat.Seat), visible);

            // The published rates assume a normal hand. Someone shedding nothing
            // but simples may be collecting terminals, which turns the safest
            // tiles on the table into the most expensive ones.
            var read = DiscardReader.Read(table.Seats[threat.Seat].Discards);
            double shape = DiscardReader.DangerMultiplier(tile, read);

            // The published rates assume a player who is definitely waiting, so
            // scale them by how likely this one actually is.
            double rate = Math.Min(1.0, seatRisk.DealInRate * threat.TenpaiProbability * shape);

            if (shape > 1.5 && seatRisk.Reason != SafetyReason.Genbutsu)
                worstReason = SafetyReason.NonSuji;

            survives *= 1.0 - rate;
            expectedCost += rate * threat.ExpectedHandValue;

            if (rate > worstSeatRate)
            {
                worstSeatRate = rate;
                worstReason = seatRisk.Reason;
            }
        }

        double combined = 1.0 - survives;
        return new TileRisk(tile, combined, worstReason) { ExpectedCost = expectedCost };
    }

    /// <summary>
    /// A tile is suji when the wait it would complete is already dead. 1-3 need
    /// the tile three higher; 7-9 the tile three lower; 4-6 need both.
    /// </summary>
    public static bool IsSuji(int tile, IReadOnlySet<int> genbutsu)
    {
        ArgumentNullException.ThrowIfNull(genbutsu);
        if (Tiles.IsHonor(tile))
            return false;

        int rank = Tiles.Rank(tile);
        int suitBase = Tiles.SuitBase(tile);

        if (rank <= 3)
            return genbutsu.Contains(suitBase + rank + 3 - 1);
        if (rank >= 7)
            return genbutsu.Contains(suitBase + rank - 3 - 1);

        return genbutsu.Contains(suitBase + rank - 3 - 1)
            && genbutsu.Contains(suitBase + rank + 3 - 1);
    }

    /// <summary>
    /// Wall reading. 2 = no-chance (a needed tile is entirely visible),
    /// 1 = one-chance (a single copy left), 0 = no information.
    /// </summary>
    public static int ChanceLevel(int tile, int[] visible)
    {
        ArgumentNullException.ThrowIfNull(visible);
        if (Tiles.IsHonor(tile))
            return 0;

        int rank = Tiles.Rank(tile);
        int suitBase = Tiles.SuitBase(tile);
        int worst = int.MaxValue;

        foreach (int offset in KabeOffsets)
        {
            int neighbour = rank + offset;
            if (neighbour is < 1 or > 9)
                continue;
            worst = Math.Min(worst, 4 - visible[suitBase + neighbour - 1]);
        }

        if (worst == int.MaxValue)
            return 0;
        if (worst <= 0)
            return 2;
        if (worst == 1)
            return 1;
        return 0;
    }
}
