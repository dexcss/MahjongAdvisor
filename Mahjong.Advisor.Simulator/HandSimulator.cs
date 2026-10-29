using Mahjong.Advisor;

namespace Mahjong.Advisor.Simulator;

public sealed record HandOutcome(
    int Winner,
    int DealtInBy,
    bool ExhaustiveDraw,
    HandValueResult? WinValue,
    bool Tsumo,
    bool[] RiichiSeats,
    bool[] TenpaiSeats);

public sealed record RunResult(
    int Hands,
    double WinRate,
    double DealInRate,
    double DrawRate,
    double OpponentWinRate,
    double AveragePoints)
{
    public override string ToString() =>
        $"win {WinRate * 100,5:0.0}%  deal-in {DealInRate * 100,5:0.0}%  "
        + $"draw {DrawRate * 100,5:0.0}%  opp-win {OpponentWinRate * 100,5:0.0}%  "
        + $"avg {AveragePoints,7:0}";
}

/// <summary>
/// Plays hands offline so a policy can be measured over thousands of deals.
///
/// Opponents call pon, which matters more than it sounds: without calls they
/// completed hands so slowly that deal-ins landed at 4% against roughly 13% in
/// real play. Rare, cheap deal-ins mean caution can never pay for itself, and
/// every defensive setting reads as harmful no matter what its true value is.
/// </summary>
public sealed class HandSimulator
{
    private readonly Random random;
    private readonly DiscardAdvisor advisor;
    private readonly CallAdvisor callAdvisor;
    private readonly Func<TableState, Advice>? discardPolicy;
    private readonly int[]?[] riichiPondSnapshots = new int[]?[4];

    /// <summary>How often a tenpai opponent declares rather than staying quiet.</summary>
    public double OpponentRiichiRate { get; init; } = 0.6;

    /// <summary>How often an opponent takes a pon that improves their hand.</summary>
    public double OpponentCallRate { get; init; } = 0.6;

    public HandSimulator(DiscardAdvisor advisor, int seed, Func<TableState, Advice>? discardPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(advisor);
        this.advisor = advisor;
        this.discardPolicy = discardPolicy;
        callAdvisor = new CallAdvisor { Threats = advisor.Threats, NoYakuPenalty = advisor.NoYakuPenalty };
        random = new Random(seed);
    }

    /// <summary>The live match scores, so placement stance matches the real game.</summary>
    private int[] currentScores = [25000, 25000, 25000, 25000];

