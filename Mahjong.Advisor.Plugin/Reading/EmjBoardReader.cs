using Mahjong.Advisor;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace MahjongAdvisor.Reading;

public sealed record BoardRead(
    TableState? Table,
    LayoutProfile? Profile,
    string Status,
    int StateCode,
    int RejectedTiles)
{
    /// <summary>
    /// The hand in addon slot order, which is the order the client draws it
    /// left to right. Lets the overlay say "3rd from the left" instead of
    /// making you decode tile notation.
    /// </summary>
    public IReadOnlyList<int> HandOrder { get; init; } = [];

    /// <summary>True when the client's tile texture ids shifted and were retuned.</summary>
    public bool TextureBaseShifted { get; init; }

    public bool Ok => Table is not null;

    public static BoardRead Failed(string status, LayoutProfile? profile = null) =>
        new(null, profile, status, -1, 0);
}

/// <summary>
/// Reads the Doman Mahjong addon into a <see cref="TableState"/>.
/// Must be called on the framework thread -- it dereferences game memory.
/// </summary>
public sealed unsafe class EmjBoardReader
{
    private readonly IGameGui gameGui;
    private readonly IReadOnlyList<LayoutProfile> profiles;
    private readonly NodeInspector inspector;

    public EmjBoardReader(IGameGui gameGui, IReadOnlyList<LayoutProfile> profiles, NodeInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(gameGui);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(inspector);
        this.gameGui = gameGui;
        this.profiles = profiles;
        this.inspector = inspector;
    }

    public LayoutProfile? ActiveProfile { get; private set; }

    /// <summary>Result of the last automatic pond detection, for the setup window.</summary>
    public PondMapping? LastAutoMap { get; private set; }

    /// <summary>Ids accepted after being detected twice in a row.</summary>
    public int[] AutoPondNodeIds { get; private set; } = [];

    private int[] pendingAutoMap = [];
    private readonly List<int>[] previousPonds = [[], [], [], []];
    private int[] previousCensus = new int[Tiles.Kinds];
    private int[] previousHand = new int[Tiles.Kinds];
    private int previousMelds = -1;
    private List<int> pendingKuikae = [];
    private bool riichiDiscardDone;
    private int[] lastCompleteHand = [];
    private int lastCompleteMelds = -1;

    /// <summary>
    /// Our own called tiles, recovered from hand deltas. Own melds are visible to
    /// us even when the meld container nodes cannot be read, and leaving them out
    /// of the visible-tile count makes every ukeire estimate slightly too wide.
    /// </summary>
    private readonly List<int> ownMeldTiles = [];

    /// <summary>Finds the discard piles in memory, which beats reading them off screen.</summary>
    public DiscardArrayLocator DiscardArrays { get; } = new();

    private readonly bool[] riichiDeclared = new bool[4];
    private readonly int[] riichiAtIndex = [-1, -1, -1, -1];
    private int previousWall = -1;

    public int[] LastScores { get; private set; } = [];

    /// <summary>How many tiles in each pile the client draws sideways.</summary>
    public int[] RotatedPerSeat { get; private set; } = new int[4];

    /// <summary>Discard counts straight from the count bytes, before any tile reading.</summary>
    public int[] DiscardCountBytes { get; private set; } = new int[4];

    /// <summary>Texture ids inside piles that did not decode as tiles.</summary>
    public IReadOnlyCollection<int> UnknownPondTextures => inspector.UnknownPondTextures;

    /// <summary>Nodes that appeared this tick. The riichi stick shows up here.</summary>
    public List<NodeInspector.ShownNode> NewlyVisible { get; private set; } = [];

    /// <summary>Most recent tile added to any pond, and whose pond it was.</summary>
    public int LastDiscardTile { get; private set; } = -1;

    public int LastDiscardSeat { get; private set; } = -1;

    /// <summary>Tiles that appeared on the board recently, newest first.</summary>
    public List<int> RecentBoardAdditions { get; } = [];

    /// <summary>Forget the detected mapping, e.g. when the table closes.</summary>
    public void ResetAutoMap()
    {
        AutoPondNodeIds = [];
        pendingAutoMap = [];
        LastAutoMap = null;
        DiscardArrays.Reset();
        LastDiscardTile = -1;
        LastDiscardSeat = -1;
        for (int i = 0; i < 4; i++)
            previousPonds[i].Clear();
        previousCensus = new int[Tiles.Kinds];
        previousHand = new int[Tiles.Kinds];
        previousMelds = -1;
        pendingKuikae = [];
        riichiDiscardDone = false;
        lastCompleteHand = [];
        lastCompleteMelds = -1;
        ownMeldTiles.Clear();
        RecentBoardAdditions.Clear();
        previousWall = -1;
        ClearRiichi();
    }

