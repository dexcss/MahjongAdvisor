using System.Text.Json;
using System.Text.Json.Serialization;

namespace MahjongAdvisor.Reading;

/// <summary>
/// Per-client addon layout. Offsets are byte offsets into AtkUnitBase*.
/// Kept in JSON so a patch break is a data edit, not a rebuild.
/// </summary>
public sealed class LayoutProfile
{
    public string Name { get; set; } = "Emj";

    public string AddonName { get; set; } = "Emj";

    /// <summary>Subtract from the raw hand-array int32 to get a 0-33 tile id.</summary>
    public int TileTextureBase { get; set; } = 76041;

    public LayoutOffsets Offsets { get; set; } = new();

    public LayoutStateCodes StateCodes { get; set; } = new();

    /// <summary>
    /// Node ids of the four discard-pile containers, in seat order starting with
    /// self. Empty until dumped in-game with <c>/mjadv nodes</c>. While empty the
    /// advisor runs efficiency-only and says so.
    /// </summary>
    public int[] PondContainerNodeIds { get; set; } = [];

    /// <summary>
    /// Node ids of the four meld containers, same seat order. Optional.
    ///
    /// The client renders each seat's called sets as a strip of up to four slot
    /// nodes, so a parent id is not always present in the tile ancestry. The
    /// slot families below are read as a union with these parent ids.
    /// </summary>
    public int[] MeldContainerNodeIds { get; set; } = [112, 113, 114, 115];

    /// <summary>
    /// Slot node ids per seat, same order. The first entry is the strip parent;
    /// the rest are the individual meld slots. Taken from the clean-room node
    /// survey: self=112 family, opponents=113/114/115 families.
    /// </summary>
    public int[][] MeldSlotNodeIds { get; set; } =
    [
        [112, 1120001, 1120002, 1120003],
        [113, 1130001, 1130002, 1130003],
        [114, 1140001, 1140002, 1140003],
        [115, 1150001, 1150002, 1150003],
    ];

    public LayoutLimits Limits { get; set; } = new();

    /// <summary>
    /// Button captions as the client renders them. Used to work out which calls a
    /// prompt is offering. Override per language if the client is not English.
    /// </summary>
    public LayoutCallLabels CallLabels { get; set; } = new();

    /// <summary>
    /// Node ids of the riichi marker beside each seat, in seat order. Hidden until
    /// that player declares. Left empty they are derived as pond id + 1, which is
    /// how the client lays them out.
    /// </summary>
    public int[] RiichiMarkerNodeIds { get; set; } = [];

    /// <summary>
    /// Closed-hand tile component nodes, left to right (0-12). Slot 13 is the
    /// freshly drawn tile at <see cref="DrawnTileNodeId"/>. Needed for the
    /// discard callback, which addresses the tile by display slot, not by the
    /// memory hand array's slot order.
    /// </summary>
    public int[] HandSlotNodeIds { get; set; } =
        [134, 1340001, 1340002, 1340003, 1340004, 1340005, 1340006, 1340007, 1340008, 1340009, 1340010, 1340011, 1340012];

    public int DrawnTileNodeId { get; set; } = 135;

    /// <summary>Within a hand-tile component: the button child, then its image child.</summary>
    public int HandButtonNodeListIndex { get; set; }

    public int HandImageNodeListIndex { get; set; } = 4;

    public bool HasPondNodes => PondContainerNodeIds.Length == 4;

    public bool HasHandSlots =>
        HandSlotNodeIds.Length == 13 && DrawnTileNodeId > 0;

    public bool HasMeldNodes =>
        Enumerable.Range(0, 4).All(seat => MeldNodeIdsForSeat(seat).Count > 0);

    /// <summary>
    /// Every node id that can contribute tiles to this seat's meld strip. The
    /// parent is first so a normal subtree read still works; the slot ids catch
    /// clients where the tiles live inside the individual slot components.
    /// </summary>
    public IReadOnlyList<int> MeldNodeIdsForSeat(int seat)
    {
        if (seat is < 0 or > 3)
            return [];

        var ids = new List<int>(8);
        void Add(int id)
        {
            if (id > 0 && !ids.Contains(id))
                ids.Add(id);
        }

        if (MeldContainerNodeIds.Length == 4)
            Add(MeldContainerNodeIds[seat]);
        if (MeldSlotNodeIds.Length == 4 && MeldSlotNodeIds[seat] is { } slots)
        {
            foreach (int id in slots)
                Add(id);
        }

        return ids;
    }
}