    public HandOutcome PlayHand(int dealerSeat = 0, int[]? scores = null)
    {
        currentScores = scores is { Length: 4 }
            ? [.. scores]
            : [25000, 25000, 25000, 25000];

        Array.Clear(riichiPondSnapshots);

        var wall = BuildWall();
        int next = 0;

        var hands = new int[4][];
        for (int p = 0; p < 4; p++)
        {
            hands[p] = new int[Tiles.Kinds];
            for (int i = 0; i < 13; i++)
                hands[p][wall[next++]]++;
        }

        var ponds = new List<int>[4];
        var meldTiles = new List<int>[4];
        for (int p = 0; p < 4; p++)
        {
            ponds[p] = [];
            meldTiles[p] = [];
        }

        var melds = new int[4];
        var riichi = new bool[4];
        var riichiAt = new int[4];
        Array.Fill(riichiAt, -1);

        int turn = dealerSeat;
        int lastDrawn = -1;
        bool skipDraw = false;

        while (next < wall.Count - 14)
        {
            if (!skipDraw)
            {
                lastDrawn = wall[next++];
                hands[turn][lastDrawn]++;

                if (Shanten.Calculate(hands[turn], melds[turn]) < 0)
                {
                    var winValue = EvaluateWin(
                        hands[turn], meldTiles[turn], riichi[turn], lastDrawn,
                        tsumo: true, winnerSeat: turn, dealerSeat: dealerSeat);

                    if (winValue is not null)
                    {
                        return new HandOutcome(
                            turn, -1, false, winValue, true,
                            [.. riichi], new bool[4]);
                    }
                }
            }

            skipDraw = false;

            int discard;
            bool justDeclared = turn == 0 && riichi[0] && riichiAt[0] == ponds[0].Count;

            if (turn == 0 && riichi[0] && !justDeclared && lastDrawn >= 0)
            {
                // Locked by our own declaration: the drawn tile is the only move.
                discard = lastDrawn;
            }
            else
            {
                discard = turn == 0
                    ? ChooseWithAdvisor(hands, ponds, meldTiles, melds, riichi, riichiAt,
                        wall.Count - 14 - next, lastDrawn)
                    : ChooseByShape(hands[turn], melds[turn]);
            }

            // Riichi was only wired for opponents, so our seat played closed hands
            // without the yaku that makes them worth playing closed.
            if (!riichi[turn] && melds[turn] == 0 && Shanten.Calculate(hands[turn], 0) == 0)
            {
                bool declare;
                if (turn == 0)
                {
                    var table = BuildTable(hands, ponds, meldTiles, melds, riichi, riichiAt,
                        wall.Count - 14 - next, lastDrawn);
                    var advice = callAdvisor.Recommend(table, -1, [CallKind.Riichi, CallKind.Pass]);
                    declare = advice.Best?.Kind == CallKind.Riichi;
                }
                else
                {
                    declare = random.NextDouble() < OpponentRiichiRate;
                }

                if (declare)
                {
                    riichi[turn] = true;
                    riichiAt[turn] = ponds[turn].Count;
                    riichiPondSnapshots[turn] = [.. ponds.Select(p => p.Count)];
                }
            }

            hands[turn][discard]--;
            ponds[turn].Add(discard);

            // Anyone waiting on it takes the win.
            for (int q = 0; q < 4; q++)
            {
                if (q == turn)
                    continue;

                hands[q][discard]++;
                HandValueResult? winValue = null;
                if (Shanten.Calculate(hands[q], melds[q]) < 0)
                {
                    winValue = EvaluateWin(
                        hands[q], meldTiles[q], riichi[q], discard,
                        tsumo: false, winnerSeat: q, dealerSeat: dealerSeat);
                }

                if (winValue is not null)
                {
                    hands[q][discard]--;
                    return new HandOutcome(
                        q, turn, false, winValue, false,
                        [.. riichi], new bool[4]);
                }

                hands[q][discard]--;
            }

            // Anyone may claim it, us included. Riichi hands cannot call.
            int caller = FindCaller(hands, meldTiles, ponds, melds, riichi, riichiAt, discard, turn,
                wall.Count - 14 - next);
            if (caller >= 0)
            {
                hands[caller][discard] -= 2;
                melds[caller]++;
                for (int i = 0; i < 3; i++)
                    meldTiles[caller].Add(discard);

                turn = caller;
                skipDraw = true;
                continue;
            }

            turn = (turn + 1) % 4;
        }

        var tenpai = new bool[4];
        for (int p = 0; p < 4; p++)
            tenpai[p] = Shanten.Calculate(hands[p], melds[p]) == 0;

        return new HandOutcome(-1, -1, true, null, false, [.. riichi], tenpai);
    }

    /// <summary>Whoever claims the tile, ourselves included.</summary>
    private int FindCaller(
        int[][] hands, List<int>[] meldTiles, List<int>[] ponds, int[] melds,
        bool[] riichi, int[] riichiAt, int tile, int discarder, int wallLeft)
    {
        for (int step = 1; step < 4; step++)
        {
            int q = (discarder + step) % 4;
            if (riichi[q] || hands[q][tile] < 2 || melds[q] >= 4)
                continue;

            if (q == 0)
            {
                // Our seat uses the same call policy the plugin uses in game.
                var table = BuildTable(hands, ponds, meldTiles, melds, riichi, riichiAt, wallLeft, -1);
                var advice = callAdvisor.Recommend(table, tile, [CallKind.Pon, CallKind.Pass]);
                if (advice.Best?.Kind == CallKind.Pon)
                    return 0;
                continue;
            }

            int before = Shanten.Calculate(hands[q], melds[q]);
            hands[q][tile] -= 2;
            int after = Shanten.Calculate(hands[q], melds[q] + 1);
            hands[q][tile] += 2;

            bool worthIt = after < before
                && CallAdvisor.HasOpenYakuPath(hands[q], tile, CallKind.Pon);

            if (worthIt && random.NextDouble() < OpponentCallRate)
                return q;
        }

        return -1;
    }

    private TableState BuildTable(
        int[][] hands, List<int>[] ponds, List<int>[] meldTiles, int[] melds,
        bool[] riichi, int[] riichiAt, int wallLeft, int drawn)
    {
        var seats = new SeatView[4];
        for (int p = 0; p < 4; p++)
        {
            seats[p] = new SeatView
            {
                Discards = [.. ponds[p]],
                MeldTiles = [.. meldTiles[p]],
                Riichi = riichi[p],
                RiichiDiscardIndex = riichiAt[p],
                RiichiPondCounts = riichiPondSnapshots[p],
            };
        }

        return new TableState
        {
            Hand = (int[])hands[0].Clone(),
            CalledMelds = melds[0],
            Seats = seats,
            Observations = Observed.OwnHand | Observed.Discards | Observed.OpponentMelds
                | Observed.OpponentRiichi | Observed.RiichiTurn,
            WallRemaining = Math.Max(0, wallLeft),
            Scores = [.. currentScores],
            DrawnTile = drawn,
        };
    }