    /// <summary>Returns the live addon pointer, or null when no table is open.</summary>
    public AtkUnitBase* TryGetAddon(out LayoutProfile? profile)
    {
        foreach (var candidate in profiles)
        {
            nint ptr = gameGui.GetAddonByName(candidate.AddonName);
            if (ptr == nint.Zero)
                continue;

            var unit = (AtkUnitBase*)ptr;
            if (!unit->IsVisible || unit->RootNode is null)
                continue;

            profile = candidate;
            ActiveProfile = candidate;
            return unit;
        }

        profile = null;
        return null;
    }

    public BoardRead Read()
    {
        var unit = TryGetAddon(out var profile);
        if (unit is null || profile is null)
            return BoardRead.Failed("No mahjong table open.");

        int stateCode = ReadAtkInt(unit, profile.StateCodes.StateCodeValueIndex, -1);

        // Hand array: HandSize consecutive int32s.
        var raw = new int[profile.Limits.HandSize];
        var baseAddress = (byte*)unit;
        for (int i = 0; i < raw.Length; i++)
            raw[i] = *(int*)(baseAddress + profile.Offsets.HandArrayStart + (i * 4));

        int textureBase = TileDecoder.ResolveTextureBase(raw, profile.TileTextureBase, out bool shifted);

        // One node walk per read, shared by the census, pond detection, containers
        // and the riichi markers. The board cannot change inside a single tick.
        inspector.BeginRead(unit, textureBase);

        var (decoded, rejected) = TileDecoder.DecodeMany(raw, textureBase);
        if (decoded.Count == 0)
            return BoardRead.Failed("Hand array empty - table is probably still dealing.", profile);

        int calledMelds = InferCalledMelds(decoded.Count);
        if (calledMelds < 0)
        {
            return BoardRead.Failed(
                $"Hand has {decoded.Count} tiles, which is not a legal shape. Mid-transition; skipping.",
                profile);
        }

        var handCounts = TileDecoder.ToCounts(decoded);

        var seats = new SeatView[4];
        for (int i = 0; i < 4; i++)
            seats[i] = new SeatView();

        var rotatedCounts = new int[4];

        // Scores are the giveaway for riichi: declaring costs exactly 1000 points,
        // and nothing else moves a score by that amount in the middle of a hand.
        var scores = new int[4];
        bool scoresSane = true;
        for (int i = 0; i < 4; i++)
        {
            int value = *(int*)(baseAddress + profile.Offsets.SeatScores[i]);
            if (value is < 0 or > 1_000_000 || value > profile.Limits.ScoreSanityMax)
            {
                scoresSane = false;
                break;
            }

            scores[i] = value;
        }

        if (scoresSane)
            LastScores = scores;

        var observations = Observed.OwnHand;

        // Discard counts always readable; the tiles themselves need node ids.
        var discardCounts = new int[4];
        for (int i = 0; i < 4; i++)
        {
            int value = *(baseAddress + profile.Offsets.SeatDiscardCountBytes[i]);
            discardCounts[i] = value <= profile.Limits.DiscardCountSanityMax ? value : 0;
        }

        // Work the discard piles out from tile positions when they have not been
        // set by hand. Requires two identical detections in a row before it is
        // trusted, so a half-drawn board cannot lock in a bad mapping.
        int[] pondIds = profile.HasPondNodes ? profile.PondContainerNodeIds : AutoPondNodeIds;
        int[] markerSource = pondIds;

        // Watch every tile on the board, not just mapped ponds. When exactly one
        // new tile appears, that is what was just discarded -- and it works before
        // the piles have been located.
        var census = inspector.CountAllTiles(unit, textureBase);
        for (int t = 0; t < Tiles.Kinds; t++)
            census[t] = Math.Max(0, census[t] - handCounts[t]);

        int grew = -1;
        int growth = 0;
        for (int t = 0; t < Tiles.Kinds; t++)
        {
            int delta = census[t] - previousCensus[t];
            if (delta > 0)
            {
                grew = t;
                growth += delta;
            }
        }

        if (grew >= 0 && growth == 1)
        {
            LastDiscardTile = grew;
            LastDiscardSeat = -1;
            RecentBoardAdditions.Insert(0, grew);
            if (RecentBoardAdditions.Count > 4)
                RecentBoardAdditions.RemoveAt(RecentBoardAdditions.Count - 1);
        }

        previousCensus = census;

        // Popups overlay extra tiles, so only look for the piles during quiet
        // states -- and stop looking once a mapping has been accepted.
        bool quietState = stateCode != profile.StateCodes.CallPrompt
            && stateCode != profile.StateCodes.CallPromptList
            && stateCode != profile.StateCodes.ChiVariantSelect
            && stateCode != profile.StateCodes.SelfDeclareList
            && stateCode != profile.StateCodes.HandResult;

        if (!profile.HasPondNodes && AutoPondNodeIds.Length != 4 && quietState)
        {
            var mapping = inspector.MapPondsByCount(unit, textureBase, discardCounts);
            if (!mapping.Ok)
                mapping = inspector.AutoMapPonds(unit, textureBase, handCounts);
            LastAutoMap = mapping;

            if (mapping.Ok)
            {
                if (pendingAutoMap.SequenceEqual(mapping.PondNodeIds))
                {
                    AutoPondNodeIds = mapping.PondNodeIds;
                    pondIds = AutoPondNodeIds;
                    markerSource = AutoPondNodeIds;
                }
                else
                {
                    pendingAutoMap = mapping.PondNodeIds;
                }
            }
            else
            {
                pendingAutoMap = [];
            }
        }

        // Memory first: one fixed offset per seat beats guessing at screen positions.
        if (DiscardArrays.TryLocate((byte*)unit, profile, discardCounts, textureBase))
        {
            bool allRead = true;
            var fromMemory = new List<int>[4];

            for (int seat = 0; seat < 4; seat++)
            {
                var tiles = DiscardArrays.ReadSeat(
                    (byte*)unit, profile, seat, discardCounts[seat], textureBase);
                if (tiles is null || tiles.Count != discardCounts[seat])
                {
                    allRead = false;
                    break;
                }

                fromMemory[seat] = tiles;
            }

            if (allRead)
            {
                for (int seat = 0; seat < 4; seat++)
                    seats[seat].Discards = fromMemory[seat];

                observations |= Observed.Discards;
                TrackNewDiscard(seats);
                pondIds = [];
            }
            else
            {
                DiscardArrays.Reset();
            }
        }

        if (pondIds.Length == 4)
        {
            // The client publishes an exact discard count per seat. Any mapping
            // that disagrees with it is wrong, whatever it looks like on screen.
            if (!PondsMatchCounts(unit, profile, pondIds, discardCounts, textureBase))
            {
                if (!profile.HasPondNodes)
                {
                    AutoPondNodeIds = [];
                    pendingAutoMap = [];
                }

                LastAutoMap = new PondMapping([], "Piles disagree with the client's counts - rejected.");
                pondIds = [];
                markerSource = [];
            }
        }

        if (pondIds.Length == 4)
        {
            bool allRead = true;
            for (int seat = 0; seat < 4; seat++)
            {
                var pond = inspector.ReadTileContainer(
                    unit, pondIds[seat], textureBase);
                if (pond is null)
                {
                    allRead = false;
                    break;
                }

                seats[seat].Discards = pond.Select(p => p.Tile).ToList();

                // Kept for the log only. The rotation flag is never set by this
                // client, so it must not be allowed to declare riichi.
                rotatedCounts[seat] = pond.Count(p => p.Rotated);
            }

            if (allRead)
            {
                // With the piles read off screen, use them to find the same data
                // in memory -- steadier, and exact in ordering.
                DiscardArrays.TryLocateAgainst(
                    (byte*)unit,
                    profile,
                    [.. seats.Select(v => (IReadOnlyList<int>)v.Discards)],
                    textureBase);

                // Keep checking a mapping we picked ourselves. It can be accepted
                // early on a nearly empty board and only later prove wrong, which
                // is exactly what a frozen mapping can never notice.
                if (AutoPondNodeIds.Length == 4 && !profile.HasPondNodes
                    && IsMappingImplausible(seats))
                {
                    AutoPondNodeIds = [];
                    pendingAutoMap = [];
                    LastAutoMap = new PondMapping(
                        [], "Previous mapping went lopsided - re-detecting.");
                    observations &= ~Observed.Discards;
                }
                else
                {
                    observations |= Observed.Discards;
                    TrackNewDiscard(seats);
                }
            }
        }

        bool meldsRead = false;
        if (profile.HasMeldNodes)
        {
            bool allRead = true;
            for (int seat = 0; seat < 4; seat++)
            {
                var melds = inspector.ReadTileContainers(
                    unit, profile.MeldNodeIdsForSeat(seat), textureBase);

                // A missing container means unknown, not empty. Counts of 1, 2 or
                // 5 cannot be a legal set of called melds, so reject them too.
                if (melds is null || !IsPlausibleMeldCount(melds.Count))
                {
                    allRead = false;
                    break;
                }

                seats[seat].MeldTiles = melds.Select(m => m.Tile).ToList();
            }

            // Our own meld count is known from the hand shape. Use it to validate
            // the mapping before trusting any of the four seats.
            if (allRead && SelfMeldCountMatches(seats[0].MeldTiles.Count, calledMelds))
            {
                observations |= Observed.OpponentMelds;
                meldsRead = true;

                ownMeldTiles.Clear();
                ownMeldTiles.AddRange(seats[0].MeldTiles);
            }
            else
            {
                // Do not leave a half-read or unvalidated mapping in the state.
                for (int seat = 0; seat < 4; seat++)
                    seats[seat].MeldTiles = [];
            }
        }

        // A fresh hand: forget who was in riichi.
        // Derived from discards, which is steadier than the AtkValue.
        int wallNow = profile.Limits.WallInitial - discardCounts.Sum();
        bool newHand = discardCounts.Sum() == 0 || (previousWall >= 0 && wallNow > previousWall + 4);
        if (newHand)
        {
            ClearRiichi();
            RecentBoardAdditions.Clear();
            ownMeldTiles.Clear();
            pendingKuikae = [];
            lastCompleteHand = [];
            lastCompleteMelds = -1;
        }

        for (int i = 0; i < 4; i++)
        {
            if (!riichiDeclared[i])
                continue;
            seats[i].Riichi = true;
            seats[i].RiichiDiscardIndex = riichiAtIndex[i];
        }

        // The riichi marker sits next to each pile and only appears on declaration.
        // Score-watching cannot work here: the client does not deduct the stick
        // from the displayed points until the hand settles.
        int[] markers = profile.RiichiMarkerNodeIds.Length == 4
            ? profile.RiichiMarkerNodeIds
            : markerSource.Length == 4 && observations.HasFlag(Observed.Discards)
                ? [.. markerSource.Select(id => id + 1)]
                : [];

        // On the first read of a new hand the old markers can still be on screen
        // for a tick. They belong to the previous hand and must not re-declare.
        if (!newHand && markers.Length == 4 && markers.All(id => id > 0))
        {
            var visible = inspector.AreNodesVisible(unit, markers);
            for (int i = 0; i < 4; i++)
            {
                if (!visible[i] || riichiDeclared[i])
                    continue;

                riichiDeclared[i] = true;
                riichiAtIndex[i] = Math.Max(0, seats[i].Discards.Count - 1);
            }

            observations |= Observed.OpponentRiichi | Observed.RiichiTurn;
        }

        var dora = new List<int>();
        for (int i = 0; i < 5; i++)
        {
            int value = *(int*)(baseAddress + profile.Offsets.DoraIndicator + (i * 4));
            if (value <= 0)
                break;
            var d = TileDecoder.Decode(value, textureBase);
            if (d.IsValid)
                dora.Add(d.Tile);
        }

        if (dora.Count > 0)
            observations |= Observed.DoraIndicators;

        // AtkValue layout changes with the addon state, so a fixed index is not a
        // reliable wall count -- it read 0 at state 6 while the board showed 46.
        // Derive from discards instead: draws taken = every discard, plus the tile
        // whoever is on turn is still holding.
        int held = decoded.Count % 3 == 2 ? 1 : 0;
        int wall = Math.Max(0, profile.Limits.WallInitial - discardCounts.Sum() - held);

        // Only trust the AtkValue if it agrees closely; otherwise keep the derived one.
        int reported = ReadAtkInt(unit, profile.StateCodes.WallCountValueIndex, -1);
        if (reported >= 0 && reported <= profile.Limits.WallInitial && Math.Abs(reported - wall) <= 2)
            wall = reported;

        RotatedPerSeat = rotatedCounts;
        DiscardCountBytes = discardCounts;
        NewlyVisible = inspector.TakeNewlyVisible(unit);

        if (newHand)
            inspector.ResetVisibility();

        // Straight after a call the client bars some discards. Work out which by
        // seeing what left the hand and what was claimed.
        if (previousMelds >= 0 && calledMelds > previousMelds)
        {
            var removed = new List<int>();
            for (int t = 0; t < Tiles.Kinds; t++)
            {
                for (int n = 0; n < previousHand[t] - handCounts[t]; n++)
                    removed.Add(t);
            }

            pendingKuikae = [.. Kuikae.ForbiddenFromHandDelta(removed)];

            // Our own meld is visible to us even when the meld nodes are not.
            // One call per read is reconstructable; several are not.
            if (calledMelds - previousMelds == 1)
                TrackOwnMeldTiles(removed);
            else
                ownMeldTiles.Clear();
        }
        else if (calledMelds != previousMelds || Tiles.Total(handCounts) % 3 == 1)
        {
            // Our discard has happened; the restriction is spent.
            pendingKuikae = [];
        }

        previousMelds = calledMelds;
        previousHand = handCounts;

        if (!meldsRead && ownMeldTiles.Count > 0)
            seats[0].MeldTiles = [.. ownMeldTiles];

        // The drawn tile is whichever one appeared since the hand was last at
        // rest, not whatever sits in the final array slot. The slot order does
        // not match the display, so reading the last entry named the wrong tile.
        int drawn = -1;
        int handTotal = Tiles.Total(handCounts);

        if (handTotal % 3 == 1)
        {
            lastCompleteHand = (int[])handCounts.Clone();
            lastCompleteMelds = calledMelds;
        }
        else if (handTotal % 3 == 2
            && lastCompleteHand.Length == Tiles.Kinds
            && calledMelds == lastCompleteMelds)
        {
            for (int t = 0; t < Tiles.Kinds; t++)
            {
                if (handCounts[t] > lastCompleteHand[t])
                {
                    drawn = t;
                    break;
                }
            }
        }

        // Nothing to compare against yet: fall back to the last slot. This is a
        // guess; the hand-delta path above is the reliable one.
        if (drawn < 0 && decoded.Count > 0 && decoded.Count % 3 == 2)
            drawn = decoded[^1].Tile;

        // Only the marker node decides this. The old frozen-hand backstop could
        // not tell riichi from ordinary tsumogiri while quietly tenpai, and it
        // locked the hand twice in one game without a declaration.
        bool selfRiichi = seats[0].Riichi;

        // The declaration discard is still a free choice among ready-keeping tiles.
        if (!selfRiichi)
            riichiDiscardDone = false;
        else if (handTotal % 3 == 1)
            riichiDiscardDone = true;

        var table = new TableState
        {
            Hand = handCounts,
            CalledMelds = calledMelds,
            Seats = seats,
            DoraIndicators = dora,
            Observations = observations,
            WallRemaining = wall,
            AkaDora = decoded.Count(d => d.IsRed),
            ForbiddenDiscards = pendingKuikae,
            SelfRiichi = selfRiichi,
            RiichiDeclarationPending = selfRiichi && !riichiDiscardDone,
            DrawnTile = drawn,
            Scores = scoresSane ? scores : [],
        };

        return new BoardRead(table, profile, "ok", stateCode, rejected)
        {
            HandOrder = [.. decoded.Select(d => d.Tile)],
            TextureBaseShifted = shifted,
        };
    }

