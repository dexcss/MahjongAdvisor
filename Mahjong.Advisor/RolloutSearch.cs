using System.Diagnostics;

namespace Mahjong.Advisor;

/// <summary>
/// Phase C: determinized Monte Carlo re-ranking of the discard advisor's top
/// candidates.
///
/// The base advisor prices a discard with a single-state EV formula: win
/// chance times value, minus committed deal-in risk. That formula is a good
/// prior but it cannot see consequences past its horizon constants. A rollout
/// can: play the rest of the hand under a sampled completion of the hidden
/// information and score what actually happens to the match scores.
///
/// Design:
/// <list type="bullet">
/// <item>Candidates: the advisor's top options. The search re-orders those; it
/// never invents a tile the advisor rejected, so the fold rails, kuikae
/// filters and riichi-declaration constraints keep their authority.</item>
/// <item>Determinizations: the unseen tiles (live wall plus three concealed
/// hands) are sampled once per rollout and reused across every candidate, so
/// candidates are compared on the same hidden information (common random
/// numbers).</item>
/// <item>Fast policy: shanten/ukeire efficiency for every seat, pon-only
/// calls, riichi when closed and tenpai, and a cheap fold heuristic
/// (genbutsu first, then terminals and honours) once someone has declared and
/// the wall is short. The full advisor is deliberately not used inside a
/// rollout: it is an order of magnitude too slow to run hundreds of times per
/// decision.</item>
/// <item>Terminal value: the exact net point change to our seat, scored with
/// the real payment engine (HandValue) - ron, tsumo, exhaustive-draw payments
/// and riichi sticks.</item>
/// </list>
///
/// Known simplifications, shared with the simulator's opponent model:
/// opponents do not chi or defend; ura dora is not counted; the dealer
/// premium on tsumo payments is dropped (TableState carries no dealer seat),
/// so every tsumo is paid as three child payments; honba is not tracked and
/// treated as zero; wind yaku are not scored because SeatWind/RoundWind are
/// unread live. None of these bias the ranking between candidates: they are
/// uniform across every rollout.
/// </summary>
public sealed class RolloutSearch
{
    /// <summary>The advisor whose ranking supplies the candidates.</summary>
    public DiscardAdvisor Base { get; init; } = new();

    /// <summary>How many of the advisor's top options the search re-ranks.</summary>
    public int Candidates { get; init; } = 3;

    /// <summary>Independent determinizations. All candidates see the same deals.</summary>
    public int Rollouts { get; init; } = 8;

    /// <summary>
    /// Base seed for the rollout streams. The same table and the same seed
    /// always produce the same answer, which is what makes the memoisation and
    /// the paired simulator runs meaningful.
    /// </summary>
    public int Seed { get; init; } = 9176;

    /// <summary>
    /// Only hands this close to ready are searched. Farther hands are pure
    /// efficiency races where the advisor's EV and a rollout agree in practice,
    /// and skipping them keeps the cost bounded.
    /// </summary>
    public int SearchMaxShanten { get; init; } = 1;

    /// <summary>How often a rollout opponent takes a pon that improves their hand.</summary>
    public double OpponentCallRate { get; init; } = 0.6;

    /// <summary>How often a tenpai rollout opponent declares riichi.</summary>
    public double OpponentRiichiRate { get; init; } = 0.6;

    /// <summary>Our rollout seat declares riichi whenever closed and tenpai.</summary>
    public double OurRiichiRate { get; init; } = 1.0;

    /// <summary>
    /// Sampled draws remaining at or below which the rollout seat folds under a
    /// declared threat. Roughly the last seven go-arounds.
    /// </summary>
    public int FoldWall { get; init; } = 30;

    /// <summary>
    /// Wall-clock budget for one <see cref="Recommend"/> call, checked between
    /// rollouts. Zero means unlimited, which is what the simulator wants. When
    /// the budget runs out the completed rollouts decide; if none completed the
    /// base advice is returned unchanged.
    /// </summary>
    public int TimeBudgetMs { get; init; }

    private const int StickValue = 1000;

