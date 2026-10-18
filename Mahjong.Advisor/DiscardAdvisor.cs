namespace Mahjong.Advisor;

public sealed record DiscardOption(
    int Tile,
    int ResultingShanten,
    int Ukeire,
    TileRisk Risk,
    double Score,
    string Rationale);

public sealed record Advice(
    IReadOnlyList<DiscardOption> Options,
    bool Folding,
    string Headline)
{
    public DiscardOption? Best => Options.Count > 0 ? Options[0] : null;
}

/// <summary>
/// Ranks discards by expected value: efficiency gain against deal-in cost.
/// Deliberately simple and inspectable -- every number it produces can be
/// traced to shanten, ukeire, or the risk table.
/// </summary>
public sealed class DiscardAdvisor
{
    /// <summary>
    /// Points conceded on a deal-in. Sits above a typical winning hand on purpose:
    /// dealing in also throws away whatever your own hand was worth.
    /// </summary>
    public double AverageDealInCost { get; init; } = 5200;

    /// <summary>
    /// What an open hand with no dora pays. Everything else is scaled up from
    /// here, because a single constant values a 1000-point hand and a mangan
    /// identically -- which is what the advisor used to do.
    /// Fitted against real payouts observed in play.
    /// </summary>
    public double AverageWinValue { get; init; } = 1300;

    /// <summary>Each extra han multiplies the payout by roughly this much.</summary>
    public double HanMultiplier { get; init; } = 1.6;

    /// <summary>
    /// Han a closed hand gains on average: riichi itself, plus its frequent
    /// companions - ippatsu, hidden dora, and self-draw.
    /// </summary>
    public double ClosedHanBonus { get; init; } = 1.8;

    /// <summary>
    /// Swing from being tenpai rather than noten when the wall runs out. You
    /// collect from players who are not ready and pay those who are, so the gap
    /// between the two outcomes is worth roughly this much.
    /// </summary>
    public double NotenPenaltyValue { get; init; } = 1500;

    /// <summary>
    /// How often a hand actually reaches an exhaustive draw. Measured at roughly
    /// one in six: each of four players wins about 21% of hands. Readiness only
    /// pays in that minority, and counting it every time made almost everything
    /// look like a push.
    /// </summary>
    public double ExhaustiveDrawRate { get; init; } = 0.16;

    /// <summary>
    /// Wall count below which readiness starts to matter. Roughly the last four
    /// go-arounds, when an exhaustive draw is a live possibility rather than a
    /// distant one.
    /// </summary>
    public int NotenAppliesBelowWall { get; init; } = 16;  // measured best over 1000 hands