    /// <summary>
    /// A legal meld strip holds 0, or 3-4 tiles per set. One or two tiles cannot
    /// be a completed call, and five cannot be a whole number of sets.
    /// </summary>
    private static bool IsPlausibleMeldCount(int count) =>
        count == 0 || (count is >= 3 and <= 16 && count != 5);

    /// <summary>
    /// Our own meld count is known from the hand shape, so it is a free check on
    /// a proposed meld-node mapping. Reject the whole mapping rather than trust a
    /// container that disagrees.
    /// </summary>
    private static bool SelfMeldCountMatches(int tileCount, int calledMelds)
    {
        if (calledMelds <= 0)
            return tileCount == 0;

        return tileCount >= calledMelds * 3 && tileCount <= calledMelds * 4;
    }

    /// <summary>
    /// Reconstructs the tiles in our own latest call from what left the hand.
    /// Pon and kan give identical removed tiles; chi needs the claimed tile,
    /// which is only added when it really completes the run.
    /// </summary>
    private void TrackOwnMeldTiles(List<int> removed)
    {
        if (removed.Count is < 2 or > 3)
            return;

        ownMeldTiles.AddRange(removed);

        int claimed = -1;
        if (removed.Distinct().Count() == 1)
        {
            claimed = removed[0];
        }
        else if (removed.Count == 2
            && LastDiscardTile is >= 0 and < Tiles.Kinds
            && FormsRun(removed[0], removed[1], LastDiscardTile))
        {
            claimed = LastDiscardTile;
        }

        if (claimed >= 0)
            ownMeldTiles.Add(claimed);
    }

