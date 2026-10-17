namespace Mahjong.Advisor;

public enum CallKind
{
    Pass,
    Chi,
    Pon,
    Kan,
    Riichi,
    Ron,
    Tsumo,
}

/// <summary>One available call, with the hand shape it would leave behind.</summary>
public sealed record CallEvaluation(
    CallKind Kind,
    int ResultingShanten,
    int Ukeire,
    bool OpensHand,
    bool YakuAtRisk,
    double Score,
    string Rationale)
{
    /// <summary>For chi, which tiles from hand complete the run.</summary>
    public IReadOnlyList<int> UsedFromHand { get; init; } = [];
}

public sealed record CallAdvice(IReadOnlyList<CallEvaluation> Options, string Headline)
{
    public CallEvaluation? Best => Options.Count > 0 ? Options[0] : null;
}

/// <summary>
/// Decides whether to claim a discarded tile.
///
/// The core question is not "does this tile fit" but "is the hand still worth
/// anything once it is open". Calling with no yaku path is the single most
/// common way to end up with a fast hand you are not allowed to win on.
/// </summary>
public sealed class CallAdvisor
{
    /// <summary>Shanten gained must beat this to justify opening a hand with no clear yaku.</summary>
    public double NoYakuPenalty { get; init; } = 2.5;

    /// <summary>How much a single step of shanten is worth when ranking.</summary>
    public double ShantenWeight { get; init; } = 10.0;

    public double UkeireWeight { get; init; } = 0.15;

    /// <summary>Used to judge whether a kan's extra dora is safe to reveal.</summary>
    public ThreatModel Threats { get; init; } = new();

    public CallAdvice Recommend(
        TableState table,
        int claimedTile,
        IReadOnlyCollection<CallKind> available)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(available);

        var options = new List<CallEvaluation>();
        var visible = table.BuildVisible();
        bool closed = table.CalledMelds == 0;
        bool hasClaim = claimedTile is >= 0 and < Tiles.Kinds;

        // Baseline: what the hand is worth if we let the tile go.
        var passUkeire = UkeireCalculator.Calculate(table.Hand, visible, table.CalledMelds);
        int passShanten = passUkeire.Shanten;
        int passWidth = passUkeire.Width;

        // On our own turn the hand still holds the drawn tile, so its shape is only
        // meaningful after choosing a discard. Riichi is judged on that shape.
        int total = Tiles.Total(table.Hand);
        if (total == 14 - (table.CalledMelds * 3))
        {
            var (bestShanten, bestWidth) = BestAfterDiscard(table, visible);
            passShanten = bestShanten;
            passWidth = bestWidth;
        }

        if (available.Contains(CallKind.Ron))
        {
            options.Add(new CallEvaluation(
                CallKind.Ron, -1, 0, false, false, double.MaxValue,
                "Winning hand — take it."));
        }

        if (available.Contains(CallKind.Tsumo))
        {
            options.Add(new CallEvaluation(
                CallKind.Tsumo, -1, 0, false, false, double.MaxValue,
                "Self-drawn win — take it."));
        }

        if (hasClaim && available.Contains(CallKind.Pon))
            AddPon(table, claimedTile, visible, passShanten, options);

        if (hasClaim && available.Contains(CallKind.Chi))
            AddChi(table, claimedTile, visible, passShanten, options);

        if (hasClaim && available.Contains(CallKind.Kan))
            AddKan(table, claimedTile, visible, passShanten, options);
        else if (available.Contains(CallKind.Kan))
            AddSelfKan(table, options);

        // Riichi needs no claimed tile: it is declared on our own turn.
        if (available.Contains(CallKind.Riichi) && closed && passShanten == 0)
        {
            string note = passWidth >= 4
                ? $"Closed tenpai waiting on {passWidth} tiles."
                : $"Closed tenpai, but only {passWidth} tiles complete it.";

            options.Add(new CallEvaluation(
                CallKind.Riichi, 0, passWidth, false, false,
                (ShantenWeight * 3) + (passWidth * UkeireWeight),
                $"{note} Riichi is a yaku on its own and adds the hidden dora, so it is worth declaring."));
        }

        options.Add(new CallEvaluation(
            CallKind.Pass, passShanten, passWidth, false, false,
            ScoreFor(passShanten, passWidth, opens: false, yakuAtRisk: false),
            passShanten == 0
                ? $"Stay as you are — already tenpai, {passWidth} tiles."
                : $"Keep the hand closed at {passShanten}-shanten, {passWidth} tiles."));

