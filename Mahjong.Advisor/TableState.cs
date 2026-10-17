namespace Mahjong.Advisor;

/// <summary>
/// Which parts of the table view were actually observed. The advisor refuses to
/// use a field that was synthesised, rather than silently treating a placeholder
/// as real -- that mistake is what makes a blind push/fold model look confident.
/// </summary>
[Flags]
public enum Observed : ushort
{
    None = 0,
    OwnHand = 1 << 0,
    Discards = 1 << 1,
    OpponentMelds = 1 << 2,
    OpponentRiichi = 1 << 3,
    RiichiTurn = 1 << 4,
    DoraIndicators = 1 << 5,
    Seats = 1 << 6,

    /// <summary>Everything the safety model needs to be trustworthy.</summary>
    SafetyReady = Discards | OpponentMelds | OpponentRiichi | RiichiTurn,
}

/// <summary>One opponent's public state.</summary>
public sealed class SeatView
{
    /// <summary>Discards in order. Needed for both genbutsu and riichi-turn logic.</summary>
    public List<int> Discards { get; set; } = [];

    /// <summary>Tiles in this seat's open melds, as a flat list.</summary>
    public List<int> MeldTiles { get; set; } = [];

    public bool Riichi { get; set; }

    /// <summary>Index into <see cref="Discards"/> at which riichi was declared, or -1.</summary>
    public int RiichiDiscardIndex { get; set; } = -1;

    /// <summary>
    /// Completed called sets. A meld is three tiles (chi/pon) or four (kan);
    /// integer division recovers the count from the flat tile list for every
    /// legal combination.
    /// </summary>
    public int MeldCount => MeldTiles.Count / 3;
}

public sealed class TableState
{
    /// <summary>Own concealed hand as a 34-count array.</summary>
    public required int[] Hand { get; init; }

    /// <summary>Melds already called from own hand.</summary>
    public int CalledMelds { get; init; }

    /// <summary>Index 0 is self; 1-3 are shimocha, toimen, kamicha.</summary>
    public required SeatView[] Seats { get; init; }

    public List<int> DoraIndicators { get; init; } = [];

    public Observed Observations { get; init; } = Observed.None;

    /// <summary>Tiles remaining in the wall. Drives push/fold aggression.</summary>
    public int WallRemaining { get; init; } = 70;

    public bool AnyThreat => Seats.Skip(1).Any(s => s.Riichi);

    /// <summary>
    /// Every tile this seat can see: own hand, own melds are already in Seats[0],
    /// all ponds, all open melds, and the dora indicators.
    /// </summary>
    public int[] BuildVisible()
    {
        var visible = new int[Tiles.Kinds];
        for (int i = 0; i < Tiles.Kinds; i++)
            visible[i] = Hand[i];

        foreach (var seat in Seats)
        {
            foreach (int t in seat.Discards)
                visible[t]++;
            foreach (int t in seat.MeldTiles)
                visible[t]++;
        }

        foreach (int t in DoraIndicators)
            visible[t]++;

        for (int i = 0; i < Tiles.Kinds; i++)
            visible[i] = Math.Min(visible[i], 4);

        return visible;
    }

    /// <summary>Current points, seat order starting with us. Empty when unread.</summary>
    public IReadOnlyList<int> Scores { get; init; } = [];

    /// <summary>
    /// Discards the client will not accept this turn, from swap-calling rules.
    /// Suggesting one of these gives advice that literally cannot be followed.
    /// </summary>
    public IReadOnlyList<int> ForbiddenDiscards { get; init; } = [];

    /// <summary>
    /// We have declared riichi, so the hand is frozen: the only legal discard is
    /// the tile just drawn.
    /// </summary>
    public bool SelfRiichi { get; init; }

    /// <summary>The tile we just drew, or -1. After riichi it is the only option.</summary>
    public int DrawnTile { get; init; } = -1;

    /// <summary>
    /// Riichi is declared but the declaration discard has not happened yet. This
    /// turn is still a free choice, limited to tiles that keep the hand ready.
    /// </summary>
    public bool RiichiDeclarationPending { get; init; }

    /// <summary>Red fives held, which each count as a dora.</summary>
    public int AkaDora { get; init; }

    /// <summary>
    /// Dora held in hand. Each one is an extra han, so this is the main thing
    /// separating a 1000-point hand from a mangan.
    /// </summary>
    public int CountDora()
    {
        int total = AkaDora;
        foreach (int indicator in DoraIndicators)
            total += Hand[DoraFromIndicator(indicator)];
        return total;
    }

    private static int DoraFromIndicator(int indicator) => Tiles.DoraFromIndicator(indicator);

    /// <summary>
    /// Tiles that are 100% safe against a given seat: anything in their own pond,
    /// plus anything discarded by anyone after their riichi declaration.
    /// </summary>
    public HashSet<int> GenbutsuAgainst(int seatIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seatIndex);
        var safe = new HashSet<int>(Seats[seatIndex].Discards);

        var target = Seats[seatIndex];
        if (!target.Riichi || target.RiichiDiscardIndex < 0)
            return safe;

        // Anything anyone else has discarded since the declaration passed without
        // a ron call, so it is dead against this seat too.
        int turnsSince = target.Discards.Count - target.RiichiDiscardIndex;
        for (int s = 0; s < Seats.Length; s++)
        {
            if (s == seatIndex)
                continue;
            var other = Seats[s];
            int from = Math.Max(0, other.Discards.Count - turnsSince);
            for (int d = from; d < other.Discards.Count; d++)
                safe.Add(other.Discards[d]);
        }

        return safe;
    }
}