    /// <summary>
    /// Hard stop per rollout. A hand can never need this many steps; the cap
    /// only exists so a bad sample can never loop forever.
    /// </summary>
    private const int MaxSteps = 500;

    private readonly object memoLock = new();
    private long memoKey;
    private Advice? memoAdvice;

    /// <summary>
    /// The advisor's answer, with its top candidates re-ranked by rollout value.
    /// Returns the advisor's advice unchanged when the search has nothing to
    /// add: folded hands (the fold order is pure safety), single-option states,
    /// riichi-locked hands, tables without readable scores, and hands farther
    /// from tenpai than <see cref="SearchMaxShanten"/>.
    /// </summary>
    public Advice Recommend(TableState table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var baseAdvice = Base.Recommend(table);

        if (baseAdvice.Folding
            || baseAdvice.Options.Count < 2
            || table.SelfRiichi
            || table.Scores is not { Count: 4 }
            || baseAdvice.Best is not { } best
            || best.ResultingShanten > SearchMaxShanten)
        {
            return baseAdvice;
        }

        long key = Signature(table);
        lock (memoLock)
        {
            if (memoAdvice is not null && memoKey == key)
                return memoAdvice;
        }

        int count = Math.Clamp(Candidates, 1, baseAdvice.Options.Count);
        var candidates = baseAdvice.Options.Take(count).ToList();
        var values = new double[count];
        var wins = new int[count];
        var dealIns = new int[count];

        long startedAt = TimeBudgetMs > 0 ? Stopwatch.GetTimestamp() : 0;
        int completed = 0;

        for (int r = 0; r < Rollouts; r++)
        {
            if (r > 0
                && TimeBudgetMs > 0
                && Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds >= TimeBudgetMs)
            {
                break;
            }

            // Common random numbers: one sampled world per rollout, reused for
            // every candidate, so the comparison is about the discard choice
            // and not about whose hidden hand happened to be lucky.
            var world = SampleWorld(table, StreamSeed(Seed, r));

            for (int c = 0; c < count; c++)
            {
                var result = Rollout(table, world, candidates[c].Tile, new Random(StreamSeed(Seed + 1, r)));
                values[c] += result.Value;
                wins[c] += result.Win ? 1 : 0;
                dealIns[c] += result.DealtIn ? 1 : 0;
            }

            completed++;
        }

        if (completed == 0)
        {
            lock (memoLock)
            {
                memoKey = key;
                memoAdvice = baseAdvice;
            }

            return baseAdvice;
        }

        var ranked = Enumerable.Range(0, count)
            .Select(i => (Index: i, Option: candidates[i], Value: values[i] / completed))
            .OrderByDescending(x => x.Value)
            .ToList();

        Advice advice;
        if (ranked[0].Index == 0)
        {
            // The advisor's own pick won the search. Keep the original advice:
            // the headline stays clean and the base memoised answer is reused.
            advice = baseAdvice;
        }
        else
        {
            var options = ranked
                .Select(x => x.Option with
                {
                    Rationale = $"{x.Option.Rationale}; rollout {x.Value:+#,0;-#,0;0} pts avg, "
                        + $"win {wins[x.Index] / (double)completed:0%}, "
                        + $"deal-in {dealIns[x.Index] / (double)completed:0%} over {completed} deals",
                })
                .ToList();
            options.AddRange(baseAdvice.Options.Skip(count));

            var top = ranked[0];
            advice = new Advice(
                options,
                baseAdvice.Folding,
                $"Searched {completed} deals per candidate: discard {Tiles.Describe(top.Option.Tile)} - {top.Option.Rationale}.");
        }

        lock (memoLock)
        {
            memoKey = key;
            memoAdvice = advice;
        }

        return advice;
    }

    /// <summary>Deterministic stream id per rollout. Shared across candidates.</summary>
    private static int StreamSeed(int seed, int rollout)
    {
        unchecked
        {
            int h = seed * 397 + rollout * 31 + 11;
            return h == int.MinValue ? 17 : h;
        }
    }

    private sealed record RolloutWorld(int[][] Hands, int[] Wall);

