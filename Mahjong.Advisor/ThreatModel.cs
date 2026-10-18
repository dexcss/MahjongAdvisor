namespace Mahjong.Advisor;

/// <summary>What one opponent looks like right now.</summary>
public readonly record struct SeatThreat(
    int Seat,
    double TenpaiProbability,
    double ExpectedHandValue,
    bool Riichi)
{
    public bool Meaningful => TenpaiProbability >= 0.2;
}

/// <summary>
/// Estimates how likely each opponent is to be ready, and what it would cost to
/// feed them.
///
/// The important part is that a player who has NOT declared riichi can still be
/// waiting. Silent tenpai and open hands are dangerous, and treating them as
/// harmless — which is what scoring only against declared riichi does — leaves
/// the majority of deal-ins unaccounted for.
///
/// The numbers are measured from top-lobby play rather than fitted: a quiet
/// closed hand is only about 4% likely to be ready on turn 8, while two melds
/// on the same turn is 53% and three is 88%. Calls are by far the strongest
/// public signal, which a curve based only on turn count cannot express.
/// </summary>
public sealed class ThreatModel
{
    /// <summary>
    /// Readiness floor used when the reader cannot see called melds. Treating an
    /// unknown meld count as zero is the most optimistic possible reading and
    /// makes every opponent look safer than they are. Blending in a fraction of
    /// the one-call rate keeps that honest without assuming every silent player
    /// has called.
    /// </summary>
    public double UnknownMeldPrior { get; init; } = 0.5;

    /// <summary>
    /// What an average opponent hand pays when you feed it.
    ///
    /// The simulator marginally prefers 2500 (254 against 220), but that is below
    /// what hands actually paid in play -- 3900, 7700, 8000, 12000 -- and the gap
    /// is small enough to be the simulator's own under-pricing of risk rather than
    /// a real finding. Left at the honest number.
    /// </summary>
    public double BaseHandValue { get; init; } = 4000;

    /// <summary>Riichi hands are worth more: the yaku, the stick, and the hidden dora.</summary>
    public double RiichiHandValueBonus { get; init; } = 1000;

    /// <summary>Each dora visible in an opponent's melds is another han for them.</summary>
    public double PerVisibleDora { get; init; } = 1500;

    /// <summary>
    /// Readiness credited to a silent seat as the wall empties, regardless of
    /// evidence. The measured damaten curve tops out at 19% on turn 16, which
    /// comes from human lobbies; on these NPC tables silent hands complete big
    /// and often - three mangan-or-better completions in one match were all
    /// silent. Without a floor a quiet table reads as nearly harmless: topThreat
    /// never crosses the concrete-threat level, the late-wall fold never arms,
    /// and live non-suji tiles get pushed one-away at wall 20 into damaten.
    /// The floor stays at zero until wall 32, then rises to a concrete threat
    /// (0.7) at wall 16, where the existing late-wall rules take over.
    /// </summary>
    public static double SilentReadyFloor(int wallRemaining)
    {
        if (wallRemaining >= 32)
            return 0.0;
        if (wallRemaining <= 16)
            return ConcreteFloor;

        return ConcreteFloor * (32 - wallRemaining) / 16.0;
    }

    /// <summary>Matches DiscardAdvisor.ConcreteThreatLevel so the late-wall rules arm.</summary>
    public const double ConcreteFloor = 0.7;

    /// <summary>Seats 1-3, i.e. everyone but us.</summary>
    public List<SeatThreat> Evaluate(TableState table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var threats = new List<SeatThreat>(3);
        int turn = HououTables.TurnFromWall(table.WallRemaining);
        bool meldsKnown = table.Observations.HasFlag(Observed.OpponentMelds);
        double silentFloor = SilentReadyFloor(table.WallRemaining);

        var doraTiles = new HashSet<int>();
        foreach (int indicator in table.DoraIndicators)
            doraTiles.Add(Tiles.DoraFromIndicator(indicator));

        for (int seat = 1; seat < table.Seats.Length; seat++)
        {
            var view = table.Seats[seat];

            // Measured readiness beats a fitted curve: a player with two melds on
            // turn 8 is 53% likely tenpai, while a quiet closed hand is only 4%.
            // Without meld data, use the quiet-hand rate as a floor and add a
            // fraction of the one-call rate, rather than pretending nobody called.
            // Late in the hand the silent-ready floor takes over: no signal is
            // not evidence of safety.
            double measured = view.Riichi
                ? 1.0
                : meldsKnown
                    ? HououTables.TenpaiChance(view.MeldCount, turn)
                    : Math.Max(
                        HououTables.TenpaiChance(0, turn),
                        UnknownMeldPrior * HououTables.TenpaiChance(1, turn));
            double probability = view.Riichi
                ? 1.0
                : Math.Max(measured, silentFloor);

            int visibleDora = view.MeldTiles.Count(doraTiles.Contains);
            double value = BaseHandValue
                + (view.Riichi ? RiichiHandValueBonus : 0)
                + (PerVisibleDora * visibleDora);

            threats.Add(new SeatThreat(seat, probability, value, view.Riichi));
        }

        return threats;
    }
}