        var ordered = options.OrderByDescending(o => o.Score).ToList();
        return new CallAdvice(ordered, Headline(ordered));
    }

    /// <summary>Best shape reachable from a 3n+2 hand once a discard is chosen.</summary>
    private static (int Shanten, int Ukeire) BestAfterDiscard(TableState table, int[] visible)
    {
        int bestShanten = int.MaxValue;
        int bestWidth = 0;
        var work = (int[])table.Hand.Clone();

        for (int t = 0; t < Tiles.Kinds; t++)
        {
            if (work[t] == 0)
                continue;

            work[t]--;
            var uk = UkeireCalculator.Calculate(work, visible, table.CalledMelds);
            work[t]++;

            if (uk.Shanten < bestShanten || (uk.Shanten == bestShanten && uk.Width > bestWidth))
            {
                bestShanten = uk.Shanten;
                bestWidth = uk.Width;
            }
        }

        return bestShanten == int.MaxValue ? (0, 0) : (bestShanten, bestWidth);
    }

    /// <summary>
    /// A kan declared from our own tiles, where no tile is being claimed.
    ///
    /// The catch is that a kan flips an extra dora indicator for EVERYONE, so
    /// handing one to a player who has already declared riichi is a common way
    /// to turn their cheap hand into an expensive one.
    /// </summary>
    private void AddSelfKan(TableState table, List<CallEvaluation> sink)
    {
        var threats = Threats.Evaluate(table);
        bool riichiOut = threats.Any(t => t.Riichi);
        double topThreat = threats.Count > 0 ? threats.Max(t => t.TenpaiProbability) : 0.0;

        string why;
        double score;

        if (riichiOut)
        {
            why = "an opponent has declared riichi, and a kan flips another dora that could be theirs";
            score = -ShantenWeight * 2;
        }
        else if (topThreat >= 0.6)
        {
            why = "someone looks close to ready, so the extra dora is as likely to help them as you";
            score = -ShantenWeight;
        }
        else
        {
            why = "nobody looks ready, so the extra dora and replacement draw are worth taking";
            score = ShantenWeight;
        }

        sink.Add(new CallEvaluation(
            Kind: CallKind.Kan,
            ResultingShanten: 0,
            Ukeire: 0,
            OpensHand: false,
            YakuAtRisk: false,
            Score: score,
            Rationale: why));
    }

    private void AddPon(
        TableState table, int claimedTile, int[] visible, int passShanten, List<CallEvaluation> sink)
    {
        if (table.Hand[claimedTile] < 2)
            return;

        var after = (int[])table.Hand.Clone();
        after[claimedTile] -= 2;
        Evaluate(table, after, visible, passShanten, CallKind.Pon,
            [claimedTile, claimedTile], claimedTile, sink);
    }

    private void AddKan(
        TableState table, int claimedTile, int[] visible, int passShanten, List<CallEvaluation> sink)
    {
        if (table.Hand[claimedTile] < 3)
            return;

        var after = (int[])table.Hand.Clone();
        after[claimedTile] -= 3;
        Evaluate(table, after, visible, passShanten, CallKind.Kan,
            [claimedTile, claimedTile, claimedTile], claimedTile, sink);
    }

    private void AddChi(
        TableState table, int claimedTile, int[] visible, int passShanten, List<CallEvaluation> sink)
    {
        if (Tiles.IsHonor(claimedTile))
            return;

        int rank = Tiles.Rank(claimedTile);
        int suitBase = Tiles.SuitBase(claimedTile);

        // The three run shapes the claimed tile can slot into.
        (int A, int B)[] shapes =
        [
            (rank - 2, rank - 1),
            (rank - 1, rank + 1),
            (rank + 1, rank + 2),
        ];

        foreach (var (a, b) in shapes)
        {
            if (a is < 1 or > 9 || b is < 1 or > 9)
                continue;

            int ta = suitBase + a - 1;
            int tb = suitBase + b - 1;
            if (table.Hand[ta] < 1 || table.Hand[tb] < 1)
                continue;

            var after = (int[])table.Hand.Clone();
            after[ta]--;
            after[tb]--;
            Evaluate(table, after, visible, passShanten, CallKind.Chi, [ta, tb], claimedTile, sink);
        }
    }

    /// <summary>
    /// Scores a call by the hand it leaves once the follow-up discard is made.
    /// Reuses the discard search so call and discard advice cannot disagree.
    /// </summary>
    private void Evaluate(
        TableState table,
        int[] concealedAfterCall,
        int[] visible,
        int passShanten,
        CallKind kind,
        IReadOnlyList<int> usedFromHand,
        int claimedTile,
        List<CallEvaluation> sink)
    {
        int meldsAfter = table.CalledMelds + 1;
        int total = Tiles.Total(concealedAfterCall);
        int expectedBeforeDiscard = 14 - (meldsAfter * 3);

        int bestShanten = int.MaxValue;
        int bestUkeire = 0;

        if (total == expectedBeforeDiscard)
        {
            // Try every discard and keep the best resulting shape.
            var work = (int[])concealedAfterCall.Clone();
            for (int t = 0; t < Tiles.Kinds; t++)
            {
                if (work[t] == 0)
                    continue;
                work[t]--;
                var uk = UkeireCalculator.Calculate(work, visible, meldsAfter);
                work[t]++;
                if (uk.Shanten < bestShanten || (uk.Shanten == bestShanten && uk.Width > bestUkeire))
                {
                    bestShanten = uk.Shanten;
                    bestUkeire = uk.Width;
                }
            }
        }
        else
        {
            // Kan draws a replacement, so no discard search is meaningful here.
            var uk = UkeireCalculator.Calculate(concealedAfterCall, visible, meldsAfter);
            bestShanten = uk.Shanten;
            bestUkeire = uk.Width;
        }

        if (bestShanten == int.MaxValue)
            return;

        bool opens = table.CalledMelds == 0;
        bool yakuRisk = opens && !HasOpenYakuPath(concealedAfterCall, claimedTile, kind);

        string gain = bestShanten < passShanten
            ? $"advances to {bestShanten}-shanten (from {passShanten})"
            : bestShanten == passShanten
                ? $"stays at {bestShanten}-shanten"
                : $"goes backwards to {bestShanten}-shanten";

        string why = yakuRisk
            ? $"{gain}, but opens the hand with no clear yaku"
            : gain;

        sink.Add(new CallEvaluation(
            Kind: kind,
            ResultingShanten: bestShanten,
            Ukeire: bestUkeire,
            OpensHand: opens,
            YakuAtRisk: yakuRisk,
            Score: ScoreFor(bestShanten, bestUkeire, opens, yakuRisk),
            Rationale: why)
        {
            UsedFromHand = usedFromHand,
        });
    }

    private double ScoreFor(int shanten, int ukeire, bool opens, bool yakuAtRisk)
    {
        double score = -(shanten * ShantenWeight) + (ukeire * UkeireWeight);
        if (yakuAtRisk)
            score -= NoYakuPenalty * ShantenWeight;
        else if (opens)
            score -= 0.2 * ShantenWeight; // open hands score less even when legal
        return score;
    }

    /// <summary>
    /// Rough test for whether an opened hand can still finish with a yaku:
    /// a dragon triplet, an all-simples shape, or a one-suit shape.
    /// Seat and round winds are excluded because the reader cannot see them yet,
    /// which makes this deliberately pessimistic rather than wrong.
    /// </summary>
    public static bool HasOpenYakuPath(int[] concealed, int claimedTile, CallKind kind)
    {
        ArgumentNullException.ThrowIfNull(concealed);

        var full = (int[])concealed.Clone();

        // Count the claimed tile as part of the finished block. Pass means the
        // hand is already complete and must not gain a phantom copy: the old
        // default of 1 turned a pair into a triplet and invented a yaku path.
        int copies = kind switch
        {
            CallKind.Pon => 3,
            CallKind.Kan => 4,
            CallKind.Chi => 1,
            _ => 0,
        };
        full[claimedTile] += copies;

        // Yakuhai: a dragon triplet (5z, 6z, 7z).
        for (int t = 31; t <= 33; t++)
        {
            if (full[t] >= 3)
                return true;
        }

        // Tanyao: nothing terminal or honour anywhere.
        bool anyTerminal = Tiles.Terminals.Any(t => full[t] > 0);
        if (!anyTerminal)
            return true;

        // Honitsu / chinitsu: at most one suit present.
        int suitsPresent = 0;
        for (int suit = 0; suit < 3; suit++)
        {
            for (int r = 0; r < 9; r++)
            {
                if (full[(suit * 9) + r] > 0)
                {
                    suitsPresent++;
                    break;
                }
            }
        }

        if (suitsPresent <= 1)
            return true;

        // Toitoi: mostly triplets already.
        int triplets = 0;
        for (int t = 0; t < Tiles.Kinds; t++)
        {
            if (full[t] >= 3)
                triplets++;
        }

        return triplets >= 3;
    }

    private static string Headline(IReadOnlyList<CallEvaluation> ordered)
    {
        if (ordered.Count == 0)
            return "No call available.";

        var best = ordered[0];
        return best.Kind switch
        {
            CallKind.Ron => "Ron — take the win.",
            CallKind.Tsumo => "Tsumo — take the win.",
            CallKind.Pass => $"Pass. {best.Rationale}",
            CallKind.Riichi => $"Declare riichi. {best.Rationale}",
            _ => $"Call {best.Kind}. {best.Rationale}.",
        };
    }
}