    private static bool FormsRun(int a, int b, int c)
    {
        if (Tiles.IsHonor(a) || Tiles.IsHonor(b) || Tiles.IsHonor(c))
            return false;
        if (Tiles.SuitBase(a) != Tiles.SuitBase(b) || Tiles.SuitBase(a) != Tiles.SuitBase(c))
            return false;

        int[] ranks = [Tiles.Rank(a), Tiles.Rank(b), Tiles.Rank(c)];
        Array.Sort(ranks);
        return ranks[0] + 1 == ranks[1] && ranks[1] + 1 == ranks[2];
    }

    /// <summary>
    /// Cross-checks candidate piles against the count bytes. Exact agreement on
    /// at least three seats, since a riichi declaration tile may render as a
    /// separate sprite and read one short.
    /// </summary>
    private bool PondsMatchCounts(
        AtkUnitBase* unit, LayoutProfile profile, int[] pondIds, int[] expected, int textureBase)
    {
        int agree = 0;
        int total = 0;

        for (int seat = 0; seat < 4; seat++)
        {
            var pond = inspector.ReadTileContainer(unit, pondIds[seat], textureBase);
            if (pond is null)
                return false;

            total += expected[seat];
            if (pond.Count == expected[seat] || pond.Count == expected[seat] - 1)
                agree++;
        }

        // Before anyone has discarded there is nothing to disagree about.
        return total == 0 || agree >= 3;
    }