    /// <summary>
    /// Samples one completion of the hidden information: three concealed hands
    /// and a live-wall draw stream, drawn from exactly the tiles this seat has
    /// not seen.
    /// </summary>
    private static RolloutWorld SampleWorld(TableState table, int seed)
    {
        var rng = new Random(seed);
        var visible = table.BuildVisible();

        var pool = new List<int>(136);
        for (int t = 0; t < Tiles.Kinds; t++)
            for (int i = visible[t]; i < 4; i++)
                pool.Add(t);

        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        int next = 0;
        var hands = new int[4][];
        for (int q = 1; q < 4; q++)
        {
            var hand = new int[Tiles.Kinds];
            int size = 13 - 3 * Math.Min(4, table.Seats[q].MeldCount);
            for (int i = 0; i < size && next < pool.Count; i++)
                hand[pool[next++]]++;
            hands[q] = hand;
        }

        // The rest is the live wall, capped by what the game reports so a
        // stale wall read cannot invent extra draws.
        int wallLength = Math.Clamp(pool.Count - next, 0, Math.Max(0, table.WallRemaining));
        var wall = new int[wallLength];
        for (int i = 0; i < wallLength; i++)
            wall[i] = pool[next++];

        return new RolloutWorld(hands, wall);
    }

    private (int Value, bool Win, bool DealtIn) Rollout(
        TableState table, RolloutWorld world, int firstDiscard, Random rng)
    {
        var hands = new int[4][];
        hands[0] = (int[])table.Hand.Clone();
        for (int q = 1; q < 4; q++)
            hands[q] = (int[])world.Hands[q].Clone();

        var ponds = new List<int>[4];
        var meldTiles = new List<int>[4];
        for (int p = 0; p < 4; p++)
        {
            ponds[p] = [];
            meldTiles[p] = [];
        }

        var melds = new int[4];
        var riichi = new bool[4];
        var scores = new int[4];
        for (int i = 0; i < 4; i++)
            scores[i] = table.Scores[i];

        int startScore = scores[0];
        int sticks = 0;
        int wallIndex = 0;

        // Everything public, updated as the hand progresses. Our seat's
        // efficiency decisions use it; opponents stay on their own hand, which
        // is exactly the simulator's opponent model.
        var seen = table.BuildVisible();

        int turn = 0;
        int pendingDiscard = firstDiscard;
        bool skipDraw = true;
        int lastDrawn = -1;
        bool declaredThisTurn = false;
        int steps = 0;

        while (true)
        {
            if (++steps > MaxSteps)
                return (scores[0] - startScore, false, false);

            int discard;
            if (skipDraw)
            {
                skipDraw = false;
                if (pendingDiscard >= 0)
                {
                    // The candidate discard itself. A closed tenpai result is
                    // played as a riichi declaration, so the rollout prices the
                    // full commitment rather than just the cut.
                    discard = pendingDiscard;
                    pendingDiscard = -1;

                    hands[0][discard]--;
                    bool tenpaiAfterCut = Shanten.Calculate(hands[0], melds[0]) == 0;
                    hands[0][discard]++;

                    if (tenpaiAfterCut
                        && !riichi[0]
                        && melds[0] == 0
                        && rng.NextDouble() < OurRiichiRate)
                    {
                        riichi[0] = true;
                        declaredThisTurn = true;
                        scores[0] -= StickValue;
                        sticks += StickValue;
                    }
                }
                else
                {
                    discard = ChooseDiscard(
                        turn, hands, melds, ponds, riichi, seen,
                        declaredThisTurn: false, lastDrawn: -1,
                        world.Wall.Length - wallIndex, rng);
                }
            }
            else
            {
                if (wallIndex >= world.Wall.Length)
                {
                    ApplyExhaustiveDraw(hands, melds, scores);
                    return (scores[0] - startScore, false, false);
                }

                lastDrawn = world.Wall[wallIndex++];
                hands[turn][lastDrawn]++;
                if (turn == 0)
                    seen[lastDrawn]++;

                if (Shanten.Calculate(hands[turn], melds[turn]) < 0)
                {
                    var value = Evaluate(table, hands[turn], meldTiles[turn], riichi[turn], lastDrawn, tsumo: true, turn);
                    if (value is not null)
                    {
                        scores[turn] += sticks;
                        sticks = 0;

                        // The dealer premium is dropped: TableState carries no
                        // dealer seat, so every tsumo is priced as three child
                        // payments. Uniform across candidates, so the ranking
                        // is unaffected.
                        for (int seat = 0; seat < 4; seat++)
                        {
                            if (seat == turn)
                                continue;

                            scores[seat] -= value.TsumoOthersPay;
                            scores[turn] += value.TsumoOthersPay;
                        }

                        return (scores[0] - startScore, turn == 0, false);
                    }
                }

                declaredThisTurn = false;
                if (!riichi[turn]
                    && melds[turn] == 0
                    && Shanten.Calculate(hands[turn], melds[turn]) == 0)
                {
                    double rate = turn == 0 ? OurRiichiRate : OpponentRiichiRate;
                    if (rng.NextDouble() < rate)
                    {
                        riichi[turn] = true;
                        declaredThisTurn = true;
                        scores[turn] -= StickValue;
                        sticks += StickValue;
                    }
                }

                if (turn == 0 && riichi[0] && !declaredThisTurn)
                {
                    // Locked by our own declaration: the drawn tile is the only
                    // legal move, exactly as the live controller plays it.
                    discard = lastDrawn;
                }
                else
                {
                    discard = ChooseDiscard(
                        turn, hands, melds, ponds, riichi, seen,
                        declaredThisTurn, lastDrawn,
                        world.Wall.Length - wallIndex, rng);
                }
            }

            hands[turn][discard]--;
            ponds[turn].Add(discard);
            if (turn != 0)
                seen[discard]++; // a hidden tile just became public

            // Ron checks, seat order like the simulator. This is what prices
            // deal-in risk: our forced candidate can feed someone's wait.
            for (int q = 0; q < 4; q++)
            {
                if (q == turn)
                    continue;

                hands[q][discard]++;
                HandValueResult? winValue = null;
                if (Shanten.Calculate(hands[q], melds[q]) < 0)
                    winValue = Evaluate(table, hands[q], meldTiles[q], riichi[q], discard, tsumo: false, q);

                if (winValue is not null)
                {
                    hands[q][discard]--;
                    scores[turn] -= winValue.RonPoints;
                    scores[q] += winValue.RonPoints;
                    scores[q] += sticks;
                    sticks = 0;

                    return (scores[0] - startScore, q == 0, turn == 0);
                }

                hands[q][discard]--;
            }

            // Pon claims, ourselves included. Riichi hands cannot call.
            int caller = FindCaller(hands, melds, riichi, discard, turn, rng);
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
    }

    private int ChooseDiscard(
        int seat, int[][] hands, int[] melds, List<int>[] ponds, bool[] riichi, int[] seen,
        bool declaredThisTurn, int lastDrawn, int wallLeft, Random rng)
    {
        var hand = hands[seat];

        // A declared hand discards its draws, including ours.
        if (riichi[seat] && !declaredThisTurn && lastDrawn >= 0)
            return lastDrawn;

        if (seat == 0)
        {
            int shanten = Shanten.Calculate(hand, melds[seat]);
            if (wallLeft <= FoldWall
                && shanten > 0
                && riichi.Any(r => r))
            {
                // Fold: safety first, the same posture the live advisor takes
                // once a threat is concrete and the wall is short.
                return SafestTile(hand, ponds, riichi);
            }

            return ChooseByShape(hand, seen, melds[seat]);
        }

        // Opponents mirror the simulator's model: pure efficiency, no defense.
        return ChooseByShape(hand, (int[])hand.Clone(), melds[seat]);
    }

    /// <summary>
    /// Nearest to ready, widest acceptance as tiebreak - the simulator's
    /// baseline discard policy, shared so the rollout and the A/B arms agree on
    /// what "efficient" means.
    ///
    /// The full ukeire table is not needed to pick a cut: every candidate tile
    /// only has to be evaluated for shanten, and only the ties are priced for
    /// width. That makes a decision roughly thirty times cheaper than calling
    /// <see cref="UkeireCalculator.Calculate"/> for every held tile, which is
    /// the difference between a rollout engine and a batch job.
    /// </summary>
    private static int ChooseByShape(int[] hand, int[] visible, int calledMelds)
    {
        int[] candidates = ShantenCandidates(hand, calledMelds);
        if (candidates.Length == 1)
            return candidates[0];

        int bestWidth = -1;
        int pick = candidates[0];
        foreach (int t in candidates)
        {
            hand[t]--;
            var uk = UkeireCalculator.Calculate(hand, visible, calledMelds);
            hand[t]++;

            if (uk.Width > bestWidth)
            {
                bestWidth = uk.Width;
                pick = t;
            }
        }

        return pick;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Lo, ulong Hi, int Melds), int[]> ShantenCandidatesCache = new();

    /// <summary>Bounds the candidate cache; rollout work far exceeds this on the way there.</summary>
    private const int ShantenCandidatesCacheLimit = 1_000_000;

    /// <summary>
    /// Every tile whose cut leaves the hand at minimum shanten, memoised on the
    /// packed hand. Rollouts revisit the same hand shapes constantly - the same
    /// opponent states recur across determinizations - so the cache converts
    /// most of the policy cost into a lookup.
    /// </summary>
    private static int[] ShantenCandidates(int[] hand, int calledMelds)
    {
        var (lo, hi) = Pack(hand);
        var key = (lo, hi, calledMelds);
        if (ShantenCandidatesCache.TryGetValue(key, out int[]? cached))
            return cached;

        int best = int.MaxValue;
        List<int>? tiles = null;
        for (int t = 0; t < Tiles.Kinds; t++)
        {
            if (hand[t] == 0)
                continue;

            hand[t]--;
            int shanten = Shanten.Calculate(hand, calledMelds);
            hand[t]++;

            if (shanten < best)
            {
                best = shanten;
                tiles = [t];
            }
            else if (shanten == best)
            {
                (tiles ??= []).Add(t);
            }
        }

        int[] result = tiles?.ToArray() ?? [];
        if (ShantenCandidatesCache.Count < ShantenCandidatesCacheLimit)
            ShantenCandidatesCache[key] = result;

        return result;
    }

    /// <summary>
    /// Packs a hand the way <see cref="Shanten"/> does: three bits per tile
    /// kind across two words. Counts never exceed four in a hand, so the
    /// masking is lossless here too.
    /// </summary>
    private static (ulong Lo, ulong Hi) Pack(int[] hand)
    {
        ulong lo = 0, hi = 0;
        for (int i = 0; i < 21; i++)
            lo |= (ulong)(hand[i] & 7) << (i * 3);
        for (int i = 21; i < Tiles.Kinds; i++)
            hi |= (ulong)(hand[i] & 7) << ((i - 21) * 3);

        return (lo, hi);
    }

    /// <summary>
    /// Cheapest fold: anything already in a declarer's pond, then honours, then
    /// terminals, then everything else. Deliberately crude - the rollout's job
    /// is ranking candidates, not playing perfect defence.
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

    private int FindCaller(
        int[][] hands, int[] melds, bool[] riichi, int tile, int discarder, Random rng)
    {
        for (int step = 1; step < 4; step++)
        {
            int q = (discarder + step) % 4;
            if (riichi[q] || hands[q][tile] < 2 || melds[q] >= 4)
                continue;

            int before = Shanten.Calculate(hands[q], melds[q]);
            hands[q][tile] -= 2;
            int after = Shanten.Calculate(hands[q], melds[q] + 1);
            hands[q][tile] += 2;

            // A call must actually improve the hand and leave a legal yaku
            // path - the same two rules that stop the live bot from opening a
            // dead hand.
            bool worthIt = after < before && CallAdvisor.HasOpenYakuPath(hands[q], tile, CallKind.Pon);
            if (worthIt && (q == 0 || rng.NextDouble() < OpponentCallRate))
                return q;
        }

        return -1;
    }

    private static void ApplyExhaustiveDraw(int[][] hands, int[] melds, int[] scores)
    {
        var tenpai = new bool[4];
        for (int p = 0; p < 4; p++)
            tenpai[p] = Shanten.Calculate(hands[p], melds[p]) == 0;

        int noten = tenpai.Count(t => !t);
        int ready = 4 - noten;
        if (ready is 0 or 4)
            return;

        int gain = 3000 / ready;
        int loss = 3000 / noten;
        for (int seat = 0; seat < 4; seat++)
            scores[seat] += tenpai[seat] ? gain : -loss;
    }

    private static HandValueResult? Evaluate(
        TableState table, int[] hand, List<int> meldTiles, bool riichi, int winTile, bool tsumo, int seat)
    {
        int dora = seat == 0 ? table.AkaDora : 0;
        foreach (int indicator in table.DoraIndicators)
            dora += hand[Tiles.DoraFromIndicator(indicator)];

        return HandValue.Evaluate(new HandValueRequest
        {
            Concealed = hand,
            Melds = HandValue.ParseMelds(meldTiles),
            Method = tsumo ? WinMethod.Tsumo : WinMethod.Ron,
            WinningTile = winTile,
            Riichi = riichi,
            Dora = dora,
        });
    }

    /// <summary>
    /// A detached copy of a table state, safe to hand to a background thread.
    /// The read layer builds states from game memory on the framework thread,
    /// so a rollout task must own its own snapshot.
    /// </summary>
    public static TableState Copy(TableState table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var seats = new SeatView[table.Seats.Length];
        for (int i = 0; i < seats.Length; i++)
        {
            var seat = table.Seats[i];
            seats[i] = new SeatView
            {
                Discards = [.. seat.Discards],
                MeldTiles = [.. seat.MeldTiles],
                Riichi = seat.Riichi,
                RiichiDiscardIndex = seat.RiichiDiscardIndex,
                RiichiPondCounts = seat.RiichiPondCounts is { Length: 4 } counts ? [.. counts] : null,
            };
        }

        return new TableState
        {
            Hand = (int[])table.Hand.Clone(),
            CalledMelds = table.CalledMelds,
            Seats = seats,
            DoraIndicators = [.. table.DoraIndicators],
            Observations = table.Observations,
            WallRemaining = table.WallRemaining,
            Scores = [.. table.Scores],
            ForbiddenDiscards = [.. table.ForbiddenDiscards],
            SelfRiichi = table.SelfRiichi,
            DrawnTile = table.DrawnTile,
            RiichiDeclarationPending = table.RiichiDeclarationPending,
            AkaDora = table.AkaDora,
        };
    }

    /// <summary>
    /// 64-bit fingerprint of everything the search reads. The same idea as the
    /// advisor's signature, plus the riichi pond snapshots that
    /// <see cref="TableState.GenbutsuAgainst"/> depends on. The plugin uses it
    /// to match a published search result to the state it was computed for.
    /// </summary>
    public static long Signature(TableState table)
    {
        ulong hash = 1469598103934665603UL;

        void Mix(int value)
        {
            hash ^= (ulong)(uint)value;
            hash *= 1099511628211UL;
        }

        for (int i = 0; i < Tiles.Kinds; i++)
            Mix(table.Hand[i]);

        Mix(table.CalledMelds);
        Mix(table.WallRemaining);
        Mix((int)table.Observations);
        Mix(table.SelfRiichi ? 1 : 0);
        Mix(table.RiichiDeclarationPending ? 1 : 0);
        Mix(table.DrawnTile);
        Mix(table.AkaDora);

        foreach (int indicator in table.DoraIndicators)
            Mix(indicator);

        foreach (int forbidden in table.ForbiddenDiscards)
            Mix(forbidden);

        foreach (int score in table.Scores)
            Mix(score);

        foreach (var seat in table.Seats)
        {
            Mix(seat.Riichi ? 1 : 0);
            Mix(seat.RiichiDiscardIndex);
            if (seat.RiichiPondCounts is { Length: 4 } counts)
                foreach (int count in counts)
                    Mix(count);
            foreach (int tile in seat.Discards)
                Mix(tile);
            foreach (int tile in seat.MeldTiles)
                Mix(tile);
        }

        return (long)hash;
    }
}
