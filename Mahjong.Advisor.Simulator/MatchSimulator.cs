using Mahjong.Advisor;

namespace Mahjong.Advisor.Simulator;

public sealed record MatchOutcome(
    int Placement,
    int[] Scores,
    int Hands,
    int OurWins,
    int OurDealIns,
    int Draws)
{
    public int FinalScore => Scores[0];
}

public sealed record MatchSummary(
    int Matches,
    int Hands,
    double FirstRate,
    double SecondRate,
    double ThirdRate,
    double FourthRate,
    double AveragePlacement,
    double AverageScore,
    double WinRate,
    double DealInRate,
    double DrawRate)
{
    public override string ToString() =>
        $"1st {FirstRate * 100,5:0.0}%  2nd {SecondRate * 100,5:0.0}%  "
        + $"3rd {ThirdRate * 100,5:0.0}%  4th {FourthRate * 100,5:0.0}%  "
        + $"avg place {AveragePlacement,4:0.00}  avg score {AverageScore,6:0}  "
        + $"win {WinRate * 100,5:0.0}%  deal-in {DealInRate * 100,5:0.0}%  draw {DrawRate * 100,5:0.0}%";
}

public sealed record MatchComparison(
    int Matches,
    int ABetter,
    int BBetter,
    int Same,
    double AveragePlacementA,
    double AveragePlacementB,
    double AverageScoreA,
    double AverageScoreB,
    double AverageWinRateA,
    double AverageWinRateB,
    double AverageDealInRateA,
    double AverageDealInRateB)
{
    public override string ToString() =>
        $"A better {ABetter,4}/{Matches}  B better {BBetter,4}/{Matches}  same {Same,4}/{Matches}  "
        + $"place A {AveragePlacementA:0.00} vs B {AveragePlacementB:0.00}  "
        + $"score A {AverageScoreA:0} vs B {AverageScoreB:0}  "
        + $"win A {AverageWinRateA * 100:0.0}% vs B {AverageWinRateB * 100:0.0}%  "
        + $"deal-in A {AverageDealInRateA * 100:0.0}% vs B {AverageDealInRateB * 100:0.0}%";
}

/// <summary>
/// Plays full hanchan (East + South) with real score accounting: dealer
/// rotation, renchan, honba, riichi sticks, exhaustive-draw payments and
/// placement. Placement is the whole point - the hand simulator starts everyone
/// at 25,000, so it can never judge a chase or a protect-the-lead decision.
/// </summary>
public static class MatchSimulator
{
    /// <summary>
    /// Paired comparison on identical match seeds. Both policies see the same deals
    /// and the same wall order, so the per-match difference is far less noisy than
    /// comparing two independent runs.
    /// </summary>
    public static MatchComparison Compare(
    DiscardAdvisor a,
    DiscardAdvisor b,
    int matches,
    int seed)
{
    var outcomes = new (MatchOutcome First, MatchOutcome Second)[matches];

    Parallel.For(0, matches, i =>
        outcomes[i] = (Play(a, seed + i), Play(b, seed + i)));

    int aBetter = 0;
    int bBetter = 0;
    int same = 0;

    foreach (var (first, second) in outcomes)
    {
        if (first.Placement < second.Placement)
            aBetter++;
        else if (second.Placement < first.Placement)
            bBetter++;
        else
            same++;
    }

    return new MatchComparison(
        matches,
        aBetter,
        bBetter,
        same,
        outcomes.Average(o => o.First.Placement),
        outcomes.Average(o => o.Second.Placement),
        outcomes.Average(o => o.First.FinalScore),
        outcomes.Average(o => o.Second.FinalScore),
        outcomes.Sum(o => o.First.OurWins) / (double)outcomes.Sum(o => o.First.Hands),
        outcomes.Sum(o => o.Second.OurWins) / (double)outcomes.Sum(o => o.Second.Hands),
        outcomes.Sum(o => o.First.OurDealIns) / (double)outcomes.Sum(o => o.First.Hands),
        outcomes.Sum(o => o.Second.OurDealIns) / (double)outcomes.Sum(o => o.Second.Hands));
}