    private int ChooseWithAdvisor(
        int[][] hands, List<int>[] ponds, List<int>[] meldTiles, int[] melds,
        bool[] riichi, int[] riichiAt, int wallLeft, int drawn)
    {
        var table = BuildTable(hands, ponds, meldTiles, melds, riichi, riichiAt, wallLeft, drawn);
        // The search policy, when installed, sits on top of the advisor and may
        // re-rank its candidates; otherwise the advisor answers directly.
        var advice = discardPolicy?.Invoke(table) ?? advisor.Recommend(table);
        return advice.Best?.Tile ?? ChooseByShape(hands[0], melds[0]);
    }

    /// <summary>Baseline opponent: nearest to ready, widest acceptance as tiebreak.</summary>
    private static int ChooseByShape(int[] hand, int calledMelds)
    {
        int bestShanten = int.MaxValue;
        int bestWidth = -1;
        int pick = -1;

        var visible = (int[])hand.Clone();

        for (int t = 0; t < Tiles.Kinds; t++)
        {
            if (hand[t] == 0)
                continue;

            hand[t]--;
            var uk = UkeireCalculator.Calculate(hand, visible, calledMelds);
            hand[t]++;

            if (uk.Shanten < bestShanten || (uk.Shanten == bestShanten && uk.Width > bestWidth))
            {
                bestShanten = uk.Shanten;
                bestWidth = uk.Width;
                pick = t;
            }
        }

        return pick;
    }

    /// <summary>
    /// Whether a complete hand is actually allowed to win. A closed hand almost
    /// always has something — riichi, self-draw, all-simples, pinfu. An open one
    /// needs a real pattern, which is what stops indiscriminate calling.
    /// <summary>
    /// Exact win legality and scoring: yaku, fu, dora and payments. A closed
    /// ron with no yaku is not a win; menzen tsumo and riichi both qualify.
    /// </summary>
    private static HandValueResult? EvaluateWin(
        int[] concealed,
        List<int> meldTiles,
        bool riichi,
        int winTile,
        bool tsumo,
        int winnerSeat,
        int dealerSeat)
    {
        return HandValue.Evaluate(new HandValueRequest
        {
            Concealed = concealed,
            Melds = HandValue.ParseMelds(meldTiles),
            Method = tsumo ? WinMethod.Tsumo : WinMethod.Ron,
            WinningTile = winTile,
            Riichi = riichi,
            Dealer = winnerSeat == dealerSeat,
        });
    }

    private List<int> BuildWall()
    {
        var wall = new List<int>(136);
        for (int t = 0; t < Tiles.Kinds; t++)
        {
            for (int i = 0; i < 4; i++)
                wall.Add(t);
        }

        for (int i = wall.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (wall[i], wall[j]) = (wall[j], wall[i]);
        }

        return wall;
    }

    public static RunResult Run(DiscardAdvisor advisor, int hands, int seed, Action<int>? progress = null)
    {
        int wins = 0, dealIns = 0, draws = 0, oppWins = 0;
        long points = 0;
        int done = 0;
        var gate = new object();

        // Seeded per hand, so every configuration sees the same deals regardless
        // of thread scheduling.
        Parallel.For(0, hands, i =>
        {
            // Rotate the dealer so the hand-level sweeps are not all played from
            // the dealer seat; payments and the first draw both depend on it.
            var outcome = new HandSimulator(advisor, seed + i).PlayHand(i % 4);

            lock (gate)
            {
                if (outcome.Winner == 0)
                {
                    wins++;
                    points += outcome.WinValue!.TotalPoints(
                        outcome.Tsumo ? WinMethod.Tsumo : WinMethod.Ron);
                }
                else if (outcome.Winner > 0)
                {
                    oppWins++;
                    if (outcome.DealtInBy == 0)
                    {
                        dealIns++;
                        points -= outcome.WinValue!.RonPoints;
                    }
                }
                else if (outcome.ExhaustiveDraw)
                {
                    draws++;
                    points += outcome.TenpaiSeats[0] ? 1500 : -1500;
                }

                done++;
                if (done % 250 == 0)
                    progress?.Invoke(done);
            }
        });

        return new RunResult(
            hands,
            wins / (double)hands,
            dealIns / (double)hands,
            draws / (double)hands,
            oppWins / (double)hands,
            points / (double)hands);
    }
}
