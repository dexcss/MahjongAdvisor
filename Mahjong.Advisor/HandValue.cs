namespace Mahjong.Advisor;

public enum WinMethod
{
    Ron,
    Tsumo,
}

/// <summary>
/// Everything a scoring decision is allowed to depend on. Winds default to
/// unknown (-1): a yaku that needs them is simply not awarded, which keeps the
/// estimate honest instead of guessing.
/// </summary>
public sealed record HandValueRequest
{
    /// <summary>Concealed tiles, 14 - 3 per meld, including the winning tile.</summary>
    public required int[] Concealed { get; init; }

    /// <summary>Called melds as tile lists (3 for chi/pon, 4 for kan).</summary>
    public IReadOnlyList<int[]> Melds { get; init; } = [];

    public WinMethod Method { get; init; } = WinMethod.Ron;

    public int WinningTile { get; init; } = -1;

    public bool Dealer { get; init; }

    public bool Riichi { get; init; }

    public bool DoubleRiichi { get; init; }

    public bool Ippatsu { get; init; }

    public bool Haitei { get; init; }

    public bool Houtei { get; init; }

    public bool Rinshan { get; init; }

    public bool Chankan { get; init; }

    /// <summary>Seat wind tile index (27-30), or -1 when unread.</summary>
    public int SeatWind { get; init; } = -1;

    /// <summary>Round wind tile index (27-30), or -1 when unread.</summary>
    public int RoundWind { get; init; } = -1;

    /// <summary>Dora in hand, including red fives.</summary>
    public int Dora { get; init; }

    /// <summary>Ura dora, only counted when <see cref="Riichi"/> is set.</summary>
    public int UraDora { get; init; }
}

public sealed record HandValueResult(
    IReadOnlyList<string> Yaku,
    int Han,
    int Fu,
    int YakumanCount,
    int RonPoints,
    int TsumoDealerPays,
    int TsumoOthersPay,
    bool Dealer)
{
    public int TotalPoints(WinMethod method) => method == WinMethod.Ron
        ? RonPoints
        : Dealer
            ? TsumoDealerPays * 3
            : TsumoDealerPays + (TsumoOthersPay * 2);
}

/// <summary>
/// Exact riichi scoring: yaku, fu, dora and the payment table. This replaces
/// guessing hand value with a formula; every estimate the advisor makes about a
/// completed hand goes through here once.
/// </summary>
public static class HandValue
{
    private readonly record struct Group(
        int Tile,
        bool IsRun,
        bool IsKan,
        bool Open,
        bool CompletedByRon);

    private readonly record struct WinRole(
        bool IsPair,
        bool IsRun,
        int GroupTile,
        int WaitFu,
        bool Ryanmen,
        bool RonCompletesTriplet);

    private sealed record Combo(int Pair, List<(int Tile, bool IsRun)> Sets);

    public static bool IsDragon(int tile) => tile is >= 31 and <= 33;

    public static bool IsWind(int tile) => tile is >= 27 and <= 30;

    public static HandValueResult? Evaluate(HandValueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Concealed.Length != Tiles.Kinds)
            throw new ArgumentException("Concealed hand must be a 34-count array.", nameof(request));

        int concealed = Tiles.Total(request.Concealed);
        if (concealed != 14 - (request.Melds.Count * 3))
            throw new ArgumentException(
                $"A hand with {request.Melds.Count} melds must hold {14 - (request.Melds.Count * 3)} concealed tiles, got {concealed}.",
                nameof(request));

        var open = new List<Group>(request.Melds.Count);
        foreach (int[] meld in request.Melds)
            open.Add(ParseMeld(meld));

        bool closed = open.Count == 0;
        HandValueResult? best = null;

        if (closed)
        {
            best = Pick(best, Kokushi(request));
            best = Pick(best, Chiitoitsu(request));
        }

        foreach (var combo in EnumerateCombos(request.Concealed, 4 - open.Count))
        {
            foreach (var role in Roles(request, combo))
            {
                best = Pick(best, ScoreStandard(request, open, combo, role, closed));
            }
        }

