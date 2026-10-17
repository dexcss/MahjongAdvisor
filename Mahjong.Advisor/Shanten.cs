namespace Mahjong.Advisor;

/// <summary>
/// Shanten distance: -1 = complete hand, 0 = tenpai, n = n tiles from tenpai.
/// Works on 3n+1 and 3n+2 hands alike.
/// </summary>
public static class Shanten
{
    /// <summary>
    /// Best shanten across all three hand shapes. Seven-pairs and thirteen-orphans
    /// are only reachable with a fully concealed hand, so they are skipped once
    /// any meld has been called.
    /// </summary>
    public static int Calculate(int[] hand, int calledMelds = 0)
    {
        ArgumentNullException.ThrowIfNull(hand);

        // Ukeire asks about the same shapes over and over — every candidate draw
        // re-walks a hand differing by one tile. Without this the decomposition
        // dominates everything, which is what stalled the simulator.
        var key = Pack(hand, calledMelds);
        if (Cache.TryGetValue(key, out int cached))
            return cached;

        int best = Standard(hand, calledMelds);
        if (calledMelds == 0)
        {
            best = Math.Min(best, SevenPairs(hand));
            best = Math.Min(best, ThirteenOrphans(hand));
        }

        if (Cache.Count < CacheLimit)
            Cache[key] = best;

        return best;
    }

    private const int CacheLimit = 4_000_000;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong, ulong), int> Cache = new();

    /// <summary>Clears the memo. Only needed if memory matters more than speed.</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>
    /// Packs the hand into two words. Counts never exceed four, so three bits
    /// each is enough for all 34 tiles, with the meld count alongside.
    /// </summary>
    private static (ulong Lo, ulong Hi) Pack(int[] hand, int calledMelds)
    {
        ulong lo = 0, hi = 0;
        for (int i = 0; i < 21; i++)
            lo |= (ulong)(hand[i] & 7) << (i * 3);
        for (int i = 21; i < Tiles.Kinds; i++)
            hi |= (ulong)(hand[i] & 7) << ((i - 21) * 3);

        hi |= (ulong)(calledMelds & 7) << 60;
        return (lo, hi);
    }

    /// <summary>Four melds plus a pair.</summary>
    public static int Standard(int[] hand, int calledMelds = 0)
    {
        ArgumentNullException.ThrowIfNull(hand);
        var work = (int[])hand.Clone();
        int score = Decompose(work, 0, calledMelds, 0, false);
        return 8 - score;
    }

    public static int SevenPairs(int[] hand)
    {
        ArgumentNullException.ThrowIfNull(hand);
        int pairs = 0, kinds = 0;
        for (int i = 0; i < Tiles.Kinds; i++)
        {
            if (hand[i] >= 1) kinds++;
            if (hand[i] >= 2) pairs++;
        }

        int s = 6 - pairs;

        // Fewer than seven distinct tiles means duplicates beyond a pair are dead
        // weight and must be replaced.
        if (kinds < 7)
            s += 7 - kinds;
        return s;
    }

    public static int ThirteenOrphans(int[] hand)
    {
        ArgumentNullException.ThrowIfNull(hand);
        int kinds = 0;
        bool pair = false;
        foreach (int t in Tiles.Terminals)
        {
            if (hand[t] >= 1) kinds++;
            if (hand[t] >= 2) pair = true;
        }

        return 13 - kinds - (pair ? 1 : 0);
    }

    /// <summary>
    /// Maximises 2*melds + partials + pair over every decomposition.
    /// Returns the block score; the caller converts it to shanten.
    /// </summary>
    private static int Decompose(int[] c, int i, int melds, int partials, bool hasPair)
    {
        while (i < Tiles.Kinds && c[i] == 0)
            i++;

        if (i == Tiles.Kinds)
        {
            // Exactly one pair is required. Five non-pair blocks means one of them
            // can never be completed, so it does not count.
            if (melds + partials == 5 && !hasPair)
                return (2 * melds) + partials - 1;
            return (2 * melds) + partials + (hasPair ? 1 : 0);
        }

        int best = int.MinValue;
        int blocks = melds + partials + (hasPair ? 1 : 0);
        bool suited = i < Tiles.FirstHonor;
        int rank = suited ? i % 9 : -1;

        if (c[i] >= 3)
        {
            c[i] -= 3;
            best = Math.Max(best, Decompose(c, i, melds + 1, partials, hasPair));
            c[i] += 3;
        }

        if (suited && rank <= 6 && c[i + 1] > 0 && c[i + 2] > 0)
        {
            c[i]--; c[i + 1]--; c[i + 2]--;
            best = Math.Max(best, Decompose(c, i, melds + 1, partials, hasPair));
            c[i]++; c[i + 1]++; c[i + 2]++;
        }

        if (blocks < 5)
        {
            if (c[i] >= 2)
            {
                if (!hasPair)
                {
                    c[i] -= 2;
                    best = Math.Max(best, Decompose(c, i, melds, partials, true));
                    c[i] += 2;
                }

                c[i] -= 2;
                best = Math.Max(best, Decompose(c, i, melds, partials + 1, hasPair));
                c[i] += 2;
            }

            if (suited && rank <= 7 && c[i + 1] > 0)
            {
                c[i]--; c[i + 1]--;
                best = Math.Max(best, Decompose(c, i, melds, partials + 1, hasPair));
                c[i]++; c[i + 1]++;
            }

            if (suited && rank <= 6 && c[i + 2] > 0)
            {
                c[i]--; c[i + 2]--;
                best = Math.Max(best, Decompose(c, i, melds, partials + 1, hasPair));
                c[i]++; c[i + 2]++;
            }
        }

        // Leave this tile unused. Always available, so every path reaches a leaf.
        int held = c[i];
        c[i] = 0;
        best = Math.Max(best, Decompose(c, i + 1, melds, partials, hasPair));
        c[i] = held;

        return best;
    }
}