    public Advice Recommend(TableState table)
    {
        ArgumentNullException.ThrowIfNull(table);

        int total = Tiles.Total(table.Hand);
        int expected = 14 - (table.CalledMelds * 3);
        if (total != expected)
        {
            throw new ArgumentException(
                $"Hand has {total} tiles but {expected} were expected for {table.CalledMelds} melds. " +
                "The read layer is mid-transition; do not score this state.",
                nameof(table));
        }

        // Discards alone are enough to judge danger now: a player who has not
        // declared riichi can still be waiting.
        // Riichi freezes the hand from the turn AFTER the declaration. On the
        // declaring turn itself you still choose, but only among tiles that leave
        // the hand ready -- the client greys out anything that breaks tenpai.
        if (table.SelfRiichi && !table.RiichiDeclarationPending)
        {
            // Once the declaration discard is down, the drawn tile is the only
            // legal move. If the read layer could not identify it, say so rather
            // than suggesting a tile the client will refuse.
            return table.DrawnTile is >= 0 and < Tiles.Kinds
                ? ForcedDraw(table)
                : new Advice(
                    [],
                    Folding: false,
                    Headline: "You are in riichi. Discard the tile you just drew - "
                        + "the hand is locked and only that tile is legal.");
        }

        bool safetyTrustworthy = table.Observations.HasFlag(Observed.Discards);
        var threats = Threats.Evaluate(table);
        double topThreat = threats.Count > 0 ? threats.Max(t => t.TenpaiProbability) : 0.0;

        // Standings change how much risk is worth taking, independent of the hand.
        var stance = Placement.ComputeFor(table);
        int turn = HououTables.TurnFromWall(table.WallRemaining);
        bool closedHand = table.CalledMelds == 0;
        double winValue = EstimateWinValue(table) * stance.HandValue;
        bool threat = safetyTrustworthy && topThreat >= 0.2;
        var visible = table.BuildVisible();
        var work = (int[])table.Hand.Clone();
        var options = new List<DiscardOption>();

        for (int t = 0; t < Tiles.Kinds; t++)
        {
            if (work[t] == 0)
                continue;

            // The client greys these out after a call, so an option here is not
            // a bad suggestion, it is an impossible one.
            if (table.ForbiddenDiscards.Contains(t))
                continue;

            work[t]--;
            var uk = UkeireCalculator.Calculate(work, visible, table.CalledMelds);
            work[t]++;

            // Declaring riichi requires staying ready, so anything that breaks
            // tenpai is not a legal declaration discard.
            if (table.RiichiDeclarationPending && uk.Shanten != 0)
                continue;

            var risk = threat
                ? Safety.EvaluateAgainstTable(t, table, Threats)
                : new TileRisk(t, 0.0, SafetyReason.Unknown);

            double win = HououTables.WinRate(uk.Shanten, uk.Width, turn, closedHand) * stance.Speed;

            // Readiness only pays at an exhaustive draw, so it is an endgame
            // consideration. Counting it from turn one made the advisor chase bare
            // tenpai over shapes that actually complete: measured over 5000 hands,
            // weighting it early cut the win rate from 27% to 15% AND left the hand
            // ready less often, which is the opposite of the intent.
            double tenpai = table.WallRemaining <= NotenAppliesBelowWall
                ? TenpaiByDrawProbability(uk.Shanten, uk.Width, table.WallRemaining)
                : 0.0;

            // Compare like with like. The win chance covers the whole hand, so the
            // danger must too: committing to this discard means committing to the
            // ones after it. A single-discard risk against a whole-hand reward is
            // what made it push into riichi three turns running.
            double pushes = PushHorizon(uk.Shanten, table.WallRemaining, MaxPushHorizonOverride);
            double cumulativeRisk = 1.0 - Math.Pow(1.0 - risk.DealInRate, pushes);
            double perDeal = risk.DealInRate > 0 ? ExpectedLoss(risk) / risk.DealInRate : AverageDealInCost;

            double ev = (win * winValue)
                + (tenpai * NotenPenaltyValue * ExhaustiveDrawRate)
                - (cumulativeRisk * perDeal * stance.Danger);

            options.Add(new DiscardOption(
                Tile: t,
                ResultingShanten: uk.Shanten,
                Ukeire: uk.Width,
                Risk: risk,
                Score: ev,
                Rationale: BuildRationale(uk, risk, threat, safetyTrustworthy)));
        }

        bool folding = false;

        // Only a concrete threat justifies abandoning the hand: someone has
        // declared riichi, or the evidence on one seat is genuinely strong.
        // Everyone sitting at a middling readiness is just what mahjong looks
        // like, and folding to it every hand guarantees the noten penalty.
        bool riichiOut = threats.Any(t => t.Riichi);
        bool concreteThreat = riichiOut || topThreat >= ConcreteThreatLevel;

        int reachable = options.Count > 0 ? options.Min(o => o.ResultingShanten) : 9;
        if (concreteThreat && reachable >= FarShantenVeto)
            folding = true;

        // Late wall: with the draw nearly gone, a hand that is still not tenpai
        // cannot win before the exhaustive draw, so the only question is deal-in
        // risk. Pushing a 1-shanten no-riichi hand here dealt 12,000 in a live
        // game (it started folding three discards too late).
        if (!folding
            && concreteThreat
            && safetyTrustworthy
            && table.WallRemaining <= LateWallThreshold
            && reachable >= 1)
        {
            folding = true;
        }

        if (concreteThreat && !folding)
        {
            // Every push is worse than sitting out: fold. The EV already carries
            // placement, hand value and the full cost of continuing.
            folding = options.Count > 0 && options.Max(o => o.Score) <= 0;
        }

        options = folding
            ? options
                .OrderBy(o => o.Risk.DealInRate)
                .ThenBy(o => o.ResultingShanten)
                .ThenByDescending(o => o.Ukeire)
                .ToList()
            : options
                .OrderByDescending(o => o.Score)
                .ThenBy(o => o.ResultingShanten)
                .ThenByDescending(o => o.Ukeire)
                .ToList();

        return new Advice(options, folding, BuildHeadline(
            options, threat, folding, safetyTrustworthy,
            table.WallRemaining <= LateWallThreshold, riichiOut));
    }

    /// <summary>
    /// What this particular hand pays if it comes in. Each dora is roughly another
    /// han, and han roughly double the payout up to the mangan ceiling, so a hand
    /// holding three dora is worth several times a bare one.
    /// </summary>
    public double EstimateWinValue(TableState table)
    {
        ArgumentNullException.ThrowIfNull(table);

        int dora = table.CountDora();
        bool closed = table.CalledMelds == 0;

        double extraHan = dora + (closed ? ClosedHanBonus : 0.0);
        double value = AverageWinValue * Math.Pow(HanMultiplier, extraHan);
        return Math.Min(value, ManganCeiling);
    }