        return best;
    }

    /// <summary>
    /// Parses a flat meld list (as the reader stores it) into groups. A run of
    /// four equal tiles is a kan; three equal is a pon; anything else is a chi.
    /// </summary>
    public static List<int[]> ParseMelds(IReadOnlyList<int> flat)
    {
        var melds = new List<int[]>();
        int i = 0;
        while (i + 2 < flat.Count)
        {
            if (i + 3 < flat.Count
                && flat[i] == flat[i + 1]
                && flat[i + 1] == flat[i + 2]
                && flat[i + 2] == flat[i + 3])
            {
                melds.Add([flat[i], flat[i + 1], flat[i + 2], flat[i + 3]]);
                i += 4;
            }
            else
            {
                melds.Add([flat[i], flat[i + 1], flat[i + 2]]);
                i += 3;
            }
        }

        return melds;
    }

    private static Group ParseMeld(int[] meld)
    {
        if (meld.Length is not (3 or 4))
            throw new ArgumentException("A meld holds three or four tiles.", nameof(meld));

        bool kan = meld.Length == 4;
        bool triplet = meld.All(t => t == meld[0]);
        if (triplet)
            return new Group(meld[0], false, kan, true, false);

        var sorted = (int[])meld.Clone();
        Array.Sort(sorted);
        return new Group(sorted[0], true, false, true, false);
    }

    private static HandValueResult? Pick(HandValueResult? current, HandValueResult? candidate)
    {
        if (current is null)
            return candidate;
        if (candidate is null)
            return current;
        return candidate.RonPoints > current.RonPoints ? candidate : current;
    }

    private static List<Combo> EnumerateCombos(int[] hand, int setsNeeded)
    {
        var combos = new List<Combo>();
        if (setsNeeded < 0)
            return combos;

        var work = (int[])hand.Clone();
        for (int pair = 0; pair < Tiles.Kinds; pair++)
        {
            if (work[pair] < 2)
                continue;

            work[pair] -= 2;
            var sets = new List<(int Tile, bool IsRun)>(setsNeeded);
            ExtractSets(work, 0, setsNeeded, sets, pair, combos);
            work[pair] += 2;
        }

        return combos;
    }

    private static void ExtractSets(
        int[] hand,
        int start,
        int needed,
        List<(int Tile, bool IsRun)> sets,
        int pair,
        List<Combo> sink)
    {
        if (sets.Count == needed)
        {
            sink.Add(new Combo(pair, new List<(int, bool)>(sets)));
            return;
        }

        int i = start;
        while (i < Tiles.Kinds && hand[i] == 0)
            i++;
        if (i >= Tiles.Kinds)
            return;

        if (hand[i] >= 3)
        {
            hand[i] -= 3;
            sets.Add((i, false));
            ExtractSets(hand, i, needed, sets, pair, sink);
            sets.RemoveAt(sets.Count - 1);
            hand[i] += 3;
        }

        if (i < 27 && (i % 9) <= 6 && hand[i + 1] > 0 && hand[i + 2] > 0)
        {
            hand[i]--;
            hand[i + 1]--;
            hand[i + 2]--;
            sets.Add((i, true));
            ExtractSets(hand, i, needed, sets, pair, sink);
            sets.RemoveAt(sets.Count - 1);
            hand[i]++;
            hand[i + 1]++;
            hand[i + 2]++;
        }
    }

    private static IEnumerable<WinRole> Roles(HandValueRequest request, Combo combo)
    {
        if (request.WinningTile < 0)
        {
            // No winning tile known: assume the most optimistic shape (ryanmen).
            yield return new WinRole(false, false, -1, 0, true, false);
            yield break;
        }

        int win = request.WinningTile;
        bool any = false;

        if (combo.Pair == win)
        {
            any = true;
            yield return new WinRole(true, false, win, 2, false, false);
        }

        foreach (var (tile, isRun) in combo.Sets)
        {
            if (!isRun || win < tile || win > tile + 2)
                continue;

            any = true;
            int rank = Tiles.Rank(tile);
            bool kanchan = win == tile + 1;
            bool penchan = (win == tile + 2 && rank == 1) || (win == tile && rank == 7);
            int fu = kanchan || penchan ? 2 : 0;
            yield return new WinRole(false, true, tile, fu, !kanchan && !penchan, false);
        }

        foreach (var (tile, isRun) in combo.Sets)
        {
            if (isRun || tile != win)
                continue;

            any = true;
            yield return new WinRole(false, false, win, 0, false, request.Method == WinMethod.Ron);
        }

        if (!any)
            yield return new WinRole(false, false, -1, 0, true, false);
    }

    private static HandValueResult? ScoreStandard(
        HandValueRequest request,
        List<Group> open,
        Combo combo,
        WinRole role,
        bool closed)
    {
        var groups = new List<Group>(open.Count + combo.Sets.Count);
        groups.AddRange(open);

        bool ronTripletMarked = false;
        foreach (var (tile, isRun) in combo.Sets)
        {
            bool ronTriplet = false;
            if (!isRun
                && role.RonCompletesTriplet
                && !ronTripletMarked
                && tile == role.GroupTile)
            {
                ronTriplet = true;
                ronTripletMarked = true;
            }

            groups.Add(new Group(tile, isRun, false, false, ronTriplet));
        }

        int pair = combo.Pair;
        var names = new List<string>();
        int yakuman = 0;

        bool allRuns = groups.All(g => g.IsRun);
        bool allTriplets = groups.All(g => !g.IsRun);
        int dragonTriplets = groups.Count(g => !g.IsRun && IsDragon(g.Tile));
        int windTriplets = groups.Count(g => !g.IsRun && IsWind(g.Tile));
        int concealedTriplets = groups.Count(g => !g.IsRun && !g.Open && !g.CompletedByRon);
        int kans = groups.Count(g => g.IsKan);

        bool allHonors = groups.All(g => !g.IsRun && Tiles.IsHonor(g.Tile)) && Tiles.IsHonor(pair);
        bool allTerminals = groups.All(g => !g.IsRun && Tiles.IsTerminal(g.Tile)) && Tiles.IsTerminal(pair);
        bool allGreen = AllGreen(groups, pair);

        if (allTerminals)
        {
            yakuman++;
            names.Add("Chinroutou");
        }

        if (allHonors)
        {
            yakuman++;
            names.Add("Tsuuiisou");
        }

        if (allGreen)
        {
            yakuman++;
            names.Add("Ryuuiisou");
        }

        if (dragonTriplets == 3)
        {
            yakuman++;
            names.Add("Daisangen");
        }

        if (windTriplets == 4)
        {
            yakuman++;
            names.Add("Daisuushii");
        }
        else if (windTriplets == 3 && IsWind(pair))
        {
            yakuman++;
            names.Add("Shousuushii");
        }

        if (closed
            && concealedTriplets == 4
            && !(request.Method == WinMethod.Ron && role.RonCompletesTriplet))
        {
            yakuman++;
            names.Add("Suuankou");
        }

        if (kans == 4)
        {
            yakuman++;
            names.Add("Suukantsu");
        }

        if (IsChuuren(request.Concealed, closed))
        {
            yakuman++;
            names.Add("Chuuren Poutou");
        }

        if (yakuman > 0)
            return BuildYakuman(request, names, yakuman);

        int han = 0;
        var yaku = new List<string>();

        void Add(string name, int value)
        {
            han += value;
            yaku.Add(name);
        }

        bool pinfu = closed && allRuns && role.Ryanmen && !IsValuePair(pair, request);
        if (pinfu)
            Add("Pinfu", 1);

        if (closed && request.Method == WinMethod.Tsumo)
            Add("Menzen tsumo", 1);

        if (closed && request.Riichi)
            Add(request.DoubleRiichi ? "Double riichi" : "Riichi", request.DoubleRiichi ? 2 : 1);

        if (closed && request.Riichi && request.Ippatsu)
            Add("Ippatsu", 1);

        if (request.Haitei)
            Add("Haitei", 1);

        if (request.Houtei)
            Add("Houtei", 1);

        if (request.Rinshan)
            Add("Rinshan", 1);

        if (request.Chankan)
            Add("Chankan", 1);

        if (IsTanyao(groups, pair))
            Add("Tanyao", 1);

        var runStarts = groups.Where(g => g.IsRun).Select(g => g.Tile).ToList();
        if (closed)
        {
            var runCounts = runStarts.GroupBy(t => t).Select(g => g.Count()).ToList();
            int identicalPairs = runCounts.Count(c => c >= 2);
            if (identicalPairs >= 2)
                Add("Ryanpeikou", 3);
            else if (identicalPairs == 1)
                Add("Iipeikou", 1);
        }

        foreach (var group in groups)
        {
            if (group.IsRun)
                continue;

            if (IsDragon(group.Tile))
                Add($"Yakuhai ({HonorName(group.Tile)})", 1);
            if (group.Tile == request.SeatWind)
                Add($"Yakuhai ({HonorName(group.Tile)} seat)", 1);
            if (group.Tile == request.RoundWind)
                Add($"Yakuhai ({HonorName(group.Tile)} round)", 1);
        }

        bool anyRun = groups.Any(g => g.IsRun);
        bool pairIsTerminalOrHonor = Tiles.IsTerminal(pair) || Tiles.IsHonor(pair);
        bool allGroupsContainTerminal = groups.All(g => g.IsRun
            ? Tiles.Rank(g.Tile) is 1 or 7
            : Tiles.IsTerminal(g.Tile) || Tiles.IsHonor(g.Tile));

        bool anyHonor = groups.Any(g => !g.IsRun && Tiles.IsHonor(g.Tile)) || Tiles.IsHonor(pair);

        if (anyRun && pairIsTerminalOrHonor && allGroupsContainTerminal)
        {
            if (anyHonor)
                Add(closed ? "Chanta" : "Chanta (open)", closed ? 2 : 1);
            else
                Add(closed ? "Junchan" : "Junchan (open)", closed ? 3 : 2);
        }

        if (allTriplets
            && groups.All(g => Tiles.IsTerminal(g.Tile) || Tiles.IsHonor(g.Tile))
            && pairIsTerminalOrHonor)
        {
            Add("Honroutou", 2);
        }

        if (allTriplets)
            Add("Toitoi", 2);

        if (concealedTriplets >= 3)
            Add("Sanankou", 2);

        if (kans >= 3)
            Add("Sankantsu", 2);

        for (int rank = 1; rank <= 7; rank++)
        {
            int suits = 0;
            for (int suit = 0; suit < 3; suit++)
            {
                if (runStarts.Contains(suit * 9 + rank - 1))
                    suits++;
            }

            if (suits == 3)
            {
                Add(closed ? "Sanshoku doujun" : "Sanshoku doujun (open)", closed ? 2 : 1);
                break;
            }
        }

        var tripletTiles = groups.Where(g => !g.IsRun).Select(g => g.Tile).ToList();
        for (int rank = 1; rank <= 9; rank++)
        {
            int suits = 0;
            for (int suit = 0; suit < 3; suit++)
            {
                if (tripletTiles.Contains(suit * 9 + rank - 1))
                    suits++;
            }

            if (suits == 3)
            {
                Add("Sanshoku doukou", 2);
                break;
            }
        }

        for (int suit = 0; suit < 3; suit++)
        {
            int b = suit * 9;
            if (runStarts.Contains(b) && runStarts.Contains(b + 3) && runStarts.Contains(b + 6))
            {
                Add(closed ? "Ittsuu" : "Ittsuu (open)", closed ? 2 : 1);
                break;
            }
        }

        var suitsUsed = new HashSet<int>();
        bool honorsUsed = false;
        foreach (var group in groups)
        {
            if (group.IsRun)
            {
                suitsUsed.Add(group.Tile / 9);
                continue;
            }

            if (Tiles.IsHonor(group.Tile))
                honorsUsed = true;
            else
                suitsUsed.Add(group.Tile / 9);
        }

        if (Tiles.IsHonor(pair))
            honorsUsed = true;
        else
            suitsUsed.Add(pair / 9);

        if (suitsUsed.Count == 1)
        {
            if (honorsUsed)
                Add(closed ? "Honitsu" : "Honitsu (open)", closed ? 3 : 2);
            else
                Add(closed ? "Chinitsu" : "Chinitsu (open)", closed ? 6 : 5);
        }

        if (dragonTriplets == 2 && IsDragon(pair))
            Add("Shousangen", 2);

        if (yaku.Count == 0)
            return null;

        if (request.Dora > 0)
        {
            han += request.Dora;
            yaku.Add($"Dora x{request.Dora}");
        }

        if (request.Riichi && request.UraDora > 0)
        {
            han += request.UraDora;
            yaku.Add($"Ura x{request.UraDora}");
        }

        int fu = Fu(request, closed, pinfu, pair, role, groups);
        return Build(request, yaku, han, fu, 0);
    }

    private static HandValueResult? Kokushi(HandValueRequest request)
    {
        if (request.Melds.Count > 0)
            return null;

        int[] orphans = [0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33];
        foreach (int tile in orphans)
        {
            if (request.Concealed[tile] < 1)
                return null;
        }

        if (Tiles.Total(request.Concealed) != 14)
            return null;

        return BuildYakuman(request, ["Kokushi Musou"], 1);
    }

    private static HandValueResult? Chiitoitsu(HandValueRequest request)
    {
        if (request.Melds.Count > 0)
            return null;

        int pairs = 0;
        for (int i = 0; i < Tiles.Kinds; i++)
        {
            if (request.Concealed[i] >= 2)
                pairs++;
        }

        if (pairs != 7)
            return null;

        bool allSimple = true;
        bool allTerminalOrHonor = true;
        bool honors = false;
        var suits = new HashSet<int>();

        for (int i = 0; i < Tiles.Kinds; i++)
        {
            if (request.Concealed[i] == 0)
                continue;

            bool terminal = Tiles.IsTerminal(i);
            bool honor = Tiles.IsHonor(i);
            if (terminal || honor)
                allSimple = false;
            if (!terminal && !honor)
                allTerminalOrHonor = false;
            if (honor)
                honors = true;
            else
                suits.Add(i / 9);
        }

        var yaku = new List<string> { "Chiitoitsu" };
        int han = 2;

        if (allSimple)
        {
            han += 1;
            yaku.Add("Tanyao");
        }

        if (suits.Count == 1)
        {
            if (honors)
            {
                han += 3;
                yaku.Add("Honitsu");
            }
            else
            {
                han += 6;
                yaku.Add("Chinitsu");
            }
        }

        if (allTerminalOrHonor)
        {
            han += 2;
            yaku.Add("Honroutou");
        }

        if (request.Riichi)
        {
            han += request.DoubleRiichi ? 2 : 1;
            yaku.Add(request.DoubleRiichi ? "Double riichi" : "Riichi");
        }

        if (request.Ippatsu && request.Riichi)
        {
            han += 1;
            yaku.Add("Ippatsu");
        }

        if (request.Method == WinMethod.Tsumo)
        {
            han += 1;
            yaku.Add("Menzen tsumo");
        }

        if (request.Haitei)
        {
            han += 1;
            yaku.Add("Haitei");
        }

        if (request.Houtei)
        {
            han += 1;
            yaku.Add("Houtei");
        }

        if (request.Rinshan)
        {
            han += 1;
            yaku.Add("Rinshan");
        }

        if (request.Dora > 0)
        {
            han += request.Dora;
            yaku.Add($"Dora x{request.Dora}");
        }

        if (request.Riichi && request.UraDora > 0)
        {
            han += request.UraDora;
            yaku.Add($"Ura x{request.UraDora}");
        }

        return Build(request, yaku, han, 25, 0);
    }

    private static bool IsChuuren(int[] hand, bool closed)
    {
        if (!closed || Tiles.Total(hand) != 14)
            return false;

        for (int suit = 0; suit < 3; suit++)
        {
            int b = suit * 9;
            bool ok = hand[b] >= 3 && hand[b + 8] >= 3;
            for (int rank = 1; rank <= 7 && ok; rank++)
            {
                if (hand[b + rank] < 1)
                    ok = false;
            }

            if (!ok)
                continue;

            int total = 0;
            for (int rank = 0; rank < 9; rank++)
                total += hand[b + rank];

            if (total == 14)
                return true;
        }

        return false;
    }

    private static bool AllGreen(List<Group> groups, int pair)
    {
        static bool Green(int tile) => tile is 19 or 20 or 21 or 23 or 25 or 32;

        foreach (var group in groups)
        {
            if (group.IsRun)
            {
                if (!Green(group.Tile) || !Green(group.Tile + 1) || !Green(group.Tile + 2))
                    return false;
            }
            else if (!Green(group.Tile))
            {
                return false;
            }
        }

        return Green(pair);
    }

    private static bool IsTanyao(List<Group> groups, int pair)
    {
        if (Tiles.IsTerminal(pair) || Tiles.IsHonor(pair))
            return false;

        foreach (var group in groups)
        {
            if (group.IsRun)
            {
                if (Tiles.Rank(group.Tile) is 1 or 7)
                    return false;
            }
            else if (Tiles.IsTerminal(group.Tile) || Tiles.IsHonor(group.Tile))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValuePair(int pair, HandValueRequest request) =>
        IsDragon(pair) || pair == request.SeatWind || pair == request.RoundWind;

    private static int Fu(
        HandValueRequest request,
        bool closed,
        bool pinfu,
        int pair,
        WinRole role,
        List<Group> groups)
    {
        if (pinfu)
            return request.Method == WinMethod.Tsumo ? 20 : 30;

        int fu = 20;

        if (closed && request.Method == WinMethod.Ron)
            fu += 10;

        if (request.Method == WinMethod.Tsumo)
            fu += 2;

        if (IsDragon(pair))
            fu += 2;
        if (pair == request.SeatWind)
            fu += 2;
        if (pair == request.RoundWind)
            fu += 2;

        fu += role.WaitFu;

        foreach (var group in groups)
        {
            if (group.IsRun)
                continue;

            bool terminal = Tiles.IsTerminal(group.Tile) || Tiles.IsHonor(group.Tile);

            if (group.IsKan)
            {
                if (group.Open)
                    fu += terminal ? 16 : 8;
                else
                    fu += terminal ? 32 : 16;
            }
            else
            {
                int value = terminal ? 4 : 2;
                if (!group.Open && !group.CompletedByRon)
                    value *= 2;
                fu += value;
            }
        }

        fu = ((fu + 9) / 10) * 10;
        return Math.Max(fu, 30);
    }

    private static HandValueResult BuildYakuman(
        HandValueRequest request,
        List<string> names,
        int count)
    {
        int basePoints = 8000 * count;
        return Build(request, names, 13 * count, 0, count, basePoints);
    }

    private static HandValueResult Build(
        HandValueRequest request,
        List<string> yaku,
        int han,
        int fu,
        int yakuman,
        int? baseOverride = null)
    {
        int basePoints = baseOverride ?? BasePoints(han, fu);
        int ron = request.Dealer ? Up(basePoints * 6) : Up(basePoints * 4);
        int tsumoDealerPays = Up(basePoints * 2);
        int tsumoOthersPay = Up(request.Dealer ? basePoints * 2 : basePoints);

        return new HandValueResult(
            yaku,
            han,
            fu,
            yakuman,
            ron,
            tsumoDealerPays,
            tsumoOthersPay,
            request.Dealer);
    }

    private static int BasePoints(int han, int fu)
    {
        if (han >= 13)
            return 8000;
        if (han >= 11)
            return 6000;
        if (han >= 8)
            return 4000;
        if (han >= 6)
            return 3000;
        if (han >= 5)
            return 2000;
        return Math.Min(fu * (1 << (2 + han)), 2000);
    }

    private static int Up(int value) => ((value + 99) / 100) * 100;

    private static string HonorName(int tile) => tile switch
    {
        27 => "East",
        28 => "South",
        29 => "West",
        30 => "North",
        31 => "Haku",
        32 => "Hatsu",
        _ => "Chun",
    };
}
