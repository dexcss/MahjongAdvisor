using Mahjong.Advisor;

namespace Mahjong.Advisor.Simulator;

public sealed record HandOutcome(
    int Winner,
    int DealtInBy,
    bool ExhaustiveDraw,
    HandValueResult? WinValue,
    bool Tsumo,
    bool[] RiichiSeats,
    bool[] TenpaiSeats,
    int OpponentMelds);

public sealed record RunResult(
    int Hands,
    double WinRate,
    double DealInRate,
    double DrawRate,
    double OpponentWinRate,
    double AveragePoints,
    double OpponentMeldsPerHand,
    double OpponentRiichiPerHand,
    double OurRiichiPerHand)
{
    public override string ToString() =>
        $"win {WinRate * 100,5:0.0}%  deal-in {DealInRate * 100,5:0.0}%  "
        + $"draw {DrawRate * 100,5:0.0}%  opp-win {OpponentWinRate * 100,5:0.0}%  "
        + $"avg {AveragePoints,7:0}  melds/h {OpponentMeldsPerHand:0.00}  "
        + $"opp riichi/h {OpponentRiichiPerHand:0.00}  our riichi/h {OurRiichiPerHand:0.00}";
}

/// <summary>
/// Plays hands offline so a policy can be measured over thousands of deals.
///
/// Opponents call pon, which matters more than it sounds: without calls they
/// completed hands so slowly that deal-ins landed at 4% against roughly 13% in
/// real play. Rare, cheap deal-ins mean caution can never pay for itself, and
/// every defensive setting reads as harmful no matter what its true value is.
///
/// Fidelity model (2026-10-30): wins are scored with the real dora indicator
/// and its ura counterpart, seat and round winds are passed to the payment
/// engine, opponents chi as kamicha and fold against declared riichi when the
/// wall runs short, and our own seat chi mirrors the live single-shape gate.
/// Calibration targets from the 0.4.4.0 Advanced live session: opponent melds
/// 0.69/hand, opponent riichi 1.22/hand, our win ~21%, our deal-in ~13%.
/// </summary>
public sealed class HandSimulator
{
    private readonly Random random;
    private readonly DiscardAdvisor advisor;
    private readonly CallAdvisor callAdvisor;
    private readonly Func<TableState, Advice>? discardPolicy;
    private readonly int[]?[] riichiPondSnapshots = new int[]?[4];

    /// <summary>How often a tenpai opponent declares rather than staying quiet.</summary>
    public double OpponentRiichiRate { get; init; } = 0.7;

    /// <summary>How often an opponent takes a pon that does not make their shape worse.</summary>
    public double OpponentCallRate { get; init; } = 0.8;

    /// <summary>
    /// How often an opponent, sitting kamicha of the discard, takes a chi that
    /// improves their hand. The pon-only model left opponents far too slow,
    /// which made every defensive setting look better than it is.
    /// </summary>
    public double OpponentChiRate { get; init; } = 0.95;

    /// <summary>
    /// Sampled draws remaining at or below which a non-tenpai opponent folds
    /// against a declared riichi instead of feeding it. Live Advanced tables
    /// visibly abandon hopeless shapes under pressure, but they also miss
    /// safe tiles and pay anyway, so the fold is probabilistic.
    /// </summary>
    public int OpponentDefenseWall { get; init; } = 16;

    /// <summary>Chance a threatened, non-tenpai opponent actually folds.</summary>
    public double OpponentDefenseRate { get; init; } = 0.75;

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

    public HandOutcome PlayHand(int dealerSeat = 0, int[]? scores = null, int roundWind = 27)
    {
        currentScores = scores is { Length: 4 }
            ? [.. scores]
            : [25000, 25000, 25000, 25000];

        Array.Clear(riichiPondSnapshots);

        var wall = BuildWall();
        int next = 0;

        // Dead wall: the last 14 tiles. Dora indicators occupy its first five
        // slots and ura indicators the next five; with no kans in the
        // simulation only the first of each is ever revealed. The ura set is
        // priced only for riichi winners, exactly as the payment engine does.
        int doraIndicator = wall[^14];
        int uraIndicator = wall[^9];

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
                        tsumo: true, winnerSeat: turn, dealerSeat: dealerSeat,
                        roundWind, doraIndicator, uraIndicator);