    /// <summary>Payouts stop doubling here; above this everything is a mangan or more.</summary>
    public double ManganCeiling { get; init; } = 8000;

    /// <summary>Adjusts risk appetite for our position in the standings.</summary>
    public PlacementAdjuster Placement { get; init; } = new();

    /// <summary>Estimates how ready each opponent is and what feeding them costs.</summary>
    public ThreatModel Threats { get; init; } = new();

    /// <summary>
    /// How heavily to penalise opening the hand with no yaku, carried through to
    /// the call policy so both halves stay in step.
    /// </summary>
    public double NoYakuPenalty { get; init; } = 2.5;

    /// <summary>
    /// How many more dangerous tiles we are signing up to throw by pushing now.
    /// Capped, because a hand can be abandoned later if it stops going anywhere.
    /// </summary>
    public static double PushHorizon(int shanten, int wallRemaining, int cap = MaxPushHorizon)
    {
        int turnsLeft = Math.Max(1, wallRemaining / 4);
        return Math.Min(turnsLeft, Math.Max(1, cap));
    }

    /// <summary>Lets the simulator sweep the horizon without editing the constant.</summary>
    public int MaxPushHorizonOverride { get; init; } = MaxPushHorizon;

    /// <summary>
    /// How many further dangerous discards a push commits to.
    ///
    /// Measured over 500 hands on a table calibrated to roughly real rates:
    /// raising this from 2 to 8 costs 3.6 points of win rate and buys only 0.8
    /// points of safety. That trade stays bad even after inflating deal-ins by
    /// the quarter the simulator still undercharges.
    ///
    /// Not 1, which ignores commitment altogether and is the units error that had
    /// the advisor pushing into a declared riichi turn after turn. Two keeps the
    /// idea while taking most of the gain, and <see cref="FarShantenVeto"/> still
    /// catches genuinely hopeless hands regardless of this number.
    /// </summary>
    public const int MaxPushHorizon = 2;

    /// <summary>At or beyond this shanten the hand is treated as hopeless under pressure.</summary>
    public int FarShantenVeto { get; init; } = 3;

    /// <summary>Below this wall count, a far-off hand is out of time.</summary>
    public int LateWallThreshold { get; init; } = 15;

    /// <summary>
    /// Readiness on a single seat that counts as a real threat rather than
    /// background risk. Danger is still priced into every discard below this;
    /// it just does not trigger giving up on the hand.
    /// </summary>
    public double ConcreteThreatLevel { get; init; } = 0.7;

    /// <summary>
    /// Points expected to be lost. Uses the per-opponent estimate when available,
    /// which prices a dora-heavy riichi higher than a cheap open hand.
    /// </summary>
    private double ExpectedLoss(TileRisk risk)
    {
        if (risk.ExpectedCost <= 0)
            return risk.DealInRate * AverageDealInCost;

        // The per-opponent estimate is the better number, but it left
        // AverageDealInCost unreachable -- sweeping it changed nothing at all.
        // Treat the setting as a scale on that estimate so it stays meaningful.
        return risk.ExpectedCost * (AverageDealInCost / DefaultDealInCost);
    }

    /// <summary>Baseline the deal-in setting is measured against.</summary>
    public const double DefaultDealInCost = 5200;

    /// <summary>The only move available once riichi has been declared.</summary>
    private static Advice ForcedDraw(TableState table)
    {
        var option = new DiscardOption(
            Tile: table.DrawnTile,
            ResultingShanten: 0,
            Ukeire: 0,
            Risk: new TileRisk(table.DrawnTile, 0.0, SafetyReason.Unknown),
            Score: 0,
            Rationale: "forced by your riichi");

        return new Advice(
            [option],
            Folding: false,
            Headline: $"You are in riichi - discard the {Tiles.Describe(table.DrawnTile)} you just drew. "
                + "Your hand is locked until you win.");
    }