public sealed class LayoutOffsets
{
    public int HandArrayStart { get; set; } = 0x0DB8;
    public int DoraIndicator { get; set; } = 0x0FD8;
    public int[] SeatScores { get; set; } = [0x0500, 0x07E0, 0x0AC0, 0x0DA0];
    public int[] SeatDiscardCountBytes { get; set; } = [0x04FE, 0x07DE, 0x0ABE, 0x0D9E];
}

public sealed class LayoutStateCodes
{
    public int OurTurnDiscard { get; set; } = 30;
    public int SelfDeclareList { get; set; } = 6;
    public int CallPrompt { get; set; } = 15;

    /// <summary>Our forced discard after declaring riichi (drawn tile only).</summary>
    public int RiichiDiscard { get; set; } = 22;

    /// <summary>Result/score screen between hands. The game advances this itself.</summary>
    public int BetweenHands { get; set; } = 27;
    /// <summary>
    /// Post-call discard surface (28 on this client). The name is historical:
    /// the reference's live captures confirm the call popup itself is 15.
    /// </summary>
    public int CallPromptList { get; set; } = 28;

    public int ChiVariantSelect { get; set; } = 25;
    public int HandResult { get; set; } = 29;
    public int EndOfHand { get; set; } = 32;
    public int NewHand { get; set; } = 2;
    public int PostDrawIdle { get; set; } = 5;

    /// <summary>
    /// First AtkValue slot of the consecutive string run holding call/self-action
    /// button labels. A button's fire value is its position in that run.
    /// </summary>
    public int ActionButtonBaseIndex { get; set; } = 7;

    /// <summary>Index into AtkValues holding the state code.</summary>
    public int StateCodeValueIndex { get; set; }

    /// <summary>Index into AtkValues holding the wall count.</summary>
    public int WallCountValueIndex { get; set; } = 1;

    /// <summary>Range of AtkValue slots scanned for the claimed tile during a call prompt.</summary>
    public int ClaimScanLo { get; set; } = 16;

    public int ClaimScanHi { get; set; } = 21;

    /// <summary>The single slot that carries the chi claim.</summary>
    public int ChiClaimedTile { get; set; } = 19;

    /// <summary>How far to scan AtkValues for the prompt's button labels.</summary>
    public int ButtonLabelScanLimit { get; set; } = 20;
}

public sealed class LayoutCallLabels
{
    public string[] Pon { get; set; } = ["pon"];
    public string[] Chi { get; set; } = ["chi"];
    public string[] Kan { get; set; } = ["kan", "ankan", "minkan", "shouminkan"];
    public string[] Riichi { get; set; } = ["riichi", "reach"];
    public string[] Ron { get; set; } = ["ron"];
    public string[] Tsumo { get; set; } = ["tsumo"];
    public string[] Pass { get; set; } = ["pass", "skip", "cancel"];
}

public sealed class LayoutLimits
{
    public int HandSize { get; set; } = 14;
    public int WallInitial { get; set; } = 70;
    public int ScoreSanityMax { get; set; } = 200000;
    public int DiscardCountSanityMax { get; set; } = 40;
}

public static class LayoutProfileLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Loads every *.json in the directory. Returns the built-in defaults if the
    /// directory is missing or empty, so a bad deploy degrades rather than throws.
    /// </summary>
    public static IReadOnlyList<LayoutProfile> LoadAll(string directory, Action<string>? warn = null)
    {
        var profiles = new List<LayoutProfile>();

        if (Directory.Exists(directory))
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    var profile = JsonSerializer.Deserialize<LayoutProfile>(File.ReadAllText(path), Options);
                    if (profile is not null && !string.IsNullOrWhiteSpace(profile.AddonName))
                        profiles.Add(profile);
                }
                catch (Exception ex)
                {
                    warn?.Invoke($"Layout profile {Path.GetFileName(path)} could not be read: {ex.Message}");
                }
            }
        }

        if (profiles.Count == 0)
        {
            profiles.Add(new LayoutProfile { Name = "Emj", AddonName = "Emj" });
            profiles.Add(new LayoutProfile { Name = "EmjL", AddonName = "EmjL" });
        }

        return profiles;
    }

    public static void Save(string path, LayoutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string json = JsonSerializer.Serialize(profile, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }
}