    private void ClearRiichi()
    {
        for (int i = 0; i < 4; i++)
        {
            riichiDeclared[i] = false;
            riichiAtIndex[i] = -1;
        }
    }

    /// <summary>
    /// Every seat discards on nearly every go-around, so piles stay within a tile
    /// or two of each other. A pile far behind the rest is not a pile.
    /// </summary>
    private static bool IsMappingImplausible(SeatView[] seats)
    {
        int biggest = seats.Max(s => s.Discards.Count);
        int smallest = seats.Min(s => s.Discards.Count);
        return biggest >= 6 && smallest * 3 < biggest;
    }

    /// <summary>
    /// Notes which pond just grew. That newest tile IS the tile a call prompt is
    /// offering -- far more reliable than guessing at AtkValue slots.
    /// </summary>
    private void TrackNewDiscard(SeatView[] seats)
    {
        int grown = 0;
        for (int seat = 0; seat < 4; seat++)
        {
            var now = seats[seat].Discards;
            var before = previousPonds[seat];

            if (now.Count > before.Count)
            {
                grown++;
                LastDiscardTile = now[^1];
                LastDiscardSeat = seat;
            }

            before.Clear();
            before.AddRange(now);
        }

        // More than one pond grew between reads, so we cannot say which discard
        // a prompt belongs to. Refuse to guess rather than name the wrong tile.
        if (grown > 1)
        {
            LastDiscardTile = -1;
            LastDiscardSeat = -1;
        }
    }

    /// <summary>
    /// 14, 11, 8, 5 and 2 concealed tiles correspond to 0-4 called melds.
    /// The 13/10/7/4 forms are the same hand awaiting a draw.
    /// Anything else means the addon is mid-write.
    /// </summary>
    private static int InferCalledMelds(int concealedCount) => concealedCount switch
    {
        14 or 13 => 0,
        11 or 10 => 1,
        8 or 7 => 2,
        5 or 4 => 3,
        2 or 1 => 4,
        _ => -1,
    };

    private static int ReadAtkInt(AtkUnitBase* unit, int index, int fallback)
    {
        if (unit->AtkValues is null || index < 0 || index >= unit->AtkValuesCount)
            return fallback;

        var value = unit->AtkValues[index];
        return value.Type switch
        {
            ValueType.Int => value.Int,
            ValueType.UInt => (int)value.UInt,
            _ => fallback,
        };
    }
}