    /// <summary>
    /// Probability of completing the hand.
    ///
    /// The draws are NOT interchangeable: getting from n-shanten to tenpai happens
    /// at the hand's acceptance rate, but the final draw is the WIN, whose width is
    /// the eventual wait -- typically a handful of tiles, not the current ukeire.
    /// Treating every draw at the ukeire rate makes a wide one-shanten look better
    /// than a tenpai, which is backwards.
    /// A ranking heuristic, not a solver result.
    /// </summary>
    public static double WinProbability(int shanten, int ukeire, int wallRemaining)
    {
        if (shanten < 0)
            return 1.0;
        if (ukeire <= 0 || wallRemaining <= 0)
            return 0.0;

        int turns = wallRemaining / 4;
        if (turns < shanten + 1)
            return 0.0;

        // Unseen from this seat: the live wall plus three concealed opponent hands.
        int unseen = wallRemaining + (3 * 13);

        if (shanten == 0)
        {
            // Already waiting: every remaining draw is a chance to win outright.
            double pTenpaiWin = Math.Clamp(ukeire / (double)unseen, 0.0, 1.0);
            double any = 1.0 - Math.Pow(1.0 - pTenpaiWin, turns);
            return Math.Clamp(any * TenpaiToWinRate, 0.0, 1.0);
        }

        // Split the turns: advance to tenpai first, then win off a typical wait.
        int advanceTurns = Math.Max(1, turns * shanten / (shanten + 1));
        int winTurns = Math.Max(1, turns - advanceTurns);
        if (advanceTurns < shanten)
            return 0.0;

        double pAdvance = Math.Clamp(ukeire / (double)unseen, 0.0, 1.0);
        double reachTenpai = 0.0;
        for (int k = shanten; k <= advanceTurns; k++)
            reachTenpai += Choose(advanceTurns, k) * Math.Pow(pAdvance, k) * Math.Pow(1 - pAdvance, advanceTurns - k);

        double pWin = Math.Clamp(TypicalWaitWidth / unseen, 0.0, 1.0);
        double winFromTenpai = 1.0 - Math.Pow(1.0 - pWin, winTurns);

        return Math.Clamp(reachTenpai * winFromTenpai * TenpaiToWinRate, 0.0, 1.0);
    }

    /// <summary>
    /// Chance of being tenpai when the wall runs out. Unlike a win this only needs
    /// <c>shanten</c> useful draws, not a final winning tile, so it stays worth
    /// chasing long after the hand can realistically be completed.
    /// </summary>
    public static double TenpaiByDrawProbability(int shanten, int ukeire, int wallRemaining)
    {
        if (shanten <= 0)
            return 1.0;
        if (ukeire <= 0 || wallRemaining <= 0)
            return 0.0;

        int turns = wallRemaining / 4;
        if (turns < shanten)
            return 0.0;

        int unseen = wallRemaining + (3 * 13);
        double p = Math.Clamp(ukeire / (double)unseen, 0.0, 1.0);

        double total = 0.0;
        for (int k = shanten; k <= turns; k++)
            total += Choose(turns, k) * Math.Pow(p, k) * Math.Pow(1 - p, turns - k);

        return Math.Clamp(total, 0.0, 1.0);
    }

    /// <summary>How often reaching tenpai converts into an actual win.</summary>
    public const double TenpaiToWinRate = 0.45;

    /// <summary>Live tiles a typical wait is worth, used for the final winning draw.</summary>
    public const double TypicalWaitWidth = 6.0;

    private static double Choose(int n, int k)
    {
        if (k < 0 || k > n)
            return 0.0;
        k = Math.Min(k, n - k);
        double result = 1.0;
        for (int i = 1; i <= k; i++)
            result = result * (n - k + i) / i;
        return result;
    }

    private static string BuildRationale(UkeireResult uk, TileRisk risk, bool threat, bool trustworthy)
    {
        string eff = uk.Shanten < 0
            ? "completes the hand"
            : uk.Shanten == 0
                ? $"tenpai, {uk.Width} winning tiles"
                : $"{uk.Shanten}-shanten, {uk.Width} tiles accepted";

        if (!threat)
        {
            return trustworthy
                ? eff
                : $"{eff} (no threat data - efficiency only)";
        }

        return $"{eff}; {risk.Explain()}";
    }

    private static string BuildHeadline(
        IReadOnlyList<DiscardOption> options, bool threat, bool folding, bool trustworthy, bool endgame,
        bool riichiOut)
    {
        if (options.Count == 0)
            return "No legal discard.";

        var best = options[0];
        string tile = Tiles.Describe(best.Tile);

        if (!trustworthy)
            return $"Discard {tile} - efficiency only. Opponent riichi and melds are not being read, so this ignores danger entirely.";

        if (folding)
        {
            string against = riichiOut ? "a declared riichi" : "an opponent who looks ready";
            return $"Fold against {against}. Discard {tile} - {best.Risk.Explain()}.";
        }


        if (threat)
            return $"Push {tile} - {best.Rationale}.";

        if (endgame && best.ResultingShanten > 0)
            return $"Discard {tile} - {best.Rationale}. Wall is nearly out; this is the best shot at tenpai before the draw.";

        return $"Discard {tile} - {best.Rationale}.";
    }
}