    public static MatchOutcome Play(DiscardAdvisor advisor, int seed, int maxHands = 14)
    {
        var sim = new HandSimulator(advisor, seed);
        var scores = new int[] { 25000, 25000, 25000, 25000 };
        int dealer = 0;
        int honba = 0;
        int sticks = 0;
        int rounds = 0;
        int hands = 0;
        int ourWins = 0;
        int ourDealIns = 0;
        int draws = 0;

        while (rounds < 8 && hands < maxHands)
        {
            var outcome = sim.PlayHand(dealer, scores);
            hands++;

            for (int seat = 0; seat < 4; seat++)
            {
                if (!outcome.RiichiSeats[seat])
                    continue;

                scores[seat] -= 1000;
                sticks++;
            }

            if (outcome.ExhaustiveDraw)
            {
                ApplyDraw(scores, outcome.TenpaiSeats);
                draws++;
                honba++;
            }
            else
            {
                var value = outcome.WinValue
                    ?? throw new InvalidOperationException("A completed hand must carry a score.");

                int winner = outcome.Winner;
                int stickGain = sticks * 1000;

                if (outcome.Tsumo)
                {
                    int total = 0;
                    for (int seat = 0; seat < 4; seat++)
                    {
                        if (seat == winner)
                            continue;

                        int pay = (seat == dealer ? value.TsumoDealerPays : value.TsumoOthersPay)
                            + (100 * honba);
                        scores[seat] -= pay;
                        total += pay;
                    }

                    scores[winner] += total;
                }
                else
                {
                    int ron = value.RonPoints + (300 * honba);
                    scores[outcome.DealtInBy] -= ron;
                    scores[winner] += ron;
                }

                scores[winner] += stickGain;
                sticks = 0;

                if (winner == 0)
                    ourWins++;
                if (outcome.DealtInBy == 0)
                    ourDealIns++;

                if (winner == dealer)
                {
                    honba++;
                }
                else
                {
                    honba = 0;
                    dealer = (dealer + 1) % 4;
                    rounds++;
                }
            }

            if (Array.Exists(scores, s => s < 0))
                break;
        }

        int placement = 1 + scores.Count(s => s > scores[0]);
        return new MatchOutcome(placement, scores, hands, ourWins, ourDealIns, draws);
    }

    public static MatchSummary Run(DiscardAdvisor advisor, int matches, int seed)
    {
        var results = new MatchOutcome[matches];

        Parallel.For(0, matches, i =>
            results[i] = Play(advisor, seed + i));

        int[] places = new int[5];
        long score = 0;
        int handCount = 0;
        int wins = 0;
        int dealIns = 0;
        int draws = 0;

        foreach (var result in results)
        {
            places[result.Placement]++;
            score += result.FinalScore;
            handCount += result.Hands;
            wins += result.OurWins;
            dealIns += result.OurDealIns;
            draws += result.Draws;
        }

        return new MatchSummary(
            matches,
            handCount,
            places[1] / (double)matches,
            places[2] / (double)matches,
            places[3] / (double)matches,
            places[4] / (double)matches,
            results.Average(r => r.Placement),
            score / (double)matches,
            wins / (double)handCount,
            dealIns / (double)handCount,
            draws / (double)handCount);
    }

    /// <summary>Tenpai players split 3,000 from the noten players.</summary>
    private static void ApplyDraw(int[] scores, bool[] tenpai)
    {
        int noten = tenpai.Count(t => !t);
        int ready = 4 - noten;
        if (ready is 0 or 4)
            return;

        int gain = 3000 / ready;
        int loss = 3000 / noten;

        for (int seat = 0; seat < 4; seat++)
            scores[seat] += tenpai[seat] ? gain : -loss;
    }
}