                    if (winValue is not null)
                    {
                        return new HandOutcome(
                            turn, -1, false, winValue, true,
                            [.. riichi], new bool[4],
                            melds[1] + melds[2] + melds[3]);
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
                    : ChooseOpponentDiscard(hands[turn], melds[turn], ponds, riichi,
                        wall.Count - 14 - next);
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
                        tsumo: false, winnerSeat: q, dealerSeat: dealerSeat,
                        roundWind, doraIndicator, uraIndicator);
                }

                if (winValue is not null)
                {
                    hands[q][discard]--;
                    return new HandOutcome(
                        q, turn, false, winValue, false,
                        [.. riichi], new bool[4],
                        melds[1] + melds[2] + melds[3]);
                }

                hands[q][discard]--;
            }

            // Anyone may claim it, us included. Riichi hands cannot call.
            var (caller, meldA, meldB) = FindCaller(hands, meldTiles, ponds, melds, riichi, riichiAt, discard, turn,
                wall.Count - 14 - next);
            if (caller >= 0)
            {
                hands[caller][meldA]--;
                hands[caller][meldB]--;
                melds[caller]++;
                meldTiles[caller].Add(meldA);
                meldTiles[caller].Add(meldB);
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

        return new HandOutcome(
            -1, -1, true, null, false, [.. riichi], tenpai,
            melds[1] + melds[2] + melds[3]);
    }

    /// <summary>Whoever claims the tile, ourselves included. Returns the caller
    /// and the two tiles their hand contributes (for a pon both are the claimed
    /// kind; for a chi they are the run's other two members).</summary>
    private (int Caller, int A, int B) FindCaller(
        int[][] hands, List<int>[] meldTiles, List<int>[] ponds, int[] melds,
        bool[] riichi, int[] riichiAt, int tile, int discarder, int wallLeft)
    {
        for (int step = 1; step < 4; step++)
        {
            int q = (discarder + step) % 4;
            if (riichi[q] || melds[q] >= 4)
                continue;

            if (q == 0)
            {
                // Our seat uses the same call policy the plugin uses in game.
                var table = BuildTable(hands, ponds, meldTiles, melds, riichi, riichiAt, wallLeft, -1);
                var advice = callAdvisor.Recommend(table, tile, [CallKind.Chi, CallKind.Pon, CallKind.Pass]);
                var kind = advice.Best?.Kind ?? CallKind.Pass;

                if (kind == CallKind.Pon && hands[0][tile] >= 2)
                    return (0, tile, tile);

                // Live gate: the controller only fires single-shape chi, so the
                // simulated seat must not take multi-shape claims either.
                if (kind == CallKind.Chi
                    && TryChiTiles(hands[0], tile, out int a, out int b)
                    && advice.Options.Count(o => o.Kind == CallKind.Chi) == 1)
                {
                    return (0, a, b);
                }

                continue;
            }

            // Pon: two in hand, must not make the shape worse and must leave a
            // yaku path. Live NPCs also pon for the yaku at equal shanten - a
            // dragon pair taken as a triplet adds yakuhai without gaining tempo.
            if (hands[q][tile] >= 2)
            {
                int before = Shanten.Calculate(hands[q], melds[q]);
                hands[q][tile] -= 2;
                int after = Shanten.Calculate(hands[q], melds[q] + 1);
                hands[q][tile] += 2;

                if (after <= before
                    && CallAdvisor.HasOpenYakuPath(hands[q], tile, CallKind.Pon)
                    && random.NextDouble() < OpponentCallRate)
                {
                    return (q, tile, tile);
                }
            }

            // Chi: kamicha only - the seat acting after the discarder - and the
            // same improve/yaku-path/rate rules as the pon.
            if (step == 1 && !Tiles.IsHonor(tile) && random.NextDouble() < OpponentChiRate)
            {
                if (TryChiTiles(hands[q], tile, out int a, out int b))
                {
                    hands[q][a]--;
                    hands[q][b]--;
                    int before = Shanten.Calculate(hands[q], melds[q]);
                    int after = Shanten.Calculate(hands[q], melds[q] + 1);
                    hands[q][a]++;
                    hands[q][b]++;

                    if (after < before
                        && CallAdvisor.HasOpenYakuPath(hands[q], tile, CallKind.Chi))
                    {
                        return (q, a, b);
                    }
                }
            }
        }

        return (-1, -1, -1);
    }

    /// <summary>The two hand tiles that complete a run with the claimed tile, if any.</summary>
    private static bool TryChiTiles(int[] hand, int tile, out int a, out int b)
    {
        int rank = Tiles.Rank(tile);
        int suitBase = Tiles.SuitBase(tile);
        (int A, int B)[] shapes =
        [
            (rank - 2, rank - 1),
            (rank - 1, rank + 1),
            (rank + 1, rank + 2),
        ];

        foreach (var (x, y) in shapes)
        {
            if (x is < 1 or > 9 || y is < 1 or > 9)
                continue;

            int ta = suitBase + x - 1;
            int tb = suitBase + y - 1;
            if (hand[ta] > 0 && hand[tb] > 0)
            {
                a = ta;
                b = tb;
                return true;
            }
        }

        a = -1;
        b = -1;
        return false;
    }

    /// <summary>
    /// Opponent discard: fold against a declared riichi once the wall runs
    /// short and the hand is not ready, else pure efficiency. Live Advanced
    /// tables visibly abandon hopeless shapes under pressure instead of
    /// feeding the declaration.
    /// </summary>
    private int ChooseOpponentDiscard(
        int[] hand, int calledMelds, List<int>[] ponds, bool[] riichi, int wallLeft)
    {
        if (riichi.Any(r => r)
            && wallLeft <= OpponentDefenseWall
            && Shanten.Calculate(hand, calledMelds) > 0
            && random.NextDouble() < OpponentDefenseRate)
        {
            return SafestTile(hand, ponds, riichi);
        }

        return ChooseByShape(hand, calledMelds);
    }

    /// <summary>
    /// Cheapest fold: anything already in a declarer's pond, then honours, then
    /// terminals, then everything else. Deliberately crude - this mirrors the
    /// rollout engine's fold so both referees agree on what "defending" means.
    /// </summary>
    private static int SafestTile(int[] hand, List<int>[] ponds, bool[] riichi)
    {
        var safe = new HashSet<int>();
        for (int q = 0; q < ponds.Length; q++)
        {
            if (riichi[q])
                safe.UnionWith(ponds[q]);
        }

        int best = -1;
        int bestClass = int.MaxValue;
        for (int t = 0; t < Tiles.Kinds; t++)
        {
            if (hand[t] == 0)
                continue;

            int cls = safe.Contains(t)
                ? 0
                : t >= Tiles.FirstHonor
                    ? 1
                    : t % 9 == 0 || t % 9 == 8
                        ? 2
                        : 3;

            if (cls < bestClass)
            {
                bestClass = cls;
                best = t;
            }
        }

        return best;
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
        int dealerSeat,
        int roundWind,
        int doraIndicator,
        int uraIndicator)
    {
        // Dora from the revealed indicator, in the concealed hand and the open
        // melds alike. The ura set counts only for riichi winners.
        int dora = CountDora(concealed, doraIndicator) + CountDora(meldTiles, doraIndicator);
        int ura = riichi
            ? CountDora(concealed, uraIndicator) + CountDora(meldTiles, uraIndicator)
            : 0;

        // East = 27, then south, west, north from the dealer. The round wind
        // comes from the match loop (East for hand-level sweeps).
        int seatWind = 27 + ((winnerSeat - dealerSeat) % 4 + 4) % 4;

        return HandValue.Evaluate(new HandValueRequest
        {
            Concealed = concealed,
            Melds = HandValue.ParseMelds(meldTiles),
            Method = tsumo ? WinMethod.Tsumo : WinMethod.Ron,
            WinningTile = winTile,
            Riichi = riichi,
            Dealer = winnerSeat == dealerSeat,
            SeatWind = seatWind,
            RoundWind = roundWind,
            Dora = dora,
            UraDora = ura,
        });
    }

    /// <summary>How many tiles match the kind the indicator names.</summary>
    private static int CountDora(System.Collections.Generic.IEnumerable<int> tiles, int indicator)
    {
        int doraKind = Tiles.DoraFromIndicator(indicator);
        int count = 0;
        foreach (int t in tiles)
        {
            if (t == doraKind)
                count++;
        }

        return count;
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
        int opponentMelds = 0, opponentRiichi = 0, ourRiichi = 0;
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

                opponentMelds += outcome.OpponentMelds;
                for (int s = 1; s < 4; s++)
                {
                    if (outcome.RiichiSeats[s])
                        opponentRiichi++;
                }
                if (outcome.RiichiSeats[0])
                    ourRiichi++;

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
            points / (double)hands,
            opponentMelds / (double)hands,
            opponentRiichi / (double)hands,
            ourRiichi / (double)hands);
    }
}
